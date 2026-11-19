using System.Windows;
using System.Windows.Controls;

namespace VideoAbridge;

internal partial class TaskLogWindow : Window
{
    public TaskLogWindow()
    {
        InitializeComponent();
    }

    private void LogBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box)
            box.ScrollToEnd();
    }
}
