using Microsoft.Extensions.Configuration;
using POS.Application.Abstractions;

namespace POS.Infrastructure.Services;

internal sealed class OfflineAuthorizationPolicy : IOfflineAuthorizationPolicy
{
    private const int DefaultOfflineAuthorizationHours = 24;

    private readonly IConfiguration _configuration;
    private readonly ICurrentSession _session;

    public OfflineAuthorizationPolicy(IConfiguration configuration, ICurrentSession session)
    {
        _configuration = configuration;
        _session = session;
    }

    public bool IsAdministrativeAccessLocked()
    {
        var syncEnabled = _configuration.GetValue<bool?>("Sync:Enabled") ?? false;
        if (!syncEnabled)
            return false;

        if (_session.LastOnlineContactUtc is null)
            return true;

        var hours = Math.Max(1, _configuration.GetValue<int?>("Sync:OfflineAuthorizationHours") ?? DefaultOfflineAuthorizationHours);
        return DateTime.UtcNow - _session.LastOnlineContactUtc.Value > TimeSpan.FromHours(hours);
    }
}
