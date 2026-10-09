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
}
