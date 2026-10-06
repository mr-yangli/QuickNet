using FastSql.Core.Schema;
using FastSql.Metadata;
using FastSql.Providers;
using FastSql.Providers.Gbase8s;
using FastSql.Providers.SqlServer;
using FastSql.Providers.Sqlite;
using FastSql.Schema;
using FastSql.Sql;
using FastSql.TestInfra;
using Xunit;

namespace FastSql.Tests.Dialects;

/// <summary>§9.5 列级结构同步的离线半：把"库里现形状"手写成读数，比出计划，再断言发射文本。
/// 这一格必须能离线穷举——真库上一趟同步只走一种形状，而这里的十几种形状各只对应一句要发的 DDL
/// 或一句<em>不该</em>发的。幂等裁判（<see cref="The_current_shape_reads_back_as_no_change"/>）尤其如此：
/// 它断言的是归一，而"第二趟零动作"在库上只是这条归一的一个读数。</summary>
public sealed class StructureSyncTests
{
    [Table("FASTSQL_SHAPE", Comment = "形状表")]
    [Index("IX_FASTSQL_SHAPE_Title", "Title")]
    sealed class Shape
    {
        public int Id { get; set; }

        [Column(Length = 32, Comment = "标题")]
        public string? Title { get; set; }

        public decimal Amount { get; set; }

        [Column(Precision = 10, Scale = 2)]
        public decimal Price { get; set; }

        public DateTime CreatedAt { get; set; }

        public int Quantity { get; set; }
    }

    /// <summary>契约里一个字备注都没写的实体：它专门盯"null 不等于要求清空"那一条。</summary>
    [Table("FASTSQL_PLAIN")]
    sealed class Plain
    {
        public int Id { get; set; }

        public string? Name { get; set; }
    }

    static readonly IDbProvider Sql = SqlServer.Provider(SqlServer.NoDriverFactory.Instance);
    static readonly ISchemaReader Reader = new SqlServerSchemaReader();

    static EntityContract Contract<T>() where T : class
        => new EntitySchemaRegistry(new ContractResolver(Sql)).Contract<T>();

    // ── 归一：现形状读回来必须算"没变化" ────────────────────────────────

