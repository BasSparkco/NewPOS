using POS.Core.Enums;

namespace POS.Application.Models;

public sealed record StockMovementDto(
    Guid Id,
    Guid ProductId,
    string ProductName,
    StockMovementType Type,
    decimal QuantityDelta,
    decimal QuantityAfter,
    string? Reference,
    string? Notes,
    DateTime CreatedAt);