using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using POS.Core.Entities;
using POS.Infrastructure.Data;

namespace POS.Tests;

public class RegisterBackfillTests
{
    [Fact]
    public async Task Backfill_assigns_a_stable_register_to_a_pre_existing_device_and_its_invoices()
    {
        await using var host = await TestServiceHost.CreateAsync();

        Guid deviceId;
        Guid invoiceId;

        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<PosDbContext>>().CreateDbContextAsync())
        {
            var now = DateTime.UtcNow;
            deviceId = Guid.NewGuid();
            db.Devices.Add(new Device
            {
                Id = deviceId,
                TenantId = host.TenantId,
                StoreId = host.StoreId,
                Name = "Legacy Register",
                SyncVersion = 1,
                EnrolledAt = now,
                CreatedAt = now,
                UpdatedAt = now
            });

            invoiceId = Guid.NewGuid();
            db.Invoices.Add(new Invoice
            {
                Id = invoiceId,
                TenantId = host.TenantId,
                StoreId = host.StoreId,
                DeviceId = deviceId,
                UserId = host.UserId,
                Status = POS.Core.Enums.InvoiceStatus.Paid,
                TotalAmount = 10m,
                Currency = "USD",
                SyncVersion = 1,
                CreatedAt = now,
                UpdatedAt = now
            });

            await db.SaveChangesAsync();
        }

        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<PosDbContext>>().CreateDbContextAsync())
        {
            RegisterBackfill.EnsureBackfilled(db);
        }

        await using var verifyDb = await host.Services.GetRequiredService<IDbContextFactory<PosDbContext>>().CreateDbContextAsync();
        var device = await verifyDb.Devices.AsNoTracking().SingleAsync(d => d.Id == deviceId);
        Assert.NotNull(device.RegisterId);

        var register = await verifyDb.Registers.AsNoTracking().SingleAsync(r => r.Id == device.RegisterId);
        Assert.Equal(host.StoreId, register.StoreId);
        Assert.Equal(1, register.Number);
        Assert.Equal("Legacy Register", register.Name);

        var invoice = await verifyDb.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId);
        Assert.Equal(device.RegisterId, invoice.RegisterId);

        // Idempotent: running it again does not create a second register or change the assignment.
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<PosDbContext>>().CreateDbContextAsync())
        {
            RegisterBackfill.EnsureBackfilled(db);
        }
        var registerCount = await verifyDb.Registers.CountAsync(r => r.StoreId == host.StoreId);
        Assert.Equal(1, registerCount);
    }

    [Fact]
    public void Backfill_is_deterministic_across_independently_run_instances()
    {
        // Two independent in-memory-equivalent runs against databases seeded with the same device Id
        // must converge on the same Register Id, since a WPF install and its API server may each run
        // this backfill independently against their own copy of the same pre-existing Devices table.
        var deviceId = Guid.NewGuid();

        var firstRegisterId = InvokeDeriveDeterministicId(deviceId);
        var secondRegisterId = InvokeDeriveDeterministicId(deviceId);

        Assert.Equal(firstRegisterId, secondRegisterId);
    }

    private static Guid InvokeDeriveDeterministicId(Guid deviceId)
    {
        var method = typeof(RegisterBackfill).GetMethod("DeriveDeterministicId",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (Guid)method.Invoke(null, [deviceId, "register-backfill-v1"])!;
    }
}
