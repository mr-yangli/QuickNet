using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;

namespace QuickSql.Providers.Gbase8s;

/// <summary>GBase 8s（Informix 血统）的主键策略（§4 的 IKeyStrategy，第六家）。
/// <b>这一格是本家与另五家分岔最密的一处，而且两档都是"有语法而没有出口"的形状</b>：
///
/// <para><b><see cref="EmitReturning"/> 抛</b>。这一族的 <c>RETURNING</c> 确实存在【文档·IBM】，
/// 但它<em>只</em>在两个宿主环境里成立：ESQL/C 的 <c>INSERT … RETURNING … INTO :宿主变量</c>，
/// 与 SPL 的 <c>RETURNING … INTO</c> 局部变量。两支都要 <c>INTO</c>，而 <c>INTO</c> 要的是
/// <b>OUT 绑定</b> —— <c>AdoParameter</c> 今天没有方向位（那一格自 §4 起一直缺，Oracle 血统与虚谷
/// 两家各为它记过一次账），所以"跟着语句一起发、当结果集读回"这一档在本家<em>发不出</em>：
/// 没有 <c>INTO</c> 的那一形在 CLI 侧收不到结果集【文档·IBM】。
/// 于是六家的分布从 <c>2 发 ∶ 3 抛</c> 变成 <b>2 发 ∶ 4 抛</b>，而抛的四家里本家多一层：
/// Oracle 血统那颗"连表达式都没被问证过"，本家这一颗是<b>表达式与语法都有、缺的是绑定的方向</b>——
/// 四家里离"能接"最近的一颗，而"最近"仍然等于没接。</para>
///
/// <para><b><see cref="SupportsPostStatementKeyRead"/> 取 true</b>，走 v0.34 立的那颗口子，
/// 而它是这颗开关<em>第二个</em>为真的家（第一颗是虚谷，那一家有真库读数）。本家没有，所以这一位
/// 今天带着<b>开关为真而前提未证</b>的形状，两句要连着读：
/// <list type="bullet">
/// <item><b>回读表达式本身有出处</b>【文档·IBM】：<c>DBINFO('sqlca.sqlerrd1')</c> 交回
/// <em>本会话最近一条 INSERT 由 <c>SERIAL</c> 生成的那个值</em>，而 64 位那一档要换
/// <c>DBINFO('serial8')</c>（<c>sqlerrd1</c> 是 32 位的槽，用它读 <c>SERIAL8</c> 会静默截高位
/// —— 这是本文件里唯一一处"两个都合法而选错会静默"的格，所以 <see cref="EmitPostStatementKeyRead"/>
/// 按 CLR 侧的键宽分两支，判据写在下面那一行）。这一格与 §9.1 本行第四列那句
/// "日期函数体系完全不同"读在一起才有意思：<b>这一家把"库里刚给了我什么"放在一颗会话函数里，
/// 而不是放在语句的回包里</b>。</item>
/// <item><b>它的前提<em>没有</em>出处</b>：Core 把回读句<em>接在同一条命令文本的尾巴上</em>
/// （中间一个 <c>;</c>），而"一条命令文本里放两句 SQL"在本家收不收【文档·IBM 本次没读到这一页】。
/// MySQL 血统与虚谷那两家能这么发，而它们各自有真库见证（虚谷 <c>T-03</c>）。本家没有，
/// 所以这一位今天写的是<b>形状对而接收未证</b>：错的话它红在<em>每一条</em>带回读的开销上，
/// 而不会红在少回填一颗键 —— 那个方向是<em>可数的</em>（§12.4 第 1 项同一判据）。
/// <b>重开触发</b>记 §14：第一台活库上第一件事问的就是这一句。</item>
/// </list>
/// <b>覆盖面仍然窄到要逐条数</b>：单一自增列、逐条形（每块一行）、同命令同会话。
/// 带并发列的回填撞不出去（回读句给不了那一格的值，与虚谷那一条同一格），多颗自增列同理；
/// 两种都落回 <see cref="EmitReturning"/> 那一抛。</para>
///
/// <para><see cref="SupportsExplicitIdentityInsert"/> 取 false，与六家里的另五家同一条路（只有 T-SQL
/// 一家为 true，那一家有 <c>SET IDENTITY_INSERT</c>）。本家的自增写在<em>类型名</em>上
/// （<c>SERIAL</c> 那一族，<c>Gbase8sTypes</c> 第三条），而"往自增列里写显式值"在这一家是
/// <b>默认就允许</b>的（写进去的值会落在序列之下，随后的 <c>SERIAL</c> 从它上面续）【文档·IBM】——
/// 也就是说这一家压根没有那一档开关要打开。所以这一位为 false 的含义与另五家不同：
/// 那五家是"没读到有这能力"，本家是"这件事不需要能力"。
/// <b>而这一格里藏着本家真正的缺口</b>：往 <c>SERIAL</c> 列写显式值之后，序列的下一颗要不要跟着挪、
/// 由哪一格里最大值决定还是由本会话决定，本次没有可引读数 —— 那是 <c>IDENTITY_ExplicitInsert</c>
/// 那一档在别家都要面对的问题（T-SQL 明确会挪），本家连"会不会撞"都答不出，整格记 §14。
/// 刻意不为 <see cref="IdentityInsertToggle"/> 留覆写点：Core 今天没有那条调用路径（六家已把它记成
/// §14 的一格），覆写它会得到一颗永远不被调用的虚函数。</para></summary>
public sealed class Gbase8sKeys : IKeyStrategy
{
    /// <summary>抛，而不是发一条"看着像在回填"的语句。完整理由在类注释第一段（<c>INTO</c> 那一格缺的是
    /// <c>AdoParameter</c> 的方向位，不是本家的语法）。表侧列名在这里点出来，因为现场读到这条错的人
    /// 下一步一定是"我这颗键为什么没回来"，而那一句要能自己答。</summary>
    public void EmitReturning(SqlWriter writer, IReadOnlyList<ColumnContract> returning)
        => throw new QuickSqlException($"GBase 8s 没有可作为结果集发出的 RETURNING，而本次请求要回填（{Columns(returning)}）。"
            + "这一家的 RETURNING 只活在 ESQL/SPL 里、且必须配 INTO 宿主变量（CLI 侧没有 INTO 的那一形收不到结果集）；"
            + "INTO 要的是 OUT 绑定，而 AdoParameter 今天没有方向位（那一格自 §4 起一直缺，Oracle 血统与虚谷各记过一次）。"
            + "单颗自增键请走语句后回读那一条（SupportsPostStatementKeyRead 为真，而它自己的前提见 Gbase8sKeys 类注释）；"
            + "多颗或带并发列的回填今天没有路，请自己生成主键（Guid/雪花）并直接用 ExecuteAffrowsAsync。");

