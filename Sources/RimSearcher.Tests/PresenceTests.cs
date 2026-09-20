using RimSearcher.Commands;
using RimSearcher.Output;
using RimSearcher.Storage;

namespace RimSearcher.Tests;

/// <summary>
/// 三件「在不在」(XML 写没写 / 按 defName·label 的 patch 计数 / 类型字段全集)进索引之后,
/// 在场那条路不许再挂「工具证不了」的假话。缺席那一档随 0.12 地板一起删了(2026-09-20)。
/// </summary>
public class PresenceTests
{
    /// <summary>
    /// yes 行不挂「XML 写了同样的值」与「从没提过这个字段」看起来一样那句:
    /// 那两者就是同一行上的 xml=here 与 xml=not-written,列自己说。
    /// </summary>
    [Fact]
    public void yes行不再说两者看起来一样()
    {
        var (fresh, _, _) = Fixture.Run("get", "ChildGun", "--defaults", Fixture.PresenceArg);
        Assert.DoesNotContain("both show yes here", fresh, StringComparison.Ordinal);
        Assert.Contains(XmlOrigin.Column, fresh, StringComparison.Ordinal);
    }

    // ---- B2 patch 计数 ----

    [Fact]
    public void 新快照按defName和label的patch计数是数字()
    {
        var (json, _, _) = Fixture.Run("inherit", "ChildGun", "--json", "--db", Fixture.PresenceDb);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var node = doc.RootElement.GetProperty("nodes")[0].GetProperty("node");
        Assert.Equal("n/a", node.GetProperty(InheritCommand.PatchOpsName).GetString());
        Assert.Equal(2, node.GetProperty("patch_ops_defname").GetInt32());
        Assert.Equal(1, node.GetProperty("patch_ops_label").GetInt32());
    }

    [Fact]
    public void 三格并排()
    {
        var (text, _, _) = Fixture.Run("inherit", "BaseGun", "--db", Fixture.PresenceDb);
        Assert.Contains("patch_ops_defname", text, StringComparison.Ordinal);
        Assert.Contains("patch_ops_label", text, StringComparison.Ordinal);
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
        // XML 有 things.Widget.chance:chance 落到那一行;def 的值就是标签名 Widget,
        // 标签在它就在;hp 这个字段长形式里没拼出来,是确定的 no,不是 under。
        Assert.Equal(XmlOrigin.Here, XmlOf("things[0].chance"));
        Assert.Equal(XmlOrigin.Here, XmlOf("things[0].def"));
        Assert.Equal(XmlOrigin.No, XmlOf("things[0].hp"));
    }

    /// <summary>
    /// 0.5.0 路径:短形式 <c>&lt;Steel&gt;75&lt;/Steel&gt;</c> 只写标签名加一段文本。
    /// xml_written 只记路径不记内容 —— 候选多于一个时说不准。
    /// **这里不许印 here。** here 的出路是 PatchOperationReplace,而 costList[0].quality
    /// 这个节点 XML 里根本没有,Replace 会打空;那比「说不准」糟,因为它是个确定的错答案。
    /// </summary>
    [Fact]
    public void 短形式标签的文本落哪一格_候选唯一才算数()
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
        // 键格:值就是标签名 Steel。
        Assert.Equal(XmlOrigin.Here, XmlOf("costList[0].thingDef"));
        // 非键格有两个,那段文本 75 落在哪一格读不出来。
        Assert.Equal(XmlOrigin.Under("costList"), XmlOf("costList[0].count"));
        Assert.Equal(XmlOrigin.Under("costList"), XmlOf("costList[0].quality"));
        // 同是短形式,statBases 的非键格只有 value 一个,没得选。
        Assert.Equal(XmlOrigin.Here, XmlOf("statBases[0].stat"));
        Assert.Equal(XmlOrigin.Here, XmlOf("statBases[0].value"));
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

    // ---- B3 类型字段全集 ----

