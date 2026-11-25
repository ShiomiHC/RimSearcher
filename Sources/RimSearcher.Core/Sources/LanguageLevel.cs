using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using ICSharpCode.Decompiler.CSharp;
using RimSearcher.Metadata;
using SRE = System.Reflection.Emit;

namespace RimSearcher.Sources;

public static class LanguageLevel
{
    public const LanguageVersion Old = LanguageVersion.CSharp10_0;

    public const LanguageVersion Modern = LanguageVersion.CSharp12_0;

    public const LanguageVersion Latest = LanguageVersion.CSharp14_0;

    public static LanguageVersion Of(string dllPath)
    {
        using var fs = File.OpenRead(dllPath);
        using var pe = new PEReader(fs);
        return Of(pe);
    }

    public static LanguageVersion Of(PEReader pe)
    {
        var md = pe.GetMetadataReader();
        if (!md.GetModuleDefinition().GetCustomAttributes().Any(a => AttributeName(md, a) == "RefSafetyRulesAttribute"))
            return Old;
        return HasExtensionBlocks(md) || UsesFieldKeyword(md, pe) ? Latest : Modern;
    }

    public static string Render(LanguageVersion v) => v switch
    {
        LanguageVersion.CSharp10_0 => "10",
        LanguageVersion.CSharp12_0 => "12",
        LanguageVersion.CSharp14_0 => "14",
        _ => v.ToString(),
    };

    public static LanguageVersion? Parse(string? s) => s switch
    {
        "10" => LanguageVersion.CSharp10_0,
        "12" => LanguageVersion.CSharp12_0,
        "14" => LanguageVersion.CSharp14_0,
        _ => null,
    };

    private static bool HasExtensionBlocks(MetadataReader md)
        => md.TypeReferences.Any(h => md.GetString(md.GetTypeReference(h).Name) == "ExtensionMarkerAttribute")
        || md.TypeDefinitions.Any(h => md.GetString(md.GetTypeDefinition(h).Name) == "ExtensionMarkerAttribute");

    private static bool UsesFieldKeyword(MetadataReader md, PEReader pe)
    {
        foreach (var th in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(th);
            foreach (var ph in type.GetProperties())
            {
                var p = md.GetPropertyDefinition(ph);
                var backing = $"<{md.GetString(p.Name)}>k__BackingField";
                var acc = p.GetAccessors();
                foreach (var mh in new[] { acc.Getter, acc.Setter })
                {
                    if (mh.IsNil) continue;
                    var m = md.GetMethodDefinition(mh);
                    if (m.RelativeVirtualAddress == 0) continue;
                    if (m.GetCustomAttributes().Any(a => AttributeName(md, a) == "CompilerGeneratedAttribute")) continue;
                    if (TouchesField(md, pe, m, th, backing)) return true;
                }
            }
        }
        return false;
    }

    private static bool TouchesField(MetadataReader md, PEReader pe, MethodDefinition m, TypeDefinitionHandle owner, string name)
    {
        ReadOnlySpan<byte> il;
        try { il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILContent().AsSpan(); }
        catch { return false; }

        var i = 0;
        while (i < il.Length)
        {
            SRE.OpCode? op;
            int opSize;
            if (il[i] == 0xFE && i + 1 < il.Length) { op = CallGraphBuilder.TwoByte[il[i + 1]]; opSize = 2; }
            else { op = CallGraphBuilder.OneByte[il[i]]; opSize = 1; }
            if (op is null) return false;

            var ot = op.Value.OperandType;
            if (ot == SRE.OperandType.InlineField)
            {
                var h = MetadataTokens.EntityHandle(BitConverter.ToInt32(il[(i + opSize)..]));
                if (h.Kind == HandleKind.FieldDefinition)
                {
                    var f = md.GetFieldDefinition((FieldDefinitionHandle)h);
                    if (f.GetDeclaringType() == owner && md.GetString(f.Name) == name) return true;
                }
            }
            i += opSize + CallGraphBuilder.OperandSize(ot, il[(i + opSize)..]);
        }
        return false;
    }

    private static string? AttributeName(MetadataReader md, CustomAttributeHandle h)
    {
        try { return AttributeNameOrThrow(md, h); }
        catch (BadImageFormatException) { return null; }
    }

    private static string? AttributeNameOrThrow(MetadataReader md, CustomAttributeHandle h)
    {
        var a = md.GetCustomAttribute(h);
        switch (a.Constructor.Kind)
        {
            case HandleKind.MemberReference:
                var parent = md.GetMemberReference((MemberReferenceHandle)a.Constructor).Parent;
                return parent.Kind switch
                {
                    HandleKind.TypeReference => md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Name),
                    HandleKind.TypeDefinition => md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)parent).Name),
                    _ => null,
                };
            case HandleKind.MethodDefinition:
                var declaring = md.GetMethodDefinition((MethodDefinitionHandle)a.Constructor).GetDeclaringType();
                return md.GetString(md.GetTypeDefinition(declaring).Name);
            default:
                return null;
        }
    }
}
