using POS.Application.Models;

namespace POS.Application.Abstractions;

/// <summary>Store-scoped provisioning/enrollment/revocation for Boxes (registers), backing the admin "Devices" screen.</summary>
public interface IDeviceManagementService
{
    Task<IReadOnlyList<DeviceListItemDto>> GetDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>Registers (tills) at the current store, each with its stable number/history and the device currently bound to it, if any.</summary>
    Task<IReadOnlyList<RegisterListItemDto>> GetRegistersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a not-yet-enrolled device row and returns a one-time enrollment code (shown once — only
    /// its hash is stored). When <paramref name="replaceRegisterId"/> is null, a brand-new Register is
    /// created for this device (adding a till). When set, the new device is bound to that *existing*
    /// Register instead — the register keeps its stable number and sale history across the hardware
    /// swap; the caller is expected to revoke the register's previous device separately.
    /// </summary>
    Task<(bool Success, string? Error, string? EnrollmentCode)> ProvisionDeviceAsync(string name, Guid? replaceRegisterId = null, CancellationToken cancellationToken = default);

    /// <summary>Blocks the device from signing in or syncing. There is no "reactivate" — provision a new device instead.</summary>
    Task<(bool Success, string? Error)> RevokeDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Redeems a one-time enrollment code and binds that device row to this machine's
    /// <see cref="ICurrentDevice.Name"/>. Also issues a persistent device secret (returned once,
    /// analogous to the enrollment code) that authenticates this terminal for background
    /// synchronization independent of any employee's own login — the caller is responsible for
    /// storing it locally.
    /// </summary>
    Task<(bool Success, string? Error, string? DeviceSecret)> EnrollCurrentMachineAsync(string enrollmentCode, CancellationToken cancellationToken = default);
}
