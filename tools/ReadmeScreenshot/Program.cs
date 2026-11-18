namespace ReadmeScreenshot;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var options = CliOptions.Parse(args);
            await HeroEditSeeder.SeedAsync(options.VideoPath, options.CacheDirectory, CancellationToken.None)
                .ConfigureAwait(false);
            ScreenshotCapture.Capture(
                options.AppPath,
                options.VideoPath,
                options.CacheDirectory,
                options.OutputPath,
                options.SettleDelay);
            await Console.Out.WriteLineAsync($"Wrote {options.OutputPath}").ConfigureAwait(false);
            return 0;
        }
        catch (ArgumentException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }
        catch (InvalidOperationException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }
        catch (IOException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }
        catch (TimeoutException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }
        catch (FlaUI.Core.Exceptions.FlaUIException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }
    }
}
