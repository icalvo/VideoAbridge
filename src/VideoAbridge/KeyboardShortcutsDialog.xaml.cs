using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Input;

namespace VideoAbridge;

[SuppressMessage(
    "Usage",
    "CA1515:Consider making public types internal",
    Justification = "WPF requires windows referenced from XAML to be public.")]
public partial class KeyboardShortcutsDialog : Window
{
    public KeyboardShortcutsDialog()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.Key is Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}
