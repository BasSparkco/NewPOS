using System.ComponentModel.DataAnnotations;
using POS.Application.Models;

namespace POS.Web.Models;

public sealed class StoresViewModel
{
    public Guid ActiveStoreId { get; init; }
    public IReadOnlyList<StoreListItemDto> Stores { get; init; } = Array.Empty<StoreListItemDto>();
    public Guid SelectedStoreId { get; init; }
    public string? SelectedStoreName { get; init; }
    public IReadOnlyList<StoreUserAccessRowDto> AccessRows { get; init; } = Array.Empty<StoreUserAccessRowDto>();
    public CreateStoreFormViewModel CreateStore { get; init; } = new();
}

public sealed class CreateStoreFormViewModel
{
    [Required]
    public string Name { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string? Phone { get; set; }
}
