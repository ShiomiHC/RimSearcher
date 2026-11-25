#nullable disable

using System;
using System.Collections.Generic;
using System.Reflection;

namespace RimSearcher.DataMod
{
    internal static class RuntimeValues
    {
        public static bool IsHandle(Type type)
            => type == typeof(IntPtr) || type == typeof(UIntPtr) || type.IsPointer
               || typeof(Assembly).IsAssignableFrom(type)
               || typeof(Module).IsAssignableFrom(type)
               || typeof(ParameterInfo).IsAssignableFrom(type);

        public static bool TryMemberText(object value, out string text)
        {
            var member = value as MemberInfo;
            if (member == null || member is Type) { text = null; return false; }
            var owner = member.DeclaringType;
            text = owner == null ? member.Name : (owner.FullName ?? owner.Name) + "." + member.Name;
            return true;
        }

        public static HashSet<string> ConstructorRandomFields(object first, object second,
                                                               IReadOnlyList<FieldInfo> fields,
                                                               TryLeafFn tryLeaf)
        {
            var noisy = new HashSet<string>(StringComparer.Ordinal);
            if (first == null || second == null) return noisy;
            foreach (var field in fields)
            {
                object a, b;
                try { a = field.GetValue(first); b = field.GetValue(second); }
                catch { continue; }
                string ta = null, tb = null;
                var leafA = a == null ? LeafRead.NotLeaf : ReadLeaf(a, tryLeaf, out ta);
                var leafB = b == null ? LeafRead.NotLeaf : ReadLeaf(b, tryLeaf, out tb);
                if (leafA == LeafRead.Unreadable || leafB == LeafRead.Unreadable) continue;
                if (leafA == LeafRead.NotLeaf && leafB == LeafRead.NotLeaf) continue;
                if (!string.Equals(ta, tb, StringComparison.Ordinal)) noisy.Add(field.Name);
            }
            return noisy;
        }

        public delegate bool TryLeafFn(object value, out string text);

        public enum LeafRead { NotLeaf, Leaf, Unreadable }

        public static LeafRead ReadLeaf(object value, TryLeafFn tryLeaf, out string text)
        {
            Exception ignored;
            return ReadLeaf(value, tryLeaf, out text, out ignored);
        }

        public static LeafRead ReadLeaf(object value, TryLeafFn tryLeaf, out string text, out Exception error)
        {
            error = null;
            try { return tryLeaf(value, out text) ? LeafRead.Leaf : LeafRead.NotLeaf; }
            catch (Exception ex) { text = null; error = ex; return LeafRead.Unreadable; }
        }
    }
}
