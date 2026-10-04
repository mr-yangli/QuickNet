using FastSql.Metadata;
using FastSql.Providers;
using FastSql.Sql;

namespace FastSql.Providers.SqlServer;

/// <summary>T-SQL 的 DDL（§9.2、§10.4）。P0 只产出文本与黄金样本比对，<b>不执行</b>（<c>CodeFirst</c> 在 P2）。
/// 三个必须写对的点：类型名与量纲都来自 <see cref="ColumnContract"/>（不是注解原文）；改精度要重写整列定义；
/// 列注释只能走 <c>sp_addextendedproperty</c>——T-SQL 没有 <c>COMMENT ON</c>。
/// 文本里不掺换行：黄金样本要跨平台逐字符比对，版式交给格式化器而不是方言。</summary>
public sealed class SqlServerCodeFirst(SqlServerTypes types) : ICodeFirstDialect
{
    public IdentifierPolicy Identifiers => IdentifierPolicy.SqlServer;

    public void EmitCreateTable(SqlWriter writer, EntityContract contract)
    {
        writer.Append("CREATE TABLE ");
        EmitTable(writer, contract.Table);
        writer.Append(" (");

        var parts = new List<string>(contract.Columns.Length + 1);

        foreach (var column in contract.Columns)
            parts.Add(Definition(column));

        if (contract.PrimaryKey.Length > 0)
        {
            var key = string.Join(", ", contract.PrimaryKey.Select(c => Identifiers.Quote(c.ColumnName)));

            parts.Add($"CONSTRAINT {Identifiers.Quote("PK_" + contract.Table.Name)} PRIMARY KEY CLUSTERED ({key})");
        }

        // 括号在上面已经开了：这里再开一层就成了 CREATE TABLE X ((...))，T-SQL 直接语法错。
        writer.Append(string.Join(", ", parts)).Append(')');

        // 索引在 T-SQL 里是独立语句，不能并进 CREATE TABLE 的括号里，用同批次的 ';' 串起来。
        foreach (var index in contract.Indexes)
        {
            writer.Append(" ; ");
            EmitCreateIndex(writer, contract.Table, index);
        }
    }

    public void EmitAddColumn(SqlWriter writer, TableInfo table, ColumnContract column)
    {
        writer.Append("ALTER TABLE ");
        EmitTable(writer, table);
        writer.Append(" ADD ").Append(Definition(column));
    }

    /// <summary>T-SQL 改精度必须重写整个列定义，<c>NULL</c>/<c>NOT NULL</c> 会一起被重置，
    /// 所以这里把可空性带上——漏掉它就会把 NOT NULL 列悄悄改成可空。</summary>
    public void EmitAlterColumnPrecision(SqlWriter writer, TableInfo table, ColumnContract column)
    {
        writer.Append("ALTER TABLE ");
        EmitTable(writer, table);
        writer.Append(" ALTER COLUMN ").Append(Definition(column));
    }

    public bool TryEmitColumnComment(SqlWriter writer, TableInfo table, ColumnContract column)
    {
        writer.Append("EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=");
        writer.AppendStringLiteral(column.Comment ?? string.Empty, unicode: true);

        writer.Append(", @level0type=N'SCHEMA', @level0name=");
        writer.AppendStringLiteral(table.Schema ?? "dbo", unicode: true);

        writer.Append(", @level1type=N'TABLE', @level1name=");
        writer.AppendStringLiteral(table.Name, unicode: true);

        writer.Append(", @level2type=N'COLUMN', @level2name=");
        writer.AppendStringLiteral(column.ColumnName, unicode: true);

        return true;
    }

    public void EmitCreateIndex(SqlWriter writer, TableInfo table, IndexContract index)
    {
        writer.Append(index.Kind == IndexKind.Unique ? "CREATE UNIQUE INDEX " : "CREATE INDEX ");
        writer.Append(Identifiers.Quote(index.Name)).Append(" ON ");
        EmitTable(writer, table);
        writer.Append(" (");

        for (var i = 0; i < index.ColumnNames.Length; i++)
        {
            if (i > 0)
                writer.Append(',');

            writer.Append(' ').Append(Identifiers.Quote(index.ColumnNames[i]));
            writer.Append(index.Descending ? " DESC" : " ASC");
        }

        writer.Append(" )");
    }

    string Definition(ColumnContract column)
    {
        var writer = new SqlWriter(new(), [], '@');

        EmitColumnDefinition(writer, column);

        return writer.ToString();
    }

    void EmitColumnDefinition(SqlWriter writer, ColumnContract column)
    {
        var stored = types.ApplyStorageDefaults(column);

        writer.Append(Identifiers.Quote(column.ColumnName)).Append(' ').Append(types.GetStoreType(stored));

        if (column.IsIdentity)
            writer.Append(" IDENTITY(1,1)");

        // 读的是 NullableInStore 而不是 IsNullable：后者是 CLR 侧的可空性，而"新增时手里没有值"那一格
        // 在实体侧只能声明成可空（§10.6 的必填尺子量的是整只实体）。值归库给的那几列库里永不为空，
        // 抄过去就得到一句"这一列可以由人写空值"的假承诺（§14 那一格，v0.24 收）。
        writer.Append(stored.NullableInStore ? " NULL" : " NOT NULL");
    }

    void EmitTable(SqlWriter writer, TableInfo table)
    {
        writer.Append(Identifiers.Quote(table.Schema ?? "dbo")).Append('.');
        writer.Append(Identifiers.Quote(table.Name));
    }
}
