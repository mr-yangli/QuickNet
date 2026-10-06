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

    /// <summary>列备注。<b>T-SQL 没有 <c>COMMENT ON</c>，只有 <c>sp_addextendedproperty</c>，而它在属性
    /// 已经存在的那一列上直接报错</b>。所以这一句自带"有没有"的判断：§9.5 的备注同步在改过备注的表上发的
    /// 就是这一句，无条件 ADD 会让那一趟以"属性已存在"收尾——那是同步失败，不是"备注没变"。
    /// 新建表那一趟走的也是这一句，只是它每一次都落进 <c>ELSE</c> 那一支。</summary>
    public bool TryEmitColumnComment(SqlWriter writer, TableInfo table, ColumnContract column)
    {
        EmitComment(writer, table, column.ColumnName, column.Comment);

        return true;
    }

    /// <summary>表备注 = 同一颗存储过程、<c>minor_id=0</c> 的那一格。<b>这一家今天同时是"发得出"与"读得回"
    /// 的那一家</b>（<see cref="SqlServerSchemaReader"/> 查 <c>sys.extended_properties</c>），所以 §9.5 的
    /// 表备注同步在它身上是双向的：写了读得回，下一趟才判得出变没变。</summary>
    public bool TryEmitTableComment(SqlWriter writer, TableInfo table, string? comment)
    {
        EmitComment(writer, table, null, comment);

        return true;
    }

    void EmitComment(SqlWriter writer, TableInfo table, string? columnName, string? comment)
    {
        writer.Append("IF EXISTS (SELECT 1 FROM sys.extended_properties WHERE class=1 AND major_id=");
        ObjectId(writer, table);

        if (columnName is null)
            writer.Append(" AND minor_id=0");
        else
        {
            writer.Append(" AND minor_id=COLUMNPROPERTY(");
            ObjectId(writer, table);
            writer.Append(", ");
            writer.AppendStringLiteral(columnName, unicode: true);
            writer.Append(", 'ColumnId')");
        }

        writer.Append(" AND name=N'MS_Description') ");
        EmitExtendedProperty(writer, "sp_updateextendedproperty", table, columnName, comment);

        writer.Append(" ELSE ");
        EmitExtendedProperty(writer, "sp_addextendedproperty", table, columnName, comment);
    }

    static void EmitExtendedProperty(SqlWriter writer, string procedure, TableInfo table, string? columnName, string? comment)
    {
        writer.Append("EXEC sys.").Append(procedure).Append(" @name=N'MS_Description', @value=");
        writer.AppendStringLiteral(comment ?? string.Empty, unicode: true);

        writer.Append(", @level0type=N'SCHEMA', @level0name=");
        writer.AppendStringLiteral(table.Schema ?? "dbo", unicode: true);

        writer.Append(", @level1type=N'TABLE', @level1name=");
        writer.AppendStringLiteral(table.Name, unicode: true);

        if (columnName is null)
            return;

        writer.Append(", @level2type=N'COLUMN', @level2name=");
        writer.AppendStringLiteral(columnName, unicode: true);
    }

    /// <summary><c>OBJECT_ID</c> 吃的是<em>字符串里的那个标识符</em>，所以这里复用 <see cref="EmitTable"/>
    /// 那一条发射路径（同一个 <c>IdentifierPolicy</c>、同一副方括号），再把结果当字面量交出去 ——
    /// 名字里带单引号时由 <c>AppendStringLiteral</c> 负责翻倍，而不是由拼接负责。</summary>
    void ObjectId(SqlWriter writer, TableInfo table)
    {
        var qualified = new SqlWriter(new(), [], '@');

        EmitTable(qualified, table);

        writer.Append("OBJECT_ID(");
        writer.AppendStringLiteral(qualified.ToString(), unicode: true);
        writer.Append(')');
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

    /// <summary>T-SQL 的 <c>DROP INDEX</c> 是"索引名 ON 表名"那一形，没有 <c>DROP INDEX 表名.索引名</c>
    /// 也没有 <c>IF EXISTS</c> 的语法糖。这里不加 <c>IF EXISTS</c> 包裹：<b>会发这一句的现场，索引名是从
    /// 目录读回来的</b>（<see cref="SqlServerSchemaReader"/> 的 <c>sys.indexes</c> 那一趟），它不在的那件事
    /// 已经在计划层判掉了，多加一层存在判断只是把"名字读错了"这种缺陷按下去。
    /// 唯一约束那一形删不动（要 <c>ALTER TABLE … DROP CONSTRAINT</c>），那一格由 <see cref="SchemaIndex.IsConstraint"/>
    /// 挡在计划层，不会走到这里。</summary>
    public bool TryEmitDropIndex(SqlWriter writer, TableInfo table, SchemaIndex index)
    {
        writer.Append("DROP INDEX ").Append(Identifiers.Quote(index.Name)).Append(" ON ");
        EmitTable(writer, table);

        return true;
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
