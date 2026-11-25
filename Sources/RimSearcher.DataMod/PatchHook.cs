using System.Collections.Generic;
using System.Xml;
using HarmonyLib;
using Verse;

namespace RimSearcher.DataMod
{
    internal static class PatchHook
    {
        public static void Install()
        {
            var harmony = new Harmony("com.rimsearcher.datamod");
            var target = AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.ApplyPatches));
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(PatchHook), nameof(After)));
        }

        private static void After(XmlDocument xmlDoc, Dictionary<XmlNode, LoadableXmlAsset> assetlookup)
        {
            PatchedXml.Capture(xmlDoc, assetlookup);
        }
    }
}
