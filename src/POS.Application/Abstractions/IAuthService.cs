namespace POS.Application.Abstractions;

public interface IAuthService
{
    /// <summary>
    /// <paramref name="tenantSlug"/> resolves which business to sign into when more than one tenant
    /// exists. It is optional only because today every real deployment (desktop SQLite, the single
    /// bootstrap tenant) has exactly one tenant to resolve to; once more than one tenant exists an
    /// omitted slug fails the login rather than guessing.
    /// </summary>
    Task<AuthResult> LoginAsync(string username, string password, string? tenantSlug = null, CancellationToken cancellationToken = default);
}

public sealed record AuthResult(bool Success, string? ErrorMessage);
