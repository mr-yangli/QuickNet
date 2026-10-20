using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;

namespace QuickSql.Providers.ClickHouse;

/// <summary>ClickHouse 的建表与改表（§9.2 的 ICodeFirstDialect 第十家；与另九家分岔的每一处都是一句
/// "本家的引擎形状与那几族不同"）。发射形判据自 0.39.0-beta.25 起有了活库出处（server 26.9.21.9 ∶
/// 驱动 ClickHouse.Client 7.14.0，探针在仓库外临时目录，回放前后一律清场：§13.5 第十七本台账与
/// <c>obj/logs/v39-ch-replay-*.md</c>），注释两槽与跳过索引声明那几格是 beta.26 批补上的【实测】。
///
/// <para>逐条分岔，每一条都写下来路：
/// <list type="bullet">
/// <item><b>契约索引（<see cref="IndexContract"/>）整格拒，跳过索引（<see cref="SkipIndexContract"/>）走骨架声明</b>：
/// 这一族的索引不是独立语句，它是<em>建表语句里</em>列/表级 <c>INDEX 名 表达式 TYPE 档 GRANULARITY n</c>
/// 那类数据骨架声明【文档·ClickHouse：CREATE TABLE 页的 INDEX 修饰词那一节，与活库逐字回放一致：
/// 列式（<c>"Name"</c>）与表达式式（<c>toMinute("At")</c>）两半都被收、读回见
/// <c>system.data_skipping_indices</c>（replay-5）】。契约里的 <see cref="IndexContract"/> 格是
/// "表建成之后另有对象"的 OLTP 形状（唯一约束/行定位器那一族语义），进不了那一格 —— 所以
/// <see cref="EmitCreateTable"/> 遇契约索引非空<b>整格拒</b>（不逐条递给 <see cref="EmitCreateIndex"/>），
/// <see cref="EmitCreateIndex"/> 本身也抛。声明这一档走注解 <see cref="ClickHouseSkipIndexAttribute"/>，
/// 后补一颗走 <see cref="EmitAddSkipIndex"/>（<c>ALTER TABLE … ADD INDEX</c>，活库已收，replay-5）。</item>
/// <item><b>主键不发表级约束子句，而是发进 ENGINE 的存储子句</b>：另几家的形状是列清单里尾随
/// <c>CONSTRAINT … PRIMARY KEY (…)</c>，本家 MergeTree 的"主键"是<em>引擎参数</em> ——
/// <c>ENGINE = MergeTree ORDER BY (cols) PRIMARY KEY (cols)</c>【文档·ClickHouse：CREATE TABLE 页
/// PRIMARY KEY 修饰词（"the PRIMARY KEY ... must match the beginning of the ORDER BY expression"）与
/// 官方示例里 MergeTree 引擎那一节的写法】。主键列<em>内侧</em>不发任何 PK 子句：那一族有列内/列外两法
/// 且官方写明不可并用，取列外那一法就得一次都不走列内。<b>无主键发 <c>ORDER BY tuple()</c></b>：
/// 官方文档明确未指定排序键的 MergeTree 表会回 <c>ORDER BY tuple()</c>，发显式的那一形与缺省同形。
/// <c>PRIMARY KEY (…)</c> 的括号形按官方示例那一节的写法带出【推断：单列表达式括号在同一页的其他
/// 示例里出现，多列括号形没有独立示例页】。</item>
/// <item><b>可空列发 <c>Nullable(T)</c> 且不带任何 NULL/NOT NULL 修饰</b>：官方 CREATE TABLE 页把
/// "给 <c>Nullable(…)</c> 型再上 <c>NULL</c>/<c>NOT NULL</c> 修饰"读成当场报错的那一类
///【文档·ClickHouse：CREATE TABLE 页关于 Nullable 列与修饰的那一段；本次逐字核对过那一页】。
/// 所以本家的可空性分两面：<em>空的许可</em>由型包裹（<c>Nullable(</c>）表达，<em>非空的义务</em>由
/// <c>NOT NULL</c> 表达 —— 两种形状互为排除，没有第三形。这一格与另九家全部不同形：那几族的
/// Not Null/Not Null 是同一根修饰词的两支。</item>
/// <item><b>表注释走 SPI 单路（<see cref="TryEmitTableComment"/>）</b>：
/// <c>ALTER TABLE … MODIFY COMMENT '注'</c> 在活库被收（replay-5：裸 <c>COMMENT</c> 形被服务端拒回
/// <c>Expected COLUMN</c>，MODIFY 形过了）。建表语句里那条尾随 <c>COMMENT '…'</c> 的路线本笔收掉：
/// Core 的建表趟如今自带表备注那一条语句（<c>SchemaSyncer.CreateStatements</c>），两家一起发就是
/// 同一句话发两遍 —— 发字的形状归 <see cref="TryEmitTableComment"/> 一个出口，读回（
/// <c>system.tables.comment</c>，见 <see cref="ClickHouseSchemaReader"/>）与其配对。</item>
/// <item><see cref="TryEmitColumnComment"/> 接上了：<c>ALTER TABLE … COMMENT COLUMN "列" '注'</c>
/// 活库被收、<c>system.columns.comment</c> 读回原字（replay-5）。从前的"恒 false、列级备注本家写不进"
/// 是选边没接那一格，本批补上 —— <c>Comment_On_Column</c> 那颗位自此从"不声明"清单挪进声明清单。
/// 建表趟的列注释也走这一句（不是列定义里内联）：好处与另几族同构 —— Core 不必另拆一条内联链，
/// 备注同步的幂等条款（§9.5）由同一条语句形状兜住。</item>
/// <item><b>ALTER 走 <c>MODIFY COLUMN</c></b>：改列的官方动词族在 alter 页
///（https://clickhouse.com/docs/sql-reference/statements/alter ），其中改列型/属性的那支是
/// <c>MODIFY COLUMN</c> —— 与 GBase 8s 的 <c>MODIFY (…)</c> 同名而形不同：本家不带括号、
/// 后跟<em>完整列定义</em>（<see cref="Definition"/> 的原形，含 <c>Nullable(…)</c> 包裹与
/// <c>NOT NULL</c> 那半段）。<see cref="EmitAlterColumnPrecision"/> 是
/// <c>Alter_Column_Precision</c> 那颗位在本家的见证。</item>
/// </list></para>
///
/// <para><see cref="Definition"/> 在 DDL 发射点拒收 <c>rowversion</c>，与 GBase 8s 同一落点同一判据：
/// 发出去是一句库不认识的型名，比不写注释更严重（§1.2 原则 4、§5.8）。来路仍要说死：这是<em>按缺席读</em>
/// —— 官方数据类型清单里没有那型，也没有"库自动递增的列"那一档（那样列在本族无处落名，缺口整格记
/// §14 与内核 <c>InsertEngine</c> 那笔自增账——<c>InsertEngine.cs</c> 把自增列剔出 INSERT 列清单那一手）。</para>
///
/// <para><b>发射形活库判据汇总</b>（写断言时逐字对，出处 replay-5 与 beta.25 的十份 golden）：
/// 建表骨架内联 <c>INDEX "名" "列" TYPE minmax GRANULARITY 1</c> ∶
/// <c>ALTER TABLE … ADD INDEX …</c> ∶ <c>ALTER TABLE … COMMENT COLUMN "列" '注'</c> ∶
/// <c>ALTER TABLE … MODIFY COMMENT '注'</c> ∶ <c>MODIFY COLUMN</c> 完整列定义（逐字回放过）。</para></summary>
public sealed class ClickHouseCodeFirst(ClickHouseTypes types) : ICodeFirstDialect
{
    public IdentifierPolicy Identifiers => IdentifierPolicy.ClickHouse;

