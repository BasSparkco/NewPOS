using POS.Application.Models;

namespace POS.Application.Abstractions;

public interface ISettingsService
{
    Task<StoreSettingsDto> GetStoreSettingsAsync(CancellationToken cancellationToken = default);
    Task UpdateStoreSettingsAsync(StoreSettingsDto settings, CancellationToken cancellationToken = default);
}