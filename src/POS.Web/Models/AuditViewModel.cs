namespace POS.Web.Models;

public sealed class AuditViewModel
{
    public DateOnly? FromDate { get; init; }
    public DateOnly? ToDate { get; init; }
    public string? Action { get; init; }
    public string? EntityName { get; init; }
    public IReadOnlyList<AuditEntryRowViewModel> Entries { get; init; } = Array.Empty<AuditEntryRowViewModel>();
}

public sealed class AuditEntryRowViewModel
{
    public DateTime CreatedAtLocal { get; init; }
    public string? Username { get; init; }
    public string Action { get; init; } = string.Empty;
    public string EntityName { get; init; } = string.Empty;
    public Guid? EntityId { get; init; }
    public string? Details { get; init; }
}
