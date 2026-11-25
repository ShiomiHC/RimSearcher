using System;
using System.Collections.Generic;
using System.Xml;
using RimSearcher.Contract;
using Verse;

namespace RimSearcher.DataMod
{
    internal static class PatchedXml
    {
        private static bool _loaded;
        private static List<PatchedXmlNodes.Node> _nodes;
        private static List<PatchedXmlNodes.InheritNode> _inheritance;
        private static string _route = IntermediateFormat.PatchRouteNone;

        public static string Route { get { EnsureLoaded(); return _route; } }

        public static List<PatchedXmlNodes.Node> Nodes { get { EnsureLoaded(); return _nodes; } }

        public static List<PatchedXmlNodes.InheritNode> Inheritance { get { EnsureLoaded(); return _inheritance; } }

        public static void Capture(XmlDocument doc, Dictionary<XmlNode, LoadableXmlAsset> lookup)
        {
            if (_loaded || doc == null) return;
            try
            {
                Read(doc, lookup);
                _route = IntermediateFormat.PatchRouteHarmony;
                _loaded = true;
            }
            catch (Exception ex)
            {
                Log.Warning("[RimSearcher] could not read the patched XML: " + ex.Message);
            }
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                var assets = LoadedModManager.LoadModXML(true);
                var lookup = new Dictionary<XmlNode, LoadableXmlAsset>();
                var doc = LoadedModManager.CombineIntoUnifiedXML(assets, lookup);
                LoadedModManager.ApplyPatches(doc, lookup);
                Read(doc, lookup);
                _route = IntermediateFormat.PatchRouteReplay;
            }
            catch (Exception ex)
            {
                Log.Warning("[RimSearcher] could not rebuild the patched XML: " + ex.Message);
                _nodes = null;
                _inheritance = null;
                _route = IntermediateFormat.PatchRouteNone;
            }
        }

        private static void Read(XmlDocument doc, Dictionary<XmlNode, LoadableXmlAsset> lookup)
        {
            var shadowed = PatchedXmlNodes.Shadowed(doc, RankOf(lookup));
            _nodes = PatchedXmlNodes.Extract(doc, DefExporter.Limits.MaxInstanceFieldDepth,
                                             DefExporter.Limits.MaxCollectionItems, shadowed, OriginOf(lookup));
            _inheritance = PatchedXmlNodes.ExtractInheritance(doc, OriginOf(lookup), shadowed);
        }

        private static Func<XmlNode, KeyValuePair<string, string>?> OriginOf(Dictionary<XmlNode, LoadableXmlAsset> lookup)
            => node =>
            {
                LoadableXmlAsset asset;
                if (lookup == null || !lookup.TryGetValue(node, out asset) || asset == null)
                    return null;
                return new KeyValuePair<string, string>(asset.mod == null ? "" : asset.mod.PackageId,
                                                        asset.name ?? "");
            };

        private static Func<XmlNode, int?> RankOf(Dictionary<XmlNode, LoadableXmlAsset> lookup)
        {
            if (lookup == null) return null;
            var mods = LoadedModManager.RunningModsListForReading;
            var rank = new Dictionary<ModContentPack, int>();
            var ordered = new List<ModContentPack>(mods);
            ordered.Sort((a, b) =>
            {
                var byPriority = a.OverwritePriority.CompareTo(b.OverwritePriority);
                return byPriority != 0 ? byPriority : mods.IndexOf(a).CompareTo(mods.IndexOf(b));
            });
            for (var i = 0; i < ordered.Count; i++) rank[ordered[i]] = i;

            return node =>
            {
                LoadableXmlAsset asset;
                int r;
                if (!lookup.TryGetValue(node, out asset) || asset == null || asset.mod == null
                    || !rank.TryGetValue(asset.mod, out r))
                    return null;
                return r;
            };
        }
    }
}
