namespace POS.Application.Abstractions;

/// <summary>
/// Stage 4T/T3: lets a genuinely fresh WPF install (empty local database, no <c>Tenant</c> row) bind
/// itself to an existing remote business instead of always seeding a brand-new independent one. See
/// tenant.md §7 T3 — "Tenant-safe WPF and offline profiles."
/// </summary>
public interface IBusinessJoinService
{
    /// <summary>
    /// Authenticates against the remote API with an existing user's credentials, then pulls that
    /// business's catalog/settings/users/devices down into the local database. Callers must confirm the
    /// local database is fresh (no <c>Tenant</c> row) before calling — this does not re-check.
    /// </summary>
    Task<(bool Success, string? Error)> JoinExistingBusinessAsync(
        string apiBaseUrl,
        string tenantSlug,
        string username,
        string password,
        CancellationToken cancellationToken = default);
}
