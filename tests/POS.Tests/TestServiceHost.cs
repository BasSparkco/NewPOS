using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using POS.Application.Abstractions;
using POS.Core.Entities;
using POS.Infrastructure;
using POS.Infrastructure.Data;

namespace POS.Tests;

internal sealed class TestServiceHost : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private TestServiceHost(
        ServiceProvider services,
        string databasePath,
        TestCurrentSession session,
        Guid storeId,
        Guid userId,
        Guid categoryId,
        Guid baseCurrencyId,
        Guid altCurrencyId)
    {
        _services = services;
        DatabasePath = databasePath;
        Session = session;
        StoreId = storeId;
        UserId = userId;
        CategoryId = categoryId;
        BaseCurrencyId = baseCurrencyId;
        AltCurrencyId = altCurrencyId;
    }

    public IServiceProvider Services => _services;
    public string DatabasePath { get; }
    public TestCurrentSession Session { get; }
    public Guid StoreId { get; }
    public Guid UserId { get; }
    public Guid CategoryId { get; }
    public Guid BaseCurrencyId { get; }
    public Guid AltCurrencyId { get; }

    public static async Task<TestServiceHost> CreateAsync(
        IDictionary<string, string?>? extraConfiguration = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"pos-tests-{Guid.NewGuid():N}.db");
        var session = new TestCurrentSession();

        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = $"Data Source={dbPath}"
        };

        if (extraConfiguration is not null)
        {
            foreach (var pair in extraConfiguration)
                settings[pair.Key] = pair.Value;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<ICurrentDevice>(new TestCurrentDevice("POS.Tests"));
        services.AddSingleton<ICurrentSession>(session);
        services.AddInfrastructure(configuration, Path.GetDirectoryName(dbPath));
        configureServices?.Invoke(services);

        var provider = services.BuildServiceProvider();
        var dbFactory = provider.GetRequiredService<IDbContextFactory<PosDbContext>>();

        var storeId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var baseCurrencyId = Guid.NewGuid();
        var altCurrencyId = Guid.NewGuid();

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var now = DateTime.UtcNow;
            var roleId = Guid.NewGuid();

            db.Currencies.AddRange(
                new Currency
                {
                    Id = baseCurrencyId,
                    Code = "USD",
                    Name = "US Dollar",
                    Symbol = "$",
                    ExchangeRate = 1m,
                    CreatedAt = now,
                    UpdatedAt = now,
                    IsDeleted = false
                },
                new Currency
                {
                    Id = altCurrencyId,
                    Code = "EUR",
                    Name = "Euro",
                    Symbol = "EUR",
                    ExchangeRate = 0.9m,
                    CreatedAt = now,
                    UpdatedAt = now,
                    IsDeleted = false
                });

            db.Stores.Add(new Store
            {
                Id = storeId,
                Name = "Test Store",
                BaseCurrencyId = baseCurrencyId,
                CreatedAt = now,
                UpdatedAt = now,
                IsDeleted = false
            });

            db.Roles.Add(new Role
            {
                Id = roleId,
                Name = "Admin",
                CreatedAt = now,
                UpdatedAt = now,
                IsDeleted = false
            });

            db.Users.Add(new User
            {
                Id = userId,
                Username = "admin",
                PasswordHash = "hash",
                RoleId = roleId,
                StoreId = storeId,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
                IsDeleted = false
            });

            db.Categories.Add(new Category
            {
                Id = categoryId,
                Name = "General",
                CreatedAt = now,
                UpdatedAt = now,
                IsDeleted = false
            });

            await db.SaveChangesAsync();
        }

        session.Set(userId, storeId, "admin", "Admin", "USD", "$");

        return new TestServiceHost(provider, dbPath, session, storeId, userId, categoryId, baseCurrencyId, altCurrencyId);
    }

    public async Task<T> ExecuteScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    public async Task ExecuteScopeAsync(Func<IServiceProvider, Task> action)
    {
        await using var scope = _services.CreateAsyncScope();
        await action(scope.ServiceProvider);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        try
        {
            if (File.Exists(DatabasePath))
                File.Delete(DatabasePath);
        }
        catch
        {
            // Best-effort cleanup for temp SQLite files.
        }
    }
}

internal sealed class TestCurrentSession : ICurrentSession
{
    public Guid UserId { get; private set; }
    public Guid StoreId { get; private set; }
    public string Username { get; private set; } = string.Empty;
    public string RoleName { get; private set; } = string.Empty;
    public string BaseCurrencyCode { get; private set; } = "USD";
    public string? CurrencySymbol { get; private set; }
    public bool IsAuthenticated => UserId != Guid.Empty && StoreId != Guid.Empty;

    public void Set(Guid userId, Guid storeId, string username, string roleName, string baseCurrencyCode, string? currencySymbol)
    {
        UserId = userId;
        StoreId = storeId;
        Username = username;
        RoleName = roleName;
        BaseCurrencyCode = baseCurrencyCode;
        CurrencySymbol = currencySymbol;
    }

    public void Clear()
    {
        UserId = Guid.Empty;
        StoreId = Guid.Empty;
        Username = string.Empty;
        RoleName = string.Empty;
        BaseCurrencyCode = "USD";
        CurrencySymbol = null;
    }
}

internal sealed class TestCurrentDevice(string name) : ICurrentDevice
{
    public string Name { get; } = name;
}