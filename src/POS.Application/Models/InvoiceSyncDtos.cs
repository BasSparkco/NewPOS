using POS.Core.Enums;

namespace POS.Application.Models;

public sealed record InvoiceSyncRunResultDto(
    int Attempted,
    int Synced,
    int Conflicts,
    int Failed);

public sealed record CategorySyncPullResultDto(
    IReadOnlyList<CategorySyncDto> Categories,
    string NextSinceVersion);

public sealed record CategorySyncPullRunResultDto(
    int Received,
    int Applied,
    int Skipped,
    int Failed,
    string NextSinceVersion);

public sealed record InvoiceSyncBatchDto(
    IReadOnlyList<InvoiceSyncInvoiceDto> Invoices);

public sealed record InvoiceSyncPushResultDto(
    IReadOnlyList<InvoiceSyncInvoiceResultDto> Results);

public sealed record InvoiceSyncPullResultDto(
    IReadOnlyList<InvoiceSyncInvoiceDto> Invoices,
    string NextSinceVersion);

public sealed record InvoiceSyncPullRunResultDto(
    int Received,
    int Applied,
    int Skipped,
    int Failed,
    string NextSinceVersion);

public sealed record ProductSyncPullResultDto(
    IReadOnlyList<ProductSyncDto> Products,
    string NextSinceVersion);

public sealed record ProductSyncPullRunResultDto(
    int Received,
    int Applied,
    int Skipped,
    int Failed,
    string NextSinceVersion);

public sealed record SettingsSyncPullResultDto(
    IReadOnlyList<SettingSyncDto> Settings,
    string NextSinceVersion);

public sealed record SettingsSyncPullRunResultDto(
    int Received,
    int Applied,
    int Skipped,
    int Failed,
    string NextSinceVersion);

public sealed record UserSyncDto(
    Guid UserId,
    string Username,
    string PasswordHash,
    string RoleName,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool IsDeleted,
    int RolePermissionsMask = 0,
    DateTime RoleUpdatedAt = default);

public sealed record UserSyncPullResultDto(
    IReadOnlyList<UserSyncDto> Users,
    string NextSinceVersion);

public sealed record UserSyncPullRunResultDto(
    int Received,
    int Applied,
    int Skipped,
    int Failed,
    string NextSinceVersion);

public sealed record AuditLogSyncDto(
    Guid Id,
    string? Username,
    string Action,
    string EntityName,
    Guid? EntityId,
    string? Details,
    DateTime CreatedAt);

public sealed record AuditLogSyncPullResultDto(
    IReadOnlyList<AuditLogSyncDto> AuditLogs,
    string NextSinceVersion);

public sealed record AuditLogSyncBatchDto(
    IReadOnlyList<AuditLogSyncDto> AuditLogs);

public sealed record AuditLogSyncPushResultDto(
    IReadOnlyList<AuditLogSyncItemResultDto> Results);

public sealed record AuditLogSyncPullRunResultDto(
    int Received,
    int Applied,
    int Skipped,
    int Failed,
    string NextSinceVersion);

public sealed record AuditLogSyncPushRunResultDto(
    int Attempted,
    int Sent,
    int Skipped,
    int Failed,
    string NextSinceVersion);

public sealed record DeviceSyncDto(
    Guid DeviceId,
    string Name,
    int SyncVersion,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool IsDeleted,
    string? EnrollmentCodeHash = null,
    DateTime? EnrolledAt = null,
    bool IsRevoked = false,
    DateTime? RevokedAt = null);

public sealed record DeviceSyncPullResultDto(
    IReadOnlyList<DeviceSyncDto> Devices,
    string NextSinceVersion);

public sealed record DeviceSyncPullRunResultDto(
    int Received,
    int Applied,
    int Skipped,
    int Failed,
    string NextSinceVersion);

public sealed record CategorySyncBatchDto(
    IReadOnlyList<CategorySyncDto> Categories);

public sealed record ProductSyncBatchDto(
    IReadOnlyList<ProductSyncDto> Products);

public sealed record DeviceSyncBatchDto(
    IReadOnlyList<DeviceSyncDto> Devices);

public sealed record UserSyncBatchDto(
    IReadOnlyList<UserSyncDto> Users);

public sealed record ProductSyncPushResultDto(
    IReadOnlyList<ProductSyncItemResultDto> Results);

public sealed record DeviceSyncPushResultDto(
    IReadOnlyList<DeviceSyncItemResultDto> Results);