    /// <summary>位掩码之外这一档真正的形状：<b>只覆盖单颗自增列</b>，而它的<em>前提</em>（同一条命令文本
    /// 里放两句）在本家未被任何一档读数证过 —— 两句都在类注释第二段，那里也写了重开触发。</summary>
    public bool SupportsPostStatementKeyRead => true;

    /// <summary>两式按 CLR 侧的键宽分岔【文档·IBM】，而这一格是本家独有的一颗：
    /// <c>sqlca.sqlerrd1</c> 那一槽是 <b>32 位</b>的（它就是 Informix 的 <c>sqlca</c> 结构里那个
    /// <c>long</c>），所以 <c>SERIAL8</c>/<c>BIGSERIAL</c> 生成的 64 位序列值要用
    /// <c>DBINFO('serial8')</c> 读；拿前一颗读后一档会<em>静默丢高位</em>（不报错、不截断提示，
    /// 交回一个对不上的数）—— 本文件里唯一一处"选错不会红而会错"的格。
    /// <c>long</c>/<c>ulong</c> 那一支走 serial8，其余走 sqlerrd1；判据取 CLR 型而不是取
    /// <c>StoreTypeName</c>，因为后者只有 <c>ApplyStorageDefaults</c> 跑过才有值，而回读句在
    /// 发射路径上就要定（与 <c>Gbase8sChangeGuard.BaseType</c> 同一格同一条理由）。
    /// <b>不引列名、不带参数</b>：这一颗是会话级的，不是列级的。前面的 <c>;</c> 与空格由 Core 补。</summary>
    public void EmitPostStatementKeyRead(SqlWriter writer, ColumnContract key)
    {
        var clrType = Nullable.GetUnderlyingType(key.ClrType) ?? key.ClrType;

        // 表源不是装饰：这一族没有 DUAL 那一颗只供"算一个表达式"用的表【文档·IBM】，
        // 而 systables 里 tabid = 1 那一行（它自己）恒在，所以它是这一家事实上的 SELECT-1 表源。
        // 目录表名不带引号是本家的一个取向：它是库自己的对象，不是我们建的对象，
        // 而本家的折叠规则是"不带引号折小写"—— systables 在目录里本来就是小写，两者相遇恰好一致。
        writer.Append(clrType == typeof(long) || clrType == typeof(ulong)
            ? "SELECT DBINFO('serial8') FROM systables WHERE tabid = 1"
            : "SELECT DBINFO('sqlca.sqlerrd1') FROM systables WHERE tabid = 1");
    }

    /// <summary>false，而含义与另五家不同一条（类注释第三段：这一家"写显式值"不需要开关）。
    /// 那一格真正欠的是"序列会不会跟着挪"，它今天没有读数。</summary>
    public bool SupportsExplicitIdentityInsert => false;

    public bool SupportsBulkKeyRetrieval => false;

    /// <summary>没有开关，而且这一口刻意不是 <c>virtual</c>（Core 今天没有调用点）。
    /// 返回 null 让 Core 走"这条语句不需要前置/后置语句"那一支，与五家同一条路。</summary>
    public string? IdentityInsertToggle(SqlWriter writer, TableInfo table, bool on) => null;

    static string Columns(IReadOnlyList<ColumnContract> returning)
    {
        var names = new System.Text.StringBuilder(returning.Count * 12);

        for (var i = 0; i < returning.Count; i++)
        {
            if (i > 0)
                names.Append(", ");

            names.Append(returning[i].ColumnName);
        }

        return names.ToString();
    }
}
