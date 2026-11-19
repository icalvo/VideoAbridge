using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace VideoAbridge.FFmpeg;

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        Action<string>? onStdErrLine = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start())
            throw new InvalidOperationException($"Failed to start {fileName}.");

        using var killOnCancel = cancellationToken.Register(() => TryKill(process));

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = new StringBuilder();
        var stderrTask = ReadLinesAsync(
            process.StandardError,
            stderr,
            onStdErrLine,
            cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        await stderrTask.ConfigureAwait(false);

        return new ProcessResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdout,
            StandardError = stderr.ToString()
        };
    }

    /// <summary>Runs a process and captures stdout as raw bytes (for PCM / binary pipes).</summary>
    public static async Task<BinaryProcessResult> RunBinaryStdoutAsync(
        string fileName,
        IEnumerable<string> arguments,
        Action<string>? onStdErrLine = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start())
            throw new InvalidOperationException($"Failed to start {fileName}.");

        using var killOnCancel = cancellationToken.Register(() => TryKill(process));

        var stdoutTask = ReadAllBytesAsync(process.StandardOutput.BaseStream, cancellationToken);
        var stderr = new StringBuilder();
        var stderrTask = ReadLinesAsync(
            process.StandardError,
            stderr,
            onStdErrLine,
            cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        await stderrTask.ConfigureAwait(false);

        return new BinaryProcessResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdout,
            StandardError = stderr.ToString()
        };
    }

    /// <summary>
    /// Reads a text stream, emitting callbacks for each line. Splits on both LF and CR so ffmpeg
    /// carriage-return progress updates are drained (BeginErrorReadLine only splits on LF and can stall).
    /// </summary>
    private static async Task ReadLinesAsync(
        StreamReader reader,
        StringBuilder capture,
        Action<string>? onLine,
        CancellationToken cancellationToken)
    {
        var buffer = new char[1024];
        var line = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;

            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (c is '\r' or '\n')
                {
                    if (line.Length == 0)
                        continue;

                    var text = line.ToString();
                    line.Clear();
                    capture.AppendLine(text);
                    onLine?.Invoke(text);
                }
                else
                {
                    line.Append(c);
                }
            }
        }

        if (line.Length > 0)
        {
            var text = line.ToString();
            capture.AppendLine(text);
            onLine?.Invoke(text);
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Best-effort cleanup when cancelling.
        }
        catch (NotSupportedException)
        {
            // Best-effort cleanup when cancelling.
        }
        catch (Win32Exception)
        {
            // Best-effort cleanup when cancelling.
        }
    }
}