    public void EmitCreateTable(SqlWriter writer, EntityContract contract)
    {
        if (contract.Indexes.Count > 0)
            throw new QuickSqlException(
                "ClickHouse 的建表拒收契约索引：这一族的索引不是独立语句，而是建表语句里"
                + "列/表级 INDEX 名 表达式 TYPE 档 GRANULARITY n 的数据骨架声明，"
                + "契约里的 IndexContract 是'表建成之后另有对象'那一格的形状（唯一约束/行定位器），进不去那一格 —— "
                + "要跳过索引那一档用 [ClickHouseSkipIndex] 声明（建表骨架里带出），"
                + "普通 [Index] 注解请摘掉再建这一族的表。");

        writer.Append("CREATE TABLE ");
        EmitTable(writer, contract.Table);
        writer.Append(" (");

        var parts = new List<string>(contract.Columns.Length + contract.SkipIndexes.Count);

        foreach (var column in contract.Columns)
            parts.Add(Definition(column));

        // 跳过索引跟列一起进括号清单（类注释第一条，活库兼收列式与表达式式）。
        // 不发契约索引那一格（上面整格拒）是因为 IndexContract 的语义在这里没有落点；
        // SkipIndexContract 的语义刚好是那颗骨架声明。
        foreach (var skip in contract.SkipIndexes)
            parts.Add("INDEX " + SkipIndexBody(skip));

        writer.Append(string.Join(", ", parts)).Append(')');

        EmitStorage(writer, contract);

        // 表注释不发这里：TryEmitTableComment 那一条 Core 语句管（类注释"表注释走 SPI 单路"条）。
    }

