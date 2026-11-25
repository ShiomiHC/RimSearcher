using System;
using System.IO;
using RimSearcher.Contract;
using Verse;

namespace RimSearcher.DataMod
{
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
            catch {  }
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

            Progress.Report(IntermediateFormat.StageExporting);

            if (string.IsNullOrEmpty(target))
            {
                Log.Error("[RimSearcher] -" + IntermediateFormat.CommandLineSwitch + " was passed without a path.");
                Root.Shutdown();
                return;
            }

            try
            {
                var written = DefExporter.Export(
                    target, GenCommandLine.CommandLineArgPassed(IntermediateFormat.SkipEconomySwitch));
                Log.Message("[RimSearcher] exported to " + written);
            }
            catch (Exception ex)
            {
                Log.Error("[RimSearcher] export failed: " + ex);
            }
            finally
            {
                Root.Shutdown();
            }
        }
    }

    public sealed class RimSearcherMod : Mod
    {
        public RimSearcherMod(ModContentPack content) : base(content)
        {
            Progress.Report(IntermediateFormat.StageModClasses);
            TryHookPatches();
        }

        private static void TryHookPatches()
        {
            string target;
            if (!GenCommandLine.TryGetCommandLineArg(IntermediateFormat.CommandLineSwitch, out target)) return;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "0Harmony") continue;
                try { PatchHook.Install(); }
                catch (Exception ex) { Log.Warning("[RimSearcher] could not hook ApplyPatches: " + ex.Message); }
                return;
            }
        }
    }
}
