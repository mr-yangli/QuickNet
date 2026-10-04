using FastSql.Metadata;
using FastSql.Providers;
using FastSql.Sql;

namespace FastSql.Providers.Sqlite;

/// <summary>SQLite 的主键策略（§4 的 <see cref="IKeyStrategy"/>，第七家）。<b>这一格是本仓库第一份
/// 四个宿主全部在活库上逐条问过的实现</b>，而它顺带把另六家的一句共同措辞改掉了：那六家的
/// <c>SupportsBulkKeyRetrieval</c> 里有两颗拿的是厂商文档（<c>PostgreSqlKeys</c> 类抬头那句"真值来自
/// 厂商文档，现场没有实例跑过"是七家里最诚实的一处自述），本家这一位是<b>问出来的</b>：
/// <c>INSERT</c>【实测·D3/403】、<c>UPDATE</c>【实测·第 2 轮 105 ∶ 404 ∶ 1319】、<c>DELETE</c>【实测·第 2 轮 106】、
/// <c>UPSERT … DO UPDATE</c>【实测·130/421】四形各一次，多列一次【实测·K/124/421】，
/// 多行一次【实测·1320/1011/1013】。
///
/// <para><b>第二档可指认的新事实是"回填不改变计数"</b>：带 <c>RETURNING</c> 的写入语句，驱动的
/// <c>ExecuteNonQuery</c> 仍然交回受影响行数（1 与 4 两颗【实测·340/341】）。这一格在另六家是
/// <em>默认成立而没人问</em>的（T-SQL 的 <c>OUTPUT</c> 走同一形状，PG 系的 <c>RETURNING</c> 文档没写），
/// 而本仓库的 <c>Affrows</c> 判据正压在这颗数上：若 <c>RETURNING</c> 把计数变成"回传的行数"，
/// <c>ExecuteAffrowsAsync</c> 那一族调用会全体错位。本家它是量过的。</para>
///
/// <para><b>自增这一档本家与 T-SQL 同一侧，而理由与它不同一条</b>：<see cref="SupportsExplicitIdentityInsert"/>
/// 取 true 的含义是"往自增列写显式值这件事被允许、且库会跟着走"，两句都有读数：
/// <c>NULL</c> 进自增列由库补号【实测·210/211】，显式值进去之后序列<em>会挪到它上面</em>
/// （一颗 100 写进去、下一号是 101【实测·54 ∶ 55】；一颗 31 亿写进去、下一号是 3000000001【实测·310/310b】）。
/// 后一条正是 <c>Gbase8sKeys</c> 类注释第三段说"这一家连会不会撞都答不出"而记在 §14 的那一颗 ——
/// <b>本家有答案，而答案是"不会撞"</b>。与 T-SQL 的分别是开关：那一家要 <c>SET IDENTITY_INSERT ON</c>
/// 才允许写，本家<em>任何时候都允许</em>，所以 <see cref="IdentityInsertToggle"/> 返回 null
/// （七家里第六家返回 null，与另五家同一条路而<em>同路不同因</em>：那五家是"没读到有这能力"，
/// 本家是"这件事不需要开关"）。
/// <b>多一句关于序列的</b>：这一族的自增没有独立对象（<c>CREATE SEQUENCE</c> 在本家不存在，实测
/// 见 <see cref="SqliteDialect"/> 类注释），它是一张 <c>sqlite_sequence</c> 目录表，而表名那颗字段存的是
/// <em>不带所有者限定的裸名</em>【实测·56 ∶ 3810 那一颗 <c>name</c> 列】，而这张表本身要到第一次
/// 自增插入才存在【实测·D6 ∶ 56】—— 于是"读一下下一号是多少"这一问
/// 在本家要跨两张表，而 SPI 里没有任何一格问它（§14 与 §9.1 本行第四列）。</para>
///
/// <para><see cref="SupportsPostStatementKeyRead"/> 取 false，而这一位要说清两层：<b>本家做得到</b>
/// —— 会话级的 <c>last_insert_rowid()</c> 存在、原样可问【实测·D5/212/1018】，而"同一条命令文本里放
/// 两句 SQL"这一手也是实测过的【实测·E3b/152/213/226/426】（那正是 <c>Gbase8sKeys</c> 第二段里
/// "形状对而接收未证"的那一颗前提，本家它有见证）。<b>但这条路在本家没有入口</b>：Core 只在批量回填位
/// 为 false 时才问这颗开关，而上面第一段刚说了本家批量位为真。于是这里取 false 说的是"无入口"，
/// 与 <c>PostgreSqlKeys</c> 那句"不是没有那条路而是用不上那条路"同一条读法，第七家第二次用到这个措辞。
/// <b>两颗限定一起留在读数里，因为它们是这条路在本家<em>真</em>不可靠的原因</b>：那颗函数是
/// <em>连接级</em>的（跨连接问不到，实测 D5），而一条多行 <c>INSERT</c> 它只回<em>最后一行</em>的号
/// 【实测·212/1018】—— 也就是说即使哪天批量位因别的原因关掉，退化路也只对"每语句一行"的形状成立，
/// 而那正是 Core 那条路自己的建模前提（§14 的 v0.34 那一格），两边在这里第一次完全对齐。</para></summary>
public sealed class SqliteKeys : IKeyStrategy
{
    public bool SupportsBulkKeyRetrieval => true;

