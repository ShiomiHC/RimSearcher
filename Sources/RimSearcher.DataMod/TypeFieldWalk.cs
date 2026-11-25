#nullable disable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace RimSearcher.DataMod
{
    internal static class TypeFieldWalk
    {
        public static HashSet<string> Collect(Type type, int maxDepth,
                                              Func<FieldInfo, bool> skip,
                                              Func<Type, bool> isLeaf)
            => For(skip, isLeaf).Collect(type, maxDepth);

        public static Walker For(Func<FieldInfo, bool> skip, Func<Type, bool> isLeaf)
            => new Walker(skip, isLeaf);

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
