using Microsoft.EntityFrameworkCore;
using POS.Application.Abstractions;
using POS.Application.Models;
using POS.Core.Entities;
using POS.Infrastructure.Data;

namespace POS.Infrastructure.Services;

internal sealed class AuditLogService : IAuditLogService
{
    private const int ActionMaxLength = 100;
    private const int EntityNameMaxLength = 100;
    private const int DetailsMaxLength = 4000;

    private readonly IDbContextFactory<PosDbContext> _dbFactory;
    private readonly ICurrentSession _session;

    public AuditLogService(IDbContextFactory<PosDbContext> dbFactory, ICurrentSession session)
    {
        _dbFactory = dbFactory;
        _session = session;
    }

    public async Task WriteAsync(
        string action,
        string entityName,
        Guid? entityId = null,
        string? details = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(action))
            throw new ArgumentException("Action is required.", nameof(action));

        if (string.IsNullOrWhiteSpace(entityName))
            throw new ArgumentException("Entity name is required.", nameof(entityName));

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;

        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            StoreId = _session.StoreId,
            UserId = _session.UserId == Guid.Empty ? null : _session.UserId,
            Action = Truncate(action.Trim(), ActionMaxLength),
            EntityName = Truncate(entityName.Trim(), EntityNameMaxLength),
            EntityId = entityId,
            Details = string.IsNullOrWhiteSpace(details) ? null : Truncate(details.Trim(), DetailsMaxLength),
            CreatedAt = now,
            UpdatedAt = now,
            IsDeleted = false
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuditLogDto>> GetRecentAsync(
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        string? action = null,
        string? entityName = null,
        int take = 200,
        CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, 1000);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var query = db.AuditLogs
            .AsNoTracking()
            .Where(x => x.StoreId == _session.StoreId && !x.IsDeleted);

        if (fromUtc is not null)
            query = query.Where(x => x.CreatedAt >= fromUtc.Value);

        if (toUtc is not null)
            query = query.Where(x => x.CreatedAt <= toUtc.Value);

        if (!string.IsNullOrWhiteSpace(action))
        {
            var actionTerm = action.Trim();
            query = query.Where(x => x.Action == actionTerm);
        }

        if (!string.IsNullOrWhiteSpace(entityName))
        {
            var entityTerm = entityName.Trim();
            query = query.Where(x => x.EntityName == entityTerm);
        }

        return await query
            .OrderByDescending(x => x.CreatedAt)
            .Take(take)
            .Select(x => new AuditLogDto(
                x.Id,
                x.StoreId,
                x.UserId,
                x.User != null ? x.User.Username : null,
                x.Action,
                x.EntityName,
                x.EntityId,
                x.Details,
                x.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}