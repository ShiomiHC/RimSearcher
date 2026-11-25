using RimSearcher.Cli;
using RimSearcher.Metadata;
using RimSearcher.Output;
using RimSearcher.Search;
using RimSearcher.Sources;

namespace RimSearcher.Commands;

public static class DeclarationMiss
{
    private abstract record Cause;

    private sealed record OnAnotherType(string Member, string Within, IReadOnlyList<CsDecl> Owners) : Cause;

    private sealed record InOtherCode(List<NameLookup.Sighting> Rows) : Cause;

    private sealed record InAssemblies(string Sentence) : Cause;

    private sealed record Unexplained : Cause;

    internal static void Explain(CommandContext ctx,
                                 IReadOnlyList<(string Rel, string[] Text, IReadOnlyList<CsDecl> Decls)> files,
                                 string? member, string? type)
    {
        var cause = Find(ctx, files, member, type);
        if (cause is OnAnotherType wrong)
        {
            ctx.Report.Notice(NoticeKind.NextStep,
                $"'{wrong.Member}' 在 {files[0].Rel} 中，声明于 " +
                string.Join("、", wrong.Owners.Select(d => $"{(d.Owner is null ? "文件本身" : d.Owner + d.OwnerTypeParams)}（第 {d.StartLine} 行）")) +
                $"，所属类型与 '{wrong.Within}' 不同。" +
                (type is { Length: > 0 } ? "" : $"改用 '--member {wrong.Member}' 读取。"));
            if (type is { Length: > 0 })
                ctx.Report.EmptyBecause(new EmptyCause(ctx.FilterAsGiven(ReadCommand.Type), wrong.Owners.Count, ctx.Without(ReadCommand.Type)), "declaration");
            return;
        }

        ctx.Report.Notice(NoticeKind.NextStep, NotFound(files, member, type, cause));
        switch (cause)
        {
            case InOtherCode code:
                NameLookup.Say(ctx, code.Rows);
                break;
            case InAssemblies assemblies:
                ctx.Report.Notice(NoticeKind.NextStep, assemblies.Sentence);
                break;
        }
    }

    private static Cause Find(CommandContext ctx,
                              IReadOnlyList<(string Rel, string[] Text, IReadOnlyList<CsDecl> Decls)> files,
                              string? member, string? type)
    {
        if (member is not { Length: > 0 }) return new Unexplained();

        var (prefix, bare) = MemberName.Split(member);
        var within = type is { Length: > 0 } ? type : prefix;
        if (within is { Length: > 0 })
        {
            var owners = MemberName.Hits(files[0].Decls, bare);
            if (owners.Count > 0) return new OnAnotherType(bare, within, owners);
        }

        using var lookup = CodeShared.OpenQuietly(ctx);

        if (bare is ".ctor" or ".cctor")
            return lookup is not null && CompilerGenerated(lookup, files, bare, within) is { } generated
                ? new InAssemblies(generated)
                : new Unexplained();

        var bases = lookup is null ? new BaseAnswer([], []) : OnBases(ctx, lookup, files, member, within);

        var rel = files[0].Rel.Replace('\\', '/');
        var inCode = NameLookup.Beyond(ctx, MemberName.Source(member), NameLookup.Side.Code, sameKind: true,
                                       skip: h => bases.Hits.Contains(h) ||
                                                  string.Equals($"{h.Tree}/{h.File}", rel, StringComparison.OrdinalIgnoreCase));
        if (bases.Rows.Count + inCode.Count > 0) return new InOtherCode([.. bases.Rows, .. inCode]);

        return lookup is not null && !bare.StartsWith("op_", StringComparison.Ordinal) &&
               WhoHasIt(ctx, lookup, bare) is { } sentence
            ? new InAssemblies(sentence)
            : new Unexplained();
    }

    private sealed record BaseAnswer(List<NameLookup.Sighting> Rows, HashSet<SourcePack.MemberHit> Hits);

