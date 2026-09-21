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