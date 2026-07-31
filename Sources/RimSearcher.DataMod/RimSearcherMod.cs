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
    /// <summary>
    /// 进度回报。编排侧判「卡住了」全靠这个文件。
    ///
    /// 为什么不留给编排侧去猜:无头跑时游戏若在读定义**之前**弹一个点不掉的对话框
    /// (缺前置、循环依赖、版本警告),进程活着不动,从外面看跟「正在慢慢加载」一模一样。
    /// 拿 CPU 占用当判据是代理指标,而代理会撒谎。这里由游戏侧直说到了哪一步。
    ///
    /// 写不出去就算了 —— 进度回报失败不该让一次导出失败。
    /// </summary>
    internal static class Progress
    {
        public static void Report(string stage)
        {
            string target;
            if (!GenCommandLine.TryGetCommandLineArg(IntermediateFormat.CommandLineSwitch, out target)) return;
            if (string.IsNullOrEmpty(target)) return;
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(target));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(target + IntermediateFormat.ProgressFileSuffix, stage);
            }
            catch { /* 报不了进度不是失败 */ }
        }
    }

    [StaticConstructorOnStartup]
    public static class UnattendedExport
    {
        static UnattendedExport()
        {
            string target;
            if (!GenCommandLine.TryGetCommandLineArg(IntermediateFormat.CommandLineSwitch, out target))
                return;

            // 到这一步说明定义已经全部就位 —— 中间那段「加载定义」是对话框最容易卡住的地方,
            // 走过去了编排侧就该换一套耐心:后面再慢是真在写数据。
            Progress.Report(IntermediateFormat.StageExporting);

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
            // 这个构造函数跑在 LoadedModManager.CreateModClasses(),而那在 LoadModXML() 之前 ——
            // 也就是**读定义之前**。编排侧要的正是这个分界点:停在这一步说明连定义都没开始读,
            // 而那正是缺前置那类对话框卡住的位置;走过去了才轮到导出本身。
            Progress.Report(IntermediateFormat.StageModClasses);
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
