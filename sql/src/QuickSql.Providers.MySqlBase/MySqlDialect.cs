using QuickSql.Metadata;
using QuickSql.Sql;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.MySqlBase;

/// <summary>MySQL 血统的 SQL 文本层（§9.4，第四家 / 第四条血统）。与已落地三族的分岔逐条对照，
/// 每一条都是一个覆写点或者一处<em>刻意不覆写</em>：
/// <list type="bullet">
/// <item>标识符反引号（<see cref="IdentifierPolicy.MySql"/>，第四颗预设）—— 五家里唯一不用双引号或方括号的一家
/// （v0.31 的第五家落的是双引号那一档，所以这句"唯一"到五家还成立），
/// 而它的引号字符<em>不取决于会话模式</em>。这一句 T-SQL 那一方括号也说得出（它从没有过模式分岔），
/// 所以真正属于这一家的是<b>另一层</b>：它的引号字符<em>本来可以是</em>模式决定的那一个 —— 那一家有
/// <c>ANSI_QUOTES</c>，它会把<b>双引号</b>变成字符串的字面量引号，而反引号在两种模式下都只作标识符引号。
/// 别家是一个没有分岔的事实，这一家是一次避开了分岔的选择。</item>
/// <item>分页 <c>LIMIT n OFFSET m</c> <b>不要求</b> <c>ORDER BY</c>（§9.4 那<b>三</b>套实现里的第一套，与金仓同一套
/// —— 本版把这里写过的"四套"改回"三套"：<b>§9.4 自己那句"套数没变、每套几家变了"才是判据</b>，
/// v0.30 那一版把"四家"顺手写成了"四套"，是自家造的错账，量的是分页形状数而不是 provider 数。
/// 套数到 v0.31 仍然是三，而第一套里现在是<b>三家</b>：金仓、这一家与第五家），
/// 所以 <see cref="PagingRequiresOrdering"/> 保持 false。与金仓真正分岔的是<b>另一半</b>：
/// 这一家 <c>OFFSET</c> 不能单独出现（见 <see cref="EmitPaging"/> 里那颗哨兵）。</item>
/// <item><b>写语句不接受目标表之外的任何表源</b>（<see cref="SupportsJoinedWrites"/> 取 false +
/// <see cref="EmitJoinedSources"/> 直接抛）：这一家既没有 <c>UPDATE … FROM</c> 也没有
/// <c>(VALUES …) AS t(cols)</c> 那种行集形状（<c>VALUES ROW()</c> 是 8.0.19 起，而且它不带列名清单，
/// 共用层发的恰恰是"按名字引它"的那一种，§12.5 的合并档在这里落不成一句）。
/// 这一档从第三族那一份孤例变成 <b>2∶2</b>（另一半是 Oracle 血统那一族，两家的理由不同一条，
/// 见 <see cref="EmitJoinedSources"/>），而"2∶2"这件事本身就是 §12.5 那道闸门不该写成 <c>if (是达梦)</c> 的
/// 全部理由；四家分布与重数见 §12.5 那一格。<b>v0.31 把它顶成 3∶2</b>（第五家同边，而它是<em>第三条理由</em>：
/// 那一家既没有这一子句，"目标能不能起别名"也只观察到产物没写 —— 三条理由与三家的位数写在
/// <c>XuguDialect</c> 类注释第三条）。于是当初那句论证换了一种站法：不再需要"两家里两家里各一半"来
/// 反对 <c>if (是达梦)</c>，多数派本身就成了事实分布。五家分布与重数见 §12.5 那一格。</item>
/// <item>没有 <c>RETURNING</c>（MariaDB 10.5 起有），所以 <see cref="DialectBase.EmitReturning"/> 转交
/// <see cref="MySqlKeys.EmitReturning"/> 那一抛 —— 转交而不是直接留基类那句"未实现"，是为了让
/// INSERT 与 UPDATE 两档共用同一份带出处的文案，与金仓那一族同一个形状。</item>
/// <item><b>布尔谓词不覆写</b>：取基类的 <c> = 1</c>/<c> = 0</c>。这一家确实有 <c>IS TRUE</c>，
/// 而 <c>TINYINT(1)</c> 装得下 <c>2</c>（<c>IS TRUE</c> 会把 2 判真、<c>= 1</c> 判假），所以两边不是同一判据。
/// 选基类那一个的理由不是"这一家更对"而是三件事叠出来的：这一族自己写进去的值只可能是 1/0
/// （<c>MySqlValues.DbTypeOf</c> 走 <c>DbType.Boolean</c>）、落地四家里<em>另两家</em>（T-SQL 与 Oracle 血统）
/// 已经是这一形（v0.31 的第五家同边，于是这一格是五家里的 <b>4∶1</b>）、
/// 而 §10.5 那一档事故（可空列上 <c>= 0</c> 匹配不到 NULL）换成 <c>IS FALSE</c> 一样匹配不到 ——
/// 换写法换不掉语义。这一格是"四家两档"里被第三家改判过一次的那一档，第四家又把它推回原处，
/// 第五家站在推回的那一边，判据与代价记 §9.3 与 §10.5。</item>
/// <item><b>恒假/恒真谓词也不覆写</b>：基类的 <c>1 = 0</c>/<c>1 = 1</c> 在这一家逐字合法，
/// 而金仓那一族必须写布尔字面量。所以合并档守卫与空集合特化这两处在 T-SQL 与这一家之间<em>同形</em>，
/// 与达梦也同形 —— 五家里四家共用这一对文本，不是巧合：那一族的 <c>WHERE</c> 要求布尔表达式，
/// 而这一家把 <c>FALSE</c> 只当成 <c>0</c> 的同义词，所以两种写法在四家里的两家里是同一个东西
/// （第五家有没有布尔字面量【无出处】，所以这句"两家"不跟着涨 —— 它落在 <c>1 = 0</c> 这一边是事实，
/// 它<em>为什么</em>落在这一边还没读到）。</item>
/// </list>
/// 类不是 <c>sealed</c>：这一族的派生叶子（TiDB、OceanBase 已落）在它上面只覆写真正分岔的那几处（§4 的能力继承链，
/// 与 <c>PostgreSqlDialect</c>、<c>OracleDialect</c> 同一条纪律）。
/// <b>本类的产出文本没有在一台真库上跑过</b> —— 授权的实例里没有一家是 MySQL 血统，见 §13.5 的出处台账。</summary>
public class MySqlDialect(DbKind kind, MySqlTypes types, MySqlKeys keys, MySqlValues values, MySqlExpressions expressions)
    : DialectBase(IdentifierPolicy.MySql, values, expressions)
{
    /// <summary><c>OFFSET</c> 单独出现时的"无上限"哨兵：这一家的语法要求 <c>OFFSET</c> 必须跟在 <c>LIMIT</c> 之后，
    /// 而 <c>LIMIT</c> 那一颗吃的是无符号大整数，文档给的"全部"写法就是 <c>18446744073709551615</c>（2^64-1）。
    /// <b>写成文本而不是参数</b>：它不是一格数据，是"没有上限"的另一种写法，占一颗参数位没有意义，
    /// 而且参数预算（<see cref="MySqlBulk"/>）量的是真值个数 —— 混进来会让 <c>RowsPerStatement</c> 少算一行。
    /// 这一颗曾经是<em>落地四家里</em>唯一一处由<em>方言类自己</em>造一个常量补语法（T-SQL 那一格相近但不是一件事：无 <c>ORDER BY</c>
    /// 时补的那颗 <c>(SELECT NULL)</c> 占位序长在<em>共用层</em>，且它是为了让一家语法要求而存在，见 §9.4），
    /// <b>v0.31 把"唯一"改掉了</b>：第五家同一条语法要求、同一处补法，而它能补的那一颗数是 <c>2147483647</c>
    /// （厂商文档写死的 INT 天花板）—— 同形不同义，所以那一格在 §13.2 里记成"两形两颗数"而不是"一家一份副本"。
    /// 所以它在黄金样本里仍然单独有一份文件
    /// （<c>golden/mysql/paging-skip-only.sql</c>，与 T-SQL 那一家的同名槽位对齐），而金仓那一份同一条链
    /// 是 <c>OFFSET @p0</c> 收尾、没有这一颗。</summary>
    const string NoLimitSentinel = "18446744073709551615";

    public override DbKind Kind { get; } = kind;

    public override char ParameterPrefix => '@';

    public override string GetStoreType(ColumnContract column) => types.GetStoreType(column);

    /// <summary>取 false 的判据见类注释第三条。<b>这颗开关不是错误豁免</b>：共用层据此整批退回一句一实体，
    /// 而 <see cref="EmitJoinedSources"/> 仍然抛 —— 走到那里说明有人绕过了开关，那种时候宁可红。</summary>
    public override bool SupportsJoinedWrites => false;

    /// <summary><c>LIMIT n OFFSET m</c>：两个子句各自可缺省，但 <b>只有 <c>OFFSET</c> 时缺省 <c>LIMIT</c> 不合法</b>
    /// （金仓那一族恰好相反：<c>OFFSET</c> 可以单独出现）。逗号那一形（<c>LIMIT m, n</c>）把偏移放在前面，
    /// 与三家的阅读顺序都反，所以取标准形 —— 它同时让这一格与金仓逐字同形（少一处假分岔）。</summary>
    protected override void EmitPaging(SqlWriter writer, SelectStatement select)
    {
        if (select.Limit is null && select.Offset is null)
            return;

        writer.Append(" LIMIT ");

        if (select.Limit is { } limit)
            EmitExpression(limit, writer);
        else
            writer.Append(NoLimitSentinel);

        if (select.Offset is { } offset)
        {
            writer.Append(" OFFSET ");
            EmitExpression(offset, writer);
        }
    }

    /// <summary>转交给 <see cref="MySqlKeys.EmitReturning"/>：与金仓那一族同一个转交形状（那一家的理由写在
    /// <c>PostgreSqlDialect.EmitReturning</c>）。间距由调用方补，这里只落地那一段 —— 而这一家两段都到不了
    /// 正常返回，因为那一口是抛的。基类那句默认文案点名的是"未实现"，这一家要点名 <c>LAST_INSERT_ID()</c>
    /// 那一颗缺的槽位，所以转交而不是留着默认那一句。</summary>
    protected override void EmitReturning(SqlWriter writer, IReadOnlyList<ColumnContract> returning) => keys.EmitReturning(writer, returning);

    /// <summary>"任何表源都不接受"的<em>第二份</em>（第三族那份在 <c>OracleDialect</c>），两家的<em>后果</em>同形而
    /// <b>理由不同一条</b>：那一族是"目标不能起别名，所以也带不进别的表"，这一家目标<em>能</em>起别名
    /// （上面那条 UPDATE 就是 ANSI 那形），缺的是"把别的表带进来"的那个子句本身。
    /// <b>v0.31 有第三份</b>（<c>XuguDialect.EmitJoinedSources</c>），而它这一份连"缺哪一半"都没被证：
    /// 三份同抛、三条理由，这一点是 §12.5 那道闸门不按库种分派的最终见证。
    /// <paramref name="keyword"/> 是 <c> FROM </c> 或 <c> USING </c> 那一格的位置名，这里连它都不看：来了就抛。
    /// <b>为什么不学金仓那一族把 INNER 的条件折进 <c>WHERE</c></b>：那一族的 <c>UPDATE … FROM</c> 只吃逗号表源，
    /// 折一下语义不变；这一家连逗号表源都没有（多表 <c>UPDATE</c> 要 <c>SET</c> 里带表名前缀的另一整条语法，
    /// 与共用层"行集 + 主键对齐"的那个形状不同源）。折得动与否不由引号决定，由那一家有没有那个子句决定。</summary>
    protected override void EmitJoinedSources(SqlWriter writer, string keyword, IReadOnlyList<JoinClause> joins, out SqlExpression? folded)
    {
        folded = null;

        if (joins.Count == 0)
            return;

        throw new QuickSqlException(
            $"{Kind} 的写语句不接受目标表之外的表源（{keyword.Trim()} 那一段有 {joins.Count} 个）：这一家没有 UPDATE … FROM / DELETE … USING，"
            + $"也没有 (VALUES …) AS t(cols) 那种带列名清单的行集，所以 §12.5 的合并档发不成一句。"
            + "整批退回一句一实体由 SupportsJoinedWrites 决定（语义与合并档等价，§12.4 第 2 项），走到这里说明那颗开关被绕过了 —— "
            + "宁可报错也不能悄悄丢掉表源，那等于丢掉连接条件（§1.2 原则 4）。");
    }
}
