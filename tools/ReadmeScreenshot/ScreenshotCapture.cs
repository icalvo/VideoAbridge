using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Exceptions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using FlaUIApplication = FlaUI.Core.Application;

namespace ReadmeScreenshot;

/// <summary>Launches VideoAbridge and captures the main window.</summary>
internal static class ScreenshotCapture
{
    public static void Capture(
        string appPath,
        string videoPath,
        string cacheDirectory,
        string outputPath,
        TimeSpan settleDelay)
    {
        var outputDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        var startInfo = new ProcessStartInfo
        {
            FileName = appPath,
            Arguments = $"\"{videoPath}\"",
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(appPath) ?? Environment.CurrentDirectory
        };
        startInfo.Environment["VIDEOABRIDGE_MARKER_CACHE"] = cacheDirectory;
        startInfo.Environment["VIDEOABRIDGE_SKIP_SCENE_DETECT"] = "1";

        using var automation = new UIA3Automation();
        using var app = FlaUIApplication.Launch(startInfo);
        try
        {
            var window = Retry.WhileNull(
                    () =>
                    {
                        var main = app.GetMainWindow(automation, TimeSpan.FromSeconds(2));
                        return IsMainWindow(main) ? main : null;
                    },
                    TimeSpan.FromSeconds(60),
                    TimeSpan.FromMilliseconds(250),
                    throwOnTimeout: true,
                    ignoreException: true,
                    timeoutMessage: "Timed out waiting for VideoAbridge MainWindow.")
                .Result
                ?? throw new InvalidOperationException("MainWindow was not found.");

            window.SetForeground();
            Thread.Sleep(settleDelay);

            FlaUI.Core.Capturing.Capture.Element(window).ToFile(outputPath);
        }
        finally
        {
            if (!app.HasExited)
                app.Close();

            if (!app.HasExited)
            {
                Thread.Sleep(500);
                if (!app.HasExited)
                    app.Kill();
            }
        }
    }

    private static bool IsMainWindow(Window? main)
    {
        if (main is null)
            return false;

        try
        {
            if (main.Properties.AutomationId.TryGetValue(out var automationId)
                && automationId == "MainWindow")
            {
                return true;
            }
        }
        catch (PropertyNotSupportedException)
        {
            // Fall back to title.
        }

        try
        {
            return main.Title.Contains("VideoAbridge", StringComparison.Ordinal);
        }
        catch (PropertyNotSupportedException)
        {
            return false;
        }
    }
}
