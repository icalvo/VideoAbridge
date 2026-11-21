using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

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

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
