using System.Collections.Frozen;
using FastSql.Metadata;
using FastSql.Providers;
using FastSql.Sql;
using FastSql.Sql.Ast;

namespace FastSql.Providers.OracleBase;

/// <summary>Oracle 血统的变更守卫（§12.4 第 2 项，第三家）。两件事在这一族同时变了，而它们方向相反：
///
/// <para><b>形状回到了三段式</b>。这一家没有 <c>IS DISTINCT FROM</c>，与 T-SQL 同一档，
/// 所以那个三段展开从 <c>SqlServerChangeGuard</c> 提到了共用层（<see cref="ChangeGuardShapes"/>）——
/// 提出来这个动作本身就是"多数派换边"的记录：三家两家用三段，一家一句（v0.30 的第四家把这一句顶成
/// 四家三家用三段，而它落进来的理由与这一族不同一条；v0.31 的第五家再顶成<b>五家里四家三段</b>，
/// 三条理由各不同 —— 见 <c>ChangeGuardShapes</c> 类注释末段与 <c>XuguChangeGuard</c> 第一条）。</para>
///
/// <para><b>而 <see cref="Compares"/> 第一次有了默认命中的一种型</b>。§9.3 那一列里"大文本"在这一族落
/// <c>CLOB</c>，而 LOB 在那一家连等号都不接受（比较式会撞 <c>invalid use of large object type</c> 那一档报错）。
/// 于是"一根没有写长度上限的 <c>string</c>"在这一族是<b>常态</b>而不是边角：同一份实体在 T-SQL 上
/// 产出的守卫文本进得了合并档（<c>NVARCHAR(MAX)</c> 可以比），在这一族连承载它的那一句都发不出去。
/// <b>而"发不出去"今天不是这一位判出来的</b>：<c>EntityTracker.Mergeable</c> 先问
/// <c>Dialect.SupportsJoinedWrites</c>（<c>EntityTracker.cs:860</c>），逐列问 <c>Compares</c> 在它之后
/// （<c>:869</c>），这一家那颗开关是 false，所以 <c>Compares</c> 在生产路径上根本问不到 ——
/// 本条上一版写作"第一次真正决定一条语句的形状"，v0.30 交账时按发射顺序重数推翻：<b>后果对、归因错</b>，
/// 它今天决定的是<em>离线那条手工构造 AST 的样本长什么样</em>。
/// 之前它是"json 那一格"与"LOB 那一格"的护栏，而两类列在样例模型里都不存在。</para>
///
/// <para>还有第二处不对称：<b>这一家的 <c>''</c> 就是 <c>NULL</c></b>。所以"库里那格是空串、新值写了 NULL"
/// 在这一族的守卫眼里是<em>没变</em>（三段式两侧都是 NULL，一条都不成立），而另四家都是"变了"。
/// 这不是守卫写错了 —— 那两格在库里本来就是同一个值，判成"变了"反而多发一条把同一个值写回去的语句。
/// 要说的是：<b>跨库对账时这一档不是同一条判定</b>，而它是<b>这一族独一份</b>的形状（v0.30 的第四家
/// 没有站进来：它的 <c>''</c> 不是 NULL；v0.31 的第五家同样没有，两档出处里都没有这一条），
/// 所以它属于 §14 那张跨库差异表而不是缺陷清单。</para>
///
/// <para>与两族同一立场：宁可退回逐条也不 <c>DBMS_LOB.COMPARE</c> 或哈希近似 —— 撞了哈希的行会被判成
/// "没变"而把新值静默丢掉，那是丢写（§12.4 第 2 项）。</para></summary>
public sealed class OracleChangeGuard : IChangeGuard
{
    /// <summary>比较式里用不了的型：四颗 LOB（<c>BFILE</c> 是"指向文件的指针"，等号同样没有定义）。
    /// <b>没有 <c>LONG</c></b>：那一型在新建表里已被厂商弃用，而它能不能进 <c>WHERE</c> 取决于版本，
    /// 现场没有一台库能证（§13.5）；真撞上了会红在发射期，比悄悄少比较一列好。</summary>
    static readonly FrozenSet<string> Incomparable = new[] { "clob", "nclob", "blob", "bfile" }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public bool Compares(ColumnContract column) => !Incomparable.Contains(BaseType(column));

    public SqlExpression BuildDifference(ColumnContract column, SqlExpression stored, SqlExpression incoming)
    {
        // NOT NULL 列上一句 <> 就够：两侧都不可能是 NULL，而多出来的两段 OR 只会让谓词更难走索引 ——
        // 与 T-SQL 那一家逐字同判，所以两家的合并语句在"只改了 NOT NULL 列"这一档上同形（只差引号与前缀）。
        if (!column.IsNullable)
            return new SqlBinaryExpression(BinaryOperator.NotEqual, stored, incoming);

        return ChangeGuardShapes.ThreeWay(stored, incoming);
    }

    static string BaseType(ColumnContract column)
    {
        // 走 GetStoreType 而不是只读 StoreTypeName：后者只有 ApplyStorageDefaults 跑过才有值，
        // 而守卫在发射路径上调用，那一趟不保证已经走完（与两族同一条理由）。
        // 切在第一个 '(' 或空格之前：TIMESTAMP(3) WITH TIME ZONE 与 INTERVAL DAY(0) TO SECOND(3)
        // 这两种带限定词的名字都要落到基名上，而 LOB 那四颗没有一颗带限定词。
        var name = OracleTypes.Instance.GetStoreType(column);
        var cut = name.IndexOfAny(['(', ' ']);

        return (cut < 0 ? name : name[..cut]).Trim();
    }
}
