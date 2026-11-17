using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VideoAbridge.ViewModels;

public partial class LongTaskItem : ObservableObject
{
    public Guid Id { get; } = Guid.NewGuid();

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isCompleted;
    [ObservableProperty] private bool _hasResultActions;
    [ObservableProperty] private bool _canCancel;
    [ObservableProperty] private string? _resultPath;

    public IRelayCommand? PrimaryActionCommand { get; set; }
    public string? PrimaryActionLabel { get; set; }
    public IRelayCommand? CancelCommand { get; set; }
    public IRelayCommand? DismissCommand { get; set; }
}
