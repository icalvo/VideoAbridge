namespace VideoAbridge.FFmpeg;

public static class FFmpegLocator
{
    public static string? FindFFmpeg() => FindTool("ffmpeg.exe", "ffmpeg");

    public static string? FindFFprobe() => FindTool("ffprobe.exe", "ffprobe");

    private static string? FindTool(string windowsName, string unixName)
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "ffmpeg", windowsName),
            Path.Combine(baseDir, "tools", "ffmpeg", windowsName),
            Path.Combine(baseDir, windowsName),
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var win = Path.Combine(dir, windowsName);
            if (File.Exists(win))
                return win;

            var unix = Path.Combine(dir, unixName);
            if (File.Exists(unix))
                return unix;
        }

        return null;
    }
}
