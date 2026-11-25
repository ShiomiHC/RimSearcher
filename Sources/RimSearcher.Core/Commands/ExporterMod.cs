using System.Diagnostics;
using RimSearcher.Cli;
using RimSearcher.Config;
using RimSearcher.Contract;
using RimTooling.Cli;
using RimTooling.RimWorld;

namespace RimSearcher.Commands;

public static class ExporterMod
{
    public const string Tool = "rimsearcher";

    public const string AssemblyFileName = "RimSearcher.DataMod.dll";

    public const string EntryNamePrefix = "RimSearcherExporter.";

    public const string StagingDirName = "RimSearcherStaging";

    private const string SupportedVersion = "1.7";

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(60);

    public static string InstalledAssemblyPath => Path.Combine(AppContext.BaseDirectory, AssemblyFileName);

    public static string ContentRoot(RimConfig config)
        => Path.Combine(Path.GetDirectoryName(config.Path) is { Length: > 0 } dir ? dir : ".", "synth");

    public static SynthModLayout Layout(RimConfig config, string gameDir)
    {
        var game = Path.GetFullPath(gameDir);
        return new SynthModLayout
        {
            Tool = Tool,
            ModsDir = Path.Combine(game, "Mods"),
            ContentRoot = Path.GetFullPath(ContentRoot(config)),
            StagingRoot = Path.Combine(game, StagingDirName),
        };
    }

    public static Func<string, SynthOwnership> Ownership()
    {
        using var self = Process.GetCurrentProcess();
        var names = new[] { self.ProcessName, "rimsearcher", "dotnet" }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return SynthModOwners.ByLiveProcess(names);
    }

    public static string RequireAssembly(string? assemblyPath = null)
    {
        var dll = assemblyPath ?? InstalledAssemblyPath;
        if (!File.Exists(dll))
            throw new CliUsageException(
                $"导出器程序集 {dll} 不存在，因此游戏无法写出导出文件。" +
                "该文件随本工具一同发布；重新安装本工具。游戏未启动。");
        return dll;
    }

    public static string NewPackageId()
        => IntermediateFormat.ExporterPackageId + ".x" + Guid.NewGuid().ToString("N")[..8];

    public static string AboutXml(string packageId) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <ModMetaData>
          <packageId>{packageId}</packageId>
          <name>RimSearcher Exporter</name>
          <author>RimSearcher</author>
          <description>Exports every def the game has loaded, including the ones generated in code, to a file that the rimsearcher CLI turns into a searchable snapshot. Adds no gameplay behaviour: it only reads what is already in memory. Synthesized by 'rimsearcher export' for one run and removed afterwards.</description>
          <supportedVersions>
            <li>{SupportedVersion}</li>
          </supportedVersions>
        </ModMetaData>

        """;

    public static Attachment Attach(RimConfig config, string gameDir, string packageId, string? assemblyPath = null)
    {
        var dll = RequireAssembly(assemblyPath);
        var layout = Layout(config, gameDir);
        if (!Directory.Exists(layout.ModsDir))
            throw new CliUsageException(
                $"{layout.ModsDir} 不存在，因此没有可放入导出器的位置。game_dir 应为游戏的安装目录。游戏未启动。");

        var owner = SynthModOwners.OfCurrentProcess();
        var token = packageId[(packageId.LastIndexOf('.') + 1)..];
        try
        {
            using (SynthModLock.Acquire(layout.StagingRoot, LockTimeout))
            {
                SynthMod.Sweep(layout, Ownership());
                var install = SynthMod.Install(layout, new SynthModSpec
                {
                    Owner = owner,
                    EntryName = EntryNamePrefix + token,
                    AboutXml = AboutXml(packageId),
                    Files = [new SynthModFile("Assemblies/" + AssemblyFileName, dll)],
                });
                return new Attachment(layout, owner, packageId, install.EntryPath);
            }
        }
        catch (SynthModException ex)
        {
            throw new CliUsageException($"无法把导出器放入 {layout.ModsDir}：{ex.Message}。游戏未启动。");
        }
    }

    public sealed class Attachment : IDisposable
    {
        private readonly SynthModLayout _layout;
        private readonly string _owner;
        private bool _done;

        internal Attachment(SynthModLayout layout, string owner, string packageId, string entryPath)
        {
            _layout = layout;
            _owner = owner;
            PackageId = packageId;
            EntryPath = entryPath;
        }

        public string PackageId { get; }

        public string EntryPath { get; }

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            try
            {
                using (SynthModLock.Acquire(_layout.StagingRoot, LockTimeout))
                {
                    SynthMod.Remove(_layout, EntryPath);
                    SynthMod.DeleteContent(_layout, _owner);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
