using System.IO;
using FluentAssertions;
using Microsoft.Win32;
using VideoAbridge.Services;

namespace VideoAbridge.Tests;

public class ExplorerContextMenuTests
{
    [Fact]
    public void BuildCommandQuotesExecutableAndPassesFileArgument()
    {
        using var dir = new TempDir();
        var exe = Path.Combine(dir.Path, "VideoAbridge.exe");
        File.WriteAllBytes(exe, [0]);

        ExplorerContextMenu.BuildCommand(exe).Should().Be($"\"{Path.GetFullPath(exe)}\" \"%1\"");
    }

    [Fact]
    public void WriteVerbsRegistersLabelIconAndCommandForEachExtension()
    {
        using var dir = new TempDir();
        var exe = Path.Combine(dir.Path, "VideoAbridge.exe");
        File.WriteAllBytes(exe, [0]);

        using var hive = new TempClassesHive();
        ExplorerContextMenu.WriteVerbs(hive.Classes, exe);

        ExplorerContextMenu.HasVerb(hive.Classes).Should().BeTrue();
        ExplorerContextMenu.IsInstalled(hive.Classes, machineClasses: null).Should().BeTrue();

        foreach (var extension in ExplorerContextMenu.VideoExtensions)
        {
            using var verb = hive.Classes.OpenSubKey(
                $@"SystemFileAssociations\{extension}\shell\{ExplorerContextMenu.VerbName}");
            verb.Should().NotBeNull($"missing verb for {extension}");
            verb.GetValue(null).Should().Be(ExplorerContextMenu.MenuLabel);
            verb.GetValue("Icon").Should().Be(Path.GetFullPath(exe));

            using var command = verb.OpenSubKey("command");
            command.Should().NotBeNull();
            command.GetValue(null).Should().Be(ExplorerContextMenu.BuildCommand(exe));
        }
    }

    [Fact]
    public void DeleteVerbsRemovesRegistration()
    {
        using var dir = new TempDir();
        var exe = Path.Combine(dir.Path, "VideoAbridge.exe");
        File.WriteAllBytes(exe, [0]);

        using var hive = new TempClassesHive();
        ExplorerContextMenu.WriteVerbs(hive.Classes, exe);
        ExplorerContextMenu.DeleteVerbs(hive.Classes);

        ExplorerContextMenu.HasVerb(hive.Classes).Should().BeFalse();
        ExplorerContextMenu.IsInstalled(hive.Classes, machineClasses: null).Should().BeFalse();
    }

    [Fact]
    public void IsInstalledTrueWhenOnlyMachineHiveHasVerb()
    {
        using var dir = new TempDir();
        var exe = Path.Combine(dir.Path, "VideoAbridge.exe");
        File.WriteAllBytes(exe, [0]);

        using var user = new TempClassesHive();
        using var machine = new TempClassesHive();
        ExplorerContextMenu.WriteVerbs(machine.Classes, exe);

        ExplorerContextMenu.IsInstalled(user.Classes, machine.Classes).Should().BeTrue();
        ExplorerContextMenu.IsInstalled(user.Classes, machineClasses: null).Should().BeFalse();
    }

    private sealed class TempClassesHive : IDisposable
    {
        private readonly string _rootPath;

        public TempClassesHive()
        {
            _rootPath = @"Software\VideoAbridge\TestClasses\" + Guid.NewGuid().ToString("N");
            Classes = Registry.CurrentUser.CreateSubKey(_rootPath)
                ?? throw new InvalidOperationException("Could not create test registry hive.");
        }

        public RegistryKey Classes { get; }

        public void Dispose()
        {
            Classes.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(_rootPath, throwOnMissingSubKey: false);
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "VideoAbridgeTests-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup for temp test dirs.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup for temp test dirs.
            }
        }
    }
}
