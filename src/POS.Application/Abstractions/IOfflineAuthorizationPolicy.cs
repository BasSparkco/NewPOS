namespace POS.Application.Abstractions;

/// <summary>
/// Enforces a finite offline-authorization validity window (tenant.md §5: "do not silently permit
/// indefinite cached administrative access"). Selling stays available offline indefinitely; only
/// administrative actions (manage products/users/settings, process refunds) get locked once this
/// device hasn't proven its authorization to the server (a successful online login) recently enough.
/// </summary>
public interface IOfflineAuthorizationPolicy
{
    /// <summary>
    /// True when administrative actions should be blocked on this device right now. Always false
    /// when sync is disabled (no server to prove authorization against in the first place).
    /// </summary>
    bool IsAdministrativeAccessLocked();
}
