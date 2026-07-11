using POS.Application.Abstractions;

namespace POS.Wpf.Services;

public sealed class CurrentDevice : ICurrentDevice
{
    public string Name { get; } = Environment.MachineName;
}