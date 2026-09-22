namespace POS.Application.Models;

public sealed record StoreListItemDto(
    Guid Id,
    string Name,
    string? Address,
    string? Phone,
    string BaseCurrencyCode,
    bool IsActiveSelection);

public sealed record StoreUserAccessRowDto(
    Guid UserId,
    string Username,
    bool HasAccess,
    bool IsHomeStore);
