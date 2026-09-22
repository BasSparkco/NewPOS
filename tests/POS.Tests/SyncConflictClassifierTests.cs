using Microsoft.EntityFrameworkCore;
using POS.Api.Infrastructure;
using POS.Core.Entities;
using POS.Infrastructure.Data;
using Xunit;

namespace POS.Tests;

/// <summary>
/// T6 matrix requirement: a sync push endpoint's <c>DbUpdateException</c> handling must distinguish a
/// retryable concurrency/duplicate-operation conflict from a permanent database validation failure,
/// rather than reporting every <c>DbUpdateException</c> as a safe-to-retry 409. Exercises
/// <see cref="SyncConflictClassifier"/> against real exceptions thrown by a real SQLite database (not
/// hand-constructed fakes), so the provider-specific error-code matching is what's actually under test.
/// </summary>
public sealed class SyncConflictClassifierTests : IAsyncDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"pos-conflict-classifier-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task A_real_unique_constraint_violation_is_classified_as_retryable()
    {
        await using var db = await CreateMigratedContextAsync();
        var now = DateTime.UtcNow;
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "T", NormalizedSlug = "t-unique", CreatedAt = now, UpdatedAt = now });
        db.Roles.Add(new Role { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Admin", PermissionsMask = 0, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();

        // Same (TenantId, Name) as the row above — a real UNIQUE index violation, the exact shape two
        // concurrent "does this role exist yet?" pushes racing each other would produce.
        db.Roles.Add(new Role { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Admin", PermissionsMask = 0, CreatedAt = now, UpdatedAt = now });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        Assert.True(SyncConflictClassifier.IsRetryable(ex), $"Expected a unique-constraint violation to be retryable. Inner: {ex.InnerException}");
    }

    [Fact]
    public async Task A_real_foreign_key_violation_is_classified_as_permanent_not_retryable()
    {
        await using var db = await CreateMigratedContextAsync();

        // References a Tenant that was never inserted — a real FK violation, the shape a genuine data
        // problem produces (e.g. a referenced row deleted mid-request), never something a retry of the
        // identical payload can fix.
        db.Roles.Add(new Role { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), Name = "Orphan", PermissionsMask = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        Assert.False(SyncConflictClassifier.IsRetryable(ex), $"Expected a foreign-key violation to be classified as a permanent failure. Inner: {ex.InnerException}");
    }

    private async Task<PosDbContext> CreateMigratedContextAsync()
    {
        var options = new DbContextOptionsBuilder<PosDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        var db = new PosDbContext(options);
        await db.Database.MigrateAsync();
        return db;
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Delay(10);
        foreach (var suffix in new[] { "", "-shm", "-wal" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
            {
                try { File.Delete(path); } catch { /* best-effort cleanup */ }
            }
        }
    }
}
