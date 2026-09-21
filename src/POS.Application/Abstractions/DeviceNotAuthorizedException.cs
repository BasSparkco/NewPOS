namespace POS.Application.Abstractions;

/// <summary>Thrown when the current machine is not a recognized, enrolled, non-revoked Box for its store and enrollment is required (see "Sync:RequireDeviceEnrollment").</summary>
public sealed class DeviceNotAuthorizedException : Exception
{
    public DeviceNotAuthorizedException(string message) : base(message)
    {
    }
}
