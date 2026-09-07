#nullable disable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace RimSearcher.DataMod
{
    /// <summary>
    /// 一个类型**能有**的字段路径全集,与值无关。拼法对齐 <c>field_values.path</c>:
    /// <c>.</c> 连接字段名,集合用一个 <c>[0]</c> 槽位代表「这一项里还能再往下」,
    /// 多态位置额外发 <c>.Class</c>。
    ///
    /// 深度预算与导出器相同:叶子不占,钻进复合对象 / 集合元素才 +1。
    /// 集合只发下标 0 —— 类型不知道运行时有几项,发 <c>[1]</c> 是在编造。
    ///
    /// 这个文件不许引用任何 RimWorld 类型:叶子判据由调用方注入
    /// (游戏侧要把 Def / Type / Unity 结构体算叶子,测试侧只需原语)。
    /// </summary>
    internal static class TypeFieldWalk
    {
        public static HashSet<string> Collect(Type type, int maxDepth,
                                              Func<FieldInfo, bool> skip,
                                              Func<Type, bool> isLeaf)
            => For(skip, isLeaf).Collect(type, maxDepth);

        /// <summary>
        /// 一批类型共用一个 walker —— 缓存就靠这个跨类型复用,而收益全在这里:
        /// 222 个 Def 子类共享基类那棵字段树,朴素走法把它重算 222 遍。
        ///
        /// 缓存**挂在实例上而不是静态字段上**,因为结果是 skip / isLeaf 这两个判据的函数。
        /// 挂静态就会让两次判据不同的调用互相串味,而串味出来的路径集看着完全正常。
        /// </summary>
        public static Walker For(Func<FieldInfo, bool> skip, Func<Type, bool> isLeaf)
            => new Walker(skip, isLeaf);

        /// <summary>测试用的默认叶子:原语、枚举、字符串、Type。</summary>
        public static bool DefaultIsLeaf(Type type)
        {
            if (type == typeof(string)) return true;
            if (type.IsEnum) return true;
            if (type.IsPrimitive || type == typeof(decimal)) return true;
            if (typeof(Type).IsAssignableFrom(type)) return true;
            return false;
        }

        internal sealed class Walker
        {
            private readonly Func<FieldInfo, bool> _skip;
            private readonly Func<Type, bool> _isLeaf;

            // 一棵子树连它**走到过哪些类型**一起缓存。复用的前提不是「上次没被截断」,
            // 而是「这次的祖先链与它无交集」—— 同一个 (类型, 预算) 在两条祖先链下的
            // 结果可以不同(TW_Root 那个闸就是照这个形状造的),只记前者会把一次没截断的
            // 结果搬到会截断的路径上,凭空多出一批路径,而且跑得更快、看着像捡回了更多。
            private readonly Dictionary<Key, Entry> _memo = new Dictionary<Key, Entry>();

            public Walker(Func<FieldInfo, bool> skip, Func<Type, bool> isLeaf)
            {
                _skip = skip;
                _isLeaf = isLeaf;
            }

            public HashSet<string> Collect(Type type, int maxDepth)
            {
                var into = new HashSet<string>(StringComparer.Ordinal);
                var e = Sub(type, maxDepth, new HashSet<Type>());
                foreach (var p in e.Paths) into.Add(p);
                return into;
            }

            private Entry Sub(Type type, int budget, HashSet<Type> stack)
            {
                if (type == null) return Entry.Empty;

                var key = new Key(type, budget);
                Entry hit;
                if (_memo.TryGetValue(key, out hit) && !hit.Touched.Overlaps(stack))
                    return hit;

                var paths = new List<string>();
                var touched = new HashSet<Type> { type };
                var clipped = false;
                stack.Add(type);
                try
                {
                    foreach (var field in FieldWalk.InstanceFields(type))
                    {
                        if (_skip != null && _skip(field)) continue;
                        var ft = UnwrapNullable(field.FieldType);

                        if (_isLeaf(ft)) { paths.Add(field.Name); continue; }

                        if (IsCollection(ft))
                        {
                            if (budget <= 0) continue;
                            var elem = NestedClass.ElementType(ft);
                            if (elem == null) continue;
                            elem = UnwrapNullable(elem);
                            var indexed = field.Name + "[0]";
                            if (_isLeaf(elem)) { paths.Add(indexed); continue; }
                            MaybeClass(indexed, elem, paths);
                            Descend(elem, indexed, budget - 1, stack, paths, touched, ref clipped);
                            continue;
                        }

                        if (budget <= 0) continue;
                        MaybeClass(field.Name, ft, paths);
                        Descend(ft, field.Name, budget - 1, stack, paths, touched, ref clipped);
                    }
                }
                finally { stack.Remove(type); }

                var entry = new Entry(paths, touched) { Clipped = clipped };
                // 这一趟被祖先链截断过,结果就只对这条路径成立,不进缓存。
                if (!clipped) _memo[key] = entry;
                return entry;
            }

            private void Descend(Type next, string prefix, int budget, HashSet<Type> stack,
                                 List<string> paths, HashSet<Type> touched, ref bool clipped)
            {
                if (stack.Contains(next)) { touched.Add(next); clipped = true; return; }
                var sub = Sub(next, budget, stack);
                foreach (var p in sub.Paths) paths.Add(prefix + "." + p);
                touched.UnionWith(sub.Touched);
                if (sub.Clipped) clipped = true;
            }

            private void MaybeClass(string path, Type type, List<string> into)
            {
                if (type.IsClass && type != typeof(string)) into.Add(path + ".Class");
            }

            private struct Key : IEquatable<Key>
            {
                private readonly Type _type;
                private readonly int _budget;
                public Key(Type type, int budget) { _type = type; _budget = budget; }
                public bool Equals(Key other) => _type == other._type && _budget == other._budget;
                public override bool Equals(object obj) => obj is Key k && Equals(k);
                public override int GetHashCode() => (_type == null ? 0 : _type.GetHashCode()) * 397 ^ _budget;
            }

            private sealed class Entry
            {
                public static readonly Entry Empty =
                    new Entry(new List<string>(), new HashSet<Type>());

                public readonly List<string> Paths;
                public readonly HashSet<Type> Touched;
                public bool Clipped;

                public Entry(List<string> paths, HashSet<Type> touched)
                {
                    Paths = paths;
                    Touched = touched;
                }
            }
        }

        private static bool IsCollection(Type type)
        {
            if (type == typeof(string)) return false;
            return typeof(IEnumerable).IsAssignableFrom(type);
        }

        private static Type UnwrapNullable(Type type)
        {
            var inner = Nullable.GetUnderlyingType(type);
            return inner ?? type;
        }
    }
}
