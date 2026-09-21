using Microsoft.EntityFrameworkCore;
using POS.Core.Entities;
using POS.Core.Enums;

namespace POS.Infrastructure.Data;

public static class DatabaseSeeder
{
    // Randomly generated once for this codebase's demo/dev seed data — not a real per-install secret.
    // Recorded in start.md. Change both places together if you rotate them.
    public const string DemoAdminPassword = "TQteaiwQUF!tGDGx";
    public const string DemoCashierPassword = "qMMWfqwXqeE69h87";

    public static void SeedIfNeeded(PosDbContext db)
    {
        NormalizeBrokenDemoPasswordHashes(db);

        var now = DateTime.UtcNow;

        var tenant = db.Tenants.FirstOrDefault();
        if (tenant is null)
        {
            tenant = new Tenant
            {
                Id             = Guid.NewGuid(),
                Name           = "Default Business",
                NormalizedSlug = "default",
                Status         = TenantStatus.Active,
                CreatedAt      = now,
                UpdatedAt      = now
            };
            db.Tenants.Add(tenant);
            db.SaveChanges();
        }
        var tenantId = tenant.Id;

        if (!db.Roles.Any(r => r.TenantId == tenantId))
        {
            var newAdminRole = new Role { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Admin", PermissionsMask = (int)Permission.All, CreatedAt = now, UpdatedAt = now };
            var newCashierRole = new Role { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Cashier", PermissionsMask = (int)Permission.None, CreatedAt = now, UpdatedAt = now };
            db.Roles.AddRange(newAdminRole, newCashierRole);
            db.SaveChanges();
        }

        var adminRole = db.Roles.Single(r => r.TenantId == tenantId && r.Name == "Admin");
        var adminRoleId = adminRole.Id;
        var cashierRoleId = db.Roles.Single(r => r.TenantId == tenantId && r.Name == "Cashier").Id;

        // Upgrade fixup: the PermissionsMask column defaults to 0 for pre-existing rows. Without this,
        // every existing Admin account would lose all admin screens the moment this ships.
        if (adminRole.PermissionsMask == 0)
        {
            adminRole.PermissionsMask = (int)Permission.All;
            adminRole.UpdatedAt = now;
            db.SaveChanges();
        }

        var ilsCurrency = db.Currencies.FirstOrDefault(c => c.Code == "ILS" && !c.IsDeleted)
            ?? throw new InvalidOperationException("Currencies seed missing ILS — apply migrations first.");

        var store = db.Stores.FirstOrDefault(s => s.TenantId == tenantId);
        if (store is null)
        {
            store = new Store
            {
                Id              = Guid.NewGuid(),
                TenantId        = tenantId,
                Name            = "Main Store",
                Address         = null,
                Phone           = null,
                BaseCurrencyId  = ilsCurrency.Id,
                CreatedAt       = now,
                UpdatedAt       = now
            };
            db.Stores.Add(store);
            db.SaveChanges();
        }
        else if (store.BaseCurrencyId == Guid.Empty)
        {
            store.BaseCurrencyId = ilsCurrency.Id;
            store.UpdatedAt      = now;
            db.SaveChanges();
        }

        if (!db.TenantCurrencyRates.Any(r => r.TenantId == tenantId && r.CurrencyId == ilsCurrency.Id))
        {
            db.TenantCurrencyRates.Add(new TenantCurrencyRate
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                CurrencyId = ilsCurrency.Id,
                ExchangeRate = 1m,
                CreatedAt = now,
                UpdatedAt = now
            });
            db.SaveChanges();
        }

        var adminUser = db.Users.FirstOrDefault(u => u.TenantId == tenantId && u.Username.ToLower() == "admin" && !u.IsDeleted);
        if (adminUser is null)
        {
            adminUser = new User
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Username = "admin",
                NormalizedUsername = "ADMIN",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(DemoAdminPassword),
                RoleId = adminRoleId,
                StoreId = store.Id,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Users.Add(adminUser);
            db.SaveChanges();
        }

        var cashierUser = db.Users.FirstOrDefault(u => u.TenantId == tenantId && u.Username.ToLower() == "cashier" && !u.IsDeleted);
        if (cashierUser is null)
        {
            cashierUser = new User
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Username = "cashier",
                NormalizedUsername = "CASHIER",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(DemoCashierPassword),
                RoleId = cashierRoleId,
                StoreId = store.Id,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Users.Add(cashierUser);
            db.SaveChanges();
        }

        foreach (var user in new[] { adminUser, cashierUser })
        {
            if (!db.UserStoreAccesses.Any(a => a.UserId == user.Id && a.StoreId == store.Id))
            {
                db.UserStoreAccesses.Add(new UserStoreAccess
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    UserId = user.Id,
                    StoreId = store.Id,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
        }

        if (db.ChangeTracker.HasChanges())
            db.SaveChanges();

        if (db.Categories.Any(c => c.TenantId == tenantId) || db.Products.Any(p => p.TenantId == tenantId))
            return;

        var catGeneral = new Category { Id = Guid.NewGuid(), TenantId = tenantId, Name = "General", CreatedAt = now, UpdatedAt = now };
        var catBev = new Category { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Beverages", CreatedAt = now, UpdatedAt = now };
        db.Categories.AddRange(catGeneral, catBev);

        var p1 = new Product
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "Sample Item A",
            Barcode = "10001",
            Price = 9.99m,
            Cost = 5.00m,
            CategoryId = catGeneral.Id,
            IsWeighted = false,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        var p2 = new Product
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "Sample Item B",
            Barcode = "10002",
            Price = 4.50m,
            Cost = 2.00m,
            CategoryId = catGeneral.Id,
            IsWeighted = false,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        var p3 = new Product
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "Water 500ml",
            Barcode = "20001",
            Price = 1.25m,
            Cost = 0.50m,
            CategoryId = catBev.Id,
            IsWeighted = false,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Products.AddRange(p1, p2, p3);

        db.Inventories.AddRange(
            new Inventory
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ProductId = p1.Id,
                StoreId = store.Id,
                Quantity = 100,
                CreatedAt = now,
                UpdatedAt = now
            },
            new Inventory
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ProductId = p2.Id,
                StoreId = store.Id,
                Quantity = 50,
                CreatedAt = now,
                UpdatedAt = now
            },
            new Inventory
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ProductId = p3.Id,
                StoreId = store.Id,
                Quantity = 200,
                CreatedAt = now,
                UpdatedAt = now
            });

        db.SaveChanges();
    }

    /// <summary>
    /// If demo users have plaintext, corrupted, or pre-2026-09-21 literal-username hashes
    /// (the trivial "admin"/"cashier" passwords seeded before password verification existed),
    /// re-hash to the current demo passwords. Does not change a BCrypt hash that verifies
    /// against neither the current nor the legacy literal password — that's a real change.
    /// </summary>
    private static void NormalizeBrokenDemoPasswordHashes(PosDbContext db)
    {
        (string Login, string Plain, string LegacyPlain)[] demo =
        [
            ("admin", DemoAdminPassword, "admin"),
            ("cashier", DemoCashierPassword, "cashier")
        ];
        var now = DateTime.UtcNow;
        var changed = false;

        foreach (var (login, plain, legacyPlain) in demo)
        {
            var user = db.Users.FirstOrDefault(u => u.Username.ToLower() == login && !u.IsDeleted);
            if (user is null)
                continue;

            if (LooksLikeBcrypt(user.PasswordHash))
            {
                try
                {
                    if (BCrypt.Net.BCrypt.Verify(plain, user.PasswordHash))
                        continue; // already on the current demo password
                    if (!BCrypt.Net.BCrypt.Verify(legacyPlain, user.PasswordHash))
                        continue; // genuinely different password — leave unchanged
                    // else: still the old trivial literal-username password — upgrade it below
                }
                catch
                {
                    // malformed bcrypt — replace below
                }
            }

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(plain);
            user.UpdatedAt    = now;
            changed             = true;
        }

        if (changed)
            db.SaveChanges();
    }

    private static bool LooksLikeBcrypt(string hash) =>
        hash.Length >= 59 && hash.StartsWith("$2", StringComparison.Ordinal);
}
