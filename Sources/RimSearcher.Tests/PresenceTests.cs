using RimSearcher.Output;

namespace RimSearcher.Tests;

/// <summary>
/// 0.5.0 起三件「在不在」进索引。新层在场与缺席两条路都要有闸:
/// 缺席不许印成 0,在场不许再挂「工具证不了」的假话。
/// </summary>
public class PresenceTests
{
    /// <summary>
    /// yes 行那句免责被 xml 列作废了一半:它说「XML 写了同样的值」与「从没提过这个字段」
    /// 在这里看起来一样,而 0.5.0 的表里,那两者就是同一行上的 xml=here 与 xml=no。
    /// 旧快照上没有那一列,原句照旧成立 —— 分档,不是删。
    /// </summary>
    [Fact]
    public void 新快照的yes行不再说两者看起来一样()
    {
        var (fresh, _, _) = Fixture.Run("get", "ChildGun", "--defaults", Fixture.PresenceArg);
        Assert.DoesNotContain("both show yes here", fresh, StringComparison.Ordinal);
        Assert.Contains(XmlOrigin.Column, fresh, StringComparison.Ordinal);

        // 旧快照:那一列不在,那句话是这条路上唯一说破它的地方,一个字都不许少。
        var (old, _, _) = Fixture.Run("get", "Apparel_ShieldBelt", "--defaults");
        Assert.Contains("both show yes here", old, StringComparison.Ordinal);
    }

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

        // XML 拿 defName 当标签名(costList.Steel),索引按列表下标(costList[0].thingDef)。
        // 值回连用同一元素上 thingDef=Steel 对到 costList.Steel,这一格是 here,不是 under。
        Assert.Equal(XmlOrigin.Here, XmlOf("costList[0].thingDef"));
    }

    [Fact]
    public void 两层defName标签能归位_没写的那一格是确定的no()
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
        // XML 有 things.Widget.chance:chance 落到那一行;def 是标签名本身不占 xml_written
        // 叶子;hp 这个字段 XML 没写。后两格是确定的 no,不是 under。
        Assert.Equal(XmlOrigin.Here, XmlOf("things[0].chance"));
        Assert.Equal(XmlOrigin.No, XmlOf("things[0].def"));
        Assert.Equal(XmlOrigin.No, XmlOf("things[0].hp"));
    }

    [Fact]
    public void 值回连对不上的容器才留在under()
    {
        // 标签是类型名 ThingDef,值是 defName ChildGun,对不到 descriptionHyperlinks.ChildGun。
        var (json, _, _) = Fixture.Run("get", "ChildGun", "--defaults", "--json", "--db", Fixture.PresenceDb);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var fields = doc.RootElement.GetProperty("defs")[0].GetProperty("fields");
        foreach (var row in fields.EnumerateArray())
            if (row.GetProperty("path").GetString() == "descriptionHyperlinks[0].def")
            {
                Assert.Equal(XmlOrigin.Under("descriptionHyperlinks"),
                             row.GetProperty(XmlOrigin.Column).GetString());
                return;
            }
        Assert.Fail("no field descriptionHyperlinks[0].def");
    }

    /// <summary>
    /// 容器在 XML 侧一个字都没写时,仍然是 no —— 上面那条不许把所有带下标的路径
    /// 一律降级成「说不准」,那样等于把这一列作废。
    /// </summary>
    [Fact]
    public void 容器本身没写过时仍然是no()
    {
        // OtherGun 在 xml_written 里一条记录都没有,于是没有任何容器前缀可依 ——
        // 每一格都该是 no,一个 under 都不许冒出来。
        var (json, _, _) = Fixture.Run("get", "OtherGun", "--defaults", "--json", "--db", Fixture.PresenceDb);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        foreach (var row in doc.RootElement.GetProperty("defs")[0].GetProperty("fields").EnumerateArray())
            Assert.Equal(XmlOrigin.No, row.GetProperty(XmlOrigin.Column).GetString());
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
