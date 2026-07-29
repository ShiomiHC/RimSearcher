using System;
using System.IO;
using RimSearcher.Contract;
using UnityEngine;
using Verse;

namespace RimSearcher.DataMod
{
    /// <summary>
    /// 两个触发入口共用同一个导出核心(06 层 1「触发入口」):
    ///
    ///   1. 设置页按钮 —— 人在场时用;
    ///   2. 命令行 <c>-rimsearcher-export=&lt;path&gt;</c> —— CLI 编排用,导完就退出。
    ///
    /// 时机是 <see cref="StaticConstructorOnStartup"/>:此时 DefGenerator 的两批 ImpliedDefs
    /// 都已生成(00 论据 1),PatchOperation 也早已应用完 —— 这正是运行时导出相对静态方案的
    /// 全部优势所在的那一刻。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class UnattendedExport
    {
        static UnattendedExport()
        {
            string target;
            if (!GenCommandLine.TryGetCommandLineArg(IntermediateFormat.CommandLineSwitch, out target))
                return;
            if (string.IsNullOrEmpty(target))
            {
                Log.Error("[RimSearcher] -" + IntermediateFormat.CommandLineSwitch + " was passed without a path.");
                Root.Shutdown();
                return;
            }

            try
            {
                var written = DefExporter.Export(target);
                Log.Message("[RimSearcher] exported to " + written);
            }
            catch (Exception ex)
            {
                // 失败也要退出:无人值守分支挂在这里不退,编排就只能等超时。
                Log.Error("[RimSearcher] export failed: " + ex);
            }
            finally
            {
                Root.Shutdown();
            }
        }
    }

    public sealed class RimSearcherSettings : ModSettings
    {
        public string exportPath = "";

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref exportPath, "exportPath", "");
        }
    }

    public sealed class RimSearcherMod : Mod
    {
        private readonly RimSearcherSettings _settings;
        private string _status = "";

        public RimSearcherMod(ModContentPack content) : base(content)
        {
            _settings = GetSettings<RimSearcherSettings>();
        }

        public override string SettingsCategory() => "RimSearcher";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            listing.Label("Export every def the game has loaded, including the ones generated in code.");
            listing.Gap();

            listing.Label("Destination file:");
            _settings.exportPath = listing.TextEntry(
                string.IsNullOrEmpty(_settings.exportPath) ? DefaultPath() : _settings.exportPath);

            listing.Gap();
            if (listing.ButtonText("Export now"))
            {
                try
                {
                    var path = string.IsNullOrEmpty(_settings.exportPath) ? DefaultPath() : _settings.exportPath;
                    var written = DefExporter.Export(path);
                    _status = "Written: " + written;
                }
                catch (Exception ex)
                {
                    _status = "Failed: " + ex.Message;
                    Log.Error("[RimSearcher] export failed: " + ex);
                }
            }

            if (!string.IsNullOrEmpty(_status))
            {
                listing.Gap();
                listing.Label(_status);
            }

            listing.End();
        }

        private static string DefaultPath()
            => Path.Combine(GenFilePaths.SaveDataFolderPath, "RimSearcher" + IntermediateFormat.FileExtension);
    }
}
