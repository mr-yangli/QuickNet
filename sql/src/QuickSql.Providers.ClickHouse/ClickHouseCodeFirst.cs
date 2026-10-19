using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;

namespace QuickSql.Providers.ClickHouse;

/// <summary>ClickHouse 的建表与改表（§9.2 的 ICodeFirstDialect 第十家；与另九家分岔的每一处都是一句
/// "本家的引擎形状与那几族不同"）。整层【文档·ClickHouse】（CREATE TABLE 页与 alter 页，
/// https://clickhouse.com/docs/sql-reference/statements/create-table 、
/// https://clickhouse.com/docs/sql-reference/statements/alter ），本机没有引擎，一次都没有实测过。
///
/// <para>逐条分岔，每一条都写下来路：
/// <list type="bullet">
/// <item><b>没有 CREATE INDEX 语句级格</b>：这一族的索引不是独立语句，它是<em>建表语句里</em>列/表级
/// <c>INDEX … GRANULARITY …</c> 那类数据骨架声明【文档·ClickHouse：CREATE TABLE 页的 INDEX 修饰词那一节】。
/// 契约里的 <see cref="IndexContract"/> 格是"表建成之后另有对象"的形状，进不了那一格 —— 所以
/// <see cref="EmitCreateTable"/> 遇契约索引非空<b>整格拒</b>（不逐条递给 <see cref="EmitCreateIndex"/>：
/// 每条各自报错会把"这一族根本没有这一档"读成"某一条索引写错了"），<see cref="EmitCreateIndex"/> 本身也抛。
/// 与 GBase 8s 的 FULLTEXT 那一格同果而不同因：那一家是语句存在、 kinds 缺一颗，这一家是整句不存在。</item>
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
/// <item><b>表注释在建表语句尾随 <c>COMMENT '…'</c></b>：官方注释块的子句序是 comments moved to
/// storage 那一支 —— COMMENT 出现在 ENGINE/存储子句之后、query settings 之前
///【文档·ClickHouse：CREATE TABLE 页的 COLUMN COMMENT/TABLE COMMENT 那一段】。
/// <see cref="TryEmitTableComment"/> 不覆写（接口默认 false）：<b>表注释由建表顺手带出</b>，
/// 后续改注释没有独立的语句形状接进 SPI，所以第二趟的"备注变了"落进"判不了"清单（§9.5）。</item>
/// <item><see cref="TryEmitColumnComment"/> 恒 false：列级 <c>COMMENT</c> 修饰词语法上在本家存在
///【文档·ClickHouse：CREATE TABLE 页】，但"结构同步"那一侧的形状（如已存在的列重发一句置值）
/// 在本家没有独立语句可发 —— 所以那一条路本笔不接，理由与 GBase 8s 那一格<b>同果不同因</b>，
/// 两家的档案分开记（§13.5 第十七本）。</item>
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
/// <para><b>本类产出的每一条文本都没有在一台活库上跑过，而且本机没有任何一台可以跑</b>。
/// 逐格口径记 §13.5 第十七本台账，那里挂着出处 URL。</para></summary>
public sealed class ClickHouseCodeFirst(ClickHouseTypes types) : ICodeFirstDialect
{
    public IdentifierPolicy Identifiers => IdentifierPolicy.ClickHouse;

    public void EmitCreateTable(SqlWriter writer, EntityContract contract)
    {
        if (contract.Indexes.Count > 0)
            throw new QuickSqlException(
                "ClickHouse 的建表拒收契约索引：这一族没有 CREATE INDEX 语句级形状，索引是建表语句里"
                + "列/表级 INDEX … GRANULARITY … 的数据骨架声明【文档·ClickHouse：CREATE TABLE 页】。"
                + "契约里的 IndexContract 是'表建成之后另有对象'那一格的形状，进不去那一格，"
                + "所以带索引实体整格拒 —— 请把 [Index] 注解摘掉再建这一族的表。");

        writer.Append("CREATE TABLE ");
        EmitTable(writer, contract.Table);
        writer.Append(" (");

        var parts = new List<string>(contract.Columns.Length);

        foreach (var column in contract.Columns)
            parts.Add(Definition(column));

        writer.Append(string.Join(", ", parts)).Append(')');

        EmitStorage(writer, contract);

        if (contract.Table.Comment is { Length: > 0 } comment)
        {
            // COMMENT 必须在 ENGINE/存储子句之后、query settings 之前【文档·ClickHouse：CREATE TABLE 页】。
            writer.Append(" COMMENT ");

            writer.AppendStringLiteral(comment, unicode: true);
        }
    }

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

    /// <summary>恒 false：列级 <c>COMMENT</c> 修饰词语法存在而结构同步那一侧没有可发的独立语句形状
    /// （类注释第五条）。走 Core "跳过并记"的退路，不把注释文本塞进建表语句。</summary>
    public bool TryEmitColumnComment(SqlWriter writer, TableInfo table, ColumnContract column) => false;

    public void EmitCreateIndex(SqlWriter writer, TableInfo table, IndexContract index)
        => throw new QuickSqlException(
            "ClickHouse 没有 CREATE INDEX 这种独立语句：这一族的索引是建表语句里列/表级 "
            + "INDEX … GRANULARITY … 的数据骨架声明【文档·ClickHouse：CREATE TABLE 页】，"
            + "而已经建出的表上补一条索引（post-hoc 那路）在本家没有语句级接入口。"
            + "所以这一格整格抛 —— 带索引实体在 EmitCreateTable 那一侧已经被整格拒，"
            + "这里红说明的是有人绕过了那一侧。");

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
