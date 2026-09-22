using Microsoft.EntityFrameworkCore;
using POS.Application.Abstractions;
using POS.Application.Models;
using POS.Core.Entities;
using POS.Core.Enums;
using POS.Infrastructure.Data;

namespace POS.Infrastructure.Services;

/// <summary>
/// Writes a sale/refund <see cref="CashMovement"/> using the caller's own <see cref="PosDbContext"/> and
/// transaction, so the invoice, its payment, and the cash-drawer ledger entry commit or roll back
/// together — the movement can never be silently lost the way a separate best-effort write could be.
/// Internal to POS.Infrastructure (not part of <see cref="ICashSessionService"/>, which is an
/// Application-layer abstraction that must not reference <see cref="PosDbContext"/>) and implemented by
/// the same <see cref="CashSessionService"/> instance so both interfaces share one register-lookup and
/// session-matching implementation.
/// </summary>
internal interface ICashMovementWriter
{
    /// <summary>
    /// Adds (never saves) a SaleReceipt movement against whichever session is active for the invoice's
    /// register, and rebinds <paramref name="payment"/>'s <c>CashSessionId</c> to it. A cash sale
    /// requires an open, authorized session on this register — when none is open (or this caller isn't
    /// authorized against the one that is, per per-cashier/shared mode), this fails rather than silently
    /// completing the sale with no cash movement recorded. The caller must roll back and surface the
    /// error rather than commit. Opening a session and recording this movement are both purely local —
    /// neither requires network connectivity.
    /// </summary>
    Task<(bool Success, string? Error)> WriteSalePaymentAsync(PosDbContext db, Invoice invoice, Payment payment, CancellationToken cancellationToken);

    /// <summary>
    /// Adds (never saves) a Refund movement against whichever session is active for the invoice's
    /// register right now (which may differ from the session the original sale was rung under). Never
    /// rewrites the original payment's own <c>CashSessionId</c>. Same "no open session → fail, don't
    /// silently drop the movement" rule as <see cref="WriteSalePaymentAsync"/>, restricted to cash
    /// payments — refunding a non-cash payment never touches the cash drawer.
    /// </summary>
    Task<(bool Success, string? Error)> WriteRefundPaymentAsync(PosDbContext db, Invoice invoice, Payment payment, CancellationToken cancellationToken);
}

/// <summary>
/// Store-configurable via the "Cash:SessionMode" setting ("PerCashier", the default — each cashier
/// opens and owns their own session — or "PerRegister" — one shared drawer session any authorized
/// cashier on that register can record against/close). Either way, at most one session can be open on
/// a given Register at a time (enforced by a DB unique filtered index), matching a fixed-register
/// workflow rather than a floating-till model.
/// </summary>
internal sealed class CashSessionService : ICashSessionService, ICashMovementWriter
{
    private const string SessionModeKey = "Cash:SessionMode";
    private const string RequireCashOutApprovalKey = "Cash:RequireApprovalForCashOut";
    private const string SharedMode = "PerRegister";

    private readonly IDbContextFactory<PosDbContext> _dbFactory;
    private readonly ICurrentSession _session;
    private readonly IAuditLogService _auditLogService;

    public CashSessionService(IDbContextFactory<PosDbContext> dbFactory, ICurrentSession session, IAuditLogService auditLogService)
    {
        _dbFactory = dbFactory;
        _session = session;
        _auditLogService = auditLogService;
    }

    public async Task<CashSessionDto?> GetActiveSessionAsync(Guid registerId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var session = await FindActiveSessionAsync(db, registerId, cancellationToken);
        return session is null ? null : await ToDto(db, session, cancellationToken);
    }

    public async Task<IReadOnlyList<CashSessionDto>> GetOpenSessionsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var storeId = _session.StoreId;
        var sessions = await db.CashSessions
            .Where(s => s.StoreId == storeId && s.Status == CashSessionStatus.Open && !s.IsDeleted)
            .OrderBy(s => s.OpenedAt)
            .ToListAsync(cancellationToken);

