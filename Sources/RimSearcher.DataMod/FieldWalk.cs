using System;
using System.Collections.Generic;
using System.Reflection;

namespace RimSearcher.DataMod
{
    internal static class FieldWalk
    {
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
