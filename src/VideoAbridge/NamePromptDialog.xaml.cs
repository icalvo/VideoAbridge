using System.Diagnostics.CodeAnalysis;
using System.Windows;

namespace VideoAbridge;

[SuppressMessage(
    "Usage",
    "CA1515:Consider making public types internal",
    Justification = "WPF requires windows referenced from XAML to be public.")]
public partial class NamePromptDialog : Window
{
    public NamePromptDialog(string title, string prompt, string initialName)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        NameBox.Text = initialName;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    public string ResultName { get; private set; } = "";

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ResultName = NameBox.Text.Trim();
        if (ResultName.Length == 0)
        {
            MessageBox.Show(
                this,
                "Name is required.",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }
}
