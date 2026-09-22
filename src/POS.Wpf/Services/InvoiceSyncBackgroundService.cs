using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using POS.Application.Abstractions;

namespace POS.Wpf.Services;

public sealed class InvoiceSyncBackgroundService : BackgroundService
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);
    private readonly IConfiguration _configuration;
    private readonly ILogger<InvoiceSyncBackgroundService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public InvoiceSyncBackgroundService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<InvoiceSyncBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_configuration.GetValue<bool?>("Sync:Enabled") ?? false)
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var sync = scope.ServiceProvider.GetRequiredService<IInvoiceSyncService>();
                    await sync.EnsureSyncScopeAsync(stoppingToken);
                    await sync.PushCurrencyPolicyAsync(stoppingToken);
                    await sync.PullCurrencyPolicyAsync(stoppingToken);
                    await sync.PushUpdatedSettingsAsync(stoppingToken);
                    await sync.PullRemoteSettingsAsync(stoppingToken);
                    await sync.PushUpdatedUsersAsync(stoppingToken);
                    await sync.PullRemoteUsersAsync(stoppingToken);
                    await sync.PushUpdatedCategoriesAsync(stoppingToken);
                    await sync.PullRemoteCategoriesAsync(stoppingToken);
                    await sync.PushUpdatedProductsAsync(stoppingToken);
                    await sync.PullRemoteProductsAsync(stoppingToken);
                    await sync.PushUpdatedDevicesAsync(stoppingToken);
                    await sync.PullRemoteDevicesAsync(stoppingToken);
                    // Registers must sync before CashSessions — a session's RegisterId is a required FK.
                    await sync.PushUpdatedRegistersAsync(stoppingToken);
                    await sync.PullRemoteRegistersAsync(stoppingToken);
                    await sync.PushUnsyncedInvoicesAsync(stoppingToken);
                    await sync.PullRemoteInvoicesAsync(stoppingToken);
                    // CashSessions must sync after Invoices — a movement's InvoiceId/PaymentId FK may
                    // reference a sale that was just pushed/pulled earlier in this same pass.
                    await sync.PushUpdatedCashSessionsAsync(stoppingToken);
                    await sync.PullRemoteCashSessionsAsync(stoppingToken);
                    await sync.PushUpdatedAuditLogsAsync(stoppingToken);
                    await sync.PullRemoteAuditLogsAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Invoice sync pass failed.");
            }

            var intervalSeconds = Math.Max(5, _configuration.GetValue<int?>("Sync:IntervalSeconds") ?? 30);
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
        }
    }
}