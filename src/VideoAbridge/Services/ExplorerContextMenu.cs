using System.IO;
using System.Security;
using Microsoft.Win32;

namespace VideoAbridge.Services;

/// <summary>
/// File Explorer "Abridge..." context menu for supported video extensions
/// under <c>Software\Classes\SystemFileAssociations</c>.
/// </summary>
internal static class ExplorerContextMenu
{
    internal const string VerbName = "VideoAbridge";
    internal const string MenuLabel = "Abridge...";

    /// <summary>Chocolatey package parameter that skips registering the context menu.</summary>
    internal const string NoExplorerContextMenuParameter = "NoExplorerContextMenu";

    internal static readonly string[] VideoExtensions =
        [".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm"];

    internal static bool IsInstalled()
    {
        using var user = TryOpenClasses(Registry.CurrentUser, writable: false);
        using var machine = TryOpenClasses(Registry.LocalMachine, writable: false);
        return IsInstalled(user, machine);
    }

    internal static bool IsInstalled(RegistryKey? userClasses, RegistryKey? machineClasses) =>
        HasVerb(userClasses) || HasVerb(machineClasses);

    /// <summary>Registers the menu for the current user (no elevation).</summary>
    /// <returns>Null on success; otherwise an error message.</returns>
    internal static string? InstallForCurrentUser(string executablePath)
    {
        try
        {
            using var user = Registry.CurrentUser.CreateSubKey(@"Software\Classes")
                ?? throw new InvalidOperationException("Could not open HKCU\\Software\\Classes.");
            WriteVerbs(user, executablePath);
            return null;
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            return ex.Message;
        }
    }

    /// <summary>Removes the menu from the current user and, when permitted, all users.</summary>
    /// <returns>Null on success; otherwise an error message.</returns>
    internal static string? Uninstall()
    {
        var errors = new List<string>();

        try
        {
            using var user = TryOpenClasses(Registry.CurrentUser, writable: true);
            if (user is not null)
                DeleteVerbs(user);
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            errors.Add(ex.Message);
        }

        try
        {
            using var machineWritable = TryOpenClasses(Registry.LocalMachine, writable: true);
            if (machineWritable is not null)
            {
                DeleteVerbs(machineWritable);
            }
            else
            {
                using var machineReadable = TryOpenClasses(Registry.LocalMachine, writable: false);
                if (HasVerb(machineReadable))
                {
                    errors.Add(
                        "The File Explorer context menu is installed for all users. "
                        + "Run VideoAbridge as administrator to remove it, or uninstall the Chocolatey package.");
                }
            }
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            errors.Add(
                "The File Explorer context menu is installed for all users. "
                + "Run VideoAbridge as administrator to remove it, or uninstall the Chocolatey package.");
        }

        return errors.Count == 0 ? null : string.Join(Environment.NewLine, errors);
    }

    internal static void WriteVerbs(RegistryKey classesRoot, string executablePath)
    {
        ArgumentNullException.ThrowIfNull(classesRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        var fullExe = Path.GetFullPath(executablePath);
        if (!File.Exists(fullExe))
            throw new FileNotFoundException("VideoAbridge executable not found.", fullExe);

        var command = BuildCommand(fullExe);
        foreach (var extension in VideoExtensions)
        {
            var verbPath = $@"SystemFileAssociations\{extension}\shell\{VerbName}";
            using var verb = classesRoot.CreateSubKey(verbPath)
                ?? throw new InvalidOperationException($"Could not create registry key '{verbPath}'.");
            verb.SetValue(null, MenuLabel);
            verb.SetValue("Icon", fullExe);
            using var cmd = verb.CreateSubKey("command")
                ?? throw new InvalidOperationException($"Could not create registry key '{verbPath}\\command'.");
            cmd.SetValue(null, command);
        }
    }

    internal static void DeleteVerbs(RegistryKey classesRoot)
    {
        ArgumentNullException.ThrowIfNull(classesRoot);

        foreach (var extension in VideoExtensions)
        {
            var shellPath = $@"SystemFileAssociations\{extension}\shell";
            using var shell = classesRoot.OpenSubKey(shellPath, writable: true);
            shell?.DeleteSubKeyTree(VerbName, throwOnMissingSubKey: false);
        }
    }

    internal static string BuildCommand(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var fullExe = Path.GetFullPath(executablePath);
        return $"\"{fullExe}\" \"%1\"";
    }

    internal static bool HasVerb(RegistryKey? classesRoot)
    {
        if (classesRoot is null)
            return false;

        // All extensions are written together; .mp4 is the canary.
        using var key = classesRoot.OpenSubKey(
            $@"SystemFileAssociations\.mp4\shell\{VerbName}");
        return key is not null;
    }

    private static RegistryKey? TryOpenClasses(RegistryKey hive, bool writable)
    {
        try
        {
            return hive.OpenSubKey(@"Software\Classes", writable);
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            return null;
        }
    }

    private static bool IsRegistryFailure(Exception ex) =>
        ex is UnauthorizedAccessException
            or SecurityException
            or IOException
            or ObjectDisposedException
            or InvalidOperationException;
}
