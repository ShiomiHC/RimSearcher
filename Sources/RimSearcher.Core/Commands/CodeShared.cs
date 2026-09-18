using RimSearcher.Cli;
using RimSearcher.Metadata;
using RimSearcher.Output;

namespace RimSearcher.Commands;

/// <summary>
/// 读程序集元数据的那几条命令(<c>il</c> / <c>types</c> / <c>members</c> / <c>callers</c>)共用的部分。
///
/// 共用的不只是取程序集这段代码,更是**它们旧的时候说的那句话** —— 三条命令各写各的检查、
/// 各写各的措辞,就会出现一条说树是新的、另一条说 dll 变了这种互相打架的输出。
/// </summary>
internal static class CodeShared
{
    internal static readonly OptionSpec Source = new()
    {
        Name = "source",
        // "scope" 不在这里:那个词在 def 侧的命令上指快照里的 mod,是另一回事(见 CommonOptions.Scope)。
        Aliases = ["tree", "from-tree"],
        Placeholder = "<tree>",
        Help = "Which decompiled source tree to read. Omit to read them all.",
        Narrows = true,
    };

    /// <summary>
    /// 打开这次要读的程序集,并把「读到的是哪一份」说清楚。
    ///
    /// <paramref name="everyTree"/> 给反查用:那条路上 <c>--source</c> 限的是「在哪棵树里找调用点」,
    /// 而两端的名字仍要在全部树里才认得出来 —— 少开一棵,调用方就会被印成一串 token。
    /// </summary>
    internal static MetadataLookup Open(CommandContext ctx, out string root, bool everyTree = false)
    {
        root = SourcesShared.Root(ctx);
        if (!Directory.Exists(root))
            throw new CliUsageException(
                $"'{root}' does not exist, so there are no assemblies to read. " +
                "'rimsearcher sources sync' creates it.");

        var only = ctx.Args.Value("source");
        if (only is { Length: > 0 } && !Directory.Exists(Path.Combine(root, only)))
            throw new CliUsageException(
                $"No decompiled source tree named '{only}'. 'rimsearcher sources list' names every tree.");

        var assemblies = AssemblyStore.ResolveAll(root, !everyTree && only is { Length: > 0 } ? [only] : null);
        if (assemblies.Count == 0)
            throw new CliUsageException(
                (!everyTree && only is { Length: > 0 }
                    ? $"The source tree '{only}' names no assembly this machine can reach. "
                    : "No source tree names an assembly this machine can reach. ") +
                "Each tree's manifest lists the dlls it came from; none of them is here, and none has been " +
                "copied in. 'rimsearcher sources sync' copies them in.");

        var lookup = new MetadataLookup(assemblies);
        Announce(ctx, lookup);
        return lookup;
    }

