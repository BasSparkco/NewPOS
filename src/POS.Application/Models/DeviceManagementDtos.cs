namespace POS.Application.Models;

public sealed record DeviceListItemDto(
    Guid Id,
    string Name,
    bool IsEnrolled,
    bool IsRevoked,
    DateTime? EnrolledAt,
    DateTime CreatedAt,
    bool IsCurrentMachine);
