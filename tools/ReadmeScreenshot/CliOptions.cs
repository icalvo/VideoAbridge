namespace ReadmeScreenshot;

internal sealed class CliOptions
{
    private CliOptions(
        string appPath,
        string videoPath,
        string cacheDirectory,
        string outputPath,
        TimeSpan settleDelay)
    {
        AppPath = appPath;
        VideoPath = videoPath;
        CacheDirectory = cacheDirectory;
        OutputPath = outputPath;
        SettleDelay = settleDelay;
    }

    public string AppPath { get; }

    public string VideoPath { get; }

    public string CacheDirectory { get; }

    public string OutputPath { get; }

    public TimeSpan SettleDelay { get; }

    public static CliOptions Parse(string[] args)
    {
        string? appPath = null;
        string? videoPath = null;
        string? cacheDirectory = null;
        string? outputPath = null;
        var settleMs = 2500;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string Next()
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException($"Missing value for {arg}.");
                return args[++i];
            }

            switch (arg)
            {
                case "--app":
                    appPath = Next();
                    break;
                case "--video":
                    videoPath = Next();
                    break;
                case "--cache":
                    cacheDirectory = Next();
                    break;
                case "--output":
                    outputPath = Next();
                    break;
                case "--settle-ms":
                    settleMs = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {arg}");
            }
        }

        if (string.IsNullOrWhiteSpace(appPath) || !File.Exists(appPath))
            throw new ArgumentException("--app must point to VideoAbridge.exe.");
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            throw new ArgumentException("--video must point to an existing sample MP4.");
        if (string.IsNullOrWhiteSpace(cacheDirectory))
            throw new ArgumentException("--cache is required.");
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("--output is required.");

        return new CliOptions(
            Path.GetFullPath(appPath),
            Path.GetFullPath(videoPath),
            Path.GetFullPath(cacheDirectory),
            Path.GetFullPath(outputPath),
            TimeSpan.FromMilliseconds(settleMs));
    }
}