    /// <summary>
    /// 安静地开:开不了就回 null,不抛也不播报。
    ///
    /// 给**落空路径上的回查**用。那里元数据是个附加的答案来源,不是这条命令的主业:
    /// <see cref="Open"/> 开不了会抛,而在 read 的落空路径上抛出去,会把「没找到这个
    /// 成员」换成「没有程序集可读」—— 那是另一个问题的答案。
    ///
    /// 也不 <see cref="Announce"/>:那两句讲的是「你读到的正文来自哪一份 dll」,而这里
    /// 没有正文,只借元数据回答「这个名字在谁身上」。名字这一级的答案经得起两份 dll
    /// 的小差异,而那两句摆在一句落空提示前面会把它压掉。
    /// </summary>
    internal static MetadataLookup? OpenQuietly(CommandContext ctx)
    {
        try
        {
            var root = SourcesShared.Root(ctx);
            if (!Directory.Exists(root)) return null;
            var assemblies = AssemblyStore.ResolveAll(root, null);
            return assemblies.Count == 0 ? null : new MetadataLookup(assemblies);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (CliUsageException) { return null; }
    }

    /// <summary>
    /// 这次读到的东西与落盘的 C# 是不是同一份。三种情形各有各的话,而一致时一个字不说 ——
    /// 常态发声等于每次查询都交一次上下文税。
    /// </summary>
    private static void Announce(CommandContext ctx, MetadataLookup lookup)
    {
        var installed = lookup.Assemblies.Where(a => a.Origin == AssemblyOrigin.Installed).ToList();
        var changed = lookup.Assemblies.Where(a => a.Origin == AssemblyOrigin.Copy && a.OriginalChanged == true).ToList();
        var gone = lookup.Assemblies.Where(a => a.Origin == AssemblyOrigin.Copy && a.OriginalMissing).ToList();

        if (installed.Count > 0)
            ctx.Report.Notice(NoticeKind.Staleness,
                "Read from where the game has it installed rather than from a copy kept with the source tree — " +
                $"{Tally.Complete(installed.Count).Render("assembly")}: {Trees(installed)}. What is below and " +
                "what 'rimsearcher read' shows can therefore be two different builds. " +
                "'rimsearcher sources sync' keeps a copy alongside the C#, after which both come from one dll.");

        if (changed.Count > 0)
            ctx.Report.Notice(NoticeKind.Staleness,
                "What is below comes from the copy kept with the source tree, so it matches the C# that " +
                "'rimsearcher read' shows. The installed dll has moved on since that copy was taken, which " +
                "means the running game has different code — " +
                $"{Tally.Complete(changed.Count).Render("assembly")}: {Trees(changed)}. " +
                "'rimsearcher sources sync' rebuilds both from the dll on disk now.");

        if (gone.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                "No longer installed, so only the copy kept with the source tree can still be read — " +
                $"{Tally.Complete(gone.Count).Render("assembly")}: {Trees(gone)}.");

        if (lookup.Unreadable.Count > 0)
            ctx.Report.Notice(NoticeKind.Boundary,
                "Could not be opened at all, and therefore not searched — " +
                $"{Tally.Complete(lookup.Unreadable.Count).Render("assembly")}: {Trees(lookup.Unreadable)}.");
    }

    private static string Trees(IReadOnlyList<ResolvedAssembly> items)
        => NameList.Render([.. items.Select(a => $"{a.Tree}/{a.Name}")], 6);

    /// <summary>
    /// 一个符号名落空时说什么。三种落空各有各的话 —— 混成一句「未找到」,
    /// 「这个成员不存在」与「装它的那个 dll 不在这里」就再也分不开。
    /// </summary>
    internal static void SayNoType(CommandContext ctx, MetadataLookup lookup, SymbolRef symbol)
    {
        var near = lookup.AllTypes()
                         .Where(t => t.Name.Contains(symbol.TypeName, StringComparison.OrdinalIgnoreCase))
                         .Take(6).Select(t => t.FullName).ToList();

        ctx.Report.Notice(NoticeKind.Boundary,
            $"No type named '{symbol.TypeName}' is in the {Tally.Complete(lookup.AssemblyCount).Render("assembly")} " +
            "read here" +
            (near.Count > 0 ? $". Names containing it: {NameList.Render(near, 6)}" : "") +
            ". 'rimsearcher sources list' names the trees that were read.");
    }

    /// <summary>成员落空。类型找到了,成员没有 —— 这时候能说的比上一条多。</summary>
    internal static void SayNoMember(CommandContext ctx, MetadataLookup lookup, TypeHit type, string member)
    {
        // 先走基类链。这句话说的是「继承来的成员在基类上」,那么它点名的也得是基类 ——
        // 从全体树里捞同名成员,捞到的多半是别的 mod 里毫不相干的类型,而读者会顺着去问它们。
        var bases = new Hierarchy(lookup).BaseChain(type.FullName)
                        .SelectMany(b => lookup.FindTypes(b).Take(1))
                        .Where(b => lookup.FindMethods(b, member).Count > 0
                                 || lookup.Members(b).Any(m => string.Equals(m.Name, member,
                                                                             StringComparison.OrdinalIgnoreCase)))
                        .Select(b => b.FullName).Take(4).ToList();

        if (bases.Count > 0)
        {
            ctx.Report.Notice(NoticeKind.Boundary,
                $"'{type.FullName}' declares no member named '{member}', and inherits it instead — the base " +
                $"chain declares it on {NameList.Render(bases, 4)}. Ask there: " +
                $"'rimsearcher il {bases[0]}.{member}'. " +
                $"'rimsearcher members {type.FullName} --inherited' lists what it inherits and from where.");
            return;
        }

        var elsewhere = lookup.FindMethodsAnywhere(member, 6)
                              .Where(m => m.Type.FullName != type.FullName)
                              .Select(m => m.Type.FullName).Distinct().Take(4).ToList();

        ctx.Report.Notice(NoticeKind.Boundary,
            $"'{type.FullName}' has no member named '{member}', and neither does anything in its base chain" +
            (elsewhere.Count > 0
                ? $". Unrelated types carry the name: {NameList.Render(elsewhere, 4)}"
                : "") +
            $". 'rimsearcher members {type.FullName}' lists what it does have.");
    }
}
