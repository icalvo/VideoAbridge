using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Windows;

[assembly: InternalsVisibleTo("VideoAbridge.Tests")]
[assembly: InternalsVisibleTo("ReadmeScreenshot")]

[assembly: ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]

// WPF XAML partial classes must remain public for the markup compiler / generated subclasses.
[assembly: SuppressMessage(
    "Usage",
    "CA1515:Consider making public types internal",
    Justification = "WPF requires App, windows, and UserControls referenced from XAML to be public.",
    Scope = "type",
    Target = "~T:VideoAbridge.App")]
[assembly: SuppressMessage(
    "Usage",
    "CA1515:Consider making public types internal",
    Justification = "WPF requires App, windows, and UserControls referenced from XAML to be public.",
    Scope = "type",
    Target = "~T:VideoAbridge.MainWindow")]
[assembly: SuppressMessage(
    "Usage",
    "CA1515:Consider making public types internal",
    Justification = "WPF requires App, windows, and UserControls referenced from XAML to be public.",
    Scope = "type",
    Target = "~T:VideoAbridge.Controls.TimelineControl")]
[assembly: SuppressMessage(
    "Usage",
    "CA1515:Consider making public types internal",
    Justification = "Exposed by TimelineControl dependency property / public events required by WPF XAML.",
    Scope = "type",
    Target = "~T:VideoAbridge.ViewModels.MarkerThumbViewModel")]
[assembly: SuppressMessage(
    "Usage",
    "CA1515:Consider making public types internal",
    Justification = "Bound from MainWindow version tab DataTemplate.",
    Scope = "type",
    Target = "~T:VideoAbridge.ViewModels.VersionTabViewModel")]
[assembly: SuppressMessage(
    "Usage",
    "CA1515:Consider making public types internal",
    Justification = "WPF requires windows referenced from XAML to be public.",
    Scope = "type",
    Target = "~T:VideoAbridge.NamePromptDialog")]
[assembly: SuppressMessage(
    "Usage",
    "CA1515:Consider making public types internal",
    Justification = "WPF requires windows referenced from XAML to be public.",
    Scope = "type",
    Target = "~T:VideoAbridge.KeyboardShortcutsDialog")]
[assembly: SuppressMessage(
    "Usage",
    "CA1515:Consider making public types internal",
    Justification = "Public EventArgs for TimelineControl events required by WPF public API surface.",
    Scope = "type",
    Target = "~T:VideoAbridge.Controls.TimeSpanEventArgs")]
[assembly: SuppressMessage(
    "Usage",
    "CA1515:Consider making public types internal",
    Justification = "Public EventArgs for TimelineControl events required by WPF public API surface.",
    Scope = "type",
    Target = "~T:VideoAbridge.Controls.TimelineItemIdEventArgs")]
