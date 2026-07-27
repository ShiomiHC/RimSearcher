namespace RimSearcher.Server.Tools;

// 结果行的共享渲染判据。locate 与 trace 各自列「符号 + 它在哪个文件」，两处都曾无条件印文件名——
// 而全量转储实测：locate 2610 行里 2489 行（95%）、trace inheritors 601 行里 589 行（98%）的
// 文件名就是 `<符号短名>.cs`，逐字可推。这些字符不承载信息，只把每行撑长，并把真正有信息的
// 那几行（符号**不在**同名文件里）淹进噪声。
//
// 判据放在这里而不是各工具内，是因为两处一旦分头演化，同一个概念又会长出两套写法。
internal static class SymbolRow
{
    // 文件名能否从类型名逐字推出来。嵌套类型按任一外层段判定：
    // `RimWorld.Bombardment.BombardmentProjectile` 声明在 Bombardment.cs 里，同样是可推的。
    public static bool FileIsDerivable(string typeName, string fileName)
    {
        foreach (var segment in typeName.Split('.'))
        {
            if (segment.Length == 0) continue;
            if (string.Equals(fileName, segment + ".cs", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    // 结果行末尾的文件注记：推得出来就不印，推不出来才印——印出来的每一个都是意外，值得看一眼。
    // 同一类型分散在多个文件（partial / 多源各有一份）也算意外，照印。
    public static string FileNote(string typeName, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return " (file not indexed)";

        var fileName = Path.GetFileName(paths[0]);
        if (paths.Count == 1 && FileIsDerivable(typeName, fileName))
            return string.Empty;

        var more = paths.Count > 1 ? $" +{paths.Count - 1} more file{(paths.Count == 2 ? "" : "s")}" : "";
        return $" ({fileName}{more})";
    }
}
