using System.Windows;
using VideoAbridge.ViewModels;

namespace VideoAbridge;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        Timeline.SeekRequested += (_, time) => _viewModel.SeekPlayhead(time);
        Timeline.MarkerSelected += (_, id) => _viewModel.SelectMarkerCommand.Execute(id);
        Timeline.DeletedRunSelected += (_, id) => _viewModel.SelectDeletedRunCommand.Execute(id);
        Timeline.MarkerMoved += (_, args) => _viewModel.MoveMarker(args.MarkerId, args.NewTime);

        Closed += (_, _) => _viewModel.Dispose();

        // LibVLC VideoView needs MediaPlayer assigned after load in some hosts
        Loaded += (_, _) =>
        {
            if (VideoView.MediaPlayer is null)
                VideoView.MediaPlayer = _viewModel.Player.MediaPlayer;
        };
    }
}
