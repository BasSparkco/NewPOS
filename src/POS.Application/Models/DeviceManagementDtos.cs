namespace POS.Application.Models;

public sealed record DeviceListItemDto(
    Guid Id,
    string Name,
    bool IsEnrolled,
    bool IsRevoked,
    DateTime? EnrolledAt,
    DateTime CreatedAt,
    bool IsCurrentMachine,
    Guid? RegisterId,
    int? RegisterNumber);

/// <summary>A logical till/register — stable across the physical machine bound to it being replaced.</summary>
public sealed record RegisterListItemDto(
    Guid Id,
    int Number,
    string Name,
    bool IsActive,
    Guid? CurrentDeviceId,
    string? CurrentDeviceName);
