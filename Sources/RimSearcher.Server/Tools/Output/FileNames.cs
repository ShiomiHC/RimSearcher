namespace RimSearcher.Server.Tools.Output;

// 结果行上的文件名怎么写。
public static class FileNames
{
    // 同一份返回里出现重名文件时，基名不再是一个能定位的标识。实测 search_regex 一次返回里
    // `RangedIndustrial.xml` / `Buildings_Security_Turrets.xml` / `Items_Resource_Manufactured.xml`
    // 各出现两次（行号不单调，是不同目录下的两份），而两处都叫调用方 `use read_code on a file`
    // ——按名去读必然只命中其中一份，另一份的命中就此消失；把两组行号合起来读则会数出一个
    // 根本不存在的文件。
    //
    // 判据与 R1/R8/R20 同源（推得出来就不印）：基名在本次返回里唯一就只印基名，重名时补上
    // **刚好能把它们分开**的那几级目录，不是无条件印全路径。
    public static IReadOnlyDictionary<string, string> Disambiguate(IEnumerable<string> paths)
    {
        var all = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sameName in all.GroupBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
        {
            var group = sameName.ToList();
            if (group.Count == 1)
            {
                result[group[0]] = sameName.Key;
                continue;
            }

            // 逐级向上加目录，直到组内互不相同。加到 4 级还分不开就给全路径——那时再省
            // 已经不是省 token，是省掉了唯一能定位的信息。
            for (int depth = 1; depth <= 4; depth++)
            {
                var candidates = group.ToDictionary(p => p, p => TailSegments(p, depth + 1));
                if (candidates.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() == group.Count)
                {
                    foreach (var (path, tail) in candidates) result[path] = tail;
                    break;
                }

                if (depth == 4) foreach (var path in group) result[path] = path;
            }
        }

        return result;
    }

    private static string TailSegments(string path, int count)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Join("/", segments.Skip(Math.Max(0, segments.Length - count)));
    }
}
