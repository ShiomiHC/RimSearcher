#nullable disable

using System;
using System.Collections.Generic;

namespace RimSearcher.DataMod
{
    internal static class NestedClass
    {
        public static bool ShouldEmit(Type runtime, Type declared)
        {
            if (runtime == null) return false;
            if (declared == null) return true;
            return runtime != declared;
        }

        public static Type ElementType(Type collection)
        {
            if (collection == null) return null;
            if (collection.IsArray) return collection.GetElementType();

            foreach (var i in Interfaces(collection))
            {
                if (!i.IsGenericType) continue;
                if (i.GetGenericTypeDefinition() != typeof(IEnumerable<>)) continue;
                var args = i.GetGenericArguments();
                if (args.Length == 1) return args[0];
            }
            return null;
        }

        private static Type[] Interfaces(Type t)
        {
            if (t.IsInterface && t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return new[] { t };
            return t.GetInterfaces();
        }
    }
}