    /// <summary>把契约的现形状按<em>目录会报的字形</em>读回来：小写、<c>numeric</c> 而不是 <c>decimal</c>、
    /// 时间类型的精度在标度那一格而不在类型名里。零动作是这一格的裁判，而它红一次就意味着下一版
    /// 真库同步会在一张根本没变过的表上发一句 <c>ALTER</c>。</summary>
    [Fact]
    public void The_current_shape_reads_back_as_no_change()
    {
        var plan = Diff(CurrentStore());

        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.NotJudged);
        Assert.Empty(plan.Refused);
    }

    /// <summary>目录字形与发射字形的四种对应。折不进来的话每一家都会在自己的表上找到假差：</summary>
    [Theory]
    [InlineData("datetime2(7)", "DATETIME2(7)")]
    [InlineData("nvarchar(max)", "NVARCHAR(MAX)")]
    [InlineData("varbinary(max)", "VARBINARY(MAX)")]
    [InlineData("binary(8)", "rowversion")]
    [InlineData("numeric(18,4)", "DECIMAL(18,4)")]
    public void Catalog_spelling_and_emitted_spelling_are_the_same_shape(string fromCatalog, string emitted)
        => Assert.True(Reader.ColumnShapeMatches(Declared(emitted), Col("C", fromCatalog)));

    // ── 六格：哪种形状差发哪一句 ────────────────────────────────────────

    /// <summary>缺列补列，顺带补它欠的备注与落在它上面的那条索引——三句在同一趟里，
    /// 因为 <c>CREATE INDEX</c> 引用的列必须已经存在。</summary>
    [Fact]
    public void A_column_the_store_does_not_have_is_added_with_its_comment_and_index()
    {
        // 索引读数为空是这份夹具唯一自洽的摆法：库里那一列都不在，跨它的索引不可能存在。
        var plan = Diff(CurrentStore(drop: "Title"), indexes: []);

        Assert.Equal(["Title"], plan.AddColumns.Select(c => c.ColumnName));
        Assert.Empty(plan.AlterColumns);
        Assert.Equal(["Title"], plan.ColumnComments.Select(c => c.ColumnName));
        Assert.Equal(["IX_FASTSQL_SHAPE_Title"], plan.CreateIndexes.Select(i => i.Name));
    }

    /// <summary>标度与长度走的都是 <c>ALTER COLUMN</c> 那一句：T-SQL 没有"只改标度"的语法，
    /// 整列定义重写是唯一的形状（所以可空性也一起被重写，见 <see cref="Nullability_alone_alters"/>）。</summary>
    [Fact]
    public void A_scale_change_is_an_alter()
        => Assert.Equal(["Amount"], Diff(CurrentStore(Amount: "numeric(18,2)")).AlterColumns.Select(c => c.ColumnName));

    [Fact]
    public void A_length_change_is_an_alter()
        => Assert.Equal(["Title"], Diff(CurrentStore(Title: "nvarchar(64)")).AlterColumns.Select(c => c.ColumnName));

    [Fact]
    public void Nullability_alone_alters()
        => Assert.Equal(["Title"], Diff(CurrentStore(titleNullable: false)).AlterColumns.Select(c => c.ColumnName));

    /// <summary>被 ALTER 的那一列<em>同时</em>进备注清单：T-SQL 的 <c>ALTER COLUMN</c> 会连带删掉那一列的
    /// 扩展属性。少了这一条，"改了长度"这一趟以"备注没了"收尾，而下一趟读到空备注只会补备注，
    /// 永远不会说这一趟丢过它——幂等裁判照样绿，那是假绿。</summary>
    [Fact]
    public void An_altered_column_carries_its_comment_back_into_the_plan()
        => Assert.Equal(["Title"], Diff(CurrentStore(Title: "nvarchar(64)")).ColumnComments.Select(c => c.ColumnName));

    [Fact]
    public void A_changed_comment_is_re_emitted_without_an_alter()
    {
        var plan = Diff(CurrentStore(titleComment: "旧标题"));

        Assert.Empty(plan.AlterColumns);
        Assert.Equal(["Title"], plan.ColumnComments.Select(c => c.ColumnName));
    }

    [Fact]
    public void A_changed_table_comment_is_re_emitted()
        => Assert.True(Diff(CurrentStore(), comment: "旧的表备注").EmitTableComment);

    /// <summary>契约没写备注（null）而库里写着一条：<b>一条都不发</b>。null 说的是"用户没说过"，
    /// 把它读成"要求清空"就是让同步去删人手写进库里的东西（列与表两格同一条判据）。</summary>
    [Fact]
    public void A_comment_the_contract_never_declared_is_left_alone()
    {
        var contract = Contract<Plain>();
        var store = new SchemaTable(contract.Table, [
            Col("Id", "int", identity: true, pk: true),
            Col("Name", "nvarchar(max)", nullable: true, comment: "库里手写的那一条"),
        ])
        { Comment = "库里手写的那一条", Indexes = [] };

        var plan = StructureDiff.Build(contract, store, Reader, IdentifierPolicy.SqlServer);

        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.ColumnComments);
        Assert.False(plan.EmitTableComment);
    }

    // ── 三条拒：读得出而故意不发 ────────────────────────────────────────

    /// <summary>非空的新列：库里已有的行没有值可填，而契约里根本没有默认值那一格（§10.3 的字段清单是闭集），
    /// 所以 Core 变不出一句 <c>DEFAULT</c>。硬发的后果是"这一趟在第 N 张表上响，而前 N-1 张已经改完"。</summary>
    [Fact]
    public void A_not_null_column_with_no_default_is_refused_and_named()
    {
        var plan = Diff(CurrentStore(drop: "Quantity"));

        Assert.Empty(plan.AddColumns);
        Assert.Single(plan.Refused, r => r.Contains("Quantity", StringComparison.Ordinal) && r.Contains("NOT NULL", StringComparison.Ordinal));
    }

    [Fact]
    public void An_extra_store_column_is_never_dropped()
    {
        var plan = Diff([.. CurrentStore(), Col("Legacy", "ntext")]);

        Assert.Single(plan.Refused, r => r.Contains("Legacy", StringComparison.Ordinal) && r.Contains("不删列", StringComparison.Ordinal));
    }

    /// <summary>主键列与自增列的形状差一律拒：改它们要连约束、外键与取值机制一起重建，
    /// 那是比"改一颗标度"贵一个数量级的动作。两格分开钉：形状差、与主键成员差。</summary>
    [Fact]
    public void An_identity_column_with_a_different_type_is_refused()
    {
        var plan = Diff(CurrentStore(Id: "bigint"));

        Assert.Empty(plan.AlterColumns);
        Assert.Contains(plan.Refused, r => r.Contains("Id", StringComparison.Ordinal));
    }

    [Fact]
    public void A_different_primary_key_membership_is_refused()
    {
        var plan = Diff(CurrentStore(pkOn: "Amount"));

        Assert.Contains(plan.Refused, r => r.Contains("主键不一致", StringComparison.Ordinal));
    }

    [Fact]
    public void An_extra_store_index_is_never_dropped()
    {
        var plan = Diff(CurrentStore(), indexes: [
            new SchemaIndex("IX_FASTSQL_SHAPE_Title", false, ["Title"]),
            new SchemaIndex("IX_extra_on_amount", false, ["Amount"]),
        ]);

        Assert.Empty(plan.CreateIndexes);
        Assert.Single(plan.Refused, r => r.Contains("IX_extra_on_amount", StringComparison.Ordinal));
    }

    [Fact]
    public void An_index_name_already_taken_by_other_columns_is_not_overwritten()
    {
        var plan = Diff(CurrentStore(), indexes: [new SchemaIndex("IX_FASTSQL_SHAPE_Title", false, ["Amount"])]);

        Assert.Empty(plan.CreateIndexes);
        Assert.Single(plan.Refused, r => r.Contains("IX_FASTSQL_SHAPE_Title", StringComparison.Ordinal));
    }

    /// <summary>人给索引改过名而列没换：<b>那不算缺索引</b>。按名字判的那把尺会在每次改名后重建一条，
    /// 而库里从此有两条同列的索引。</summary>
    [Fact]
    public void An_index_renamed_but_on_the_same_columns_is_present()
    {
        var plan = Diff(CurrentStore(), indexes: [new SchemaIndex("IX_shape_title_v2", false, ["Title"])]);

        Assert.Empty(plan.CreateIndexes);
        Assert.Empty(plan.Refused);
    }

    // ── 删索引：另一颗开关，默认关 ─────────────────────────────────────

    /// <summary>开了那一档，库里多出的那一条就进删除清单，而<em>不再</em>进"要人工"：同一件事报两遍
    /// 会让"这一趟到底删不删"在报告里读不出来。补建那一半一格不动。</summary>
    [Fact]
    public void An_extra_store_index_is_dropped_when_the_switch_is_on()
    {
        var plan = Diff(CurrentStore(), indexes: [
            new SchemaIndex("IX_FASTSQL_SHAPE_Title", false, ["Title"]),
            new SchemaIndex("IX_extra_on_amount", false, ["Amount"]),
        ], dropIndexes: true);

        Assert.Equal(["IX_extra_on_amount"], plan.DropIndexes.Select(i => i.Name));
        Assert.Empty(plan.CreateIndexes);
        Assert.Empty(plan.Refused);
    }

    /// <summary>默认那一档下<em>删除清单必须是空的</em>：这一条与上面那一条是同一份读数、只差一颗开关，
    /// 它钉的是"没开就是不发"，而不是"没开也发了但没记进清单"。</summary>
    [Fact]
    public void The_default_switch_leaves_the_extra_index_in_the_manual_list()
    {
        var plan = Diff(CurrentStore(), indexes: [
            new SchemaIndex("IX_FASTSQL_SHAPE_Title", false, ["Title"]),
            new SchemaIndex("IX_extra_on_amount", false, ["Amount"]),
        ]);

        Assert.Empty(plan.DropIndexes);
        Assert.Single(plan.Refused, r => r.Contains("IX_extra_on_amount", StringComparison.Ordinal));
    }

    /// <summary>唯一约束即便开了那一档也不删：<c>DROP INDEX</c> 删不动约束，发出去只会以报错收尾，
    /// 而这一趟的其余句子已经发出去了——那是"半套状态"，比不删坏得多。它进"要人工"并指名它是约束。</summary>
    [Fact]
    public void A_unique_constraint_is_not_dropped_even_with_the_switch_on()
    {
        var plan = Diff(CurrentStore(), indexes: [
            new SchemaIndex("IX_FASTSQL_SHAPE_Title", false, ["Title"]),
            new SchemaIndex("UQ_shape_amount", true, ["Amount"]) { IsConstraint = true },
        ], dropIndexes: true);

        Assert.Empty(plan.DropIndexes);
        Assert.Single(plan.Refused, r => r.Contains("UQ_shape_amount", StringComparison.Ordinal) && r.Contains("约束", StringComparison.Ordinal));
    }

    /// <summary>撞名的那一条（库里那颗同名索引管的是别的列）<b>不能被顺手删掉再按契约建一条</b>：
    /// 删了它那一列上的查询当场失去索引，而这一趟紧接着建的又是另一颗。开关开不开，这一格都是"要人工"。</summary>
    [Fact]
    public void A_name_clash_is_still_manual_with_the_switch_on()
    {
        var plan = Diff(CurrentStore(), indexes: [new SchemaIndex("IX_FASTSQL_SHAPE_Title", false, ["Amount"])], dropIndexes: true);

        Assert.Empty(plan.DropIndexes);
        Assert.Empty(plan.CreateIndexes);
        Assert.Single(plan.Refused, r => r.Contains("IX_FASTSQL_SHAPE_Title", StringComparison.Ordinal));
    }

    /// <summary>这一家读不回索引清单（<c>Indexes</c> 为 null）时，<b>开了删索引那一档也一条不删</b>：
    /// "读不出"不是"库里没有"，把 null 当成空清单就是删掉那一张表上的全部索引。判不了那一句照旧指名。</summary>
    [Fact]
    public void A_family_that_cannot_read_indexes_never_drops_any()
    {
        var contract = Contract<Shape>();
        var blind = new SchemaTable(contract.Table, CurrentStore()) { Comment = "形状表" };

        var plan = StructureDiff.Build(contract, blind, Reader, IdentifierPolicy.SqlServer, dropIndexes: true);

        Assert.Empty(plan.DropIndexes);
        Assert.Contains(plan.NotJudged, n => n.Contains("索引清单", StringComparison.Ordinal));
    }

    /// <summary>开了那一档而形状读回来正是契约那一份：幂等裁判不变红。这一条是"删了又建、建了又删"
    /// 那种来回摆动的钉——同列序列换了名也算同一条，所以改名不会因这一档而被删。</summary>
    [Fact]
    public void The_current_index_list_with_the_switch_on_is_still_no_change()
    {
        var plan = Diff(CurrentStore(), dropIndexes: true);

        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.DropIndexes);
    }

    // ── 判不了：六家今天的样子 ────────────────────────────────────────

    /// <summary>读不回来的那些档进"判不了"清单并指名，<b>既不响也不假装同步过</b>（§12.2）。
    /// 带默认值的 SPI 位让六家一个字节不必改，代价是它们在列级这一档上今天只有这一份报告：
    /// 主键、列形状、列备注、索引清单、表备注五档一次报齐，而动作清单是空的。
    /// <b>可空性不在这一格</b>——它是唯一家家都读得回来的事实，所以这份夹具必须把它摆对，
    /// 摆歪了就是"在一家判不了形状库上发了一句 ALTER"。</summary>
    [Fact]
    public void A_family_that_cannot_read_reports_not_judged()
    {
        var contract = Contract<Shape>();
        var blind = new SchemaTable(contract.Table, [
            new SchemaColumn("Id", "integer", null, null, false, 1),
            new SchemaColumn("Title", "text", null, null, true, 2),
            new SchemaColumn("Amount", "numeric", null, null, false, 3),
            new SchemaColumn("Price", "numeric", null, null, false, 4),
            new SchemaColumn("CreatedAt", "timestamp", null, null, false, 5),
            new SchemaColumn("Quantity", "integer", null, null, false, 6),
        ]);

        var plan = StructureDiff.Build(contract, blind, Sqlite.Provider(SqlServer.NoDriverFactory.Instance).Schema, IdentifierPolicy.Sqlite);

        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.Refused);
        Assert.Contains(plan.NotJudged, n => n.Contains("主键列", StringComparison.Ordinal));
        Assert.Contains(plan.NotJudged, n => n.Contains("列形状", StringComparison.Ordinal));
        Assert.Contains(plan.NotJudged, n => n.Contains("列备注", StringComparison.Ordinal));
        Assert.Contains(plan.NotJudged, n => n.Contains("索引清单", StringComparison.Ordinal));
        Assert.Contains(plan.NotJudged, n => n.Contains("表备注", StringComparison.Ordinal));
    }

    [Fact]
    public void A_family_that_cannot_read_table_comments_says_so()
    {
        var contract = Contract<Shape>();

        var plan = StructureDiff.Build(contract, new SchemaTable(contract.Table, CurrentStore()) { Indexes = [] }, Reader, IdentifierPolicy.SqlServer);

        Assert.False(plan.EmitTableComment);
        Assert.Contains(plan.NotJudged, n => n.Contains("表备注", StringComparison.Ordinal));
    }

    // ── 发射文本：只有 T-SQL 需要幂等的备注句 ─────────────────────────

    /// <summary>备注那一句在 T-SQL 里必须"有则改、无则加"：<c>sp_addextendedproperty</c> 在属性已经在的时候
    /// 直接报错，而 §9.5 的备注同步发的恰恰是"属性在而值变了"的那一列。新建表那一趟走的是 <c>ELSE</c> 那一支。</summary>
    [Fact]
    public void The_TSQL_comment_statement_updates_when_the_property_already_exists()
    {
        var contract = Contract<Shape>();
        var sql = Statement(w => Assert.True(Sql.CodeFirst.TryEmitColumnComment(w, contract.Table, contract.Columns[1]))).ToString();

        Assert.Contains("IF EXISTS (SELECT 1 FROM sys.extended_properties", sql, StringComparison.Ordinal);
        Assert.Contains("sp_updateextendedproperty @name=N'MS_Description', @value=N'标题'", sql, StringComparison.Ordinal);
        Assert.Contains(" ELSE ", sql, StringComparison.Ordinal);
        Assert.Contains("sp_addextendedproperty @name=N'MS_Description', @value=N'标题'", sql, StringComparison.Ordinal);
        Assert.Contains("@level2type=N'COLUMN', @level2name=N'Title'", sql, StringComparison.Ordinal);
        Assert.Contains("COLUMNPROPERTY(OBJECT_ID(N'[dbo].[FASTSQL_SHAPE]'), N'Title', 'ColumnId')", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void The_TSQL_table_comment_statement_has_no_column_level()
    {
        var sql = Statement(w => Assert.True(Sql.CodeFirst.TryEmitTableComment(w, Contract<Shape>().Table, "形状表"))).ToString();

        Assert.Contains("AND minor_id=0", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("@level2type", sql, StringComparison.Ordinal);
        Assert.Contains("@value=N'形状表'", sql, StringComparison.Ordinal);
    }

    /// <summary>T-SQL 的删索引是"索引名 ON 表名"那一形：索引名交回的是目录原文，表名走的是
    /// <c>EmitTableName</c> 那一条既有发射路径，两边都过这一家的方括号。这里不加 <c>IF EXISTS</c> 包裹
    /// （名字来自目录，"它不在"那件事在计划层就判掉了），所以整句就是这两颗名字。</summary>
    [Fact]
    public void The_TSQL_drop_index_statement_names_the_index_and_the_table()
    {
        var sql = Statement(w => Assert.True(
            Sql.CodeFirst.TryEmitDropIndex(w, Contract<Shape>().Table, new SchemaIndex("IX_extra_on_amount", false, ["Amount"])))).ToString();

        Assert.Equal("DROP INDEX [IX_extra_on_amount] ON [dbo].[FASTSQL_SHAPE]", sql);
    }

    /// <summary>今天不接这一颗的两家交回 false 且不写一个字：Core 因此把那一条记进"判不了"，
    /// 而不是发一句空文本、也不是换一种形状硬凑（默认实现让六家在这一颗上不必改一个字节）。</summary>
    [Theory]
    [InlineData("Sqlite")]
    [InlineData("Gbase8s")]
    public void A_family_that_does_not_drop_indexes_declines(string family)
    {
        var provider = (IDbProvider) (family == "Sqlite"
            ? Sqlite.Provider(SqlServer.NoDriverFactory.Instance)
            : Gbase8s.Provider(SqlServer.NoDriverFactory.Instance));

        var writer = Statement(w => Assert.False(
            provider.CodeFirst.TryEmitDropIndex(w, Contract<Plain>().Table, new SchemaIndex("IX_plain_name", false, ["Name"]))));

        Assert.Equal(string.Empty, writer.ToString());
    }

    /// <summary>没有列注释这一档的两家交回 false 且不写一个字——Core 因此把那一格记进"判不了"，
    /// 而不是发一句空文本、也不是改走别的形状（那一条退路从前写在注里，今天由结果清单兑现）。</summary>
    [Theory]
    [InlineData("Sqlite")]
    [InlineData("Gbase8s")]
    public void A_family_without_column_comments_declines(string family)
    {
        var provider = (IDbProvider) (family == "Sqlite"
            ? Sqlite.Provider(SqlServer.NoDriverFactory.Instance)
            : Gbase8s.Provider(SqlServer.NoDriverFactory.Instance));

        var contract = Contract<Plain>();
        var writer = Statement(w => Assert.False(provider.CodeFirst.TryEmitColumnComment(w, contract.Table, contract.Columns[1])));

        Assert.Equal(string.Empty, writer.ToString());
    }

    // ── 夹具 ─────────────────────────────────────────────────────────

    /// <summary>契约的现形状，按<em>目录报的字形</em>写：小写、numeric、时间精度落在标度那一格。
    /// 每一格都能单独拧歪，拧歪的那一格就是这一趟要发的那一句。</summary>
    static List<SchemaColumn> CurrentStore(
        string? Id = null,
        string pkOn = "Id",
        string? Title = null,
        bool titleNullable = true,
        string titleComment = "标题",
        string? Amount = null,
        string? Price = null,
        string? CreatedAt = null,
        string? Quantity = null,
        string? drop = null)
    {
        var store = new List<SchemaColumn>
        {
            Col("Id", Id ?? "int", identity: true, pk: pkOn == "Id"),
            Col("Title", Title ?? "nvarchar(32)", nullable: titleNullable, comment: titleComment),
            Col("Amount", Amount ?? "numeric(18,4)", pk: pkOn == "Amount"),
            Col("Price", Price ?? "decimal(10,2)"),
            Col("CreatedAt", CreatedAt ?? "datetime2(3)"),
            Col("Quantity", Quantity ?? "int"),
        };

        return drop is null ? store : [.. store.Where(c => c.Name != drop)];
    }

    static StructurePlan Diff(IReadOnlyList<SchemaColumn> columns, string? comment = "形状表", List<SchemaIndex>? indexes = null, bool dropIndexes = false)
    {
        var contract = Contract<Shape>();
        var store = new SchemaTable(contract.Table, columns)
        {
            Comment = comment,
            // 默认给一条与契约同列序列的读数：索引那一档因此不进任何一份清单，除非用例把它拧歪。
            Indexes = indexes ?? [new SchemaIndex("IX_FASTSQL_SHAPE_Title", false, ["Title"])],
        };

        return StructureDiff.Build(contract, store, Reader, IdentifierPolicy.SqlServer, dropIndexes);
    }

    static SchemaColumn Col(string name, string type, bool nullable = false, string? comment = "", bool identity = false, bool pk = false)
        => new(name, type, null, null, nullable, 1) { Comment = comment, IsIdentity = identity, IsPrimaryKey = pk };

    /// <summary>只声明存储类型名的列契约：形状那一颗判据吃的正是它（<c>StoreTypeName</c> 非空即"显式声明"）。</summary>
    static ColumnContract Declared(string storeType) => new()
    {
        MemberName = "C",
        ClrType = typeof(string),
        ColumnName = "C",
        LengthSemantics = LengthSemantics.Char,
        Unicode = true,
        IsNullable = true,
        IsPrimary = false,
        IsIdentity = false,
        MaxChars = null,
        MaxBytes = null,
        Precision = null,
        Scale = null,
        StoreTypeName = storeType,
        Origin = new OriginTrace(MetadataLayer.Convention, "用例直给"),
    };

    static SqlWriter Statement(Action<SqlWriter> emit)
    {
        var writer = new SqlWriter(new(), [], '@');

        emit(writer);

        return writer;
    }
}
