using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using POS.Application.Abstractions;
using POS.Infrastructure.Data;
using POS.Infrastructure.Services;

namespace POS.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers EF Core. SQLite relative paths are resolved under <paramref name="applicationBasePath"/> (use the app folder so
    /// <c>pos.db</c> is always next to the executable, regardless of the process current directory when using <c>dotnet run</c>).
    /// </summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        string? applicationBasePath = null)
    {
        services.AddHttpClient();

        var provider = configuration["Database:Provider"]?.Trim();
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Connection string 'Default' is not configured.");

        services.AddDbContextFactory<PosDbContext>(options =>
            ConfigurePosDbContext(options, provider, connectionString, applicationBasePath ?? AppContext.BaseDirectory));

        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IProductCatalogService, ProductCatalogService>();
        services.AddScoped<ISaleService, SaleService>();
        services.AddScoped<ICurrencyService, CurrencyService>();
        services.AddScoped<ISettingsService, SettingsService>();
        services.AddScoped<IAuditLogService, AuditLogService>();
        services.AddScoped<IUserManagementService, UserManagementService>();
        services.AddScoped<ITenantProvisioningService, TenantProvisioningService>();
        services.AddScoped<IDeviceManagementService, DeviceManagementService>();
        services.AddScoped<IInvoiceSyncService, InvoiceSyncService>();
        services.AddScoped<IOfflineAuthorizationPolicy, OfflineAuthorizationPolicy>();
        services.AddScoped<IBusinessJoinService, BusinessJoinService>();

        return services;
    }

    public static void ConfigurePosDbContext(
        DbContextOptionsBuilder options,
        string? configuredProvider,
        string connectionString,
        string? applicationBasePath = null)
    {
        if (IsPostgres(configuredProvider, connectionString))
        {
            options.UseNpgsql(connectionString);
            return;
        }

        var basePath = applicationBasePath ?? AppContext.BaseDirectory;
        var sqliteConnectionString = ResolveSqliteDataSource(connectionString, basePath);
        options.UseSqlite(sqliteConnectionString);
    }

    private static string ResolveSqliteDataSource(string connectionString, string basePath)
    {
        const string prefix = "Data Source=";
        if (!connectionString.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return connectionString;

        var pathPart = connectionString[prefix.Length..].Trim();
        if (pathPart.Length == 0)
            return connectionString;

        if (pathPart.StartsWith(':') || pathPart.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
            return connectionString;

        if (Path.IsPathRooted(pathPart))
            return connectionString;

        var fullPath = Path.GetFullPath(Path.Combine(basePath, pathPart));
        return $"{prefix}{fullPath}";
    }

    private static bool IsPostgres(string? configuredProvider, string connectionString)
    {
        if (!string.IsNullOrWhiteSpace(configuredProvider))
            return configuredProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase)
                || configuredProvider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase)
                || configuredProvider.Equals("Npgsql", StringComparison.OrdinalIgnoreCase);

        return connectionString.Contains("Host=", StringComparison.OrdinalIgnoreCase)
            || connectionString.Contains("Username=", StringComparison.OrdinalIgnoreCase)
            || connectionString.Contains("Port=", StringComparison.OrdinalIgnoreCase);
    }
}
