using System.Diagnostics.CodeAnalysis;

namespace VideoAbridge.ViewModels;

[SuppressMessage(
    "Usage",
    "CA1515:Consider making public types internal",
    Justification = "Bound from MainWindow version tab DataTemplate.")]
public sealed class VersionTabViewModel(Guid id, string name, bool isActive)
{
    public Guid Id { get; } = id;
    public string Name { get; } = name;
    public bool IsActive { get; } = isActive;
}