    /// <summary>跳过索引声明的骨架本体：<c>"名" 表达式 TYPE 档 GRANULARITY n</c>。
    /// 前面的 <c>INDEX</c>/<c>ADD INDEX</c> 两个动词由调用位给：建表内联与 ALTER 后补共用这一份文本
    ///（两形都在活库收过，出处见类注释首段）。档位参数的校验在这里（ClickHouse 的语义不进中性契约）：
    /// set 要 max_rows，tokenbf/ngrambf 要桶（后者还要 n 元宽），缺一颗当场红而不是发一句库不认的型。
    /// 表达式：属性挂的（<see cref="SkipIndexContract.MemberName"/> 非 null）是 plain 列名，加方言引号；
    /// 类挂的原样抄，标识符由声明者自己负责。GRANULARITY ≤ 0 按"声明里没写"处理，发本家缺省 1。</summary>
    string SkipIndexBody(SkipIndexContract skip)
    {
        var kind = skip.Kind switch
        {
            SkipIndexKind.MinMax => "minmax",
            SkipIndexKind.Set => $"set({Require(skip.SetMaxRows, skip, "SetMaxRows（set 档的 max_rows）")})",
            SkipIndexKind.BloomFilter => "bloom_filter",
            SkipIndexKind.TokenBloomFilter => $"tokenbf_v1({Require(skip.BucketSize, skip, "BucketSize（tokenbf 档的桶大小）")}, "
                + $"{HashFunctionsOf(skip)}, {skip.Seed})",
            SkipIndexKind.NgramBloomFilter => $"ngrambf_v1({Require(skip.NgramSize, skip, "NgramSize（ngrambf 档的 n 元宽）")}, "
                + $"{Require(skip.BucketSize, skip, "BucketSize（ngrambf 档的桶大小）")}, {HashFunctionsOf(skip)}, {skip.Seed})",
            _ => throw new QuickSqlException($"跳过索引 {skip.Name} 的档位 {skip.Kind} 在本家没有形状（ClickHouse 只认"
                + " minmax ∶ set ∶ bloom_filter ∶ tokenbf_v1 ∶ ngrambf_v1 五档）"),
        };

        var expression = skip.MemberName is null ? skip.Expression : Identifiers.Quote(skip.Expression);

        return $"{Identifiers.Quote(skip.Name)} {expression} TYPE {kind} GRANULARITY {(skip.Granularity > 0 ? skip.Granularity : 1)}";
    }

    static int HashFunctionsOf(SkipIndexContract skip) => skip.HashFunctions > 0 ? skip.HashFunctions : 1;

    static int Require(int value, SkipIndexContract skip, string argument)
        => value > 0 ? value
            : throw new QuickSqlException($"跳过索引 {skip.Name}（{skip.Kind} 档）缺 {argument}：这一档的参数是"
                + "型名的一部分，缺一颗发出去就是一句库不认的 TYPE —— 请在声明里补上");


