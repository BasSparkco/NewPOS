using CommunityToolkit.Mvvm.ComponentModel;

namespace POS.Wpf.ViewModels;

/// <summary>
/// A row in the Products screen's category rail. <see cref="CategoryId"/> is null for the
/// pinned "All products" row.
/// </summary>
public partial class CategoryRailItem : ObservableObject
{
    public Guid? CategoryId { get; set; }
    public string Name { get; set; } = "";

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editName = "";
}