    /// <summary>类注释第三段：显式值合法、且库会把序列挪到它上面，两颗都有实测。取 true 的下游后果
    /// 与 T-SQL 同一条（<c>InsertStrategy</c> 的"带主键插入"那一档在本家可行），差别只在不需要开关。</summary>
    public bool SupportsExplicitIdentityInsert => true;

    /// <summary>句尾 <c>RETURNING a, b</c>，与 PG 系同形（那一家是【文档】，本家是【实测】四宿主）。
    /// 调用方负责自己那侧的空格（<c>DialectBase</c> 在 <c>VALUES</c> 与整句末尾各补一次），
    /// 与 <c>PostgreSqlKeys</c> 同一句纪律逐字抄。
    /// <b>一颗本家特有的取舍</b>：这一族的 <c>RETURNING</c> 对<em>任何</em>表达式都成立，
    /// 所以它理论上能回填"库里刚算出来的任何一格"（<c>DEFAULT</c> 值、<c>CURRENT_TIMESTAMP</c>），
    /// 而 <c>ColumnContract.StoreFillsValue</c> 那条链今天只有 <c>SqlServerTypes</c> 会置位 ——
    /// 本家有承接的语法而契约没有那一格的调用方，这一格与 <c>Gbase8sConcurrency</c> 类注释里
    /// "两颗一起记 §14"那一句并排（那一家是有表达式没有回包，本家是两者都有而没人调用）。</summary>
    public void EmitReturning(SqlWriter writer, IReadOnlyList<ColumnContract> returning)
    {
        writer.Append("RETURNING ");

        for (var i = 0; i < returning.Count; i++)
        {
            if (i > 0)
                writer.Append(", ");

            writer.AppendIdentifier(IdentifierPolicy.Sqlite, returning[i].ColumnName);
        }
    }

    /// <summary>没有开关，而且这一口刻意不是 <c>virtual</c>（Core 今天没有调用点，六家已把它记成 §14 的
    /// 一格）。返回 null 让 Core 走"这条语句不需要前置/后置语句"那一支 —— 本家的 null 与另五家的 null
    /// 不同因（类注释第三段）。</summary>
    public string? IdentityInsertToggle(SqlWriter writer, TableInfo table, bool on) => null;

    /// <summary>类注释第四段：<b>做得到，但没有入口</b>。</summary>
    public bool SupportsPostStatementKeyRead => false;

    /// <summary>不可达（<see cref="SupportsBulkKeyRetrieval"/> 为 true 时 Core 不会走到这里）；
    /// 留抛不留空体，理由与 <c>PostgreSqlKeys</c> 那一份同一条。抛的话里本家多说一句"其实做得到"，
    /// 因为本家的这一条与 GBase 8s 那一条只差一颗开关的读法，容易抄错。</summary>
    public void EmitPostStatementKeyRead(SqlWriter writer, ColumnContract key)
        => throw new FastSqlException("SQLite 走批量 RETURNING，语句后回读那一档在这一家没有入口"
            + $"（本次要回填的是「{key.ColumnName}」）。这一家确实有 last_insert_rowid()、也支持一条文本里放两句"
            + "（两处都有实测，见 SqliteKeys 类注释第四段），但 Core 只在批量回填位为 false 时才问这颗开关；"
            + "真要退到那一条，注意它是连接级的、且多行插入只回最后一行的号。");
}
