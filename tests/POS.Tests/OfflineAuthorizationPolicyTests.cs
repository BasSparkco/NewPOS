using Microsoft.Extensions.DependencyInjection;
using POS.Application.Abstractions;

namespace POS.Tests;

public class OfflineAuthorizationPolicyTests
{
    [Fact]
    public async Task Administrative_access_is_never_locked_when_sync_is_disabled()
    {
        await using var host = await TestServiceHost.CreateAsync(
            new Dictionary<string, string?> { ["Sync:Enabled"] = "false" });

        await host.ExecuteScopeAsync(services =>
        {
            var policy = services.GetRequiredService<IOfflineAuthorizationPolicy>();
            host.Session.SetLastOnlineContactUtc(null);

            Assert.False(policy.IsAdministrativeAccessLocked());
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Administrative_access_is_locked_when_sync_is_enabled_and_never_contacted()
    {
        await using var host = await TestServiceHost.CreateAsync(
            new Dictionary<string, string?> { ["Sync:Enabled"] = "true" });

        await host.ExecuteScopeAsync(services =>
        {
            var policy = services.GetRequiredService<IOfflineAuthorizationPolicy>();
            host.Session.SetLastOnlineContactUtc(null);

            Assert.True(policy.IsAdministrativeAccessLocked());
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Administrative_access_is_open_when_last_contact_is_within_the_default_24_hour_window()
    {
        await using var host = await TestServiceHost.CreateAsync(
            new Dictionary<string, string?> { ["Sync:Enabled"] = "true" });

        await host.ExecuteScopeAsync(services =>
        {
            var policy = services.GetRequiredService<IOfflineAuthorizationPolicy>();
            host.Session.SetLastOnlineContactUtc(DateTime.UtcNow.AddHours(-23));

            Assert.False(policy.IsAdministrativeAccessLocked());
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Administrative_access_is_locked_when_last_contact_exceeds_the_default_24_hour_window()
    {
        await using var host = await TestServiceHost.CreateAsync(
            new Dictionary<string, string?> { ["Sync:Enabled"] = "true" });

        await host.ExecuteScopeAsync(services =>
        {
            var policy = services.GetRequiredService<IOfflineAuthorizationPolicy>();
            host.Session.SetLastOnlineContactUtc(DateTime.UtcNow.AddHours(-25));

            Assert.True(policy.IsAdministrativeAccessLocked());
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Offline_authorization_window_is_configurable()
    {
        await using var host = await TestServiceHost.CreateAsync(
            new Dictionary<string, string?>
            {
                ["Sync:Enabled"] = "true",
                ["Sync:OfflineAuthorizationHours"] = "2"
            });

        await host.ExecuteScopeAsync(services =>
        {
            var policy = services.GetRequiredService<IOfflineAuthorizationPolicy>();

            host.Session.SetLastOnlineContactUtc(DateTime.UtcNow.AddHours(-1));
            Assert.False(policy.IsAdministrativeAccessLocked());

            host.Session.SetLastOnlineContactUtc(DateTime.UtcNow.AddHours(-3));
            Assert.True(policy.IsAdministrativeAccessLocked());
            return Task.CompletedTask;
        });
    }
}
