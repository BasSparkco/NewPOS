using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using POS.Application.Abstractions;
using POS.Application.Models;
using POS.Core.Entities;
using POS.Core.Enums;
using POS.Infrastructure.Data;

namespace POS.Tests;

public class CashSessionServiceTests
{
    [Fact]
    public async Task Opening_a_session_records_the_float_and_prevents_a_second_open_session_on_the_same_register()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var registerId = await GetOrCreateRegisterIdAsync(services);
            var cashSessions = services.GetRequiredService<ICashSessionService>();

            var (success, error, session) = await cashSessions.OpenSessionAsync(registerId, 200m);
            Assert.True(success, error);
            Assert.NotNull(session);
            Assert.Equal(CashSessionStatus.Open, session!.Status);
            Assert.Equal(200m, session.OpeningCashAmount);

            var (secondSuccess, secondError, _) = await cashSessions.OpenSessionAsync(registerId, 100m);
            Assert.False(secondSuccess);
            Assert.NotNull(secondError);

            var summary = await cashSessions.GetSummaryAsync(session.Id);
            Assert.NotNull(summary);
            Assert.Single(summary!.Movements, m => m.Type == CashMovementType.OpeningFloat && m.Amount == 200m);
        });
    }

    [Fact]
    public async Task Closing_a_session_computes_expected_balance_and_discrepancy_from_cash_movements_only()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var registerId = await GetOrCreateRegisterIdAsync(services);
            var cashSessions = services.GetRequiredService<ICashSessionService>();

            var (_, _, session) = await cashSessions.OpenSessionAsync(registerId, 100m);

            var (cashInOk, cashInError) = await cashSessions.RecordManualMovementAsync(session!.Id, CashMovementType.CashIn, 50m, "Float top-up");
            Assert.True(cashInOk, cashInError);

            var (cashOutOk, cashOutError) = await cashSessions.RecordManualMovementAsync(session.Id, CashMovementType.CashOut, 20m, "Paid-out expense");
            Assert.True(cashOutOk, cashOutError);

            // Expected: 100 opening + 50 in - 20 out = 130. Counted 125 -> discrepancy -5 (short).
            var (closeSuccess, closeError, summary) = await cashSessions.CloseSessionAsync(session.Id, 125m);
            Assert.True(closeSuccess, closeError);
            Assert.NotNull(summary);
            Assert.Equal(130m, summary!.ExpectedCashAmount);
            Assert.Equal(130m, summary.Session.ExpectedCashAmount);
            Assert.Equal(-5m, summary.Session.DiscrepancyAmount);
            Assert.Equal(CashSessionStatus.Closed, summary.Session.Status);

            // Closed sessions cannot be closed again or receive further movements.
            var (reCloseSuccess, reCloseError, _) = await cashSessions.CloseSessionAsync(session.Id, 130m);
            Assert.False(reCloseSuccess);
            Assert.NotNull(reCloseError);
        });
    }

    [Fact]
    public async Task Cash_sale_and_refund_payments_are_recorded_against_the_open_session_without_blocking_the_sale()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var sales = services.GetRequiredService<ISaleService>();
            var cashSessions = services.GetRequiredService<ICashSessionService>();
            var catalog = services.GetRequiredService<IProductCatalogService>();

            var product = await catalog.CreateProductAsync(new ProductEditDto
            {
                Name = "Cash Session Test Item",
                Price = 15m,
                Cost = 5m,
                CategoryId = host.CategoryId,
                InitialStock = 10m,
                IsActive = true
            });

            // No cash session open yet — completing a sale must still succeed (never blocked).
            var invoiceId = await sales.StartNewSaleAsync();

            await using var db = await services.GetRequiredService<IDbContextFactory<PosDbContext>>().CreateDbContextAsync();
            var registerId = await db.Invoices.AsNoTracking().Where(i => i.Id == invoiceId).Select(i => i.RegisterId).SingleAsync();
            Assert.NotNull(registerId);

            var (openSuccess, openError, session) = await cashSessions.OpenSessionAsync(registerId!.Value, 0m);
            Assert.True(openSuccess, openError);

            await sales.AddOrMergeLineAsync(invoiceId, product.Id, 1m);
            var result = await sales.CompleteCashSaleAsync(invoiceId, product.Price);
            Assert.True(result.Success, result.ErrorMessage);

            var summaryAfterSale = await cashSessions.GetSummaryAsync(session!.Id);
            Assert.NotNull(summaryAfterSale);
            Assert.Single(summaryAfterSale!.Movements, m => m.Type == CashMovementType.SaleReceipt && m.InvoiceId == invoiceId);
            Assert.Equal(product.Price, summaryAfterSale.TotalCashReceipts);

            var (refundSuccess, refundError) = await sales.RefundInvoiceAsync(invoiceId);
            Assert.True(refundSuccess, refundError);

            var summaryAfterRefund = await cashSessions.GetSummaryAsync(session.Id);
            Assert.NotNull(summaryAfterRefund);
            Assert.Single(summaryAfterRefund!.Movements, m => m.Type == CashMovementType.Refund && m.InvoiceId == invoiceId);
            Assert.Equal(product.Price, summaryAfterRefund.TotalCashRefunds);

            // Net cash impact of a sale immediately refunded in the same session is zero.
            var (closeSuccess, closeError, closedSummary) = await cashSessions.CloseSessionAsync(session.Id, 0m);
            Assert.True(closeSuccess, closeError);
            Assert.Equal(0m, closedSummary!.ExpectedCashAmount);
        });
    }

    [Fact]
    public async Task Closing_a_session_succeeds_while_an_unrelated_open_invoice_has_no_payment_in_progress()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var sales = services.GetRequiredService<ISaleService>();
            var cashSessions = services.GetRequiredService<ICashSessionService>();

            // A held/open invoice with no payment currently in flight must not block closing — its
            // eventual sale will simply be recorded against whichever session is active *then*.
            var invoiceId = await sales.StartNewSaleAsync();

            await using var db = await services.GetRequiredService<IDbContextFactory<PosDbContext>>().CreateDbContextAsync();
            var registerId = await db.Invoices.AsNoTracking().Where(i => i.Id == invoiceId).Select(i => i.RegisterId).SingleAsync();
            Assert.NotNull(registerId);

            var (_, _, session) = await cashSessions.OpenSessionAsync(registerId!.Value, 100m);

            var (closeSuccess, closeError, summary) = await cashSessions.CloseSessionAsync(session!.Id, 100m);
            Assert.True(closeSuccess, closeError);
            Assert.NotNull(summary);
            Assert.Equal(0m, summary!.Session.DiscrepancyAmount);

            // The held invoice is untouched — still Open, still referencing its own original register.
            var stillOpen = await db.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId);
            Assert.Equal(InvoiceStatus.Open, stillOpen.Status);

            // Completing it now, with no session open on the register anymore, is correctly rejected
            // rather than silently completing without a cash movement (the session-is-required rule).
            await sales.AddOrMergeLineAsync(invoiceId, (await services.GetRequiredService<IProductCatalogService>().CreateProductAsync(new ProductEditDto
            {
                Name = "Close Guard Item",
                Price = 10m,
                Cost = 4m,
                CategoryId = host.CategoryId,
                InitialStock = 5m,
                IsActive = true
            })).Id, 1m);
            var completionWithoutSession = await sales.CompleteCashSaleAsync(invoiceId, 10m);
            Assert.False(completionWithoutSession.Success);

            // Opening a fresh session lets it complete normally, recorded against the new session.
            var (_, _, secondSession) = await cashSessions.OpenSessionAsync(registerId.Value, 0m);
            var completion = await sales.CompleteCashSaleAsync(invoiceId, 10m);
            Assert.True(completion.Success, completion.ErrorMessage);

            var secondSummary = await cashSessions.GetSummaryAsync(secondSession!.Id);
            Assert.Single(secondSummary!.Movements, m => m.Type == CashMovementType.SaleReceipt && m.InvoiceId == invoiceId);
        });
    }

    [Fact]
    public async Task Completing_a_cash_sale_is_rejected_without_an_open_cash_session()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var sales = services.GetRequiredService<ISaleService>();
            var catalog = services.GetRequiredService<IProductCatalogService>();

            var product = await catalog.CreateProductAsync(new ProductEditDto
            {
                Name = "No Session Item",
                Price = 12m,
                Cost = 4m,
                CategoryId = host.CategoryId,
                InitialStock = 10m,
                IsActive = true
            });

            var invoiceId = await sales.StartNewSaleAsync();
            await sales.AddOrMergeLineAsync(invoiceId, product.Id, 1m);

            // No cash session has ever been opened on this register.
            var result = await sales.CompleteCashSaleAsync(invoiceId, product.Price);
            Assert.False(result.Success);
            Assert.NotNull(result.ErrorMessage);

            // The invoice itself is untouched — still Open, so the cashier can retry after opening one.
            await using var db = await services.GetRequiredService<IDbContextFactory<PosDbContext>>().CreateDbContextAsync();
            var invoice = await db.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId);
            Assert.Equal(InvoiceStatus.Open, invoice.Status);
            Assert.Empty(await db.Payments.AsNoTracking().Where(p => p.InvoiceId == invoiceId).ToListAsync());
        });
    }

    [Fact]
    public async Task Refunding_a_cash_invoice_is_rejected_when_no_cash_session_is_open_at_refund_time()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var sales = services.GetRequiredService<ISaleService>();
            var cashSessions = services.GetRequiredService<ICashSessionService>();
            var catalog = services.GetRequiredService<IProductCatalogService>();

            var product = await catalog.CreateProductAsync(new ProductEditDto
            {
                Name = "Refund No Session Item",
                Price = 15m,
                Cost = 5m,
                CategoryId = host.CategoryId,
                InitialStock = 10m,
                IsActive = true
            });

            var invoiceId = await sales.StartNewSaleAsync();

            await using var db = await services.GetRequiredService<IDbContextFactory<PosDbContext>>().CreateDbContextAsync();
            var registerId = await db.Invoices.AsNoTracking().Where(i => i.Id == invoiceId).Select(i => i.RegisterId).SingleAsync();
            Assert.NotNull(registerId);

            var (_, _, session) = await cashSessions.OpenSessionAsync(registerId!.Value, 0m);
            await sales.AddOrMergeLineAsync(invoiceId, product.Id, 1m);
            var completion = await sales.CompleteCashSaleAsync(invoiceId, product.Price);
            Assert.True(completion.Success, completion.ErrorMessage);

            // Close the only open session before attempting the refund.
            var (closeSuccess, closeError, _) = await cashSessions.CloseSessionAsync(session!.Id, product.Price);
            Assert.True(closeSuccess, closeError);

            var (refundSuccess, refundError) = await sales.RefundInvoiceAsync(invoiceId);
            Assert.False(refundSuccess);
            Assert.NotNull(refundError);

            // The invoice is untouched — still Paid, not silently cancelled without its cash movement.
            var stillPaid = await db.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId);
            Assert.Equal(InvoiceStatus.Paid, stillPaid.Status);
        });
    }

    /// <summary>
    /// Validates the concurrency mechanism itself: <see cref="CashSession.SyncVersion"/> is an EF
    /// optimistic-concurrency token, so a write built from a stale read (as if it had been read before a
    /// concurrent write committed) is rejected rather than silently overwriting that concurrent change —
    /// the exact property <see cref="ICashSessionService.CloseSessionAsync"/> and
    /// <see cref="ICashSessionService.RecordManualMovementAsync"/> rely on to detect and retry past a
    /// payment racing a close.
    /// </summary>
    [Fact]
    public async Task A_stale_cash_session_write_is_rejected_instead_of_silently_overwriting_a_concurrent_change()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            var registerId = await GetOrCreateRegisterIdAsync(services);
            var cashSessions = services.GetRequiredService<ICashSessionService>();
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();

            var (_, _, session) = await cashSessions.OpenSessionAsync(registerId, 100m);

            // A stale snapshot, as if read just before a concurrent write commits.
            await using var staleDb = await dbFactory.CreateDbContextAsync();
            var staleSession = await staleDb.CashSessions.SingleAsync(s => s.Id == session!.Id);

            // A real, later write against the same row (e.g. a sale's cash movement, or a close).
            var (cashInOk, cashInError) = await cashSessions.RecordManualMovementAsync(session!.Id, CashMovementType.CashIn, 25m);
            Assert.True(cashInOk, cashInError);

            // Committing the stale snapshot must fail loudly (DbUpdateConcurrencyException), never
            // silently overwrite the CashIn that was just recorded.
            staleSession.Notes = "stale write";
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleDb.SaveChangesAsync());

            var summary = await cashSessions.GetSummaryAsync(session.Id);
            Assert.Single(summary!.Movements, m => m.Type == CashMovementType.CashIn);
        });
    }

    [Fact]
    public async Task CashOut_requiring_approval_rejects_self_approval_and_an_unauthorized_approver()
    {
        await using var host = await TestServiceHost.CreateAsync();

        await host.ExecuteScopeAsync(async services =>
        {
            await using (var db = await services.GetRequiredService<IDbContextFactory<PosDbContext>>().CreateDbContextAsync())
            {
                var now = DateTime.UtcNow;
                db.Settings.Add(new Setting
                {
                    Id = Guid.NewGuid(),
                    TenantId = host.TenantId,
                    StoreId = host.StoreId,
                    Key = "Cash:RequireApprovalForCashOut",
                    Value = "true",
                    CreatedAt = now,
                    UpdatedAt = now
                });
                await db.SaveChangesAsync();
            }

            var registerId = await GetOrCreateRegisterIdAsync(services);
            var cashSessions = services.GetRequiredService<ICashSessionService>();
            var (_, _, session) = await cashSessions.OpenSessionAsync(registerId, 100m);

            var (selfApproveOk, selfApproveError) = await cashSessions.RecordManualMovementAsync(
                session!.Id, CashMovementType.CashOut, 30m, approvedByUserId: host.UserId);
            Assert.False(selfApproveOk);
            Assert.NotNull(selfApproveError);

            var (noApproverOk, noApproverError) = await cashSessions.RecordManualMovementAsync(session.Id, CashMovementType.CashOut, 30m);
            Assert.False(noApproverOk);
            Assert.NotNull(noApproverError);

            // A real manager (Permission.ManageSettings) approves — succeeds.
            Guid managerId;
            await using (var db = await services.GetRequiredService<IDbContextFactory<PosDbContext>>().CreateDbContextAsync())
            {
                var managerRoleId = await db.Roles.Where(r => r.TenantId == host.TenantId && r.PermissionsMask == (int)Permission.All)
                    .Select(r => r.Id).FirstAsync();
                managerId = Guid.NewGuid();
                var now = DateTime.UtcNow;
                db.Users.Add(new User
                {
                    Id = managerId,
                    TenantId = host.TenantId,
                    Username = "manager2",
                    NormalizedUsername = "MANAGER2",
                    PasswordHash = "hash",
                    RoleId = managerRoleId,
                    StoreId = host.StoreId,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                await db.SaveChangesAsync();
            }

            var (approvedOk, approvedError) = await cashSessions.RecordManualMovementAsync(
                session.Id, CashMovementType.CashOut, 30m, approvedByUserId: managerId);
            Assert.True(approvedOk, approvedError);
        });
    }

    /// <summary>
    /// T6 matrix: "close a cash session opened under a different tenant/store → rejected." Closing is
    /// scoped by the caller's own current store (<see cref="CashSessionService.CloseSessionAsync"/> filters
    /// on <c>s.StoreId == _session.StoreId</c>), so a real, valid cash session id belonging to a different
    /// tenant's store must be indistinguishable from a nonexistent one, not merely "access denied" — no
    /// disclosure that the id even exists.
    /// </summary>
    [Fact]
    public async Task Closing_a_cash_session_belonging_to_a_different_tenant_and_store_is_rejected()
    {
        await using var host = await TestServiceHost.CreateAsync();

        var otherSessionId = Guid.Empty;
        await host.ExecuteScopeAsync(async services =>
        {
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();

            var now = DateTime.UtcNow;
            var otherTenantId = Guid.NewGuid();
            db.Tenants.Add(new Tenant { Id = otherTenantId, Name = "Other Tenant", NormalizedSlug = "other-tenant-cash", Status = TenantStatus.Active, CreatedAt = now, UpdatedAt = now });

            var otherStore = new Store { Id = Guid.NewGuid(), TenantId = otherTenantId, Name = "Other Tenant Store", BaseCurrencyId = host.BaseCurrencyId, CreatedAt = now, UpdatedAt = now };
            db.Stores.Add(otherStore);

            var otherRole = new Role { Id = Guid.NewGuid(), TenantId = otherTenantId, Name = "Admin", PermissionsMask = (int)Permission.All, CreatedAt = now, UpdatedAt = now };
            db.Roles.Add(otherRole);

            var otherUserId = Guid.NewGuid();
            db.Users.Add(new User
            {
                Id = otherUserId,
                TenantId = otherTenantId,
                Username = "other.tenant.cashier",
                NormalizedUsername = "OTHER.TENANT.CASHIER",
                PasswordHash = "hash",
                RoleId = otherRole.Id,
                StoreId = otherStore.Id,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            });

            var otherRegisterId = Guid.NewGuid();
            db.Registers.Add(new Register { Id = otherRegisterId, TenantId = otherTenantId, StoreId = otherStore.Id, Number = 1, Name = "Register 1", CreatedAt = now, UpdatedAt = now });

            otherSessionId = Guid.NewGuid();
            db.CashSessions.Add(new CashSession
            {
                Id = otherSessionId,
                TenantId = otherTenantId,
                StoreId = otherStore.Id,
                RegisterId = otherRegisterId,
                OpenedByUserId = otherUserId,
                OpenedAt = now,
                OpeningCashAmount = 100m,
                CurrencyCode = "USD",
                Status = CashSessionStatus.Open,
                CreatedAt = now,
                UpdatedAt = now
            });

            await db.SaveChangesAsync();
        });

        await host.ExecuteScopeAsync(async services =>
        {
            var cashSessions = services.GetRequiredService<ICashSessionService>();

            // host's own session is still tenant A / host.StoreId — attempting to close tenant B's real,
            // valid, open session id must fail exactly like a nonexistent id would.
            var (success, error, summary) = await cashSessions.CloseSessionAsync(otherSessionId, 100m);
            Assert.False(success);
            Assert.NotNull(error);
            Assert.Null(summary);

            await using var db = await services.GetRequiredService<IDbContextFactory<PosDbContext>>().CreateDbContextAsync();
            var stillOpen = await db.CashSessions.AsNoTracking().Where(s => s.Id == otherSessionId).Select(s => s.Status).SingleAsync();
            Assert.Equal(CashSessionStatus.Open, stillOpen);
        });
    }

    private static async Task<Guid> GetOrCreateRegisterIdAsync(IServiceProvider services)
    {
        var sales = services.GetRequiredService<ISaleService>();
        var invoiceId = await sales.StartNewSaleAsync();

        await using var db = await services.GetRequiredService<IDbContextFactory<PosDbContext>>().CreateDbContextAsync();
        var registerId = await db.Invoices.AsNoTracking().Where(i => i.Id == invoiceId).Select(i => i.RegisterId).SingleAsync();
        Assert.NotNull(registerId);

        // This invoice only existed to discover the register's id — leaving it Open would trip the
        // "cannot close with open invoices" guard in unrelated tests that go on to close a session here.
        await sales.CancelInvoiceAsync(invoiceId);

        return registerId!.Value;
    }
}
