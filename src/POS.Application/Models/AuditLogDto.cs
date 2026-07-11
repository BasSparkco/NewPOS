namespace POS.Application.Models;

public sealed record AuditLogDto(
    Guid Id,
    Guid StoreId,
    Guid? UserId,
    string? Username,
    string Action,
    string EntityName,
    Guid? EntityId,
    string? Details,
    DateTime CreatedAt);