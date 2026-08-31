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
        {
            var into = new HashSet<string>(StringComparer.Ordinal);
            Walk(type, "", 0, maxDepth, skip, isLeaf, into, new HashSet<Type>());
            return into;
        }

        /// <summary>测试用的默认叶子:原语、枚举、字符串、Type。</summary>
        public static bool DefaultIsLeaf(Type type)
        {
            if (type == typeof(string)) return true;
            if (type.IsEnum) return true;
            if (type.IsPrimitive || type == typeof(decimal)) return true;
            if (typeof(Type).IsAssignableFrom(type)) return true;
            return false;
        }

        private static void Walk(Type type, string prefix, int depth, int maxDepth,
                                 Func<FieldInfo, bool> skip, Func<Type, bool> isLeaf,
                                 HashSet<string> into, HashSet<Type> stack)
        {
            if (type == null) return;
            if (!stack.Add(type)) return;
            try
            {
                foreach (var field in FieldWalk.InstanceFields(type))
                {
                    if (skip != null && skip(field)) continue;
                    var ft = UnwrapNullable(field.FieldType);
                    var path = prefix.Length == 0 ? field.Name : prefix + "." + field.Name;

                    if (isLeaf(ft))
                    {
                        into.Add(path);
                        continue;
                    }

                    if (IsCollection(ft))
                    {
                        if (depth >= maxDepth) continue;
                        var elem = NestedClass.ElementType(ft);
                        if (elem == null) continue;
                        elem = UnwrapNullable(elem);
                        var indexed = path + "[0]";
                        if (isLeaf(elem))
                        {
                            into.Add(indexed);
                            continue;
                        }
                        MaybeClass(indexed, elem, into);
                        Walk(elem, indexed, depth + 1, maxDepth, skip, isLeaf, into, stack);
                        continue;
                    }

                    if (depth >= maxDepth) continue;
                    MaybeClass(path, ft, into);
                    Walk(ft, path, depth + 1, maxDepth, skip, isLeaf, into, stack);
                }
            }
            finally { stack.Remove(type); }
        }

        private static void MaybeClass(string path, Type type, HashSet<string> into)
        {
            if (type.IsClass && type != typeof(string))
                into.Add(path + ".Class");
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
