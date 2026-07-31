using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace RimSearcher.Sources;

/// <summary>
/// 哪些 dll 不值得反编译,以及一个 dll 引用了谁 —— 只这两件事。
/// 变更判据在 <see cref="SourceTreeState"/>,目录遮蔽在 <see cref="ModFolders"/>。
/// </summary>
public static class AssemblyFilter
{
    // 运行时与引擎程序集:反编译它们既无意义又会让产物膨胀十倍。
    //
    // 判定分「精确名」与「点分家族」两档,不能一律 StartsWith:裸前缀会把
    // SystematicWeapons.dll / UnityEngineTweaks.dll / I18NPlus.dll 这类正常 mod 程序集
    // 一并排掉。家族前缀带结尾的点,只吃 "Foo." 开头的真·子命名空间。
    //
    // 逐条判断依据:
    //   mscorlib        只有 mscorlib.dll 这一个,没有 mscorlib.* 家族 → 精确
    //   netstandard     同上 → 精确
    //   System          System.dll 存在,System.Xml/System.Core/… 也存在 → 精确 + 家族
    //   UnityEngine     UnityEngine.dll 存在,UnityEngine.CoreModule 等模块化拆分也存在 → 精确 + 家族
    //   I18N            I18N.dll 存在(Mono 的字符集库),I18N.West/I18N.CJK 也存在 → 精确 + 家族
    //   Newtonsoft.Json 本体 + Newtonsoft.Json.Bson 之类同厂扩展;不可能是 mod 名 → 精确 + 家族
    //   Microsoft.      没有裸 Microsoft.dll,只有 Microsoft.CSharp 这类 → 只家族
    //   Unity.          没有裸 Unity.dll(引擎本体叫 UnityEngine),只有 Unity.TextMeshPro 这类 → 只家族
    //   Mono.           没有裸 Mono.dll,只有 Mono.Security / Mono.Posix 这类 → 只家族
    //   websocket-sharp 游戏 Managed 下的单个第三方库,无家族 → 精确
    private static readonly string[] ExcludedExactNames =
    [
        "mscorlib", "netstandard", "System", "UnityEngine",
        "I18N", "Newtonsoft.Json", "websocket-sharp",
    ];

    private static readonly string[] ExcludedFamilyPrefixes =
    [
        "System.", "UnityEngine.", "I18N.", "Newtonsoft.Json.",
        "Microsoft.", "Unity.", "Mono.",
    ];

    public static bool IsRuntimeAssembly(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);

        foreach (var exact in ExcludedExactNames)
            if (name.Equals(exact, StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var family in ExcludedFamilyPrefixes)
            if (name.StartsWith(family, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    /// <summary>
    /// 这个 dll 的 AssemblyRef 名字集合。反编译只需要编译期类型解析,AssemblyRef 正是
    /// 编译期引用集合 —— 比 About.xml 的 modDependencies(含纯 XML patch 依赖)更贴合。
    /// 读不出来返回空集:解析目录少一个只是让泛型约束退化成 object,不是整次反编译失败。
    /// </summary>
    public static HashSet<string> References(string assemblyPath)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return result;
            var reader = pe.GetMetadataReader();
            if (!reader.IsAssembly) return result;
            foreach (var handle in reader.AssemblyReferences)
                result.Add(reader.GetString(reader.GetAssemblyReference(handle).Name));
        }
        catch { /* 读不动就当没有引用 */ }
        return result;
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
