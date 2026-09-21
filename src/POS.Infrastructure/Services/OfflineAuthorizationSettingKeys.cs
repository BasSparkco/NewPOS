namespace POS.Infrastructure.Services;

/// <summary>
/// Shared between <see cref="AuthService"/> (reads it back into <see cref="POS.Application.Abstractions.ICurrentSession"/>
/// at login) and <see cref="InvoiceSyncService"/> (writes it on every successful online re-login).
/// </summary>
internal static class OfflineAuthorizationSettingKeys
{
    public const string LastOnlineContactUtc = "Sync.LastOnlineContactUtc";
}
