using System.Xml;
using HarmonyLib;
using Verse;

namespace RimSearcher.DataMod
{
    /// <summary>
    /// Harmony 路线的**全部** Harmony 类型都关在这个类里。
    ///
    /// 这不是洁癖:CLR 到首次用上某个类型时才去解析它所在的程序集,所以只要
    /// <c>HarmonyLib</c> 不出现在别处,没装 Harmony 的环境下这个类一次都不加载,
    /// 什么都不会炸。<c>0Harmony.dll</c> 于是既不必自带,也不成为 modlist 的前置。
    /// 谁在别的文件里写下一个 Harmony 类型,这条保证当场作废。
    ///
    /// 装载时机成立:<c>LoadAllActiveMods</c> 里 <c>CreateModClasses()</c>(61 行)早于
    /// <c>ApplyPatches</c>(116 行),而 <c>Mod</c> 子类的构造函数就跑在前者里。
    /// </summary>
    internal static class PatchHook
    {
        public static void Install()
        {
            var harmony = new Harmony("com.rimsearcher.datamod");
            var target = AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.ApplyPatches));
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(PatchHook), nameof(After)));
        }

        /// <summary>参数名必须与 <c>ApplyPatches(XmlDocument xmlDoc, …)</c> 逐字一致 ——
        /// Harmony 按名字注入,拼错了不报错,只是永远拿不到值。</summary>
        private static void After(XmlDocument xmlDoc)
        {
            PatchedXml.Capture(xmlDoc);
        }
    }
}
