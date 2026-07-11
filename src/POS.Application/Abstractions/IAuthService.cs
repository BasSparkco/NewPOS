namespace POS.Application.Abstractions;

public interface IAuthService
{
    Task<AuthResult> LoginAsync(string username, CancellationToken cancellationToken = default);
}

public sealed record AuthResult(bool Success, string? ErrorMessage);
