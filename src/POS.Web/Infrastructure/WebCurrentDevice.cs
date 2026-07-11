using POS.Application.Abstractions;

namespace POS.Web.Infrastructure;

internal sealed class WebCurrentDevice : ICurrentDevice
{
    public WebCurrentDevice(IConfiguration configuration)
    {
        Name = configuration["Device:Name"]?.Trim() is { Length: > 0 } configuredName
            ? configuredName
            : $"WEB {Environment.MachineName}";
    }

    public string Name { get; }
}