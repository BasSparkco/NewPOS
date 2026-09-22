using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using POS.Application.Abstractions;
using POS.Application.Models;
using POS.Core.Enums;
using POS.Infrastructure.Data;

namespace POS.Tests;

/// <summary>
/// Stage 4T/T4.5: a single rehearsal of the full offline-first cash lifecycle the user asked to see
/// exercised end to end — an offline cash sale and refund, an employee logout, background sync
/// continuing under this terminal's own device credential while logged out, the employee reconnecting,
/// and closing the till with a correct reconciliation. Each phase reuses the same real services
/// (ISaleService, ICashSessionService, IInvoiceSyncService) other test files exercise individually —
/// this test's value is proving they compose correctly across a realistic shift, not re-testing any one
/// rule in isolation.
/// </summary>
public class EndToEndWorkflowTests
{
    [Fact]
    public async Task Offline_sale_refund_logout_device_sync_reconnect_and_cash_reconciliation()
    {
        var pushedInvoiceIds = new HashSet<Guid>();
        var pushedCashSessionIds = new HashSet<Guid>();
        var deviceTokenRequested = false;
        var userLoginCount = 0;
        Guid enrolledDeviceId = Guid.Empty;

        var handler = new FakeSyncHttpMessageHandler(async (request, cancellationToken) =>
        {
            var path = request.RequestUri?.AbsolutePath;

            if (path == "/api/auth/login")
            {
                userLoginCount++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { accessToken = "user-token" })
                };
            }

            if (path == "/api/devices/token")
            {
                deviceTokenRequested = true;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { accessToken = "device-token", expiresAtUtc = DateTime.UtcNow.AddHours(24) })
                };
            }

            if (path == "/api/sync/invoices/push")
            {
                var body = await request.Content!.ReadFromJsonAsync<InvoiceSyncBatchDto>(cancellationToken: cancellationToken);
                foreach (var invoice in body!.Invoices)
                    pushedInvoiceIds.Add(invoice.InvoiceId);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new InvoiceSyncPushResultDto(
                        body.Invoices.Select(i => new InvoiceSyncInvoiceResultDto(i.InvoiceId, "Applied", i.SyncVersion, null)).ToList()))
                };
            }

            if (path == "/api/sync/cash-sessions/push")
            {
                var body = await request.Content!.ReadFromJsonAsync<CashSessionSyncBatchDto>(cancellationToken: cancellationToken);
                foreach (var session in body!.Sessions)
                    pushedCashSessionIds.Add(session.CashSessionId);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new CashSessionSyncPushResultDto(
                        body.Sessions.Select(s => new CashSessionSyncItemResultDto(s.CashSessionId, "Applied", s.SyncVersion, null)).ToList()))
                };
            }

            // Every other endpoint a real background pass would also touch (registers, categories,
            // products, settings, users, devices, audit logs, pulls) isn't exercised by name in this
            // test — a 404 is a harmless "nothing changed" from the caller's perspective for all of them.
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        await using var host = await TestServiceHost.CreateAsync(
            new Dictionary<string, string?>
            {
                ["Sync:Enabled"] = "true",
                ["Sync:ApiBaseUrl"] = "https://sync.example.test",
                ["Sync:BatchSize"] = "10"
            },
            services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));
            });

        Guid invoiceId = default, registerId = default;
        CashSessionDto? session = null;

        // --- Phase 0: enroll this machine so it has its own device credential for later. ---
        await host.ExecuteScopeAsync(async services =>
        {
            var devices = services.GetRequiredService<IDeviceManagementService>();
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();

            var (provisionOk, provisionError, enrollmentCode) = await devices.ProvisionDeviceAsync("POS.Tests");
            Assert.True(provisionOk, provisionError);
            var (enrollOk, enrollError, deviceSecret) = await devices.EnrollCurrentMachineAsync(enrollmentCode!);
            Assert.True(enrollOk, enrollError);
            Assert.False(string.IsNullOrEmpty(deviceSecret));

            await using var db = await dbFactory.CreateDbContextAsync();
            var device = await db.Devices.SingleAsync(d => d.StoreId == host.StoreId && d.Name == "POS.Tests");
            enrolledDeviceId = device.Id;
            var now = DateTime.UtcNow;
            db.Settings.Add(new POS.Core.Entities.Setting
            {
                Id = Guid.NewGuid(),
                TenantId = host.TenantId,
                StoreId = host.StoreId,
                Key = "Sync.DeviceSecret",
                Value = deviceSecret!,
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        });

        // --- Phase 1 & 2: offline cash sale, then an offline refund — no network involved at all;
        // still logged in as the seeded admin from TestServiceHost.CreateAsync. ---
        await host.ExecuteScopeAsync(async services =>
        {
            var sales = services.GetRequiredService<ISaleService>();
            var catalog = services.GetRequiredService<IProductCatalogService>();
            var cashSessions = services.GetRequiredService<ICashSessionService>();
            var dbFactory = services.GetRequiredService<IDbContextFactory<PosDbContext>>();

            var product = await catalog.CreateProductAsync(new ProductEditDto
            {
                Name = "Workflow Item",
                Price = 40m,
                Cost = 15m,
                CategoryId = host.CategoryId,
                InitialStock = 20m,
                IsActive = true
            });

            invoiceId = await sales.StartNewSaleAsync();
            await using var db = await dbFactory.CreateDbContextAsync();
            registerId = (await db.Invoices.AsNoTracking().Where(i => i.Id == invoiceId).Select(i => i.RegisterId).SingleAsync())!.Value;

            var (openOk, openError, openedSession) = await cashSessions.OpenSessionAsync(registerId, 200m);
            Assert.True(openOk, openError);
            session = openedSession;

            await sales.AddOrMergeLineAsync(invoiceId, product.Id, 2m);
            var completion = await sales.CompleteCashSaleAsync(invoiceId, 80m);
            Assert.True(completion.Success, completion.ErrorMessage);

            var (refundOk, refundError) = await sales.RefundInvoiceAsync(invoiceId);
            Assert.True(refundOk, refundError);

            var midSummary = await cashSessions.GetSummaryAsync(session!.Id);
            Assert.Equal(80m, midSummary!.TotalCashReceipts);
            Assert.Equal(80m, midSummary.TotalCashRefunds);
        });

        // --- Phase 3: the employee logs out. ---
        host.Session.Clear();
        Assert.False(host.Session.IsAuthenticated);
        Assert.False(host.Session.HasSyncScope);

        // --- Phase 4: background sync keeps running under this terminal's own device credential — a
        // fresh scope, matching InvoiceSyncBackgroundService creating a new one on every real pass. ---
        await host.ExecuteScopeAsync(async services =>
        {
            var sync = services.GetRequiredService<IInvoiceSyncService>();

            await sync.EnsureSyncScopeAsync();
            Assert.True(host.Session.HasSyncScope);
            Assert.False(host.Session.IsAuthenticated);

            var invoicePushWhileLoggedOut = await sync.PushUnsyncedInvoicesAsync();
            Assert.True(invoicePushWhileLoggedOut.Attempted >= 1);
            Assert.True(deviceTokenRequested, "Sync must authenticate via the device credential, not a user login, while logged out.");
            Assert.Equal(0, userLoginCount);
            Assert.Contains(invoiceId, pushedInvoiceIds);
        });

        // --- Phase 5: the employee reconnects (signs back in). ---
        host.Session.Set(host.TenantId, host.UserId, host.StoreId, "admin", "Admin", (int)Permission.All, "USD", "$");
        host.Session.SetPassword("admin");
        Assert.True(host.Session.IsAuthenticated);

        // --- Phase 5b: sync resumes as the reconnected employee — another fresh scope, its own fresh
        // (uncached) authorized client, so this proves a real /api/auth/login call, not a reused token. ---
        await host.ExecuteScopeAsync(async services =>
        {
            var sync = services.GetRequiredService<IInvoiceSyncService>();

            var cashSessionPushAfterReconnect = await sync.PushUpdatedCashSessionsAsync();
            Assert.True(cashSessionPushAfterReconnect.Attempted >= 1);
            Assert.Equal(1, userLoginCount); // authenticates as the reconnected employee, not the device.
            Assert.Contains(session!.Id, pushedCashSessionIds);
        });

        // --- Phase 6: cash reconciliation — the sale and its immediate refund net to zero. ---
        await host.ExecuteScopeAsync(async services =>
        {
            var cashSessions = services.GetRequiredService<ICashSessionService>();

            var (closeOk, closeError, closedSummary) = await cashSessions.CloseSessionAsync(session!.Id, 200m);
            Assert.True(closeOk, closeError);
            Assert.Equal(200m, closedSummary!.ExpectedCashAmount);
            Assert.Equal(0m, closedSummary.Session.DiscrepancyAmount);
            Assert.Equal(CashSessionStatus.Closed, closedSummary.Session.Status);
        });
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://sync.example.test/")
        };
    }

    private sealed class FakeSyncHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            responder(request, cancellationToken);
    }
}
