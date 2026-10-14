using QuickSql.Metadata;
using QuickSql.Sql;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.SqlServer;

/// <summary>T-SQL 方言（§9.4）。四个必须记住的分岔点：
/// <list type="bullet">
/// <item>分页 <c>OFFSET n ROWS FETCH NEXT m ROWS ONLY</c> 强制要求 <c>ORDER BY</c>，所以
/// <see cref="PagingRequiresOrdering"/> 为 true —— §5.12 的「聚合剥序但分页绝不剥序」在这里是编译器不管、
/// 写错就报错的硬约束。</item>
/// <item>标识符用方括号，且目标别名只能出现在 <c>UPDATE alias FROM table AS alias</c> 这种写法里，
/// 所以 UPDATE/DELETE 整个覆写。</item>
/// <item><c>OUTPUT inserted.*</c> 位于列清单与 <c>VALUES</c> 之间，而 PG 系的 <c>RETURNING</c> 在语句末尾。</item>
/// <item>字符串字面量要带 <c>N</c> 前缀（§10.4）。</item>
/// </list></summary>
public sealed class SqlServerDialect(SqlServerTypes types, SqlServerKeys keys, SqlServerValues values, SqlServerExpressions expressions)
    : DialectBase(IdentifierPolicy.SqlServer, values, expressions)
{
    public override DbKind Kind => DbKind.SqlServer;

    public override char ParameterPrefix => '@';

    /// <summary>五家里唯一一颗 true（v0.34 第二笔）：带前缀的参数名（<c>@p0</c>）在本家驱动上绿过两段历史 ——
    /// v0.19–v0.31 一直是原样递 <c>@p0</c>（两台实例 95 条全绿），v0.32 摘前缀之后也全绿。
    /// 摘法是为虚谷立的，本家不为它付每颗参数一次的 <c>Substring</c>（§12.8 第 6/11/12 行那五枚钉的量就是这笔账）。</summary>
    public override bool DriverAcceptsPrefixedParameterNames => true;

    public override bool PrefixUnicodeLiterals => true;

    public override bool PagingRequiresOrdering => true;

    public override string GetStoreType(ColumnContract column) => types.GetStoreType(column);

    protected override bool ReturningPrecedesValues => true;

    protected override void EmitReturning(SqlWriter writer, IReadOnlyList<ColumnContract> returning) => keys.EmitReturning(writer, returning);

    protected override void EmitPaging(SqlWriter writer, SelectStatement select)
    {
        if (select.Offset is null && select.Limit is null)
            return;

        EmitPagingOrdering(writer, select);

        writer.Append(" OFFSET ");

        // FETCH NEXT 必须挂在 OFFSET 之后，所以缺省偏移要落成常量 0 而不是省略整个子句。
        EmitExpression(select.Offset ?? new InlineLiteral("0"), writer);

        writer.Append(" ROWS");

        if (select.Limit is { } limit)
        {
            writer.Append(" FETCH NEXT ");
            EmitExpression(limit, writer);
            writer.Append(" ROWS ONLY");
        }
    }

    /// <summary><c>UPDATE [t0] SET [Name] = @p FROM [Orders] AS [t0] WHERE [t0].[Id] = @p</c>：
    /// T-SQL 的 <c>UPDATE</c> 目标不接受别名声明，别名要在 <c>FROM</c> 里给。</summary>
    public override void Emit(in UpdateStatement update, SqlWriter writer)
    {
        writer.Append("UPDATE ");
        EmitIdentifier(writer, update.Table.Alias);

        writer.Append(" SET ");

        for (var i = 0; i < update.Sets.Count; i++)
        {
            if (i > 0)
                writer.Append(", ");

            EmitIdentifier(writer, update.Sets[i].Column.ColumnName);
            writer.Append(" = ");
            EmitExpression(update.Sets[i].Value, writer);
        }

        // UPDATE 的 OUTPUT 在 SET 之后、FROM 之前；不回写就等于"无痕降级"（§1.2 原则 4），所以这里必须落地。
        if (update.Returning.Count > 0)
        {
            writer.Append(' ');
            keys.EmitReturning(writer, update.Returning);
        }

        writer.Append(" FROM ");
        EmitTableName(writer, update.Table.Table);
        writer.Append(" AS ");
        EmitIdentifier(writer, update.Table.Alias);

        EmitJoins(update.Joins, writer);

        if (update.Where is { } filter)
        {
            writer.Append(" WHERE ");
            EmitExpression(filter, writer);
        }
    }

    public override void Emit(in DeleteStatement delete, SqlWriter writer)
    {
        writer.Append("DELETE FROM ");
        EmitIdentifier(writer, delete.Table.Alias);

        writer.Append(" FROM ");
        EmitTableName(writer, delete.Table.Table);
        writer.Append(" AS ");
        EmitIdentifier(writer, delete.Table.Alias);

        EmitJoins(delete.Joins, writer);

        if (delete.Where is { } filter)
        {
            writer.Append(" WHERE ");
            EmitExpression(filter, writer);
        }
    }

    /// <summary>行集源在本家起的别名：目标用 <c>t0</c>（<see cref="DialectBase.WriteTargetAlias"/>），源用另一颗
    /// 不与业务列撞的字面名。两边都过 <see cref="EmitIdentifier"/> 取号，引号与折叠由策略给。</summary>
    const string MergeSource = "qsrc";

    /// <summary><c>InsertOrUpdate</c> 在本家的发射：<b>T-SQL 的 <c>MERGE INTO … USING (VALUES …) … WHEN MATCHED …
    /// WHEN NOT MATCHED … ;</b></b>。这一族有真正的多行 <c>VALUES</c> 行集语法（不像虚谷那一家 <c>(VALUES …)</c>
    /// 当场语法错），所以源直接落一颗带列名清单的行集，参数逐格登记一次、后面各支都引它的别名；
    /// <c>ON</c> 与两支的去向由共用层那份 <see cref="DialectBase.EmitMergeTail"/> 发（三家一处真相）。</summary>
    ///
    /// <para><b>目标位带 <c>WITH (HOLDLOCK)</c></b>：这是 T-SQL <c>MERGE</c> 那条有名的并发缺口
    /// （两条并发 <c>MERGE</c> 能在"没命中→插"与"命中→改"之间交错，插出重复键或丢更新）的官方缓解——文档口径
    /// 是把 <c>MERGE</c> 当一条 <c>SERIALIZABLE</c> 范围的读来锁住目标；<c>HOLDLOCK</c> 只在事务里持续到提交，
    /// 与 <c>InsertOrUpdateEngine</c> 那句"多块时自开一条事务"配套。这一格【文档·Microsoft MERGE 语句】，
    /// 是否真挡住那一竞态要在一台活 SQL Server 上并发跑一次才算结清（§14、集成册补一场）。</para></summary>
    public override void Emit(in UpsertStatement upsert, SqlWriter writer)
    {
        // 主键是自增列时整句裹一对 SET IDENTITY_INSERT … ON/OFF：这一格是活库抓出来的——SQL Server 编译整条 MERGE
        // 时就验 WHEN NOT MATCHED INSERT 那一支写了显式自增键，于是"全命中、INSERT 根本不触发"也照样报 544
        // （集成册 QUICKSQL_TEST_CONNECTION 第一跑红在此）。打开让显式自增键合法（本家声明了 SupportsExplicitIdentityInsert），
        // 关掉恢复；SQL Server 收 MultiStatement，这对开关与 MERGE 同一条命令递交、同一个会话。
        var identityKey = upsert.Keys.Any(static k => k.IsIdentity);

        if (identityKey)
            writer.Append(keys.IdentityInsertToggle(writer, upsert.Table.Table, true)!).Append("; ");

        var target = upsert.Table.Alias;

        writer.Append("MERGE INTO ");
        EmitTableName(writer, upsert.Table.Table);
        writer.Append(" WITH (HOLDLOCK) AS ");
        EmitIdentifier(writer, target);

        // USING (VALUES (@p0,…),(@p…)) AS qsrc (col0, col1, …)：一次把整批行当一张表带进来。
        writer.Append(" USING (VALUES ");

        for (var row = 0; row < upsert.Rows.Count; row++)
        {
            if (row > 0)
                writer.Append(", ");

            var cells = upsert.Rows[row];

            writer.Append('(');

            for (var column = 0; column < cells.Count; column++)
            {
                if (column > 0)
                    writer.Append(", ");

                // 本档不产 DEFAULT 格（引擎每格都参数化），所以这里每格都是一颗登记的参数。
                EmitExpression(cells[column]!, writer);
            }

            writer.Append(')');
        }

        writer.Append(") AS ");
        EmitIdentifier(writer, MergeSource);
        writer.Append(" (");
        EmitIdentifierList(writer, upsert.Columns);
        writer.Append(')');

        EmitMergeTail(writer, target, MergeSource, upsert, semicolon: true);

        if (identityKey)
            writer.Append(' ').Append(keys.IdentityInsertToggle(writer, upsert.Table.Table, false)!);
    }
}