    private static BaseAnswer OnBases(CommandContext ctx, MetadataLookup lookup,
                                      IReadOnlyList<(string Rel, string[] Text, IReadOnlyList<CsDecl> Decls)> files,
                                      string member, string? type)
    {
        var name = MemberName.Source(member);
        var hierarchy = new Hierarchy(lookup);
        var declarers = new Dictionary<string, (TypeHit Type, MemberRow Row, List<string> Heirs)>(StringComparer.Ordinal);
        foreach (var (decl, hit) in TypesInFiles(lookup, files, type))
        {
            if (Climb(hierarchy, lookup, hit, name) is not { } found) continue;
            if (declarers.TryGetValue(found.Type.FullName, out var known)) known.Heirs.Add(decl.Name);
            else declarers[found.Type.FullName] = (found.Type, found.Row, [decl.Name]);
        }

        var rows = new List<NameLookup.Sighting>();
        var hits = new HashSet<SourcePack.MemberHit>();
        var root = SourcesShared.Root(ctx);
        foreach (var (full, (declarer, row, heirs)) in declarers)
        {
            var note = $"（{NameList.Render([.. heirs.Distinct(StringComparer.Ordinal)], Limits.MaxSuggestions)} 的基类型）";
            var inSource = (SourcePack.FindMembers(root, declarer.Assembly.Tree, name) ?? [])
                .Where(h => h.Member.Name == row.Name && h.Member.Type == SourceTypeName(full))
                .ToList();
            hits.UnionWith(inSource);
            rows.AddRange(inSource.Count > 0
                ? inSource.Select(h => NameLookup.CodeRowFor(ctx, row.Name, h, note))
                : [new NameLookup.Sighting(NameLookup.Where.CodeMember, row.Name, "C# " + CsKind.Of(row.Kind),
                                           $"{full}：{row.Signature}{note}",
                                           $"{CommandRegistry.ExeName} members {full} --name {row.Name}", Redirects: false)]);
        }
        return new BaseAnswer([.. rows.Take(Limits.MaxSuggestions)], hits);
    }

    private static string? CompilerGenerated(MetadataLookup lookup,
                                             IReadOnlyList<(string Rel, string[] Text, IReadOnlyList<CsDecl> Decls)> files,
                                             string compiled, string? within)
    {
        var holder = TypesInFiles(lookup, files, within)
            .Where(t => within is { Length: > 0 } || t.Decl.Owner is null)
            .Select(t => t.Hit)
            .FirstOrDefault(t => lookup.Members(t).Any(m => m.Name == compiled));
        return holder is null ? null :
            $"{holder.FullName} 的程序集中确有 {compiled}，反编译源码中没有对应的代码块；" +
            $"该构造函数的内容见 'rimsearcher il {holder.FullName}.{compiled}'。";
    }

    private static IEnumerable<(CsDecl Decl, TypeHit Hit)> TypesInFiles(
        MetadataLookup lookup, IReadOnlyList<(string Rel, string[] Text, IReadOnlyList<CsDecl> Decls)> files, string? within)
    {
        var types = lookup.AllTypes().ToLookup(t => $"{t.Assembly.Tree}/{SourceTypeName(t.FullName)}",
                                               StringComparer.OrdinalIgnoreCase);
        foreach (var (rel, text, _) in files)
        {
            var tree = rel.Replace('\\', '/').Split('/')[0];
            var decls = CsOutline.Scan(text);
            foreach (var d in decls.Where(d => d.Kind is "class" or "struct" or "record" &&
                                               (within is not { Length: > 0 } || GenericName.Is(within, d.Name, d.TypeParams))))
            {
                var path = string.Join(".", decls.Where(c => (c.Kind == "namespace" || CsOutline.IsType(c.Kind)) &&
                                                             c.StartLine <= d.StartLine && c.EndLine >= d.EndLine)
                                                 .OrderBy(c => c.StartLine).Select(c => c.Name));
                if (types[$"{tree}/{path}"].FirstOrDefault(t => (GenericName.Split(t.Name).Arity ?? 0) == GenericName.Arity(d.TypeParams)) is { } hit)
                    yield return (d, hit);
            }
        }
    }

