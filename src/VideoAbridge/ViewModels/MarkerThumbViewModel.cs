using System.Windows.Media.Imaging;

namespace VideoAbridge.ViewModels;

public sealed class MarkerThumbViewModel
{
    public Guid MarkerId { get; init; }
    public TimeSpan Time { get; init; }
    public BitmapImage? BeforeImage { get; init; }
    public BitmapImage? AfterImage { get; init; }
}
