using FastSql.Metadata;
using FastSql.Providers;
using FastSql.Sql.Ast;

namespace FastSql.Providers.PostgreSqlBase;

/// <summary>PG 系的变更守卫（§12.4 第 2 项）。§4 那一格写着"PG 系一句 <c>IS DISTINCT FROM</c>"，
/// 这一家确实有那一型 —— 三值逻辑下"两侧同空"算相等，而它把 T-SQL 那三段 <c>OR</c> 压成一句。
///
/// <b>但可空性不同的两列不该发同一种文本</b>：NOT NULL 列上一句 <c>&lt;&gt;</c> 就够，与 T-SQL 那一族
/// 同一条理由（多出来的空值判断只会让谓词更难走索引），而且它让两家的合并语句在"只改了 NOT NULL 列"
/// 这一档上逐字同形 —— 跨库对读黄金样本时，剩下的差异就真是差异了。
/// <b>这句"两家"是 v0.22 的口径，今天同形的有四家</b>：Oracle 血统、MySQL 血统与 v0.31 的第五家
/// 在 NOT NULL 列上都发同一颗 <c>&lt;&gt;</c>（<c>grep -c "BinaryOperator.NotEqual"</c> 数得到），
/// 而"这一家逐字同形"那半句没变 —— 变的只是同形的一边有几个人。
///
/// <b>与 T-SQL 同一立场：宁可退回逐条也不哈希</b>（<see cref="Compares"/> 返回 false 时调用方整批退回），
/// 撞了哈希的行会被判成"没变"而把新值静默丢掉，那是丢写。</summary>
public sealed class PostgreSqlChangeGuard : IChangeGuard
{
    public bool Compares(ColumnContract column)
    {
        // 这一家的 json 型没有等值运算符（jsonb 才有），比较式在它上面直接语法错。
        // 今天的类型映射产不出 json（Json_Type 是声明给 doctor 与后续映射用的），所以这一支是护栏而不是现状。
        return BaseType(column) != "json";
    }

    public SqlExpression BuildDifference(ColumnContract column, SqlExpression stored, SqlExpression incoming)
        => new SqlBinaryExpression(column.IsNullable ? BinaryOperator.IsDistinctFrom : BinaryOperator.NotEqual, stored, incoming);

    static string BaseType(ColumnContract column)
    {
        // 走 GetStoreType 而不是只读 StoreTypeName：后者只有 ApplyStorageDefaults 跑过才有值，
        // 而守卫在发射路径上调用，那一趟不保证已经走完（与 T-SQL 那一族同一条理由）。
        var name = PostgreSqlTypes.Instance.GetStoreType(column);
        var paren = name.IndexOf('(');

        return (paren < 0 ? name : name[..paren]).Trim();
    }
}
