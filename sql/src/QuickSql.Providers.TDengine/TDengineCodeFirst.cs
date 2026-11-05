using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;

namespace QuickSql.Providers.TDengine;

/// <summary>TDengine 的建表与改表（整层厂商文档口径，本机没有引擎）。分岔逐条：
/// 契约索引整格拒——本家没有用户索引那一档（查询面全靠引擎自建的数据骨架），IndexContract 是
/// "表建成之后另有对象"的 OLTP 形状，进不去；主键不发任何子句——本家的主键是
/// "首列 TIMESTAMP 系统自动设为主键"的隐含事实（VendorDoc·TDengine：建表页原话），
/// 所以建表前先把契约的键形状校验成与本家隐含事实同形（恰一颗、是首列、型名 TIMESTAMP；
/// 复合键没有落点），校验不过当场红而不是发一张"键语义对不上"的表；
/// 列定义只有 "名" 型名一段——官方 column_definition 文法里没有 NULL/NOT NULL、没有 DEFAULT、
/// 没有列注释，可空性与默认值在 DDL 上没有落点（有 DefaultValue 的契约在发射点拒收）；
/// identity 列拒收（本家没有"由库递增的列"那一档，主键值由调用方自己给）；
/// rowversion 在发射点拒收——发出去是一句库不认识的型名。改列走 MODIFY COLUMN 完整列定义
///（VendorDoc·TDengine：改可变长类型长度的官方形）；表注释走 ALTER TABLE … COMMENT '注'
///（置值形，幂等）；列注释没有这一格（TryEmitColumnComment 恒 false）。</summary>
public sealed class TDengineCodeFirst(TDengineTypes types) : ICodeFirstDialect
{
    public IdentifierPolicy Identifiers => IdentifierPolicy.TDengine;

    public void EmitCreateTable(SqlWriter writer, EntityContract contract)
    {
        if (contract.Indexes.Count > 0)
            throw new QuickSqlException(
                "TDengine 的建表拒收契约索引：本家没有用户索引那一档（查询面全靠引擎自建的数据骨架），"
                + "契约里的 IndexContract 是'表建成之后另有对象'那一格的形状（唯一约束/行定位器），进不去 —— "
                + "普通 [Index] 注解请摘掉再建这一族的表。");

        if (contract.SkipIndexes.Count > 0)
            throw new QuickSqlException(
                "TDengine 的建表拒收跳过索引声明：[ClickHouseSkipIndex] 是 ClickHouse 那一族的数据骨架声明，"
                + "本家没有对应的那一档 —— 这份契约里的跳过索引声明搬错了家。");

        ValidateKeyShape(contract);

        writer.Append("CREATE TABLE ");
        EmitTable(writer, contract.Table);
        writer.Append(" (");

        var parts = new List<string>(contract.Columns.Length);

        foreach (var column in contract.Columns)
            parts.Add(Definition(column));

        writer.Append(string.Join(", ", parts)).Append(')');

        // 表注释不发这里：TryEmitTableComment 那一条 Core 语句管（两处一起发就是同一句话发两遍）。
    }

    /// <summary>键形状校验：把契约的键折成与本家隐含事实同形的那一形。本家主键 = 首列 TIMESTAMP
    ///（系统自动设），所以契约里的主键必须恰一颗、是契约首列、且 CLR 型映射 TIMESTAMP；
    /// 无主键放行（首列时间戳仍然必须——insert-only 日志表的合法形状，写路径要主键的地方内核本来就拒）；
    /// 复合键没有落点（本家的 COMPOSITE KEY 是"时间戳之外再加一颗"的扩展形，契约里没有
    /// "时间戳 + 复合"那一形可表达，重开面记 notes）。</summary>
    void ValidateKeyShape(EntityContract contract)
    {
        if (contract.PrimaryKey.Length == 0)
        {
            RequireTimestampFirst(contract);
            return;
        }

        if (contract.PrimaryKey.Length > 1)
            throw new QuickSqlException(
                "TDengine 的建表拒收复合主键：本家的主键是'首列 TIMESTAMP 系统自动设为主键'的隐含事实，"
                + "复合形状没有落点（官方的 COMPOSITE KEY 是在时间戳之外再加一颗键列的扩展形，"
                + "契约里没有'时间戳 + 复合'那一形可表达）。请把主键收敛成首列时间戳那一颗。");

        var key = contract.PrimaryKey[0];

        if (!ReferenceEquals(key, contract.Columns[0]))
            throw new QuickSqlException(
                $"TDengine 的主键必须是契约首列，而这份契约把主键点在了 {key.ColumnName} 上：本家的主键是"
                + "'首列 TIMESTAMP 系统自动设为主键'的隐含事实【VendorDoc·TDengine：建表页】，"
                + "契约里的键形状要与它同形 —— 把时间戳列放成实体的第一个成员并声明为主键"
                + "（[Key] 或 [QuickColumn(IsPrimary = Yes)]），int Id 血统的模型在这一家建不出表。");

        RequireTimestampFirst(contract);
    }

