using POS.Application.Models;

namespace POS.Application.Abstractions;

/// <summary>Store-scoped provisioning/enrollment/revocation for Boxes (registers), backing the admin "Devices" screen.</summary>
public interface IDeviceManagementService
{
    Task<IReadOnlyList<DeviceListItemDto>> GetDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a not-yet-enrolled device row and returns a one-time enrollment code (shown once — only its hash is stored).</summary>
    Task<(bool Success, string? Error, string? EnrollmentCode)> ProvisionDeviceAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Blocks the device from signing in or syncing. There is no "reactivate" — provision a new device instead.</summary>
    Task<(bool Success, string? Error)> RevokeDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default);

    /// <summary>Redeems a one-time enrollment code and binds that device row to this machine's <see cref="ICurrentDevice.Name"/>.</summary>
    Task<(bool Success, string? Error)> EnrollCurrentMachineAsync(string enrollmentCode, CancellationToken cancellationToken = default);
}