public sealed record CategorySyncPushResultDto(
    IReadOnlyList<CategorySyncItemResultDto> Results);

public sealed record UserSyncPushResultDto(
    IReadOnlyList<UserSyncItemResultDto> Results);

public sealed record SettingsSyncBatchDto(
    IReadOnlyList<SettingSyncDto> Settings);

public sealed record SettingsSyncPushResultDto(
    IReadOnlyList<SettingSyncItemResultDto> Results);

public sealed record InvoiceSyncInvoiceResultDto(
    Guid InvoiceId,
    string Status,
    int ServerSyncVersion,
    string? ErrorMessage);

public sealed record ProductSyncPushRunResultDto(
    int Attempted,
    int Sent,
    int Skipped,
    int Failed,
    string NextSinceVersion);

public sealed record DeviceSyncPushRunResultDto(
    int Attempted,
    int Sent,
    int Skipped,
    int Failed,
    string NextSinceVersion);

public sealed record CategorySyncPushRunResultDto(
    int Attempted,
    int Sent,
    int Skipped,
    int Failed,
    string NextSinceVersion);

public sealed record InvoiceSyncInvoiceDto(
    Guid InvoiceId,
    Guid? DeviceId,
    string? DeviceName,
    int SyncVersion,
    InvoiceStatus Status,
    decimal TotalAmount,
    decimal TaxPercent,
    string Currency,
    string? Notes,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<InvoiceSyncLineDto> Lines,
    IReadOnlyList<InvoiceSyncPaymentDto> Payments,
    string? Username);

public sealed record SettingsSyncPushRunResultDto(
    int Attempted,
    int Sent,
    int Skipped,
    int Failed,
    string NextSinceVersion);

public sealed record UserSyncPushRunResultDto(
    int Attempted,
    int Sent,
    int Skipped,
    int Failed,
    string NextSinceVersion);

public sealed record CurrencyPolicySyncDto(
    Guid StoreId,
    Guid BaseCurrencyId,
    DateTime UpdatedAt,
    IReadOnlyList<CurrencyDto> Currencies);

public sealed record CurrencyPolicySyncPullResultDto(
    CurrencyPolicySyncDto? Policy,
    string NextSinceVersion);

public sealed record CurrencyPolicySyncPushResultDto(
    string Status,
    DateTime ServerUpdatedAt,
    string? ErrorMessage);

public sealed record CurrencyPolicySyncPullRunResultDto(
    int Received,
    int Applied,
    int Skipped,
    int Failed,
    string NextSinceVersion);

public sealed record CurrencyPolicySyncPushRunResultDto(
    int Attempted,
    int Sent,
    int Skipped,
    int Failed,
    string NextSinceVersion);

public sealed record InvoiceSyncLineDto(
    Guid LineId,
    Guid ProductId,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountPercent,
    decimal LineTotal,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool IsDeleted);

public sealed record ProductSyncItemResultDto(
    Guid ProductId,
    string Status,
    DateTime ServerUpdatedAt,
    string? ErrorMessage);

public sealed record DeviceSyncItemResultDto(
    Guid DeviceId,
    string Status,
    DateTime ServerUpdatedAt,
    string? ErrorMessage);

public sealed record CategorySyncItemResultDto(
    Guid CategoryId,
    string Status,
    DateTime ServerUpdatedAt,
    string? ErrorMessage);

public sealed record AuditLogSyncItemResultDto(
    Guid AuditLogId,
    string Status,
    DateTime ServerCreatedAt,
    string? ErrorMessage);

public sealed record SettingSyncItemResultDto(
    string Key,
    string Status,
    DateTime ServerUpdatedAt,
    string? ErrorMessage);

public sealed record UserSyncItemResultDto(
    Guid UserId,
    string Status,
    DateTime ServerUpdatedAt,
    string? ErrorMessage);

public sealed record InvoiceSyncPaymentDto(
    Guid PaymentId,
    decimal Amount,
    PaymentMethod Method,
    DateTime PaidAt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool IsDeleted);

public sealed record ProductSyncDto(
    Guid ProductId,
    string Name,
    string? Barcode,
    decimal Price,
    decimal Cost,
    Guid CategoryId,
    string CategoryName,
    decimal QuantityOnHand,
    bool IsActive,
    string? ImagePath,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool IsDeleted);

public sealed record CategorySyncDto(
    Guid CategoryId,
    string Name,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool IsDeleted);

