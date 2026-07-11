using Microsoft.EntityFrameworkCore;
using POS.Core.Entities;

namespace POS.Infrastructure.Data;

public static class DatabaseSeeder
{
    public static void SeedIfNeeded(PosDbContext db)
    {
        NormalizeBrokenDemoPasswordHashes(db);

        var now = DateTime.UtcNow;

        if (!db.Roles.Any())
        {
            var adminRole = new Role { Id = Guid.NewGuid(), Name = "Admin", CreatedAt = now, UpdatedAt = now };
            var cashierRole = new Role { Id = Guid.NewGuid(), Name = "Cashier", CreatedAt = now, UpdatedAt = now };
            db.Roles.AddRange(adminRole, cashierRole);
            db.SaveChanges();
        }

        var adminRoleId = db.Roles.Single(r => r.Name == "Admin").Id;
        var cashierRoleId = db.Roles.Single(r => r.Name == "Cashier").Id;

        var ilsCurrency = db.Currencies.FirstOrDefault(c => c.Code == "ILS" && !c.IsDeleted)
            ?? throw new InvalidOperationException("Currencies seed missing ILS — apply migrations first.");

        var store = db.Stores.FirstOrDefault();
        if (store is null)
        {
            store = new Store
            {
                Id              = Guid.NewGuid(),
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

        if (!db.Users.Any(u => u.Username.ToLower() == "admin" && !u.IsDeleted))
        {
            var adminUser = new User
            {
                Id = Guid.NewGuid(),
                Username = "admin",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("admin"),
                RoleId = adminRoleId,
                StoreId = store.Id,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Users.Add(adminUser);
        }

        if (!db.Users.Any(u => u.Username.ToLower() == "cashier" && !u.IsDeleted))
        {
            var cashierUser = new User
            {
                Id = Guid.NewGuid(),
                Username = "cashier",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("cashier"),
                RoleId = cashierRoleId,
                StoreId = store.Id,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Users.Add(cashierUser);
        }

        if (db.ChangeTracker.HasChanges())
            db.SaveChanges();

        if (db.Categories.Any() || db.Products.Any())
            return;

        var catGeneral = new Category { Id = Guid.NewGuid(), Name = "General", CreatedAt = now, UpdatedAt = now };
        var catBev = new Category { Id = Guid.NewGuid(), Name = "Beverages", CreatedAt = now, UpdatedAt = now };
        db.Categories.AddRange(catGeneral, catBev);

        var p1 = new Product
        {
            Id = Guid.NewGuid(),
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
                ProductId = p1.Id,
                StoreId = store.Id,
                Quantity = 100,
                CreatedAt = now,
                UpdatedAt = now
            },
            new Inventory
            {
                Id = Guid.NewGuid(),
                ProductId = p2.Id,
                StoreId = store.Id,
                Quantity = 50,
                CreatedAt = now,
                UpdatedAt = now
            },
            new Inventory
            {
                Id = Guid.NewGuid(),
                ProductId = p3.Id,
                StoreId = store.Id,
                Quantity = 200,
                CreatedAt = now,
                UpdatedAt = now
            });

        db.SaveChanges();
    }

    /// <summary>
    /// If demo users have plaintext or corrupted hashes, re-hash to the default passwords.
    /// Does not change valid BCrypt hashes that already use a different password.
    /// </summary>
    private static void NormalizeBrokenDemoPasswordHashes(PosDbContext db)
    {
        (string Login, string Plain)[] demo = [("admin", "admin"), ("cashier", "cashier")];
        var now = DateTime.UtcNow;
        var changed = false;

        foreach (var (login, plain) in demo)
        {
            var user = db.Users.FirstOrDefault(u => u.Username.ToLower() == login && !u.IsDeleted);
            if (user is null)
                continue;

            if (LooksLikeBcrypt(user.PasswordHash))
            {
                try
                {
                    if (BCrypt.Net.BCrypt.Verify(plain, user.PasswordHash))
                        continue;
                    continue; // different password — leave unchanged
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
