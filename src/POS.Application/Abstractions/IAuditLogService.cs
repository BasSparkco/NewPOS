using POS.Application.Models;

namespace POS.Application.Abstractions;

public interface IAuditLogService
{
    Task WriteAsync(
        string action,
        string entityName,
        Guid? entityId = null,
        string? details = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditLogDto>> GetRecentAsync(
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        string? action = null,
        string? entityName = null,
        int take = 200,
        CancellationToken cancellationToken = default);
}