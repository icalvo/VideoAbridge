using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VideoAbridge.ViewModels;

internal sealed partial class LongTaskItem : ObservableObject
{
    private readonly StringBuilder _log = new();
    private const int MaxLogChars = 512_000;

    public Guid Id { get; } = Guid.NewGuid();

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isCompleted;
    [ObservableProperty] private bool _hasResultActions;
    [ObservableProperty] private bool _canCancel;
    [ObservableProperty] private string? _resultPath;
    [ObservableProperty] private string _logText = "";

    public IRelayCommand? PrimaryActionCommand { get; set; }
    public string? PrimaryActionLabel { get; set; }
    public IRelayCommand? CancelCommand { get; set; }
    public IRelayCommand? DismissCommand { get; set; }

    /// <summary>Appends a line to the task log (marshals to the UI thread).</summary>
    public void AppendLog(string line)
    {
        if (string.IsNullOrEmpty(line))
            return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        if (dispatcher.CheckAccess())
            AppendLogCore(line);
        else
            _ = dispatcher.BeginInvoke(() => AppendLogCore(line));
    }

    private void AppendLogCore(string line)
    {
        if (_log.Length > 0)
            _log.AppendLine();
        _log.Append(line);

        if (_log.Length > MaxLogChars)
        {
            _log.Remove(0, _log.Length - MaxLogChars);
            var text = _log.ToString();
            var firstNl = text.IndexOf('\n', StringComparison.Ordinal);
            if (firstNl is >= 0 and < 200)
                _log.Remove(0, firstNl + 1);
        }

        LogText = _log.ToString();
    }

    [RelayCommand]
    private void ShowLog()
    {
        var app = Application.Current;
        if (app is null)
            return;

        foreach (Window window in app.Windows)
        {
            if (window is TaskLogWindow logWindow && ReferenceEquals(logWindow.DataContext, this))
            {
                logWindow.Activate();
                return;
            }
        }

        new TaskLogWindow
        {
            DataContext = this,
            Owner = app.MainWindow
        }.Show();
    }
}
