using System.Security.Cryptography;
using System.Text;
using POS.Core.Entities;

namespace POS.Infrastructure.Data;

/// <summary>
/// Migrates pre-existing Device rows (created before the logical Register/physical Device split,
/// Stage 4T's cash-session and hardware-replacement work) onto a Register each, and backfills
/// Invoice.RegisterId from the invoice's Device. Runs at every startup (idempotent — only touches
/// rows still missing a RegisterId) alongside <see cref="DatabaseSeeder.SeedIfNeeded"/>.
///
/// Register ids are derived deterministically from the Device id (not <see cref="Guid.NewGuid"/>)
/// so that a WPF install and its API server — which may each run this backfill independently against
/// their own copy of the same pre-existing Devices table — converge on the *same* Register identity
/// for "the same" device without needing a coordinated migration run or a new sync round-trip. This
/// is a deliberate, narrower substitute for giving Register its own push/pull sync surface, which is
/// still open work (see STATUS.md).
/// </summary>
public static class RegisterBackfill
{
    public static void EnsureBackfilled(PosDbContext db)
    {
        BackfillDeviceRegisters(db);
        BackfillInvoiceRegisters(db);
    }

    private static void BackfillDeviceRegisters(PosDbContext db)
    {
        var orphanDevices = db.Devices.Where(d => d.RegisterId == null).ToList();
        if (orphanDevices.Count == 0)
            return;

        var now = DateTime.UtcNow;

        foreach (var storeGroup in orphanDevices.GroupBy(d => d.StoreId))
        {
            var storeId = storeGroup.Key;
            var existingNumbers = db.Registers
                .Where(r => r.StoreId == storeId)
                .Select(r => r.Number)
                .ToHashSet();
            var nextNumber = existingNumbers.Count == 0 ? 1 : existingNumbers.Max() + 1;

            // Deterministic ordering so independently-run backfills (WPF vs API, both starting from
            // the same already-synced Devices rows) assign identical numbers without coordinating.
            foreach (var device in storeGroup.OrderBy(d => d.CreatedAt).ThenBy(d => d.Id))
            {
                var registerId = DeriveDeterministicId(device.Id, "register-backfill-v1");
                var register = new Register
                {
                    Id = registerId,
                    TenantId = device.TenantId,
                    StoreId = storeId,
                    Number = nextNumber++,
                    Name = device.Name,
                    IsActive = !device.IsRevoked,
                    SyncVersion = 1,
                    CreatedAt = now,
                    UpdatedAt = now,
                    IsDeleted = false
                };
                db.Registers.Add(register);
                device.RegisterId = registerId;
            }
        }

        db.SaveChanges();
    }

    private static void BackfillInvoiceRegisters(PosDbContext db)
    {
        var pendingInvoices = db.Invoices.Where(i => i.RegisterId == null).ToList();
        if (pendingInvoices.Count == 0)
            return;

        var deviceRegisterMap = db.Devices
            .Where(d => d.RegisterId != null)
            .ToDictionary(d => d.Id, d => d.RegisterId!.Value);

        foreach (var invoice in pendingInvoices)
        {
            if (deviceRegisterMap.TryGetValue(invoice.DeviceId, out var registerId))
                invoice.RegisterId = registerId;
        }

        // Bypasses SaveChanges' sync-change capture deliberately: this is a one-time schema backfill of
        // historical rows, not a business change, and must not flood every existing invoice back through
        // sync as if it had just been edited. See PosDbContext.SaveChanges/CapturePendingSyncChanges.
        db.SaveChangesWithoutSyncCapture();
    }

    private static Guid DeriveDeterministicId(Guid seed, string salt)
    {
        var input = seed.ToByteArray().Concat(Encoding.UTF8.GetBytes(salt)).ToArray();
        var hash = SHA256.HashData(input);
        var guidBytes = new byte[16];
        Array.Copy(hash, guidBytes, 16);
        return new Guid(guidBytes);
    }
}
