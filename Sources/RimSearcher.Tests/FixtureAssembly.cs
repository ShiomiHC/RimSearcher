using System.Reflection;
using System.Reflection.Emit;
using RimSearcher.Metadata;
using RimSearcher.Sources;

namespace RimSearcher.Tests;

/// <summary>
/// 反编译树里那份程序集的夹具。
///
/// 元数据侧那四条命令(<c>types</c> / <c>members</c> / <c>il</c> / <c>callers</c>)读的是 dll,
/// 不是 .cs —— 拿手写的源码文件当语料,它们全都只能查空,而查空的输出与「这几条命令坏了」
/// 逐字同形。所以这里当场发出一个真的程序集。
///
/// 形状是刻意的,每一样对着一处收口:
///   virtual / override  —— <c>--overrides</c> 判的是元数据位,不是名字相同
///   abstract            —— 没有方法体,与「查无此方法」不是一回事
///   同名重载            —— 一个名字给出多行,而不是挑一个
///   属性                —— C# 里的 <c>Name</c> 在 IL 里是 <c>get_Name</c>
///   跨类型调用          —— 边表里得有边,否则 <c>callers</c> 的空表说明不了什么
///
/// 它落在 <c>vanilla</c> 树里而不是自成一棵:多一棵树会把 code-search 那十几份基线里的
/// 「across 3 source trees」全改一遍,而那个数与这件事无关。
/// </summary>
internal static class FixtureAssembly
{
    internal const string Name = "FixtureMod";

    /// <summary>
    /// 发一份程序集,装进树里,并把边表一起建出来。
    ///
    /// <paramref name="installedDir"/> 是清单里记的「安装位置」。同一份字节抄两处,
    /// 于是哈希对得上 —— 对不上的话每条命令都会先说一句「原件已经变了」,
    /// 而那句话正确却与这些测试要看的东西无关。
    /// </summary>
    internal static void BuildInto(string treeDir, string installedDir)
    {
        // 放进子目录而不是根下:真实的 mod 一律是 <mod>/Assemblies/*.dll,而副本按这条
        // 相对路径分层放。搁在根下的话,「往下数」与「只数顶层」两种实现给的数一样,
        // 于是这里量不出副本层级 —— 而只数顶层时印出来的 0 与「从来没抄过」同形。
        var installedRel = Path.Combine("Assemblies", Name + ".dll");
        var installed = Path.Combine(installedDir, installedRel);
        Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
        Emit(installed);

        AssemblyStore.CopyInto(treeDir, installedDir, [installed]);
        var copy = AssemblyStore.CopyPath(treeDir, installedRel);

        var manifest = new SourceTreeState
        {
            PackageId = "vanilla",
            GameVersion = "1.6",
            Root = installedDir,
            Assemblies =
            [
                new SourceAssembly
                {
                    Path = installedRel.Replace(Path.DirectorySeparatorChar, '/'),
                    Sha256 = AssemblyFilter.Sha256(installed),
                },
            ],
        };
        manifest.Write(treeDir);

        var resolved = new ResolvedAssembly
        {
            Tree = Path.GetFileName(treeDir.TrimEnd(Path.DirectorySeparatorChar))!,
            Name = Name,
            Path = copy,
            Origin = AssemblyOrigin.Copy,
        };

        var graph = CallGraphBuilder.Build(
            resolved.Tree, [resolved], [installedDir],
            [.. manifest.Assemblies.Select(a => a.Sha256)]);
        CallGraphStore.Write(treeDir, graph);
    }

    private static void Emit(string path)
    {
        var ab = new PersistedAssemblyBuilder(new AssemblyName(Name), typeof(object).Assembly);
        var module = ab.DefineDynamicModule(Name);

        // Verse.Widgets —— 静态类,同名重载两个。被调方,边表里的目标。
        var widgets = module.DefineType("Verse.Widgets",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);

        var label1 = widgets.DefineMethod("Label", MethodAttributes.Public | MethodAttributes.Static,
                                          typeof(void), [typeof(string)]);
        label1.GetILGenerator().Emit(OpCodes.Ret);

        var label2 = widgets.DefineMethod("Label", MethodAttributes.Public | MethodAttributes.Static,
                                          typeof(void), [typeof(string), typeof(int)]);
        label2.GetILGenerator().Emit(OpCodes.Ret);

        // Verse.ThingComp —— 基类。字段两个,virtual 一个。
        var comp = module.DefineType("Verse.ThingComp", TypeAttributes.Public | TypeAttributes.Class);
        comp.DefineField("parent", typeof(object), FieldAttributes.Public);
        comp.DefineField("props", typeof(object), FieldAttributes.Public);

        var compCtor = comp.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, []);
        var cc = compCtor.GetILGenerator();
        cc.Emit(OpCodes.Ldarg_0);
        cc.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        cc.Emit(OpCodes.Ret);

        var postSpawn = comp.DefineMethod("PostSpawnSetup",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot,
            typeof(void), [typeof(bool)]);
        postSpawn.GetILGenerator().Emit(OpCodes.Ret);

        // Verse.CompProperties —— 抽象方法一个:没有方法体,而那与「查无此方法」是两件事。
        var props = module.DefineType("Verse.CompProperties",
            TypeAttributes.Public | TypeAttributes.Abstract);
        props.DefineMethod("Resolve",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Abstract
            | MethodAttributes.NewSlot,
            typeof(void), Type.EmptyTypes);

        var propsCtor = props.DefineConstructor(MethodAttributes.Family, CallingConventions.Standard, []);
        var pc = propsCtor.GetILGenerator();
        pc.Emit(OpCodes.Ldarg_0);
        pc.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        pc.Emit(OpCodes.Ret);

        // RimWorld.CompShield —— 覆写 + 调用别的类型 + 一个属性。
        var shield = module.DefineType("RimWorld.CompShield", TypeAttributes.Public | TypeAttributes.Class, comp);

        var shieldCtor = shield.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, []);
        var sc = shieldCtor.GetILGenerator();
        sc.Emit(OpCodes.Ldarg_0);
        sc.Emit(OpCodes.Call, compCtor);
        sc.Emit(OpCodes.Ret);

        var over = shield.DefineMethod("PostSpawnSetup",
            MethodAttributes.Public | MethodAttributes.Virtual, typeof(void), [typeof(bool)]);
        var oc = over.GetILGenerator();
        oc.Emit(OpCodes.Ldstr, "shield");
        oc.Emit(OpCodes.Call, label1);
        oc.Emit(OpCodes.Ret);
        shield.DefineMethodOverride(over, postSpawn);

        var getName = shield.DefineMethod("get_Name",
            MethodAttributes.Public | MethodAttributes.SpecialName, typeof(string), Type.EmptyTypes);
        var gc = getName.GetILGenerator();
        gc.Emit(OpCodes.Ldstr, "shield");
        gc.Emit(OpCodes.Ret);

        var name = shield.DefineProperty("Name", PropertyAttributes.None, typeof(string), Type.EmptyTypes);
        name.SetGetMethod(getName);

        widgets.CreateType();
        comp.CreateType();
        props.CreateType();
        shield.CreateType();

        ab.Save(path);
    }
}
