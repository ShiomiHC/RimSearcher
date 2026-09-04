using System;
using System.Collections.Generic;
using System.Reflection;

namespace RimSearcher.DataMod
{
    /// <summary>
    /// 一个类型的实例字段,**含基类声明的私有字段**。
    ///
    /// 非有它不可:<c>Type.GetFields(NonPublic | Instance)</c> 在派生类上**不返回基类的私有
    /// 字段** —— 这是 .NET 反射的规则,不是 bug,但它正好撞上 RimWorld def 的一种常见形状:
    /// 基类声明私有字段 + 每个 def 的运行时类都是子类。实测 <c>CreepJoinerBaseDef</c> 的
    /// <c>weight / minCombatPoints / canOccurRandomly / excludes / requires</c> 五个字段,
    /// 在 24 个 def 上整条不入索引(16787 个 def 里这种形状的有 220 个)。
    ///
    /// 而落空那句话会把成因说成「每个 def 上都是 null」—— <c>weight = 1f</c> 一个都不是 null。
    /// 「错的输出与对的输出同形」这套输出不许留,**说错成因比说不出更坏**,所以这里必须
    /// 自己走基类链。
    ///
    /// 这个文件不许引用任何 RimWorld 类型:DataMod 本体是 net472,而测试工程是 net10.0,
    /// 靠 <c>&lt;Compile Link&gt;</c> 把这一个文件编进去才验得动。<c>[Unsaved]</c> 的过滤
    /// 因此留在调用方。
    /// </summary>
    internal static class FieldWalk
    {
        /// <summary>
        /// 类型 → 它的实例字段。一个类型的字段表在进程内不会变,而这个方法被叫的次数是
        /// 「走到的类型结点数」,不是「类型数」—— 每个 def 一遍、每个类型全集一遍,同一个
        /// 嵌套类型会被重走成千上万次,每次都重新分配 HashSet、重新 GetFields。
        /// 缓存不改行为:同样的顺序、同样的过滤,只是不再重算。
        /// </summary>
        private static readonly Dictionary<Type, FieldInfo[]> Cache = new Dictionary<Type, FieldInfo[]>();

        public static IReadOnlyList<FieldInfo> InstanceFields(Type type)
        {
            if (Cache.TryGetValue(type, out var cached)) return cached;
            var built = new List<FieldInfo>(Collect(type)).ToArray();
            Cache[type] = built;
            return built;
        }

        private static IEnumerable<FieldInfo> Collect(Type type)
        {
            // 子类用 new 遮住同名字段时,近的那个赢 —— 与「运行时读到的是哪一个」一致。
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var field in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic |
                                                  BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (field.IsStatic) continue;
                    if (field.Name.IndexOf('<') >= 0) continue;
                    if (!seen.Add(field.Name)) continue;
                    yield return field;
                }
            }
        }
    }
}
