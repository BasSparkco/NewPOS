using POS.Application.Models;

namespace POS.Application.Abstractions;

public interface ISaleService
{
    Task<Guid> StartNewSaleAsync(CancellationToken cancellationToken = default);
    Task AddOrMergeLineAsync(Guid invoiceId, Guid productId, decimal quantity, CancellationToken cancellationToken = default);
    Task RemoveLineAsync(Guid invoiceId, Guid lineId, CancellationToken cancellationToken = default);
    Task<decimal> GetInvoiceTotalAsync(Guid invoiceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CartLineDto>> GetCartLinesAsync(Guid invoiceId, CancellationToken cancellationToken = default);
    Task CancelInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default);
    Task HoldInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default);
    Task ResumeInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default);
    /// <summary>Cancels a Paid invoice and returns all line quantities back to inventory.</summary>
    Task<(bool Success, string? Error)> RefundInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default);
    Task SetLineQuantityAsync(Guid invoiceId, Guid lineId, decimal quantity, CancellationToken cancellationToken = default);
    /// <summary>Applies a percentage discount (0–100) to a single line item.</summary>
    Task SetLineDiscountAsync(Guid invoiceId, Guid lineId, decimal discountPercent, CancellationToken cancellationToken = default);
    /// <summary>Sets the invoice-level tax rate (e.g. 17 for 17% VAT).</summary>
    Task SetInvoiceTaxAsync(Guid invoiceId, decimal taxPercent, CancellationToken cancellationToken = default);
    /// <summary>Sets a free-text note on the invoice, printed on the receipt.</summary>
    Task SetInvoiceNoteAsync(Guid invoiceId, string? note, CancellationToken cancellationToken = default);
    /// <summary>Returns the full totals breakdown (subtotal, tax, total) for display.</summary>
    Task<InvoiceSummaryDto> GetInvoiceSummaryAsync(Guid invoiceId, CancellationToken cancellationToken = default);
    Task<SaleCompletionResult> CompleteCashSaleAsync(Guid invoiceId, decimal cashTendered, CancellationToken cancellationToken = default);
    /// <summary>Adds a one-off, non-catalog line (e.g. an item the cashier priced manually) to the invoice.</summary>
    Task AddCustomItemAsync(Guid invoiceId, string name, decimal quantity, decimal unitPrice, CancellationToken cancellationToken = default);
}

public sealed record SaleCompletionResult(bool Success, string? ErrorMessage, ReceiptDto? Receipt);