    [Fact]
    public void 新快照fields能分开全是null和没有这个字段()
    {
        var (nulls, _, nullCode) = Fixture.Run("fields", "ThingDef", "--path-contains", "neverSet",
                                               "--db", Fixture.PresenceDb);
        Assert.Equal(1, nullCode);
        Assert.Contains("every def of the type has them as null", nulls, StringComparison.Ordinal);
        // 两态各是 index_gap 的一行,状态词分得开(Docs/25 §20)。
        Assert.Matches(@"neverSet\s+null-on-type\s+rimsearcher code-search", nulls);

        var (missing, _, missCode) = Fixture.Run("fields", "ThingDef", "--path-contains", "noSuchFieldXYZ",
                                                 "--db", Fixture.PresenceDb);
        Assert.Equal(1, missCode);
        Assert.Contains("none of the fields the type itself declares has it either", missing, StringComparison.Ordinal);
        Assert.Matches(@"noSuchFieldXYZ\s+undeclared\s+rimsearcher code-search", missing);
        Assert.DoesNotContain(IndexGap.NullOnType, missing, StringComparison.Ordinal);

        // 这个否定的依据是一张**有深度上限**的表:类型图里有一个 471 个类型的强连通分量,
        // 摊平成路径不存在「展开完」这回事(Docs/22 第 12 节)。所以它得把自己的量程说出来 ——
        // 不说的话,「嵌套过深所以没测到」与「这个类型真没这个字段」印出来完全同形。
        Assert.Matches(@"reaches \d+ segments deep", missing);
    }

