using POS.Application.Models;
using POS.Core.Enums;

namespace POS.Application.Abstractions;

/// <summary>
/// Opens, closes, and records movements against a <see cref="POS.Core.Entities.CashSession"/> — the
/// financial cash-drawer session on a Register. Independent of user login state: closing a session is
/// always an explicit action here, never an implicit side effect of <see cref="ICurrentSession.Clear"/>.
/// </summary>
public interface ICashSessionService
{
    /// <summary>
    /// The session the current user should record against for <paramref name="registerId"/>, honoring
    /// the store's configured mode: a shared session (one drawer, any cashier can ring against it) or
    /// per-cashier (each cashier's own session; the current user's own open session is returned even if
    /// a different cashier's session is separately open on the same register).
    /// </summary>
    Task<CashSessionDto?> GetActiveSessionAsync(Guid registerId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CashSessionDto>> GetOpenSessionsAsync(CancellationToken cancellationToken = default);

    Task<(bool Success, string? Error, CashSessionDto? Session)> OpenSessionAsync(
        Guid registerId, decimal openingCashAmount, string? currencyCode = null, CancellationToken cancellationToken = default);

    Task<(bool Success, string? Error, CashSessionSummaryDto? Summary)> CloseSessionAsync(
        Guid cashSessionId, decimal closingCountedAmount, string? notes = null, CancellationToken cancellationToken = default);

    /// <summary>Manual cash addition/removal (bank drop, float top-up, paid-out expense). CashOut may require an approving manager depending on store policy.</summary>
    Task<(bool Success, string? Error)> RecordManualMovementAsync(
        Guid cashSessionId, CashMovementType type, decimal amount, string? notes = null, Guid? approvedByUserId = null, CancellationToken cancellationToken = default);

    Task<CashSessionSummaryDto?> GetSummaryAsync(Guid cashSessionId, CancellationToken cancellationToken = default);
}
