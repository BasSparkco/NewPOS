using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using POS.Application.Abstractions;
using POS.Core.Entities;
using POS.Core.Enums;
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
        Guid tenantId,
        Guid storeId,
        Guid userId,
        Guid categoryId,
        Guid baseCurrencyId,
        Guid altCurrencyId)
    {
        _services = services;
        DatabasePath = databasePath;
        Session = session;
        TenantId = tenantId;
        StoreId = storeId;
        UserId = userId;
        CategoryId = categoryId;
        BaseCurrencyId = baseCurrencyId;
        AltCurrencyId = altCurrencyId;
    }

    public IServiceProvider Services => _services;
    public string DatabasePath { get; }
    public TestCurrentSession Session { get; }
    public Guid TenantId { get; }
    public Guid StoreId { get; }
    public Guid UserId { get; }
    public Guid CategoryId { get; }
    public Guid BaseCurrencyId { get; }
    public Guid AltCurrencyId { get; }

    public static async Task<TestServiceHost> CreateAsync(
        IDictionary<string, string?>? extraConfiguration = null,
        Action<IServiceCollection>? configureServices = null,
        bool seedBaseline = true)
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

        var tenantId = Guid.NewGuid();
        var storeId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var baseCurrencyId = Guid.NewGuid();
        var altCurrencyId = Guid.NewGuid();

        if (!seedBaseline)
        {
            // A genuinely fresh WPF install: real migrations (not EnsureCreated) already seed global
            // reference Currency rows — with the same fixed IDs a real remote server's migrations
            // produce — before any Tenant/Store/User exists. Mirror that exactly: real migrations,
            // no Tenant/Store/Role/User/Category rows, session left unauthenticated.
            await using var freshDb = await dbFactory.CreateDbContextAsync();
            await freshDb.Database.EnsureDeletedAsync();
            await freshDb.Database.MigrateAsync();

            return new TestServiceHost(provider, dbPath, session, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty);
        }

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var now = DateTime.UtcNow;
            var roleId = Guid.NewGuid();

            db.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = "Test Tenant",
                NormalizedSlug = "test-tenant",
                Status = TenantStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
                IsDeleted = false
            });

            db.Currencies.AddRange(
                new Currency
                {
                    Id = baseCurrencyId,
                    Code = "USD",
                    Name = "US Dollar",
                    Symbol = "$",
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
                    CreatedAt = now,
                    UpdatedAt = now,
                    IsDeleted = false
                });

            db.TenantCurrencyRates.AddRange(
                new TenantCurrencyRate
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    CurrencyId = baseCurrencyId,
                    ExchangeRate = 1m,
                    CreatedAt = now,
                    UpdatedAt = now,
                    IsDeleted = false
                },
                new TenantCurrencyRate
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    CurrencyId = altCurrencyId,
                    ExchangeRate = 0.9m,
                    CreatedAt = now,
                    UpdatedAt = now,
                    IsDeleted = false
                });

            db.Stores.Add(new Store
            {
                Id = storeId,
                TenantId = tenantId,
                Name = "Test Store",
                BaseCurrencyId = baseCurrencyId,
                CreatedAt = now,
                UpdatedAt = now,
                IsDeleted = false
            });

            db.Roles.Add(new Role
            {
                Id = roleId,
                TenantId = tenantId,
                Name = "Admin",
                PermissionsMask = (int)Permission.All,
                CreatedAt = now,
                UpdatedAt = now,
                IsDeleted = false
            });

            db.Users.Add(new User
            {
                Id = userId,
                TenantId = tenantId,
                Username = "admin",
                NormalizedUsername = "ADMIN",
                PasswordHash = "hash",
                RoleId = roleId,
                StoreId = storeId,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
                IsDeleted = false
            });

            db.UserStoreAccesses.Add(new UserStoreAccess
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UserId = userId,
                StoreId = storeId,
                CreatedAt = now,
                UpdatedAt = now,
                IsDeleted = false
            });

            db.Categories.Add(new Category
            {
                Id = categoryId,
                TenantId = tenantId,
                Name = "General",
                CreatedAt = now,
                UpdatedAt = now,
                IsDeleted = false
            });

            await db.SaveChangesAsync();
        }

        session.Set(tenantId, userId, storeId, "admin", "Admin", (int)Permission.All, "USD", "$");
        session.SetPassword("admin");

        return new TestServiceHost(provider, dbPath, session, tenantId, storeId, userId, categoryId, baseCurrencyId, altCurrencyId);
    }

    /// <summary>
    /// Opens (idempotently) a cash session on this host's single auto-provisioned register, with a
    /// generous opening float, so tests unrelated to cash-session behavior can still complete a cash
    /// sale — completing one now requires an open, authorized session (tenant.md §5b). Every
    /// <c>ISaleService.StartNewSaleAsync</c> call within the same host resolves to the same Device/
    /// Register (keyed by <see cref="TestCurrentDevice"/>'s fixed name), so a throwaway invoice reliably
    /// discovers/creates it without needing any register id from the caller.
    /// </summary>
    public async Task<Guid> OpenDefaultCashSessionAsync(decimal openingAmount = 1000m)
    {
        await using var scope = _services.CreateAsyncScope();
        var sales = scope.ServiceProvider.GetRequiredService<ISaleService>();
        var cashSessions = scope.ServiceProvider.GetRequiredService<ICashSessionService>();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PosDbContext>>();

        var invoiceId = await sales.StartNewSaleAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var registerId = await db.Invoices.AsNoTracking().Where(i => i.Id == invoiceId).Select(i => i.RegisterId).SingleAsync()
            ?? throw new InvalidOperationException("Test setup: new invoice has no register.");
        await sales.CancelInvoiceAsync(invoiceId);

        var existing = await cashSessions.GetActiveSessionAsync(registerId);
        if (existing is not null)
            return existing.Id;

        var (success, error, session) = await cashSessions.OpenSessionAsync(registerId, openingAmount);
        if (!success)
            throw new InvalidOperationException($"Test setup could not open a cash session: {error}");

        return session!.Id;
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
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid StoreId { get; private set; }
    public string Username { get; private set; } = string.Empty;
    public string RoleName { get; private set; } = string.Empty;
    public int PermissionsMask { get; private set; }
    public string BaseCurrencyCode { get; private set; } = "USD";
    public string? CurrencySymbol { get; private set; }
    public bool IsAuthenticated => UserId != Guid.Empty && StoreId != Guid.Empty;
    public bool HasSyncScope => StoreId != Guid.Empty;
    public string? Password { get; private set; }
    public DateTime? LastOnlineContactUtc { get; private set; }

    public void SetDeviceSyncScope(Guid tenantId, Guid storeId)
    {
        TenantId = tenantId;
        StoreId = storeId;
    }

    public void Set(Guid tenantId, Guid userId, Guid storeId, string username, string roleName, int permissionsMask, string baseCurrencyCode, string? currencySymbol)
    {
        TenantId = tenantId;
        UserId = userId;
        StoreId = storeId;
        Username = username;
        RoleName = roleName;
        PermissionsMask = permissionsMask;
        BaseCurrencyCode = baseCurrencyCode;
        CurrencySymbol = currencySymbol;
    }

    public void SetPassword(string? password) => Password = password;

    public void SetLastOnlineContactUtc(DateTime? lastOnlineContactUtc) => LastOnlineContactUtc = lastOnlineContactUtc;

    public void Clear()
    {
        TenantId = Guid.Empty;
        UserId = Guid.Empty;
        StoreId = Guid.Empty;
        Username = string.Empty;
        RoleName = string.Empty;
        PermissionsMask = 0;
        BaseCurrencyCode = "USD";
        CurrencySymbol = null;
        Password = null;
        LastOnlineContactUtc = null;
    }
}

internal sealed class TestCurrentDevice(string name) : ICurrentDevice
{
    public string Name { get; } = name;
}