    private static (TypeHit Type, MemberRow Row)? Climb(Hierarchy hierarchy, MetadataLookup lookup, TypeHit start, string name)
    {
        var links = hierarchy.BaseChain(start.FullName)
                             .Select(link => hierarchy.Relation(link)?.Type).OfType<TypeHit>()
                             .Select(t => (Type: t, Rows: lookup.Members(t))).ToList();
        foreach (var comparison in new[] { StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase })
            foreach (var (type, rows) in links)
                if (rows.FirstOrDefault(r => string.Equals(r.Name, name, comparison)) is { } row)
                    return (type, row);
        return null;
    }

    private static string SourceTypeName(string fullName)
        => string.Join(".", fullName.Split('+').Select(GenericName.Strip));

    private static string NotFound(IReadOnlyList<(string Rel, string[] Text, IReadOnlyList<CsDecl> Decls)> files,
                                   string? member, string? type, Cause cause)
    {
        var name = member is { Length: > 0 } ? member : type!;
        var (rel, text, decls) = files[0];

        var pool = files.SelectMany(f => f.Decls)
                        .Where(d => member is { Length: > 0 } ? !CsOutline.IsType(d.Kind) : CsOutline.IsType(d.Kind))
                        .Select(d => d.Name).Distinct(StringComparer.Ordinal).ToList();
        var close = Suggestion.Closest(pool, MemberName.Source(GenericName.Strip(name)));
        if (member is not { Length: > 0 })
            close = [.. close.SelectMany(n => files.SelectMany(f => f.Decls)
                                                   .Where(d => CsOutline.IsType(d.Kind) && d.Name == n)
                                                   .Select(d => d.Display))
                             .Distinct(StringComparer.Ordinal)];

        return (files.Count == 1
                   ? $"{rel}（{Tally.Complete(text.Length).Render("line")}，" +
                     $"{Tally.Complete(decls.Count).Render("declaration")}）中"
                   : $"{NameList.Render([.. files.Select(f => f.Rel)], files.Count)} 中均") +
               $"没有找到名为 '{name}' 的{(member is { Length: > 0 } ? "成员" : "类型")}。" +
               Suggestion.Say(close) +
               (cause is Unexplained
                   ? ""
                   : "");
    }

    private static string? WhoHasIt(CommandContext ctx, MetadataLookup lookup, string member)
    {
        var hits = lookup.FindMembersAnywhere(member, 32);
        if (hits.Count == 0) return null;

        var types = hits.Select(h => h.Type.FullName).Distinct(StringComparer.Ordinal).ToList();
        var shown = types.Take(Limits.MaxSuggestions).ToList();

        var kinds = hits.Select(h => h.Kind).Distinct(StringComparer.Ordinal).ToList();

        return $"程序集中确有 '{member}'：声明于 " +
               NameList.Render(shown, Limits.MaxSuggestions) +
               (types.Count > shown.Count
                   ? Cjk.Join("，另有", Tally.Complete(types.Count - shown.Count).Render("type"))
                   : "") +
               $"（种类为{NameList.Render([.. kinds.Select(CsKind.Of)], 3)}）。" +
               $"声明见 'rimsearcher members {shown[0]} --name {member}'" +
               (ReadableIn(ctx, hits, member) is { } file
                   ? $"，源码见 'rimsearcher read {file} --member {member}'。"
                   : "。");
    }

    private static string? ReadableIn(CommandContext ctx, IReadOnlyList<MemberRow> hits, string member)
    {
        var root = SourcesShared.Root(ctx);
        foreach (var type in hits.Select(h => h.Type).DistinctBy(t => t.FullName).Take(Limits.MaxSuggestions))
        {
            var outer = type.FullName.Split('+')[0].Split('.')[^1].Split('`')[0];
            foreach (var rel in SourcePaths.Files(root, outer + ".cs", type.Assembly.Tree))
                if (MemberName.Hits(CsOutline.Scan(File.ReadAllLines(Path.Combine(root, rel))), member).Count > 0)
                    return rel;
        }
        return null;
    }
}
