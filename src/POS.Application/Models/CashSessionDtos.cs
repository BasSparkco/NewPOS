using POS.Core.Enums;

namespace POS.Application.Models;

public sealed record CashSessionDto(
    Guid Id,
    Guid RegisterId,
    int RegisterNumber,
    string RegisterName,
    Guid OpenedByUserId,
    string OpenedByUsername,
    DateTime OpenedAt,
    decimal OpeningCashAmount,
    string CurrencyCode,
    CashSessionStatus Status,
    bool IsSharedSession,
    Guid? ClosedByUserId,
    DateTime? ClosedAt,
    decimal? ClosingCountedAmount,
    decimal? ExpectedCashAmount,
    decimal? DiscrepancyAmount);

public sealed record CashMovementDto(
    Guid Id,
    CashMovementType Type,
    decimal Amount,
    PaymentMethod Method,
    string CurrencyCode,
    Guid? InvoiceId,
    Guid PerformedByUserId,
    string PerformedByUsername,
    Guid? ApprovedByUserId,
    string? Notes,
    DateTime CreatedAt);

public sealed record CashSessionSummaryDto(
    CashSessionDto Session,
    IReadOnlyList<CashMovementDto> Movements,
    decimal ExpectedCashAmount,
    decimal TotalCashReceipts,
    decimal TotalCashRefunds,
    decimal TotalCashIn,
    decimal TotalCashOut,
    decimal TotalNonCashReceipts);
