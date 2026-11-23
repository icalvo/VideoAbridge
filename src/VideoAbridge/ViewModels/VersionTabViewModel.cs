using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VideoAbridge.ViewModels;

[SuppressMessage(
    "Usage",
    "CA1515:Consider making public types internal",
    Justification = "Bound from MainWindow version tab DataTemplate.")]
public sealed partial class VersionTabViewModel(VersionId id, string name, bool isActive) : ObservableObject
{
    public VersionId Id { get; } = id;

    [ObservableProperty] private string _name = name;
    [ObservableProperty] private bool _isActive = isActive;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editName = name;

    public void BeginEdit()
    {
        EditName = Name;
        IsEditing = true;
    }

    public void CancelEdit() => IsEditing = false;
}