    /// <summary>ENGINE 那一段：本家所有表都要有存储子句，主键归 <c>ORDER BY</c>/<c>PRIMARY KEY</c>
    /// 两颗引擎参数（类注释第二条）。有主键 <c>ORDER BY (cols)</c> 在前、<c>PRIMARY KEY (cols)</c>
    /// 在后；无主键发 <c>ORDER BY tuple()</c>。两形都只在 1 个主键列与多列主键上验证过文本形状，
    /// 复合列序与契约里 <see cref="EntityContract.PrimaryKey"/> 的顺序一致
    ///（<see cref="IdentifierPolicy"/> 恒加引号，折叠路走不到）。</summary>
    void EmitStorage(SqlWriter writer, EntityContract contract)
    {
        writer.Append(" ENGINE = MergeTree ");

        if (contract.PrimaryKey.Length > 0)
        {
            var key = string.Join(", ", contract.PrimaryKey.Select(c => Identifiers.Quote(c.ColumnName)));

            writer.Append("ORDER BY (").Append(key).Append(") PRIMARY KEY (").Append(key).Append(')');
        }
        else
        {
            writer.Append("ORDER BY tuple()");
        }
    }

    /// <summary>加列：<c>ALTER TABLE … ADD COLUMN 列定义</c>。本家没有"括号里塞清单"那一支
    ///（GBase 8s 的 <c>ADD (…)</c> 那一形），加列一次一条 —— 与 DuckDb 血统那一档同形。
    /// 可空性读 <see cref="Definition"/> 的同一份判据（<see cref="ColumnContract.NullableInStore"/>），
    /// 否则"加一次列"会把形状分裂出去。</summary>
    public void EmitAddColumn(SqlWriter writer, TableInfo table, ColumnContract column)
    {
        writer.Append("ALTER TABLE ");
        EmitTable(writer, table);
        writer.Append(" ADD COLUMN ").Append(Definition(column));
    }

    /// <summary>改列：<c>ALTER TABLE … MODIFY COLUMN 完整列定义</c>【文档·ClickHouse：alter 页】。
    /// "完整"这一格的代价与 GBase 8s 那一段同文：把列定义整份重写，本家照此重写的形状是
    /// <c>Nullable(…)</c> 包裹与否与 <c>NOT NULL</c> —— 两侧都出自契约里的同一格
    ///（<see cref="ColumnContract.NullableInStore"/>），所以"改一次精度"不会把可空性的形状改岔。</summary>
    public void EmitAlterColumnPrecision(SqlWriter writer, TableInfo table, ColumnContract column)
    {
        writer.Append("ALTER TABLE ");
        EmitTable(writer, table);
        writer.Append(" MODIFY COLUMN ").Append(Definition(column));
    }

    /// <summary>列注释：<c>ALTER TABLE … COMMENT COLUMN "列" '注'</c>（活库被收，出处见类注释首段）。
    /// 必须幂等（§9.5）：这一句是置值形，重发多少遍都把备注顶成同一个值 —— 与 sp_addextendedproperty
    /// 那一家"已存在会撞"的形状不同，本家第二趟重发不红。</summary>
    public bool TryEmitColumnComment(SqlWriter writer, TableInfo table, ColumnContract column)
    {
        writer.Append("ALTER TABLE ");
        EmitTable(writer, table);
        writer.Append(" COMMENT COLUMN ").Append(Identifiers.Quote(column.ColumnName)).Append(' ');
        writer.AppendStringLiteral(column.Comment!, unicode: false);

        return true;
    }

    /// <summary>表注释：<c>ALTER TABLE … MODIFY COMMENT '注'</c>（活库被收：裸 <c>COMMENT</c> 形被
    /// 服务端拒回 <c>Expected COLUMN</c>，MODIFY 形过了）。comment 为 null 按清文字处理（发空串）：
    /// 读回那一侧 <c>system.tables.comment</c> 本来就把空串当作"说过而没有"，两侧同一个空形闭合。</summary>
    public bool TryEmitTableComment(SqlWriter writer, TableInfo table, string? comment)
    {
        writer.Append("ALTER TABLE ");
        EmitTable(writer, table);
        writer.Append(" MODIFY COMMENT ");
        writer.AppendStringLiteral(comment ?? "", unicode: false);

        return true;
    }

