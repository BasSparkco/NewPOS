using Microsoft.EntityFrameworkCore;
using POS.Application.Abstractions;
using POS.Application.Models;
using POS.Application.Support;
using POS.Core.Entities;
using POS.Infrastructure.Data;

namespace POS.Infrastructure.Services;

internal sealed class DeviceManagementService : IDeviceManagementService
{
    private readonly IDbContextFactory<PosDbContext> _dbFactory;
    private readonly ICurrentSession _session;
    private readonly ICurrentDevice _currentDevice;
    private readonly IAuditLogService _auditLogService;

    public DeviceManagementService(
        IDbContextFactory<PosDbContext> dbFactory,
        ICurrentSession session,
        ICurrentDevice currentDevice,
        IAuditLogService auditLogService)
    {
        _dbFactory = dbFactory;
        _session = session;
        _currentDevice = currentDevice;
        _auditLogService = auditLogService;
    }

    public async Task<IReadOnlyList<DeviceListItemDto>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var storeId = _session.StoreId;
        var currentName = _currentDevice.Name;

        var rows = await db.Devices
            .AsNoTracking()
            .Where(d => d.StoreId == storeId && !d.IsDeleted)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new { d.Id, d.Name, d.EnrolledAt, d.IsRevoked, d.CreatedAt, d.RegisterId })
            .ToListAsync(cancellationToken);

        var registerNumbers = await db.Registers
            .AsNoTracking()
            .Where(r => r.StoreId == storeId)
            .Select(r => new { r.Id, r.Number })
            .ToDictionaryAsync(r => r.Id, r => r.Number, cancellationToken);

        return rows
            .Select(d => new DeviceListItemDto(
                d.Id,
                d.Name,
                d.EnrolledAt is not null,
                d.IsRevoked,
                d.EnrolledAt,
                d.CreatedAt,
                string.Equals(d.Name, currentName, StringComparison.Ordinal),
                d.RegisterId,
                d.RegisterId is not null && registerNumbers.TryGetValue(d.RegisterId.Value, out var number) ? number : null))
            .ToList();
    }

    public async Task<IReadOnlyList<RegisterListItemDto>> GetRegistersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var storeId = _session.StoreId;

        var registers = await db.Registers
            .AsNoTracking()
            .Where(r => r.StoreId == storeId && !r.IsDeleted)
            .OrderBy(r => r.Number)
            .ToListAsync(cancellationToken);

        var devices = await db.Devices
            .AsNoTracking()
            .Where(d => d.StoreId == storeId && !d.IsDeleted && d.RegisterId != null && !d.IsRevoked)
            .ToListAsync(cancellationToken);

        return registers
            .Select(r =>
            {
                var current = devices
                    .Where(d => d.RegisterId == r.Id)
                    .OrderByDescending(d => d.CreatedAt)
                    .FirstOrDefault();
                return new RegisterListItemDto(r.Id, r.Number, r.Name, r.IsActive, current?.Id, current?.Name);
            })
            .ToList();
    }

    public async Task<(bool Success, string? Error, string? EnrollmentCode)> ProvisionDeviceAsync(
        string name, Guid? replaceRegisterId = null, CancellationToken cancellationToken = default)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0)
            return (false, "A label for the new Box is required.", null);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var storeId = _session.StoreId;

        var exists = await db.Devices.AnyAsync(d => d.StoreId == storeId && d.Name == name && !d.IsDeleted, cancellationToken);
        if (exists)
            return (false, $"A device named '{name}' already exists at this store.", null);

        var tenantId = await db.Stores
            .AsNoTracking()
            .Where(s => s.Id == storeId && !s.IsDeleted)
            .Select(s => (Guid?)s.TenantId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Store not found.");

        var now = DateTime.UtcNow;
        Guid registerId;

        if (replaceRegisterId is { } targetRegisterId)
        {
            var register = await db.Registers
                .FirstOrDefaultAsync(r => r.Id == targetRegisterId && r.StoreId == storeId && !r.IsDeleted, cancellationToken);
            if (register is null)
                return (false, "Register not found.", null);

            var hasActiveDevice = await db.Devices
                .AnyAsync(d => d.RegisterId == register.Id && !d.IsDeleted && !d.IsRevoked, cancellationToken);
            if (hasActiveDevice)
                return (false, "This register already has an active device. Revoke it first before binding a replacement.", null);

            registerId = register.Id;
        }
        else
        {
            var nextNumber = (await db.Registers
                .Where(r => r.StoreId == storeId)
                .Select(r => (int?)r.Number)
                .MaxAsync(cancellationToken) ?? 0) + 1;

            var register = new Register
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                StoreId = storeId,
                Number = nextNumber,
                Name = name,
                IsActive = true,
                SyncVersion = 1,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Registers.Add(register);
            registerId = register.Id;
        }

        var enrollmentCode = RandomPasswordGenerator.Generate(12);
        var device = new Device
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            StoreId = storeId,
            RegisterId = registerId,
            Name = name,
            SyncVersion = 1,
            EnrollmentCodeHash = BCrypt.Net.BCrypt.HashPassword(enrollmentCode),
            EnrolledAt = null,
            IsRevoked = false,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync(cancellationToken);

        await TryWriteAuditAsync("DeviceProvisioned", nameof(Device), device.Id,
            replaceRegisterId is null
                ? $"Name={device.Name}"
                : $"Name={device.Name}, replacing device on RegisterId={replaceRegisterId}",
            cancellationToken);

        return (true, null, enrollmentCode);
    }

    public async Task<(bool Success, string? Error)> RevokeDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var storeId = _session.StoreId;

        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId && d.StoreId == storeId && !d.IsDeleted, cancellationToken);
        if (device is null)
            return (false, "Device not found.");

        if (device.IsRevoked)
            return (true, null);

        var now = DateTime.UtcNow;
        device.IsRevoked = true;
        device.RevokedAt = now;
        device.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        await TryWriteAuditAsync("DeviceRevoked", nameof(Device), device.Id, $"Name={device.Name}", cancellationToken);

        return (true, null);
    }

    public async Task<(bool Success, string? Error, string? DeviceSecret)> EnrollCurrentMachineAsync(string enrollmentCode, CancellationToken cancellationToken = default)
    {
        enrollmentCode = (enrollmentCode ?? string.Empty).Trim();
        if (enrollmentCode.Length == 0)
            return (false, "Enrollment code is required.", null);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var storeId = _session.StoreId;
        var machineName = _currentDevice.Name;

        var candidates = await db.Devices
            .Where(d => d.StoreId == storeId && !d.IsDeleted && !d.IsRevoked && d.EnrolledAt == null && d.EnrollmentCodeHash != null)
            .ToListAsync(cancellationToken);

        Device? match = null;
        foreach (var candidate in candidates)
        {
            if (VerifyCode(enrollmentCode, candidate.EnrollmentCodeHash))
            {
                match = candidate;
                break;
            }
        }

        if (match is null)
            return (false, "Invalid or already-used enrollment code.", null);

        var nameConflict = await db.Devices.AnyAsync(
            d => d.StoreId == storeId && d.Id != match.Id && d.Name == machineName && !d.IsDeleted,
            cancellationToken);
        if (nameConflict)
            return (false, $"Another device already uses this machine's name ('{machineName}'). Ask an administrator to resolve it.", null);

        var deviceSecret = RandomPasswordGenerator.Generate(32);
        var now = DateTime.UtcNow;
        match.Name = machineName;
        match.EnrolledAt = now;
        match.EnrollmentCodeHash = null; // one-time use — consume it
        match.DeviceSecretHash = BCrypt.Net.BCrypt.HashPassword(deviceSecret);
        match.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        await TryWriteAuditAsync("DeviceEnrolled", nameof(Device), match.Id, $"Name={match.Name}", cancellationToken);

        return (true, null, deviceSecret);
    }

    private static bool VerifyCode(string code, string? hash)
    {
        if (string.IsNullOrEmpty(hash))
            return false;

        try
        {
            return BCrypt.Net.BCrypt.Verify(code, hash);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            return false;
        }
    }

    private async Task TryWriteAuditAsync(string action, string entityName, Guid? entityId, string? details, CancellationToken cancellationToken)
    {
        try
        {
            await _auditLogService.WriteAsync(action, entityName, entityId, details, cancellationToken);
        }
        catch
        {
            // Audit logging is best-effort and must not block the business write.
        }
    }
}
