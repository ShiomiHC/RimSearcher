// DataMod 本体是 Nullable=disable,而这个文件还要被 Nullable=enable 的测试工程
// <Compile Link> 进去编一遍(FieldWalk 同样的安排)。null 在这里是**判据的一档**
// (「声明类型不可知」),不是要消灭的东西,所以两边都按 net472 的语义读它。
#nullable disable

using System;
using System.Collections.Generic;

namespace RimSearcher.DataMod
{
    /// <summary>
    /// 一个嵌套对象的运行时类型该不该单发一条 <c>&lt;path&gt;.Class</c>。
    ///
    /// 判据是**运行时类型 ≠ 那个位置声明的类型** —— 也就是「XML 里写了 <c>Class=</c>」。
    /// 类型不是字段,不写这一条就整个丢掉,而「哪些 def 挂了这个类」是 def 侧最常见的反查。
    ///
    /// 0.2.0~0.3.0 的判据是「路径以 <c>]</c> 收尾」,前提写在注释里:「<c>&lt;li Class=&gt;</c>
    /// 只出现在列表里」。那个前提是错的 —— <c>&lt;genStep Class="GenStep_RocksFromGrid"&gt;</c>
    /// 是**单字段**上的 Class,167 个 GenStepDef 于是一条都没进索引,而 def 侧一个字都说不出
    /// 它跑的是哪段代码。ThinkTreeDef.thinkRoot 同形。
    ///
    /// 换判据不是净增:老判据给**每个**列表元素都发一条,其中绝大多数的运行时类型正好等于
    /// 声明的元素类型(<c>List&lt;StatModifier&gt;</c> 里的 StatModifier —— 实测一份 15964 个 def
    /// 的快照里,53509 条 .Class 中单是这一个值就占 14729 条)。那些行报告的是一件作者没做的事,
    /// 换判据之后它们消失,腾出来的额度远多于新进来的单字段多态。
    ///
    /// 这个文件不许引用任何 RimWorld 类型 —— 与 <see cref="FieldWalk"/> 同一个理由:
    /// DataMod 是 net472、测试工程是 net10.0,靠 <c>&lt;Compile Link&gt;</c> 把它编进去才验得动。
    /// 导出器这一层没有别的闸。
    /// </summary>
    internal static class NestedClass
    {
        /// <summary>
        /// <paramref name="declared"/> 为 null = 那个位置的声明类型不可知(非泛型集合等),
        /// 这时一律发:少一条 Class 是整条查询断掉,多一条只是一行冗余。
        /// </summary>
        public static bool ShouldEmit(Type runtime, Type declared)
        {
            if (runtime == null) return false;
            if (declared == null) return true;
            return runtime != declared;
        }

        /// <summary>
        /// 一个集合的元素声明类型。<c>List&lt;CompProperties&gt;</c> → CompProperties。
        ///
        /// 拿不到就回 null(上面那条于是一律发)。非泛型的 <c>IList</c>、多参数的字典、
        /// 数组以外的自定义集合都归这一档 —— 猜一个元素类型出来会把「作者写了 Class=」
        /// 判反,而那是个说错成因的输出。
        /// </summary>
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
            // 类型自己就是 IEnumerable<T> 时 GetInterfaces() 不含它自己。
            if (t.IsInterface && t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return new[] { t };
            return t.GetInterfaces();
        }
    }
}
