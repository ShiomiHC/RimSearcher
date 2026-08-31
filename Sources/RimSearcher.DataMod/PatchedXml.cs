using System;
using System.Collections.Generic;
using System.Xml;
using RimSearcher.Contract;
using Verse;

namespace RimSearcher.DataMod
{
    /// <summary>
    /// 打补丁**之后**的那份合并 XML 从哪来。抽取本身在 <see cref="PatchedXmlNodes"/>,
    /// 这里只管路线。
    ///
    /// 为什么要它:<see cref="XmlNodeExporter"/> 逐 mod 重读 <c>Defs/</c>,拿到的是磁盘上的
    /// 原文 —— PatchOperation 一条都没跑过。于是别的 mod 用 <c>PatchOperationAdd</c> 加进来的
    /// 一行,在 <c>xml</c> 列上与「谁都没写过」逐字同形,而那一列的出路(Replace 还是 Add)
    /// 正相反。
    ///
    /// 两条路线产出同形,运行时按 Harmony 在不在挑,走了哪条记在导出元数据里:
    ///
    ///   - <b>Harmony</b>:Postfix 抄一份游戏真正用过的文档。精确,近乎零耗时。
    ///     Harmony 类型全关在 <see cref="PatchHook"/> 里,没有 Harmony 的环境下 CLR 一次
    ///     都不解析它。
    ///   - <b>重放</b>:<c>LoadModXML</c> / <c>CombineIntoUnifiedXML</c> / <c>ApplyPatches</c>
    ///     三个都是 public static,自己再拼一遍。不带依赖,代价是耗时,以及「有状态的 patch
    ///     应当一致」这个「应当」。
    ///
    /// 补丁对象拿得到,尽管加载末尾清过一次:<c>ClearPatchesCache()</c> 只是把
    /// <c>ModContentPack.patches</c> 置空,而 <c>Patches</c> 属性会重新从 <c>Patches/</c> 解析。
    /// </summary>
    internal static class PatchedXml
    {
        private static bool _loaded;
        private static List<PatchedXmlNodes.Node> _nodes;
        private static string _route = IntermediateFormat.PatchRouteNone;

        /// <summary>走的是哪条路线。读它会**先**把那份文档弄到手 —— 元数据行写在最前面,
        /// 而路线要到拿到文档才知道。</summary>
        public static string Route { get { EnsureLoaded(); return _route; } }

        /// <summary>拿不到那份文档时是 <c>null</c>。<b>不是空表</b> —— 空表会让每一条
        /// 原文写过的路径都变成「被补丁删掉了」。</summary>
        public static List<PatchedXmlNodes.Node> Nodes { get { EnsureLoaded(); return _nodes; } }

        /// <summary>Harmony 的 Postfix 调这个。文档太大,当场抽完就放手,不留引用。</summary>
        public static void Capture(XmlDocument doc)
        {
            if (_loaded || doc == null) return;
            try
            {
                _nodes = Extract(doc);
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
                // hotReload: true —— ModContentPack.LoadDefs 开头那句
                // `if (!hotReload && defs.Count != 0) Log.ErrorOnce(…)` 在导出时点必然成立
                // (def 早加载完了),走默认的 false 会让每次导出都吐一条 Log.Error。
                // 那个参数在 LoadDefs 里只用于这个检查。
                // LoadDefs 只是读盘再 yield,不碰 ModContentPack.defs —— 重放对游戏状态是
                // 只读的。唯一的副作用:访问 Patches 会把加载末尾清掉的那份缓存重新解析回来,
                // 于是进程里多留一批 PatchOperation 对象。无人值守导出跑完就退出;设置页那条
                // 路上它是几 MB 的常驻,不影响正确性。
                var assets = LoadedModManager.LoadModXML(true);
                var lookup = new Dictionary<XmlNode, LoadableXmlAsset>();
                var doc = LoadedModManager.CombineIntoUnifiedXML(assets, lookup);
                // 不调 ErrorCheckPatches():它把每条 patch 的 ConfigErrors 打成 Log.Error,
                // 游戏启动时已经打过一遍。这里只要 ApplyPatches 本身。
                LoadedModManager.ApplyPatches(doc, lookup);
                _nodes = Extract(doc);
                _route = IntermediateFormat.PatchRouteReplay;
            }
            catch (Exception ex)
            {
                // 拿不到就宣布缺这一层,不是让整次导出失败 —— 也不是拿原文冒充打完补丁的。
                Log.Warning("[RimSearcher] could not rebuild the patched XML: " + ex.Message);
                _nodes = null;
                _route = IntermediateFormat.PatchRouteNone;
            }
        }

        private static List<PatchedXmlNodes.Node> Extract(XmlDocument doc)
            => PatchedXmlNodes.Extract(doc, DefExporter.Limits.MaxFieldDepth,
                                       DefExporter.Limits.MaxCollectionItems);
    }
}
