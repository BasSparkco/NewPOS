using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using POS.Infrastructure.Data;

namespace POS.Infrastructure;

public static class ServiceProviderExtensions
{
    public static void ApplyPosDatabaseMigrations(this IServiceProvider services, bool seedDemoData = true)
    {
        using var scope = services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();
        using var db = factory.CreateDbContext();
        db.Database.Migrate();
        RegisterBackfill.EnsureBackfilled(db);

        if (seedDemoData)
            DatabaseSeeder.SeedIfNeeded(db);
    }
}
