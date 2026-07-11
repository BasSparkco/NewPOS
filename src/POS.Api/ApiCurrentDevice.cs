using POS.Application.Abstractions;

namespace POS.Api;

internal sealed class ApiCurrentDevice : ICurrentDevice
{
    public ApiCurrentDevice(IConfiguration configuration)
    {
        Name = configuration["Device:Name"]?.Trim() is { Length: > 0 } configuredName
            ? configuredName
            : $"API {Environment.MachineName}";
    }

    public string Name { get; }
}