    /// <summary>建成之后后补一颗跳过索引：<c>ALTER TABLE … ADD INDEX …</c>（活库被收，replay-5；
    /// 读回见 <c>system.data_skipping_indices</c>）。<b>Core 的结构同步不会走到这一句</b>：
    /// 同步引擎只按 <see cref="IndexContract"/> 算索引差，而本家那一格整格拒；跳过索引在 schema 读侧
    /// 没有形状（expr/type/granularity 三格进不了 SchemaIndex），差算不出来、计划里就没有它的行。
    /// 这是给宿主的手工口：带 <see cref="SkipIndexContract"/> 调这里，不假装"同步会带它"。</summary>
    public void EmitAddSkipIndex(SqlWriter writer, TableInfo table, SkipIndexContract skip)
    {
        writer.Append("ALTER TABLE ");
        EmitTable(writer, table);
        writer.Append(" ADD INDEX ").Append(SkipIndexBody(skip));
    }

    public void EmitCreateIndex(SqlWriter writer, TableInfo table, IndexContract index)
        => throw new QuickSqlException(
            "ClickHouse 没有 CREATE INDEX 这种独立语句：这一族的索引要么在建表骨架里（"
            + "[ClickHouseSkipIndex]，EmitCreateTable 带出），要么在建成之后按 ALTER … ADD INDEX 补一颗（"
            + "本家 CodeFirst 的 EmitAddSkipIndex，按 SkipIndexContract），而契约里的 IndexContract 是"
            + "唯一约束/行定位器那一格的 OLTP 形状，两路都进不去 —— 这里红说明有人把 [Index] 那一家的"
            + "语义搬到了本家。要跳过索引请换声明那一档。");

    /// <summary>列的 DDL 字形：<c>"名" Nullable(T)</c> 或 <c>"名" T NOT NULL</c>，此外没有第三形
    ///（类注释第三条）。<b>主键列不发列内 PK 子句</b>：列外那一法已经写在 ENGINE 那一段里，两法不可并用。
    /// <c>rowversion</c> 在发射点拒收，判据与来路写在类注释倒数第二段。</summary>
    string Definition(ColumnContract column)
    {
        var writer = new SqlWriter(new(), [], '@');

        EmitColumnDefinition(writer, column);

        return writer.ToString();
    }

    /// <summary>列定义本体：与 <see cref="Definition"/> 同一段文本（一个用中间 buffer、一个直发 writer），
    /// 改这一块看两处。可空性读 <see cref="ColumnContract.NullableInStore"/> 而不是
    /// <see cref="ColumnContract.IsNullable"/>：判据与另几族同一条（DDL 里的"值归库给"那格并进去）。</summary>
    void EmitColumnDefinition(SqlWriter writer, ColumnContract column)
    {
        var stored = types.ApplyStorageDefaults(column);
        var storeType = types.GetStoreType(stored);

        if (string.Equals(storeType.Trim(), "rowversion", StringComparison.OrdinalIgnoreCase))
            throw new QuickSqlException("ClickHouse 不建 rowversion 列：这一族的类型清单里没有那一型（按缺席读"
                + "【文档·ClickHouse：其数据类型清单页没列出任何与 SQL Server rowversion 同实务的一型】）"
                + "，而且这一族连\"由库自动递增的列\"那一档都没有 —— 发出去是一句库不认识的型名。"
                + "请改用应用给值的并发列：[ConcurrencyToken] 而不带 [Column(TypeName = \"rowversion\")]，"
                + "比较式与写侧与另几族逐字同形（ClickHouseChangeGuard）。");

        var shape = stored.NullableInStore ? $"Nullable({storeType})" : storeType;

        writer.Append(Identifiers.Quote(column.ColumnName)).Append(' ').Append(shape);

        if (!stored.NullableInStore)
            writer.Append(" NOT NULL");
    }

    void EmitTable(SqlWriter writer, TableInfo table)
    {
        if (table.Schema is { Length: > 0 } schema)
        {
            writer.Append(Identifiers.Quote(schema)).Append('.');
        }

        writer.Append(Identifiers.Quote(table.Name));
    }
}
