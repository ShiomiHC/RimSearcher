using RimSearcher.Output;

namespace RimSearcher.Tests;

/// <summary>
/// 0.5.0 起三件「在不在」进索引。新层在场与缺席两条路都要有闸:
/// 缺席不许印成 0,在场不许再挂「工具证不了」的假话。
/// </summary>
public class PresenceTests
{
    // ---- B2 patch 计数 ----

    [Fact]
    public void 新快照按defName和label的patch计数是数字()
    {
        var (json, _, _) = Fixture.Run("inherit", "ChildGun", "--json", "--db", Fixture.PresenceDb);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var node = doc.RootElement.GetProperty("nodes")[0].GetProperty("node");
        Assert.Equal("n/a", node.GetProperty("patch_ops").GetString());
        Assert.Equal(2, node.GetProperty("patch_ops_defname").GetInt32());
        Assert.Equal(1, node.GetProperty("patch_ops_label").GetInt32());
    }

    [Fact]
    public void 新快照免责不再把defName和label说成没数()
    {
        var (text, _, _) = Fixture.Run("inherit", "BaseGun", "--db", Fixture.PresenceDb);
        Assert.Contains("patch_ops_defname", text, StringComparison.Ordinal);
        Assert.Contains("by thingClass", text, StringComparison.Ordinal);
        Assert.Contains("by a wildcard", text, StringComparison.Ordinal);
        Assert.DoesNotContain("by defName, by label", text, StringComparison.Ordinal);
        Assert.DoesNotContain("does not count xpaths by defName", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 旧快照不把没数过的defName计数印成零()
    {
        var (text, _, _) = Fixture.Run("inherit", "BaseProjectile");
        Assert.Contains("only counted @Name=", text, StringComparison.Ordinal);
        Assert.Contains("a newer export also counts xpaths by defName=", text, StringComparison.Ordinal);
        var (json, _, _) = Fixture.Run("inherit", "BaseProjectile", "--json");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var node = doc.RootElement.GetProperty("nodes")[0].GetProperty("node");
        Assert.False(node.TryGetProperty("patch_ops_defname", out _));
        Assert.False(node.TryGetProperty("patch_ops_label", out _));
    }

    // ---- B1 XML 写没写 ----

    [Fact]
    public void 新快照get能分开本节点写的和父节点写的()
    {
        var (json, _, _) = Fixture.Run("get", "ChildGun", "--defaults", "--json", "--db", Fixture.PresenceDb);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var fields = doc.RootElement.GetProperty("defs")[0].GetProperty("fields");
        string XmlOf(string path)
        {
            foreach (var row in fields.EnumerateArray())
                if (row.GetProperty("path").GetString() == path)
                    return row.GetProperty(XmlOrigin.Column).GetString()!;
            Assert.Fail($"no field {path}");
            return "";
        }
        Assert.Equal(XmlOrigin.Here, XmlOf("damage"));
        Assert.Equal(XmlOrigin.Parent, XmlOf("speed"));
        Assert.Equal(XmlOrigin.Parent, XmlOf("thingClass"));
        Assert.Equal(XmlOrigin.No, XmlOf("burstCount"));
    }

    [Fact]
    public void 旧快照get不把xml写成印成没写()
    {
        var (text, _, _) = Fixture.Run("get", "Apparel_ShieldBelt");
        Assert.Contains("not indexed (exporter 0.2.0)", text, StringComparison.Ordinal);
        var (json, _, _) = Fixture.Run("get", "Apparel_ShieldBelt", "--json");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var row = doc.RootElement.GetProperty("defs")[0].GetProperty("fields")[0];
        Assert.False(row.TryGetProperty(XmlOrigin.Column, out _));
    }

    // ---- B3 类型字段全集 ----

    [Fact]
    public void 新快照fields能分开全是null和没有这个字段()
    {
        var (nulls, _, nullCode) = Fixture.Run("fields", "ThingDef", "--path-contains", "neverSet",
                                               "--db", Fixture.PresenceDb);
        Assert.Equal(1, nullCode);
        Assert.Contains("every def of the type has them as null", nulls, StringComparison.Ordinal);
        Assert.Contains("The type has the field", nulls, StringComparison.Ordinal);

        var (missing, _, missCode) = Fixture.Run("fields", "ThingDef", "--path-contains", "noSuchFieldXYZ",
                                                 "--db", Fixture.PresenceDb);
        Assert.Equal(1, missCode);
        Assert.Contains("does not declare such a field", missing, StringComparison.Ordinal);
        Assert.DoesNotContain("every def of the type has them as null", missing, StringComparison.Ordinal);
    }

    [Fact]
    public void 旧快照fields落空说清分不开()
    {
        var (text, _, code) = Fixture.Run("fields", "ThingDef", "--path-contains", "zzzzNoSuch");
        Assert.Equal(1, code);
        Assert.Contains("does not list the fields a type can have", text, StringComparison.Ordinal);
        Assert.Contains("null on every def from one the type does not have", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 导入把新层写进库()
    {
        using var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Fixture.PresenceDb};Pooling=False");
        raw.Open();
        using var cmd = raw.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM xml_written";
        Assert.True((long)cmd.ExecuteScalar()! > 0);
        cmd.CommandText = "SELECT COUNT(*) FROM type_fields WHERE def_type = 'ThingDef' AND path = 'neverSet'";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT patch_ops_defname FROM xml_nodes WHERE def_name = 'ChildGun'";
        Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void 旧导出导入后新表是空的()
    {
        using var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Fixture.Db};Pooling=False");
        raw.Open();
        using var cmd = raw.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM xml_written";
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT COUNT(*) FROM type_fields";
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
    }
}
