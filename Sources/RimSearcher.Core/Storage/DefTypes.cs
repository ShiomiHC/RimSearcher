namespace RimSearcher.Storage;

/// <summary>
/// 一个名字下挂着的东西归不归这个 def —— 同名跨 def 类型是 RimWorld 常态,而快照里
/// 有三张表按 <c>def_name</c> 存东西(译文、继承层、defs 自己),按名字关联就会串味(R2)。
///
/// 放在 Storage 而不是命令层:导入侧要用同一条判据把译文绑到 def 上,而两侧各写一份
/// 「什么算同一个类型」正是这条规则最容易长出第二个产地的地方。
/// </summary>
public static class DefTypes
{
    /// <summary>
    /// 两个 <c>def_type</c> 是否指同一个类型。
    ///
    /// 需要这个判断而不是直接 <c>==</c>,是因为两者不同源:继承层的是 XML 根元素名,
    /// defs 表的是 <c>AllDefTypesWithDatabases</c> 的桶名(只产出「祖先链上没有非抽象 Def」
    /// 的类型)。实测本机 modded 快照,在**没有同名歧义**的 def 里有 26 个对不上,三种形状:
    ///   Blindhealer             CreepJoinerFormKindDef          → PawnKindDef        (子类落进基类桶)
    ///   AncientComplex_Loot     ComplexLayoutDef                → LayoutDef          (同上)
    ///   DefaultCareForColonist  Defaults.Defs.DefaultSettingDef → DefaultSettingDef  (带命名空间)
    /// 前两种由调用点的「无歧义时回退到唯一候选」兜住;第三种在**同时有同名歧义**时连回退都
    /// 走不到,所以这里补一层:全等优先,再退到去掉命名空间后相等。
    ///
    /// 次选那一层理论上能把两个 mod 各自的 <c>A.FooDef</c> / <c>B.FooDef</c> 配到一起,
    /// 但调用点只在候选里挑,配错的前提是同一个 defName 下同时存在这两个类型 —— 比
    /// 「该显示的东西不显示」罕见得多,而后者正是这一轮反复在修的那类错(缺席被读成事实)。
    /// </summary>
    public static bool Same(string? a, string? b)
    {
        if (a is null || b is null) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        static string Leaf(string s) => s[(s.LastIndexOf('.') + 1)..];
        return string.Equals(Leaf(a), Leaf(b), StringComparison.OrdinalIgnoreCase);
    }
}