    [Fact]
    public void 导入把新层写进库()
    {
        using var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Fixture.PresenceDb};Pooling=False");
        raw.Open();
        using var cmd = raw.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM xml_written";
        Assert.True((long)cmd.ExecuteScalar()! > 0);
        cmd.CommandText = DeclaredPathsSql + " WHERE n.name = 'ThingDef' AND d.path = 'neverSet'";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT patch_ops_defname FROM xml_nodes WHERE def_name = 'ChildGun'";
        Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
    }

    /// <summary>
    /// xml 列读的是打补丁之前的 XML,所以 PatchOperationAdd 加进来的一行在那里报 no,
    /// 而 no 的出路是 Add —— 会插出第二份。实证:官方 Royalty 给 PowerClaw 加了
    /// recipeMaker.researchPrerequisite,xml 列报 no。
    ///
    /// 报数只在非 0 时印(06「patch 溯源」的口径)。**沉默那一半也要钉**:这个数两头都
    /// 低估(按 thingClass 或通配符寻址的 xpath 不留痕迹;三样都没有的普通 def 连
    /// xml_nodes 都不进),要是哪天它变成无条件印,常驻免责声明会把非 0 那次的分量冲掉。
    /// </summary>
    [Fact]
    public void 被patch点过名才报数()
    {
        var (chatty, _, _) = Fixture.Run("get", "ChildGun", "--defaults", "--db", Fixture.PresenceDb);
        Assert.Contains("3 patch xpaths name this def; the 'xml' column above was read before patches ran. "
                        + "'rimsearcher inherit ChildGun' breaks the count down", chatty);

        // OtherGun 不在 xml_nodes 里(既没有 Name= 也没有 ParentName、又不 abstract),
        // 于是连计数都没有 —— 这一半沉默,常驻的那句话在 --help 与 --defaults 的说明里。
        var (quiet, _, _) = Fixture.Run("get", "OtherGun", "--defaults", "--db", Fixture.PresenceDb);
        Assert.DoesNotContain("patch xpath", quiet);
    }

    /// <summary>
    /// 大小写不敏感的查询要用得上索引,索引本身就得是 NOCASE 的 —— schema 里
    /// <c>idx_fv_leaf_nc</c> 那一对的注释早就写下了这条规则,而 <c>type_fields</c> 漏了。
    /// 代价在这张表上最大:纯官方 1373 万行,BINARY 索引一次都没被用上,SQLite 转去
    /// 覆盖扫 path 索引,实测 12.2s;换成 NOCASE 后 0.119s。
    ///
    /// 钉的是索引定义而不是查询计划:fixture 那张表只有几行,优化器在小表上本来就不用索引,
    /// 计划断言在这里恒真,测不出这件事。
    /// </summary>
    /// <summary>声明层现行形状的读法,与 <c>SnapshotDb.TypeDeclaredPaths</c> 同一条路。</summary>
    private const string DeclaredPathsSql =
        "SELECT COUNT(*) FROM type_names n "
        + "JOIN type_subtrees ts ON ts.type_id = n.id "
        + "JOIN subtree_paths sp ON sp.subtree_id = ts.subtree_id "
        + "JOIN type_field_paths d ON d.id = sp.path_id";

    /// <summary>
    /// 路径提进字典表之后,同一条路径在多个 def 类型下必须共用一个 id —— 那正是省下来的东西
    /// (纯官方 1373 万行只有 52 万个不同 path)。字典没去重的话表还是那么大,而这里
    /// **一样绿**:查询结果与去重与否无关。
    /// </summary>
    [Fact]
    public void 路径字典跨def类型共用一条()
    {
        using var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Fixture.PresenceDb};Pooling=False");
        raw.Open();
        using var cmd = raw.CreateCommand();

        cmd.CommandText = "SELECT COUNT(*) FROM type_field_paths";
        var distinctPaths = (long)cmd.ExecuteScalar()!;
        cmd.CommandText = "SELECT COUNT(DISTINCT path) FROM type_field_paths";
        Assert.Equal(distinctPaths, (long)cmd.ExecuteScalar()!);

        // fixture 里 ThingDef 与 HediffDef 都声明了 defName,两条引用指向同一个字典行。
        cmd.CommandText = "SELECT COUNT(DISTINCT sp.path_id) FROM subtree_paths sp "
                        + "JOIN type_field_paths d ON d.id = sp.path_id WHERE d.path = 'defName'";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT COUNT(DISTINCT n.name) FROM type_names n "
                        + "JOIN type_subtrees ts ON ts.type_id = n.id "
                        + "JOIN subtree_paths sp ON sp.subtree_id = ts.subtree_id "
                        + "JOIN type_field_paths d ON d.id = sp.path_id WHERE d.path = 'defName'";
        Assert.True((long)cmd.ExecuteScalar()! > 1);
    }

    /// <summary>
    /// <c>leaf</c> 住在路径字典上,而且逐行等于 <c>NoiseFilter.Leaf(path)</c>。
    ///
    /// 挪得动的**理由**是它是 path 的纯函数(单一算处,就在导入那一行),不是「实测每条
    /// path 底下只有一个值」—— 后者是这几份快照的性质。所以这条闸重算一遍那个函数来比,
    /// 而不是去查「有没有一条 path 配了两个 leaf」:那种查法在函数被改坏时一样绿。
    /// </summary>
    [Fact]
    public void leaf住在路径字典上且就是那个纯函数()
    {
        using var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Fixture.PresenceDb};Pooling=False");
        raw.Open();
        using var cmd = raw.CreateCommand();

        // 大表上不许再有这一列 —— 有的话下面的比对一样绿,而库一个字节都没省。
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('field_values') WHERE name = 'leaf'";
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);

        cmd.CommandText = "SELECT path, leaf FROM field_value_paths";
        var checked_ = 0;
        using (var rd = cmd.ExecuteReader())
            while (rd.Read())
            {
                Assert.Equal(NoiseFilter.Leaf(rd.GetString(0)), rd.GetString(1));
                checked_++;
            }
        // 空表会让上面的循环一次不跑而这条测试照绿 —— 那正是「没注入」的样子。
        Assert.True(checked_ > 0, "路径字典是空的,上面的逐行比对一次都没发生");
    }

    /// <summary>
    /// 值进字典之后,<c>NULL</c> 与空串必须还分得开:给 NULL 也发个号的话,「这个字段没有值」
    /// 与「它的值是空串」在库里就同形了。
    ///
    /// 实测七份快照 150 万行里 <c>value</c> 一条 NULL 都没有(空串 1289 条),所以这条空值
    /// 通路没有天然样本 —— 它是靠自己往库里种一条来测的。种进去之后 <c>get</c> 还看得见
    /// 那一行,才说明取值那个 JOIN 是外连:换成内连,这一行会整个消失而不是显示为空。
    /// </summary>
    [Fact]
    public void 值字典不给NULL发号()
    {
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Fixture.PresenceDb};Pooling=False"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();

            cmd.CommandText = "SELECT COUNT(*) FROM field_value_values WHERE value IS NULL";
            Assert.Equal(0L, (long)cmd.ExecuteScalar()!);

            // 字典真去重了 —— 自己不许有重复值。
            cmd.CommandText = "SELECT COUNT(*) FROM field_value_values";
            var dict = (long)cmd.ExecuteScalar()!;
            Assert.True(dict > 0, "值字典是空的,下面几条钉不到东西");
            cmd.CommandText = "SELECT COUNT(DISTINCT value) FROM field_value_values";
            Assert.Equal(dict, (long)cmd.ExecuteScalar()!);

            // 大表上不许再有这一列 —— 有的话上面几条一样绿,而库一个字节都没省。
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('field_values') WHERE name = 'value'";
            Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
        }

        // 落在本进程独占的根里,用完**不删** —— 这份库刚被 CLI 读过,而读侧开着
        // mmap_size=1G,紧接着 File.Delete 在 Windows 上会间歇性地撞上「文件正被
        // 另一个进程使用」(实测十轮红一次)。清理交给 TestTemp 下次启动按 pid 做。
        var path = Path.Combine(TestTemp.Root, $"rs-null-value-{Guid.NewGuid():N}.db");
        File.Copy(Fixture.PresenceDb, path, overwrite: true);
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = """
                INSERT INTO field_value_paths (id, path, leaf)
                  SELECT MAX(id) + 1, 'nullprobe', 'nullprobe' FROM field_value_paths;
                INSERT INTO field_values (def_id, path_id, value_id, is_default)
                  SELECT (SELECT id FROM defs WHERE def_name = 'ChildGun'),
                         (SELECT id FROM field_value_paths WHERE path = 'nullprobe'), NULL, 0;
                """;
            cmd.ExecuteNonQuery();

            // 注入真发生了吗 —— 没有这一条,下面那个 Contains 测的是别的东西。
            cmd.CommandText = "SELECT COUNT(*) FROM field_values WHERE value_id IS NULL";
            Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        }

        var (probe, _, _) = Fixture.Run("get", "ChildGun", "--defaults", "--db", path);
        Assert.Contains("nullprobe", probe, StringComparison.Ordinal);
    }


    /// <summary>
    /// 子树分解是**无损的集合去重**,不是内容取舍:复原出来的 (类型, 路径) 对必须与导出
    /// 声明的逐条相同。压缩比是这一条的副产品 —— 先钉相等,再钉省下来了。
    ///
    /// 钉的是从库里读回来的东西,不是导入时那几个内存字典 —— 后者与「写没写进去」分不开。
    /// </summary>
    [Fact]
    public void 子树分解复原回原样()
    {
        // fixture 的两条 type_fields 记录,逐字取自 Fixture 里那两个数组。
        var declared = new HashSet<string>(StringComparer.Ordinal)
        {
            "ThingDef|burstCount", "ThingDef|costList[0].count", "ThingDef|costList[0].quality",
            "ThingDef|costList[0].thingDef", "ThingDef|damage", "ThingDef|defName",
            "ThingDef|descriptionHyperlinks[0].def", "ThingDef|label", "ThingDef|neverSet",
            "ThingDef|speed", "ThingDef|statBases[0].stat", "ThingDef|statBases[0].value",
            "ThingDef|thingClass", "ThingDef|things[0].chance", "ThingDef|things[0].def",
            "ThingDef|things[0].hp",
            "HediffDef|defName", "HediffDef|label", "HediffDef|maxSeverity",
        };

        using var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Fixture.PresenceDb};Pooling=False");
        raw.Open();
        using var cmd = raw.CreateCommand();
        cmd.CommandText = "SELECT n.name, d.path FROM type_names n "
                        + "JOIN type_subtrees ts ON ts.type_id = n.id "
                        + "JOIN subtree_paths sp ON sp.subtree_id = ts.subtree_id "
                        + "JOIN type_field_paths d ON d.id = sp.path_id";
        var restored = new List<string>();
        using (var rd = cmd.ExecuteReader())
            while (rd.Read()) restored.Add(rd.GetString(0) + "|" + rd.GetString(1));

        // 行数也钉:集合相等挡不住同一对被摊开两遍(那正是「子树重叠」会有的样子)。
        Assert.Equal(declared.Count, restored.Count);
        Assert.Equal(declared, restored.ToHashSet(StringComparer.Ordinal));

        // 省下来的:defName 与 label 各自那棵单路径子树被两个类型共用,于是子树数
        // 少于 (类型, 首段) 对数。fixture 小,差额就是这两棵。
        cmd.CommandText = "SELECT COUNT(*) FROM type_subtrees";
        var pairs = (long)cmd.ExecuteScalar()!;
        cmd.CommandText = "SELECT COUNT(DISTINCT subtree_id) FROM subtree_paths";
        Assert.Equal(pairs - 2, (long)cmd.ExecuteScalar()!);
    }

    /// <summary>
    /// NOCASE 的查找配 BINARY 的索引等于没有索引。这条规则本来只长在 <c>idx_fv_leaf_nc</c>
    /// 的注释里,而注释挡不住下一张表 —— <c>type_fields</c> 就那么踩了,1373 万行上 12.2s。
    ///
    /// 闸查的是 <see cref="SnapshotSchema.NoCaseLookups"/> 那份清单与 schema 对不对得上,
    /// 外加「不加索引的那些必须写出理由」。**查不出「新表忘了登记」** —— 那一条没有机器兜底,
    /// 清单自己的注释里说了。
    /// </summary>
    [Fact]
    public void 每处NOCASE查找都登记了索引落点()
    {
        var indexes = SnapshotSchema.Indexes;
        foreach (var l in SnapshotSchema.NoCaseLookups)
        {
            if (l.NeedsIndex)
                Assert.Contains($"ON {l.Table}({l.Column} COLLATE NOCASE", indexes, StringComparison.Ordinal);
            else
                // 「不加」也是个决定,得带着理由 —— 沉默会被下一个人读成「还没来得及加」。
                Assert.False(string.IsNullOrWhiteSpace(l.Why), $"{l.Table}.{l.Column}");
        }

        // `--path-contains` 的谓词只有 `LIKE '%x%'`,前缀不定,索引帮不上忙 —— 它唯一的
        // 作用是把优化器骗去扫自己(旧形状上那条 1.7G,12.2s 那条计划)。加回来会静默变慢。
        // 拆表之后这个谓词落在 type_field_paths 上,所以钉的是它。
        Assert.DoesNotContain("ON type_field_paths(path)", indexes, StringComparison.Ordinal);

        // 查询侧确实是 NOCASE —— 两边任何一侧改了都要一起改,否则索引又失效。
        Assert.Contains("COLLATE NOCASE", SnapshotDb.TypeDeclaredPathsWhere, StringComparison.Ordinal);

        // 名册的索引必须留在**导入中途**那一批里。挪回下面这批 schema 一样对、查询一样快,
        // 只有导入慢 —— 实测 1475 秒对建两条索引的代价,而慢不报错,没有闸就读不出来。
        Assert.DoesNotContain("ON injection_keys(", indexes, StringComparison.Ordinal);
        Assert.Contains("ON injection_keys(def_name_id)", SnapshotSchema.InjectionKeyIndexes,
                        StringComparison.Ordinal);
        Assert.Contains("ON injection_keys(def_type_id, def_name_id, suggested_path_id)",
                        SnapshotSchema.InjectionKeyIndexes, StringComparison.Ordinal);

        // 字典表反过来**不许**中途建索引:导入期那次自查用的是内存里的同一份字典,
        // 不回库找号。真加了不会错,只是白建三条 —— 而白建的索引没人读得出来。
        Assert.DoesNotContain("ON injection_key_", SnapshotSchema.InjectionKeyIndexes,
                              StringComparison.Ordinal);
    }

    // ---- 0.6.0:记下文本之后,短形式多候选格能分开 ----

    private static string XmlOfJson(string json, string path)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var fields = doc.RootElement.GetProperty("defs")[0].GetProperty("fields");
        foreach (var row in fields.EnumerateArray())
            if (row.GetProperty("path").GetString() == path)
                return row.GetProperty(XmlOrigin.Column).GetString()!;
        Assert.Fail($"no field {path}");
        return "";
    }

    [Fact]
    public void 记下文本后短形式恰好一格匹配就是here其余是no()
    {
        var (json, _, _) = Fixture.Run("get", "ChildGun", "--defaults", "--json",
                                       "--db", Fixture.PresenceTextDb);
        // 键格、显式子路径、长形式没拼到、候选只剩一格:一个字不动。
        Assert.Equal(XmlOrigin.Here, XmlOfJson(json, "costList[0].thingDef"));
        Assert.Equal(XmlOrigin.Here, XmlOfJson(json, "statBases[0].stat"));
        Assert.Equal(XmlOrigin.Here, XmlOfJson(json, "statBases[0].value"));
        Assert.Equal(XmlOrigin.Here, XmlOfJson(json, "things[0].chance"));
        Assert.Equal(XmlOrigin.Here, XmlOfJson(json, "things[0].def"));
        Assert.Equal(XmlOrigin.No, XmlOfJson(json, "things[0].hp"));
        Assert.Equal(XmlOrigin.Parent, XmlOfJson(json, "speed"));
        Assert.Equal(XmlOrigin.Under("descriptionHyperlinks"),
                     XmlOfJson(json, "descriptionHyperlinks[0].def"));
        // 文本 75 对上 count,quality 确定没写。
        Assert.Equal(XmlOrigin.Here, XmlOfJson(json, "costList[0].count"));
        Assert.Equal(XmlOrigin.No, XmlOfJson(json, "costList[0].quality"));
    }

    [Fact]
    public void 记下文本后零匹配退回under不许判no()
    {
        var (json, _, _) = Fixture.Run("get", "PatchedGun", "--defaults", "--json",
                                       "--db", Fixture.PresenceTextDb);
        Assert.Equal(XmlOrigin.Here, XmlOfJson(json, "costList[0].thingDef"));
        Assert.Equal(XmlOrigin.Under("costList"), XmlOfJson(json, "costList[0].count"));
        Assert.Equal(XmlOrigin.Under("costList"), XmlOfJson(json, "costList[0].quality"));
    }

    [Fact]
    public void 记下文本后多于一格匹配都退回under()
    {
        var (json, _, _) = Fixture.Run("get", "TwinGun", "--defaults", "--json",
                                       "--db", Fixture.PresenceTextDb);
        Assert.Equal(XmlOrigin.Here, XmlOfJson(json, "costList[0].thingDef"));
        Assert.Equal(XmlOrigin.Under("costList"), XmlOfJson(json, "costList[0].count"));
        Assert.Equal(XmlOrigin.Under("costList"), XmlOfJson(json, "costList[0].quality"));
    }

    /// <summary>
    /// 空短标签 <c>&lt;Steel /&gt;</c>:文本是空串,一个字符都没写给任何一格。
    ///
    /// 空串谁都对不上,于是走零匹配那一档 —— 全部候选退回 under。官方 Data 里这样的
    /// 标签有 96 处(fixedInventory 一类),它们的候选格恰好都停在代码默认值上,所以
    /// 「说不准」与「其实都没写」在输出里同形;这里钉的是不许因此改判成确定的 no。
    /// </summary>
    [Fact]
    public void 空短标签没有文本落格全都说不准()
    {
        var (json, _, _) = Fixture.Run("get", "BareGun", "--defaults", "--json",
                                       "--db", Fixture.PresenceTextDb);
        Assert.Equal(XmlOrigin.Here, XmlOfJson(json, "costList[0].thingDef"));
        Assert.Equal(XmlOrigin.Under("costList"), XmlOfJson(json, "costList[0].count"));
        Assert.Equal(XmlOrigin.Under("costList"), XmlOfJson(json, "costList[0].quality"));
    }

    // ---- 0.7.0:路径取自打完补丁的 XML ----

    /// <summary>
    /// 补丁加进来的一行,出路是 Replace 而不是 Add —— 这与老快照上它报的 <c>no</c> 正相反。
    /// 但也不能并进 here:代价不一样,你的 patch 从此依赖那个加它的 mod 在场。
    /// </summary>
    [Fact]
    public void 补丁加的行报here带patch后缀()
    {
        var (json, _, _) = Fixture.Run("get", "PatchGun", "--defaults", "--json",
                                       "--db", Fixture.PresencePatchDb);
        Assert.Equal(XmlOrigin.Here, XmlOfJson(json, "damage"));
        Assert.Equal(XmlOrigin.Here + XmlOrigin.PatchSuffix,
                     XmlOfJson(json, "recipeMaker.researchPrerequisite"));
        Assert.Equal(XmlOrigin.Parent + XmlOrigin.PatchSuffix, XmlOfJson(json, "speed"));
        // 这一行没被补丁动过,后缀不许跟着整个 def 走。
        Assert.Equal(XmlOrigin.Here, XmlOfJson(json, "costList[0].count"));
        Assert.Equal(XmlOrigin.No, XmlOfJson(json, "costList[0].quality"));
    }

    /// <summary>整个短形式标签是补丁加的:承接文本那一格带后缀,另一格仍是确定的 no。</summary>
    [Fact]
    public void 补丁加的短形式标签后缀落在承接文本那一格()
    {
        var (json, _, _) = Fixture.Run("get", "PatchListGun", "--defaults", "--json",
                                       "--db", Fixture.PresencePatchDb);
        Assert.Equal(XmlOrigin.Here + XmlOrigin.PatchSuffix, XmlOfJson(json, "costList[0].thingDef"));
        Assert.Equal(XmlOrigin.Here + XmlOrigin.PatchSuffix, XmlOfJson(json, "costList[0].count"));
        Assert.Equal(XmlOrigin.No, XmlOfJson(json, "costList[0].quality"));
    }

    /// <summary>
    /// 0.6.0 及更早的快照收的是打补丁之前的原文 —— 不许给它们印后缀。
    /// 「没有后缀」在那些库上的含义是「分不开」,不是「没被补丁加过」;那半句由
    /// patch xpath 计数那条通知说,它只在这一档出现。
    /// </summary>
    [Fact]
    public void 老快照不印补丁后缀但仍报xpath计数()
    {
        var (text, _, _) = Fixture.Run("get", "ChildGun", "--defaults", Fixture.PresenceTextArg);
        Assert.DoesNotContain(XmlOrigin.PatchSuffix, text);
        Assert.Contains("3 patch xpaths name this def", text);

        var (patched, _, _) = Fixture.Run("get", "PatchGun", "--defaults", Fixture.PresencePatchArg);
        Assert.Contains(XmlOrigin.PatchSuffix, patched);
        Assert.DoesNotContain("patch xpaths name this def", patched);
    }

    [Fact]
    public void 零五快照导入后文本列是空的()
    {
        using var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Fixture.PresenceDb};Pooling=False");
        raw.Open();
        using var cmd = raw.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM xml_written WHERE inner_text IS NOT NULL";
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void 零六快照导入后文本列与路径等长()
    {
        using var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Fixture.PresenceTextDb};Pooling=False");
        raw.Open();
        using var cmd = raw.CreateCommand();
        cmd.CommandText = "SELECT path, inner_text FROM xml_written WHERE node_key = 'ChildGun' AND path = 'costList.Steel'";
        using var rd = cmd.ExecuteReader();
        Assert.True(rd.Read());
        Assert.Equal("75", rd.GetString(1));
    }
}