public sealed record SettingSyncDto(
    string Key,
    string Value,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool IsDeleted);

public sealed record RegisterSyncDto(
    Guid RegisterId,
    int Number,
    string Name,
    bool IsActive,
    int SyncVersion,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool IsDeleted);

public sealed record RegisterSyncBatchDto(IReadOnlyList<RegisterSyncDto> Registers);

public sealed record RegisterSyncItemResultDto(Guid RegisterId, string Status, DateTime ServerUpdatedAt, string? ErrorMessage);

public sealed record RegisterSyncPushResultDto(IReadOnlyList<RegisterSyncItemResultDto> Results);

public sealed record RegisterSyncPullResultDto(IReadOnlyList<RegisterSyncDto> Registers, string NextSinceVersion);

public sealed record RegisterSyncPushRunResultDto(int Attempted, int Sent, int Skipped, int Failed, string NextSinceVersion);

public sealed record RegisterSyncPullRunResultDto(int Received, int Applied, int Skipped, int Failed, string NextSinceVersion);

/// <summary>
/// One ledger entry nested under a <see cref="CashSessionSyncDto"/> aggregate. Movements are append-only
/// (never edited/deleted after creation), so — unlike invoice lines/payments — reconciling them on apply
/// only ever needs to insert whichever incoming movements aren't already present locally by
/// <see cref="MovementId"/>; there is no update/remove case to handle.
/// </summary>
public sealed record CashMovementSyncDto(
    Guid MovementId,
    CashMovementType Type,
    decimal Amount,
    PaymentMethod Method,
    string CurrencyCode,
    Guid? InvoiceId,
    Guid? PaymentId,
    /// <summary>The originating device's own local id for the performer, sent alongside the username so a
    /// later username change can never strand this row: the apply side verifies the id resolves to a real,
    /// active, tenant/store-scoped user before trusting it (never accepted on its own — an id that doesn't
    /// resolve locally falls back to the username, and a genuinely unresolvable actor still skips the
    /// movement rather than misattributing it), then falls back to the username. Neither field alone is
    /// treated as proof of identity or authorization.</summary>
    Guid PerformedByUserId,
    string PerformedByUsername,
    Guid? ApprovedByUserId,
    string? ApprovedByUsername,
    string? Notes,
    DateTime CreatedAt);

/// <summary>
/// The full CashSession aggregate — header fields plus every movement recorded against it — synced as
/// one atomic unit, the same way an Invoice carries its Items/Payments. SyncVersion-based conflict
/// precedence (like Invoice, not the UpdatedAt-based rule used for simpler aggregates) so a Closed
/// session's history can never be overwritten by a stale Open snapshot from another node.
/// </summary>
public sealed record CashSessionSyncDto(
    Guid CashSessionId,
    Guid RegisterId,
    int SyncVersion,
    /// <summary>See <see cref="CashMovementSyncDto.PerformedByUserId"/> for why both an id hint and a
    /// username are carried — an id-only lookup preserves the actor's identity across a later username
    /// change, but is only trusted once verified against a real, active, tenant/store-scoped user.</summary>
    Guid OpenedByUserId,
    string OpenedByUsername,
    DateTime OpenedAt,
    decimal OpeningCashAmount,
    string CurrencyCode,
    CashSessionStatus Status,
    bool IsSharedSession,
    Guid? ClosedByUserId,
    string? ClosedByUsername,
    DateTime? ClosedAt,
    decimal? ClosingCountedAmount,
    decimal? ExpectedCashAmount,
    decimal? DiscrepancyAmount,
    string? Notes,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool IsDeleted,
    IReadOnlyList<CashMovementSyncDto> Movements);

public sealed record CashSessionSyncBatchDto(IReadOnlyList<CashSessionSyncDto> Sessions);

public sealed record CashSessionSyncItemResultDto(Guid CashSessionId, string Status, int ServerSyncVersion, string? ErrorMessage);

public sealed record CashSessionSyncPushResultDto(IReadOnlyList<CashSessionSyncItemResultDto> Results);

public sealed record CashSessionSyncPullResultDto(IReadOnlyList<CashSessionSyncDto> Sessions, string NextSinceVersion);

public sealed record CashSessionSyncPushRunResultDto(int Attempted, int Sent, int Skipped, int Failed, string NextSinceVersion);

public sealed record CashSessionSyncPullRunResultDto(int Received, int Applied, int Skipped, int Failed, string NextSinceVersion);