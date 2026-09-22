using POS.Application.Models;

namespace POS.Application.Abstractions;

public interface IInvoiceSyncService
{
    /// <summary>
    /// Restores a store scope for background sync from the local device/store when no interactive user
    /// is signed in (see <see cref="ICurrentSession.SetDeviceSyncScope"/>). Call once at the start of a
    /// sync pass, before any push/pull method, so device-credential sync keeps running after logout.
    /// </summary>
    Task EnsureSyncScopeAsync(CancellationToken cancellationToken = default);
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

    /// <summary>Pushes newly-provisioned/renamed Registers. Must run before <see cref="PushUpdatedCashSessionsAsync"/> — a CashSession's RegisterId is a required FK on the server.</summary>
    Task<RegisterSyncPushRunResultDto> PushUpdatedRegistersAsync(CancellationToken cancellationToken = default);
    Task<RegisterSyncPullRunResultDto> PullRemoteRegistersAsync(CancellationToken cancellationToken = default);

    /// <summary>Pushes CashSession aggregates (header + every movement) that changed locally. Must run after <see cref="PushUnsyncedInvoicesAsync"/> and <see cref="PushUpdatedRegistersAsync"/> — a movement's InvoiceId/PaymentId and a session's RegisterId are FKs the server must already have.</summary>
    Task<CashSessionSyncPushRunResultDto> PushUpdatedCashSessionsAsync(CancellationToken cancellationToken = default);
    Task<CashSessionSyncPullRunResultDto> PullRemoteCashSessionsAsync(CancellationToken cancellationToken = default);
}