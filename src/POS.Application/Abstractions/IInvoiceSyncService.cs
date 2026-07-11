using POS.Application.Models;

namespace POS.Application.Abstractions;

public interface IInvoiceSyncService
{
    Task<InvoiceSyncRunResultDto> PushUnsyncedInvoicesAsync(CancellationToken cancellationToken = default);
    Task<AuditLogSyncPushRunResultDto> PushUpdatedAuditLogsAsync(CancellationToken cancellationToken = default);
    Task<CategorySyncPushRunResultDto> PushUpdatedCategoriesAsync(CancellationToken cancellationToken = default);
    Task<CurrencyPolicySyncPushRunResultDto> PushCurrencyPolicyAsync(CancellationToken cancellationToken = default);
    Task<DeviceSyncPushRunResultDto> PushUpdatedDevicesAsync(CancellationToken cancellationToken = default);
    Task<ProductSyncPushRunResultDto> PushUpdatedProductsAsync(CancellationToken cancellationToken = default);
    Task<SettingsSyncPushRunResultDto> PushUpdatedSettingsAsync(CancellationToken cancellationToken = default);
    Task<UserSyncPushRunResultDto> PushUpdatedUsersAsync(CancellationToken cancellationToken = default);
    Task<CategorySyncPullRunResultDto> PullRemoteCategoriesAsync(CancellationToken cancellationToken = default);
    Task<CurrencyPolicySyncPullRunResultDto> PullCurrencyPolicyAsync(CancellationToken cancellationToken = default);
    Task<AuditLogSyncPullRunResultDto> PullRemoteAuditLogsAsync(CancellationToken cancellationToken = default);
    Task<DeviceSyncPullRunResultDto> PullRemoteDevicesAsync(CancellationToken cancellationToken = default);
    Task<InvoiceSyncPullRunResultDto> PullRemoteInvoicesAsync(CancellationToken cancellationToken = default);
    Task<ProductSyncPullRunResultDto> PullRemoteProductsAsync(CancellationToken cancellationToken = default);
    Task<SettingsSyncPullRunResultDto> PullRemoteSettingsAsync(CancellationToken cancellationToken = default);
    Task<UserSyncPullRunResultDto> PullRemoteUsersAsync(CancellationToken cancellationToken = default);
}