        var result = new List<CashSessionDto>(sessions.Count);
        foreach (var s in sessions)
            result.Add(await ToDto(db, s, cancellationToken));
        return result;
    }

    public async Task<(bool Success, string? Error, CashSessionDto? Session)> OpenSessionAsync(
        Guid registerId, decimal openingCashAmount, string? currencyCode = null, CancellationToken cancellationToken = default)
    {
        if (openingCashAmount < 0)
            return (false, "Opening cash amount cannot be negative.", null);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var storeId = _session.StoreId;

        var register = await db.Registers.FirstOrDefaultAsync(r => r.Id == registerId && r.StoreId == storeId && !r.IsDeleted, cancellationToken);
        if (register is null)
            return (false, "Register not found.", null);

        var alreadyOpen = await db.CashSessions.AnyAsync(
            s => s.RegisterId == registerId && s.Status == CashSessionStatus.Open && !s.IsDeleted, cancellationToken);
        if (alreadyOpen)
            return (false, "A cash session is already open on this register.", null);

        var isShared = await IsSharedModeAsync(db, storeId, cancellationToken);
        var now = DateTime.UtcNow;
        var session = new CashSession
        {
            Id = Guid.NewGuid(),
            TenantId = register.TenantId,
            StoreId = storeId,
            RegisterId = registerId,
            OpenedByUserId = _session.UserId,
            OpenedAt = now,
            OpeningCashAmount = openingCashAmount,
            CurrencyCode = string.IsNullOrWhiteSpace(currencyCode) ? _session.BaseCurrencyCode : currencyCode.Trim().ToUpperInvariant(),
            Status = CashSessionStatus.Open,
            IsSharedSession = isShared,
            IsSynced = false,
            SyncVersion = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.CashSessions.Add(session);

        db.CashMovements.Add(new CashMovement
        {
            Id = Guid.NewGuid(),
            TenantId = register.TenantId,
            CashSessionId = session.Id,
            Type = CashMovementType.OpeningFloat,
            Amount = openingCashAmount,
            Method = PaymentMethod.Cash,
            CurrencyCode = session.CurrencyCode,
            PerformedByUserId = _session.UserId,
            CreatedAt = now,
            UpdatedAt = now
        });

        await db.SaveChangesAsync(cancellationToken);
        await TryWriteAuditAsync("CashSessionOpened", nameof(CashSession), session.Id,
            $"Register={register.Number}, Opening={openingCashAmount:0.##} {session.CurrencyCode}", cancellationToken);

        return (true, null, await ToDto(db, session, cancellationToken));
    }

    /// <summary>
    /// A held/open invoice with no payment currently in flight does not block closing — its eventual
    /// sale/refund movement will simply land in whichever session is active *then* (see
    /// <see cref="WritePaymentMovementAsync"/>), preserving that invoice's own history intact. What must
    /// never happen is a payment/refund/manual movement committing against this exact session after this
    /// method has already read its movements and computed the expected balance from them — that race is
    /// closed by <see cref="CashSession.SyncVersion"/> being an EF optimistic-concurrency token: every
    /// write that touches a session (this one included, via <see cref="TouchSessionForWrite"/>) bumps it
    /// as part of the same save, so whichever of two racing writers commits second gets a
    /// <see cref="DbUpdateConcurrencyException"/> and must reload and recompute rather than silently
    /// closing over stale data (or overwriting a movement that was just added).
    /// </summary>
    public async Task<(bool Success, string? Error, CashSessionSummaryDto? Summary)> CloseSessionAsync(
        Guid cashSessionId, decimal closingCountedAmount, string? notes = null, CancellationToken cancellationToken = default)
    {
        if (closingCountedAmount < 0)
            return (false, "Closing count cannot be negative.", null);

        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var storeId = _session.StoreId;

            var session = await db.CashSessions
                .FirstOrDefaultAsync(s => s.Id == cashSessionId && s.StoreId == storeId && !s.IsDeleted, cancellationToken);
            if (session is null)
                return (false, "Cash session not found.", null);

            if (session.Status != CashSessionStatus.Open)
                return (false, "Cash session is already closed.", null);

            if (!session.IsSharedSession && session.OpenedByUserId != _session.UserId)
                return (false, "Only the cashier who opened this session (or a shared-mode session) can close it.", null);

            var movements = await db.CashMovements
                .Where(m => m.CashSessionId == cashSessionId && !m.IsDeleted)
                .ToListAsync(cancellationToken);

            var expected = ComputeExpectedCash(session.OpeningCashAmount, movements);

            var now = DateTime.UtcNow;
            session.Status = CashSessionStatus.Closed;
            session.ClosedByUserId = _session.UserId;
            session.ClosedAt = now;
            session.ClosingCountedAmount = closingCountedAmount;
            session.ExpectedCashAmount = expected;
            session.DiscrepancyAmount = closingCountedAmount - expected;
            session.Notes = notes;
            TouchSessionForWrite(session, now);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Something else (a sale/refund/manual movement) committed against this session between
                // our read and our write. Reload the now-current state — including that movement — and
                // recompute rather than closing over data that was already stale by the time we wrote it.
                continue;
            }

            await TryWriteAuditAsync("CashSessionClosed", nameof(CashSession), session.Id,
                $"Counted={closingCountedAmount:0.##}, Expected={expected:0.##}, Discrepancy={session.DiscrepancyAmount:0.##} {session.CurrencyCode}",
                cancellationToken);

            var summary = await BuildSummaryAsync(db, session, movements, cancellationToken);
            return (true, null, summary);
        }

        return (false, "Could not close the session — it kept changing concurrently on this register. Please try again.", null);
    }

    public async Task<(bool Success, string? Error)> RecordManualMovementAsync(
        Guid cashSessionId, CashMovementType type, decimal amount, string? notes = null, Guid? approvedByUserId = null, CancellationToken cancellationToken = default)
    {
        if (type is not (CashMovementType.CashIn or CashMovementType.CashOut))
            return (false, "Only CashIn/CashOut can be recorded manually.");

        if (amount <= 0)
            return (false, "Amount must be positive.");

        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var storeId = _session.StoreId;

            var session = await db.CashSessions
                .FirstOrDefaultAsync(s => s.Id == cashSessionId && s.StoreId == storeId && !s.IsDeleted, cancellationToken);
            if (session is null)
                return (false, "Cash session not found.");

            if (session.Status != CashSessionStatus.Open)
                return (false, "Cash session is not open.");

            if (!session.IsSharedSession && session.OpenedByUserId != _session.UserId)
                return (false, "Only the cashier who opened this session can record cash movements on it.");

            if (type == CashMovementType.CashOut)
            {
                var requiresApproval = await GetBoolSettingAsync(db, storeId, RequireCashOutApprovalKey, cancellationToken);
                if (requiresApproval)
                {
                    if (approvedByUserId is null || approvedByUserId == _session.UserId)
                        return (false, "A different manager must approve this cash removal.");

                    var approverPermissions = await db.Users
                        .AsNoTracking()
                        .Where(u => u.Id == approvedByUserId && u.TenantId == session.TenantId && u.IsActive && !u.IsDeleted)
                        .Select(u => (int?)u.Role!.PermissionsMask)
                        .FirstOrDefaultAsync(cancellationToken);

                    if (approverPermissions is null || !((Permission)approverPermissions.Value).HasFlag(Permission.ManageSettings))
                        return (false, "The approving user was not found or is not authorized to approve cash removals.");
                }
            }

            var now = DateTime.UtcNow;
            db.CashMovements.Add(new CashMovement
            {
                Id = Guid.NewGuid(),
                TenantId = session.TenantId,
                CashSessionId = session.Id,
                Type = type,
                Amount = amount,
                Method = PaymentMethod.Cash,
                CurrencyCode = session.CurrencyCode,
                PerformedByUserId = _session.UserId,
                ApprovedByUserId = approvedByUserId,
                Notes = notes,
                CreatedAt = now,
                UpdatedAt = now
            });
            TouchSessionForWrite(session, now);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // The session was closed (or another movement landed) concurrently — reload and re-check
                // "is it still open" rather than silently recording against a session that just closed.
                continue;
            }

            await TryWriteAuditAsync(type == CashMovementType.CashIn ? "CashSessionCashIn" : "CashSessionCashOut",
                nameof(CashSession), session.Id, $"Amount={amount:0.##} {session.CurrencyCode}", cancellationToken);

            return (true, null);
        }

        return (false, "Could not record this movement — the session kept changing concurrently. Please try again.");
    }

    public async Task<CashSessionSummaryDto?> GetSummaryAsync(Guid cashSessionId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var storeId = _session.StoreId;
        var session = await db.CashSessions.FirstOrDefaultAsync(s => s.Id == cashSessionId && s.StoreId == storeId && !s.IsDeleted, cancellationToken);
        if (session is null)
            return null;

        var movements = await db.CashMovements.Where(m => m.CashSessionId == cashSessionId && !m.IsDeleted).ToListAsync(cancellationToken);
        return await BuildSummaryAsync(db, session, movements, cancellationToken);
    }

    public async Task<(bool Success, string? Error)> WriteSalePaymentAsync(PosDbContext db, Invoice invoice, Payment payment, CancellationToken cancellationToken) =>
        await WritePaymentMovementAsync(db, invoice, payment, CashMovementType.SaleReceipt, cancellationToken);

    public async Task<(bool Success, string? Error)> WriteRefundPaymentAsync(PosDbContext db, Invoice invoice, Payment payment, CancellationToken cancellationToken) =>
        await WritePaymentMovementAsync(db, invoice, payment, CashMovementType.Refund, cancellationToken);

    /// <summary>
    /// Adds the movement (and, for a sale, rebinds the payment) to the caller's own <paramref name="db"/>
    /// without saving — the caller's own <c>SaveChangesAsync</c>/commit is what makes this atomic with the
    /// sale/refund it belongs to, and includes the concurrency-token bump on the parent session (see
    /// <see cref="TouchSessionForWrite"/>), so a session closed concurrently between this call and the
    /// caller's own <c>SaveChangesAsync</c> surfaces as a <see cref="DbUpdateConcurrencyException"/> the
    /// caller must roll back on, never as a silently-lost movement. Idempotent by (PaymentId, Type) so a
    /// retried outer transaction can never double-record the same payment. Both cash and non-cash
    /// payments are non-cash-drawer no-ops here except that a cash payment/refund without an open,
    /// authorized session on this register is rejected outright — required per tenant.md §5b, not
    /// silently skipped.
    /// </summary>
    private async Task<(bool Success, string? Error)> WritePaymentMovementAsync(PosDbContext db, Invoice invoice, Payment payment, CashMovementType type, CancellationToken cancellationToken)
    {
        if (payment.Method != PaymentMethod.Cash)
            return (true, null); // Only cash moves the drawer; a card/other-method payment has nothing to record here.

        var alreadyRecorded = await db.CashMovements.AnyAsync(
            m => m.PaymentId == payment.Id && m.Type == type && !m.IsDeleted, cancellationToken);
        if (alreadyRecorded)
            return (true, null);

        if (invoice.RegisterId is null)
            return (false, "This register has no stable identity yet — an administrator needs to re-run the startup upgrade before cash sessions can be used here.");

        var activeSession = await FindActiveSessionAsync(db, invoice.RegisterId.Value, cancellationToken);
        if (activeSession is null)
        {
            return (false, type == CashMovementType.SaleReceipt
                ? "A cash session must be open on this register before completing a cash sale."
                : "A cash session must be open on this register before refunding a cash payment.");
        }

        var now = DateTime.UtcNow;
        db.CashMovements.Add(new CashMovement
        {
            Id = Guid.NewGuid(),
            TenantId = invoice.TenantId,
            CashSessionId = activeSession.Id,
            Type = type,
            Amount = payment.Amount,
            Method = payment.Method,
            CurrencyCode = invoice.Currency,
            InvoiceId = invoice.Id,
            PaymentId = payment.Id,
            PerformedByUserId = _session.UserId,
            CreatedAt = now,
            UpdatedAt = now
        });
        // Only a sale receipt rebinds the payment's own CashSessionId (the session it was
        // originally taken in). A later refund records its own ledger entry, tagged to the session
        // the refund happens in, but must not rewrite that original sale-time link.
        if (type == CashMovementType.SaleReceipt)
        {
            payment.CashSessionId = activeSession.Id;
            payment.UpdatedAt = now;
        }

        TouchSessionForWrite(activeSession, now);
        return (true, null);
    }

    /// <summary>
    /// Marks a session as touched by a local write: bumps the optimistic-concurrency token
    /// (<see cref="CashSession.SyncVersion"/>) so a racing close/movement is detected rather than
    /// silently lost, refreshes <see cref="CashSession.UpdatedAt"/>, and flags it unsynced so the next
    /// sync pass picks up the change. Every code path that adds a <see cref="CashMovement"/> or otherwise
    /// changes a session must call this as part of the same write.
    /// </summary>
    private static void TouchSessionForWrite(CashSession session, DateTime now)
    {
        session.SyncVersion++;
        session.UpdatedAt = now;
        session.IsSynced = false;
    }

    private async Task<CashSession?> FindActiveSessionAsync(PosDbContext db, Guid registerId, CancellationToken cancellationToken)
    {
        var storeId = _session.StoreId;
        var open = await db.CashSessions
            .FirstOrDefaultAsync(s => s.RegisterId == registerId && s.StoreId == storeId && s.Status == CashSessionStatus.Open && !s.IsDeleted, cancellationToken);

        if (open is null)
            return null;

        if (open.IsSharedSession || open.OpenedByUserId == _session.UserId)
            return open;

        // Per-cashier mode and this open session belongs to someone else — not "active" for this caller.
        return null;
    }

    private static decimal ComputeExpectedCash(decimal openingCashAmount, IReadOnlyList<CashMovement> movements)
    {
        var cashMovements = movements.Where(m => m.Method == PaymentMethod.Cash);
        var receipts = cashMovements.Where(m => m.Type is CashMovementType.SaleReceipt or CashMovementType.CashIn).Sum(m => m.Amount);
        var payouts = cashMovements.Where(m => m.Type is CashMovementType.Refund or CashMovementType.CashOut).Sum(m => m.Amount);
        return openingCashAmount + receipts - payouts;
    }

    private async Task<CashSessionSummaryDto> BuildSummaryAsync(PosDbContext db, CashSession session, IReadOnlyList<CashMovement> movements, CancellationToken cancellationToken)
    {
        var expected = session.Status == CashSessionStatus.Closed && session.ExpectedCashAmount.HasValue
            ? session.ExpectedCashAmount.Value
            : ComputeExpectedCash(session.OpeningCashAmount, movements);

        var cashMovements = movements.Where(m => m.Method == PaymentMethod.Cash).ToList();
        var totalCashReceipts = cashMovements.Where(m => m.Type == CashMovementType.SaleReceipt).Sum(m => m.Amount);
        var totalCashRefunds = cashMovements.Where(m => m.Type == CashMovementType.Refund).Sum(m => m.Amount);
        var totalCashIn = cashMovements.Where(m => m.Type == CashMovementType.CashIn).Sum(m => m.Amount);
        var totalCashOut = cashMovements.Where(m => m.Type == CashMovementType.CashOut).Sum(m => m.Amount);
        var totalNonCashReceipts = movements.Where(m => m.Method != PaymentMethod.Cash && m.Type == CashMovementType.SaleReceipt).Sum(m => m.Amount);

        var dto = await ToDto(db, session, cancellationToken);
        var movementDtos = await ToMovementDtos(db, movements, cancellationToken);

        return new CashSessionSummaryDto(dto, movementDtos, expected, totalCashReceipts, totalCashRefunds, totalCashIn, totalCashOut, totalNonCashReceipts);
    }

    private static async Task<CashSessionDto> ToDto(PosDbContext db, CashSession session, CancellationToken cancellationToken)
    {
        var register = await db.Registers.AsNoTracking().Where(r => r.Id == session.RegisterId)
            .Select(r => new { r.Number, r.Name }).FirstOrDefaultAsync(cancellationToken);
        var openedByUsername = await db.Users.AsNoTracking().Where(u => u.Id == session.OpenedByUserId)
            .Select(u => u.Username).FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

        return new CashSessionDto(
            session.Id,
            session.RegisterId,
            register?.Number ?? 0,
            register?.Name ?? string.Empty,
            session.OpenedByUserId,
            openedByUsername,
            session.OpenedAt,
            session.OpeningCashAmount,
            session.CurrencyCode,
            session.Status,
            session.IsSharedSession,
            session.ClosedByUserId,
            session.ClosedAt,
            session.ClosingCountedAmount,
            session.ExpectedCashAmount,
            session.DiscrepancyAmount);
    }

    private static async Task<IReadOnlyList<CashMovementDto>> ToMovementDtos(PosDbContext db, IReadOnlyList<CashMovement> movements, CancellationToken cancellationToken)
    {
        var userIds = movements.Select(m => m.PerformedByUserId).Distinct().ToList();
        var usernames = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Username, cancellationToken);

        return movements
            .OrderBy(m => m.CreatedAt)
            .Select(m => new CashMovementDto(
                m.Id, m.Type, m.Amount, m.Method, m.CurrencyCode, m.InvoiceId, m.PerformedByUserId,
                usernames.GetValueOrDefault(m.PerformedByUserId, string.Empty), m.ApprovedByUserId, m.Notes, m.CreatedAt))
            .ToList();
    }

    private static async Task<bool> IsSharedModeAsync(PosDbContext db, Guid storeId, CancellationToken cancellationToken)
    {
        var mode = await db.Settings.AsNoTracking()
            .Where(s => s.StoreId == storeId && s.Key == SessionModeKey && !s.IsDeleted)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken);
        return string.Equals(mode?.Trim(), SharedMode, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> GetBoolSettingAsync(PosDbContext db, Guid storeId, string key, CancellationToken cancellationToken)
    {
        var raw = await db.Settings.AsNoTracking()
            .Where(s => s.StoreId == storeId && s.Key == key && !s.IsDeleted)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken);
        return bool.TryParse(raw, out var parsed) && parsed;
    }

    private async Task TryWriteAuditAsync(string action, string entityName, Guid? entityId, string? details, CancellationToken cancellationToken)
    {
        try
        {
            await _auditLogService.WriteAsync(action, entityName, entityId, details, cancellationToken);
        }
        catch
        {
            // Audit logging is best-effort and must not block the business write.
        }
    }
}