    /// <summary>每一张表的第一列都必须是 TIMESTAMP（本家的硬性事实，与有没有声明主键无关）。</summary>
    void RequireTimestampFirst(EntityContract contract)
    {
        var first = contract.Columns[0];
        var storeType = types.GetStoreType(types.ApplyStorageDefaults(first));

        if (!string.Equals(storeType.Trim(), "TIMESTAMP", StringComparison.OrdinalIgnoreCase))
            throw new QuickSqlException(
                $"TDengine 的每一张表第一列都必须是 TIMESTAMP，而这份契约的首列 {first.ColumnName}"
                + $" 的存储型是 {storeType}【VendorDoc·TDengine：建表页——\"表的第一个字段必须是 TIMESTAMP，"
                + "系统会自动将其设为主键\"】。把一个 DateTime 成员放成实体的第一个成员"
                + "（insert-only 的日志表可以不声明主键，写路径要主键的地方内核会自己拒）；"
                + "int Id 血统的模型在这一家建不出表。");
    }

    /// <summary>加列：ALTER TABLE … ADD COLUMN 列定义。本家的加列一次一条、只能加在尾部
    ///（首列时间戳的位置是建表时定的），Core 的补列计划照这个形状发。</summary>
    public void EmitAddColumn(SqlWriter writer, TableInfo table, ColumnContract column)
    {
        writer.Append("ALTER TABLE ");
        EmitTable(writer, table);
        writer.Append(" ADD COLUMN ").Append(Definition(column));
    }

    /// <summary>改列：ALTER TABLE … MODIFY COLUMN 完整列定义（VendorDoc·TDengine：改可变长类型
    /// 长度的官方形；"完整"的代价是把列定义整份重写）。能力位刻意不声明 Alter_Column_Precision：
    /// 改宽的官方句在，但收窄/改型的行为边界（数据截断？拒回？）没有读数，活库复核再翻。</summary>
    public void EmitAlterColumnPrecision(SqlWriter writer, TableInfo table, ColumnContract column)
    {
        writer.Append("ALTER TABLE ");
        EmitTable(writer, table);
        writer.Append(" MODIFY COLUMN ").Append(Definition(column));
    }

    /// <summary>本家没有列注释这一格：官方 column_definition 文法里没有 COMMENT 项、
    /// INS_COLUMNS 目录也没有备注列——写不进也读不回，交 false 让那一格进"判不了"清单。</summary>
    public bool TryEmitColumnComment(SqlWriter writer, TableInfo table, ColumnContract column) => false;

    /// <summary>表注释：ALTER TABLE … COMMENT '注'（VendorDoc·TDengine：表注释可用于普通表，
    /// ALTER 修改形同页；置值形幂等，重发多少遍都把备注顶成同一个值）。comment 为 null 按清文字处理
    ///（发空串），与读回那一侧的空串口径闭合。</summary>
    public bool TryEmitTableComment(SqlWriter writer, TableInfo table, string? comment)
    {
        writer.Append("ALTER TABLE ");
        EmitTable(writer, table);
        writer.Append(" COMMENT ");
        writer.AppendStringLiteral(comment ?? "", unicode: false);

        return true;
    }

    public void EmitCreateIndex(SqlWriter writer, TableInfo table, IndexContract index)
        => throw new QuickSqlException(
            "TDengine 没有 CREATE INDEX 这种独立语句：本家没有用户索引那一档，查询面全靠引擎自建的"
            + "数据骨架（时序引擎按主键时间戳与标签自动组织），而契约里的 IndexContract 是"
            + "唯一约束/行定位器那一格的 OLTP 形状 —— 这里红说明有人把 [Index] 那一家的语义搬到了本家。");

    /// <summary>列的 DDL 字形：只有 "名" 型名一段。官方 column_definition 文法里没有
    /// NULL/NOT NULL、没有 DEFAULT、没有列注释——可空性与默认值在 DDL 上没有落点，
    /// 契约里带的那些格在这里都是"发不出去"而不是"可以不发"。</summary>
    string Definition(ColumnContract column)
    {
        var writer = new SqlWriter(new(), [], '?');

        EmitColumnDefinition(writer, column);

        return writer.ToString();
    }

    /// <summary>列定义本体：与 Definition 同一段文本（一个用中间 buffer、一个直发 writer），改这一块看两处。</summary>
    void EmitColumnDefinition(SqlWriter writer, ColumnContract column)
    {
        var stored = types.ApplyStorageDefaults(column);
        var storeType = types.GetStoreType(stored);

        if (string.Equals(storeType.Trim(), "rowversion", StringComparison.OrdinalIgnoreCase))
            throw new QuickSqlException("TDengine 不建 rowversion 列：这一族的类型清单里没有那一型（按缺席读"
                + "【文档·TDengine：类型页清单没列出任何与 SQL Server rowversion 同实务的一型】）"
                + "，而且这一族连\"由库自动递增的列\"那一档都没有 —— 发出去是一句库不认识的型名。"
                + "请改用应用给值的并发列：[ConcurrencyToken] 而不带 [Column(TypeName = \"rowversion\")]，"
                + "比较式与写侧与另几族逐字同形（TDengineChangeGuard）。");

        if (column.IsIdentity)
            throw new QuickSqlException(
                $"TDengine 不建 identity 列（{column.ColumnName}）：本家没有\"由库自动递增的列\"那一档，"
                + "主键就是那颗时间戳、值由调用方自己给 —— 声明里的自增那一格请摘掉。");

        if (column.DefaultValue is { } value)
            throw new QuickSqlException(
                $"TDengine 的建表拒收列默认值（{column.ColumnName} = {value}）：官方 column_definition 文法里"
                + "没有 DEFAULT 项【VendorDoc·TDengine：建表页】，这一格在 DDL 上没有落点 —— "
                + "请摘掉 HasDefaultValue/[DefaultValue] 声明，缺省语义放到应用侧。");

        writer.Append(Identifiers.Quote(column.ColumnName)).Append(' ').Append(storeType);
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
