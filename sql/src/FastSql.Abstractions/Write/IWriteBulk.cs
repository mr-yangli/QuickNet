using System.Diagnostics.CodeAnalysis;
using FastSql.Aop;
using FastSql.Providers;
using FastSql.Sql;

namespace FastSql.Write;

/// <summary>批量写链（<c>UpdateRange</c> / <c>DeleteRange</c>）走哪一档（§12.1 的写侧扩展）。
/// <b>只有今天真有实现的档才进这颗枚举</b>，与 <see cref="Providers.BulkMethod"/> 同一条纪律：
/// StagingCopy（先 COPY 进临时表再一句 <c>UPDATE … FROM</c>）在 §12.1 的原稿里有名字，
/// 而今天没有一家接到能报行数的实现，所以这里没有它的成员。
/// <see cref="Auto"/> 与插入那一族的口径逐字相同：不是"另一档"，是"由这条出口自己从上往下取第一个满足条件的档"。
/// <b>而 <see cref="Upsert"/> 是这颗枚举里第一颗"不在阶梯上"的成员</b>：它不比谁快，它换的是语义
/// （库里没有的那一行会被插进去），所以它<em>只在被点名时</em>参与，<see cref="Auto"/> 永不取它。</summary>
public enum BulkWriteMethod
{
    /// <summary>从上往下取第一个满足条件的档：Merged → SingleRow。
    /// <c>DeleteRange</c> 那一条链上 Merged 永远满足（一句 <c>WHERE 键 IN (…)</c> 是单条语句，不欠多语句能力），
    /// 所以它的 Auto 恒等于 Merged；<c>UpdateRange</c> 的 Merged 要 <see cref="IBulkStrategy.SupportsMergedStatements"/>
    /// 为真才可达，拿不到的那一家落逐条、<b>不算降级</b>（它没承诺过任何一档）。
    /// <b>这一句"取第一个满足条件的档"里今天仍然没有 <see cref="Upsert"/> 的位置</b>（2026-10-07 的第二条裁决）：
    /// 条件的第一条是语义而不是速度，而 upsert 的语义与其余两档不同 —— 让 Auto 拿它等于把"改不到就是没这行"
    /// 那一种调用悄悄变成"改不到就插一行"。</summary>
    Auto,

    /// <summary>多条语句并成一条命令文本一次递交（<c>UpdateRange</c>），或一句 <c>IN</c> 清单删整批键
    /// （<c>DeleteRange</c>）。分块仍按 §12.3 的参数预算走。</summary>
    Merged,

    /// <summary><b>一句</b> <c>INSERT … VALUES (…),(…) 冲突子句</c> 把整批改完（2026-10-07 的第二条裁决，
    /// MySQL 血统的 <c>ON DUPLICATE KEY UPDATE</c> ∶ PG 血统的 <c>ON CONFLICT … DO UPDATE SET … = EXCLUDED.…</c>）。
    /// <b>只有 <see cref="IUpdateBulk{T}"/> 有这一档</b>：<see cref="IDeleteBulk{T}"/> 点名它当场拒——
    /// "缺键就插"在删除链上没有意义。
    /// <para>它与 <see cref="Merged"/> 的差别不是快慢而是<em>命中之外那一支的去向</em>：Merged 打不中就是
    /// 0 affected 而这一档写进去一个新行。因此 <see cref="Auto"/> 永不取它，而点名它而这一家没接时
    /// 按 <see cref="Providers.FallbackPolicy"/> 三档办——<b>降级退到的是"整行改"那一档，语义跟着退</b>，
    /// 那句话写进 <see cref="BulkWriteReport.Reason"/> 而不是留给现场猜。</para>
    /// <para>两道当场拒：<b>全局过滤器在场</b>（那一句没有 WHERE 可以叠过滤器，命中范围会宽过调用方以为的），
    /// 以及<b>主键由库给值而这一行的键仍是默认值</b>（0 ∶ <see cref="Guid.Empty"/> ∶ 空串——那种键的冲突判定
    /// 出不来，插进去的是一个新行而不是改了那一行）。两道的判据与 <c>null</c> 键同一道门，见
    /// <see cref="IUpdateBulk{T}.Explain"/>。</para></summary>
    Upsert,

    /// <summary>逐条：一行一条语句、一次往返。阶梯最底那一档，永远可达。</summary>
    SingleRow,
}

/// <summary>一次批量写到底走了哪一档、付了几次往返、为什么。形状与 <see cref="BulkReport"/> 逐字同族
/// （同一份"现场先看到为什么，而不是去读源码"的纪律），拆成两颗而不是复用，是因为档位集合不同：
/// 插入阶梯有装载通道那一档而这里没有，这里也不该冒出一颗 <see cref="BulkMethod.CopyChannel"/> 取值。</summary>
public sealed record BulkWriteReport(
    BulkWriteMethod Requested,
    BulkWriteMethod Method,
    int RowCount,
    int RoundTrips,
    int ChunkSize,
    bool Degraded,
    string Reason)
{
    /// <summary>这一批<em>实际</em>是不是一次提交（<see cref="IUpdateBulk{T}.WithBatchTransaction"/> 那一条裁决的落地位）。
    /// <b>它是 <c>init</c> 属性而不是位置参数</b>：决策段与执行段各判一次"要不要包"，而执行段才知道驱动有没有给事务，
    /// 放进位置参数会让所有构造点（含离线册的台账）跟着改号。<b>环境事务在场时这一颗恒为 false</b>——
    /// 那不是"没包"，是"包得比这一批更大"，提交归开事务的那个人（§8.2 那一条同族）。</summary>
    public bool UsedTransaction { get; init; }
}

/// <summary>批量写链的离线产物：一批命令文本（一条命令 = 一次往返）加上决策的那句话。
/// 与 <see cref="InsertPlan"/> 的分工同一族，而块的内容不同：这里一块是"多条语句并成的一条命令文本"，
/// 参数表整条命令共用一份（名字由 <see cref="SqlWriter"/> 的共享计数器发，跨语句不重号）。</summary>
public sealed record BulkWritePlan(
    IReadOnlyList<DialectSql> Commands,
    int RowCount,
    int RowsPerCommand,
    int ParameterBudget,
    string Reason,
    IReadOnlyList<HookTrace> HookReplay)
{
    /// <summary>与 <see cref="InsertPlan.Provider"/> 同理：内联字面量要问那一家方言的 binder。</summary>
    public required IDbProvider Provider { get; init; }

    public bool IsSingleCommand => Commands.Count == 1;

    /// <summary>不连库；<b>只有单条命令时可取</b>，口径与 <see cref="InsertPlan.ToSql"/> 逐字相同。</summary>
    public string ToSql() => Commands.Count switch
    {
        1 => Commands[0].Text,
        0 => throw new FastSqlException("0 行批量写没有对应的 SQL 文本：不产生命令，执行会直接返回 0。"),
        _ => throw new FastSqlException($"这批写被分成 {Commands.Count} 条命令（{Reason}），没有单一 SQL 文本。取 Explain().Commands[i]，或用 ToLogSql() 看全部。"),
    };

    /// <summary>逐条命令内联字面量，多条时带 <c>-- 命令 n</c> 分节：交给人审阅的文本必须能看出有几条命令。</summary>
    public string ToLogSql()
    {
        if (Commands.Count == 0)
            return "-- 0 行：不产生任何命令";

        var lines = new string[Commands.Count];

        for (var i = 0; i < Commands.Count; i++)
        {
            var log = Commands[i].ToLogSql(Provider);

            lines[i] = Commands.Count == 1 ? log : $"-- 命令 {i + 1}{Environment.NewLine}{log}";
        }

        return string.Join(Environment.NewLine, lines);
    }

    public override string ToString() => Reason + Environment.NewLine + ToLogSql();
}

/// <summary><c>orm.UpdateRange(rows)</c> 的链：<b>按主键整行更新</b>——每一行把除主键、自增列、
/// 并发列以外的全部列写进 <c>SET</c>，主键进 <c>WHERE</c>。它不是 <see cref="IUpdate{T}"/> 的批量皮
/// （那条链改的是调用方点名的几列），也不与 <c>SaveChanges</c> 合流：那条要跟踪器的脏列判定，这一条的契约
/// 就是"整行交出去"。取值零表达式树（列清单来自契约、值来自编译好的读写委托），所以这一条入口
/// 与 <see cref="FastSqlOrm.InsertRange{T}"/> 同档：<b>没有 RUC/RDC 入口税</b>。</summary>
public interface IUpdateBulk<T> where T : class
{
    /// <summary>请求哪一档。<b>默认不是 <see cref="BulkWriteMethod.Auto"/> 而是它自己判出来的那一档</b>：
    /// 这条链存在的意义就是批量，没有"默认逐条"那一种调用姿势。<paramref name="fallback"/> 的三档语义
    /// 与 <see cref="IInsert{T}.WithBulk"/> 逐字相同（Never 给 CI 用）。</summary>
    IUpdateBulk<T> WithBulk(BulkWriteMethod method, FallbackPolicy fallback = FallbackPolicy.DegradeAndLog);

    /// <summary>这一批要不要<em>一次提交</em>。<b>默认 true</b>（2026-10-07 的裁决），执行段在没有环境事务在场时
    /// 自开一条事务、末尾提交一次；传 false 关掉，回到"每条语句自己提交、半批可以在库里落地"。
    /// <para><b>这一颗首先不是原子性开关，是速度开关</b>：并档把往返并了而没把提交并了——InnoDB 每次提交
    /// flush 一次 redo，2000 行整行更新在同一台远端实例上量到 <c>6386 ms ∶ 263 ms</c>，
    /// 差距全部来自提交次数（裸 ADO 同一形状 <c>7419 ∶ 920</c> 是它的对照组）
    /// 【实测·<c>obj/logs/fscompare-C.txt</c> S3 那四支诊断档】。
    /// <b>只在语句数 &gt; 1 时才包</b>：一条语句本身就原子，包它只多付两次往返。</para>
    /// <para>关掉它的唯一理由是调用方自己要用"半批已落地"做断点续传——那种用法要么自己开事务、
    /// 要么自己逐条调 <c>orm.Update&lt;T&gt;()</c>，所以这里给的是一等公民的位置而不是暗示的路。</para></summary>
    IUpdateBulk<T> WithBatchTransaction(bool atomic = true);

    /// <summary>不连库看这批会发出的命令文本（档已按 <see cref="WithBulk"/> 的请求落定）。
    /// <b>"零入口税"说的是 <see cref="WithBulk"/> 与 <see cref="ExplainBulk"/> 这两颗，不是这一颗</b>：
    /// 建文本要把 <c>orm.Filters</c> 上的全局过滤器叠进 <c>WHERE</c>，那一步走闭包常量求值 ——
    /// 与 <c>UpdateEngine</c>/<c>DeleteEngine</c> 的 <c>Where</c> 翻译同一套标注（§5.10），
    /// 没有装过滤器的项目照样走到这句标注，税在类型面上不在分支上。</summary>
    [RequiresUnreferencedCode("全局过滤器的叠加（Filters.ApplyTo）走闭包常量求值，被裁剪的产物上谓词翻译会换一条退路。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：谓词翻译的退路是编译表达式树。")]
    BulkWritePlan Explain();

    /// <summary>不连库回答"这一批会走哪一档、几条命令、为什么"——判据与执行共用同一颗纯函数，
    /// 口径与 <see cref="IInsert{T}.ExplainBulk"/> 同一条。<b>这一颗不交过滤器那笔税</b>：判档只看
    /// provider 表态与行数，不建文本。</summary>
    BulkWriteReport ExplainBulk(BulkWriteMethod? requested = null);

    /// <summary>执行并交回决策本身。返回值逐字对齐 <see cref="IInsert{T}.ExecuteBulkAsync"/>：
    /// 一条命令的 <c>ExecuteNonQuery</c> 行数按驱动口径累加，降级不留痕于 AOP（本档的
    /// <c>BulkDegraded</c> 接线等事件形状改成两种档位通用那天，缺口记 §14）。</summary>
    [RequiresUnreferencedCode("执行前建文本要走全局过滤器的叠加（同 Explain 那句）。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：谓词翻译的退路是编译表达式树。")]
    Task<BulkWriteReport> ExecuteBulkAsync(CancellationToken ct = default);
}

/// <summary><c>orm.DeleteRange(rows)</c> 的链：<b>按主键删整批</b>，Merged 档产出的是
/// <c>DELETE … WHERE 单列主键 IN (@p0…@pN)</c> —— 一句单条语句，不欠多语句能力，所以这一条链的
/// Auto 在所有 provider 上都恒等于 Merged（预算不够切块，与 §12.3 同一把尺）。
/// 复合主键当场拒并指路（<c>IN</c> 装不下元组，逐对 <c>OR</c> 展开会绕开参数预算的账）。
/// 与 <see cref="IUpdateBulk{T}"/> 同一族理由：<c>WHERE</c> 长在键上而不是表达式树上，入口零反射税。</summary>
public interface IDeleteBulk<T> where T : class
{
    IDeleteBulk<T> WithBulk(BulkWriteMethod method, FallbackPolicy fallback = FallbackPolicy.DegradeAndLog);

    /// <summary>同 <see cref="IUpdateBulk{T}.WithBatchTransaction"/>，而这一条链上它<em>几乎不触发</em>：
    /// Merged 档是一句 <c>WHERE 键 IN (…)</c>，一条语句本身就原子，包事务只多两次往返。
    /// 真正会包的只有一种形状——键数超出一句的预算、这批被切成两句以上（MySQL 血统 65535 颗、
    /// SQL Server 2100 颗，所以那一家两万颗键就是两条命令）。</summary>
    IDeleteBulk<T> WithBatchTransaction(bool atomic = true);

    [RequiresUnreferencedCode("全局过滤器的叠加（Filters.ApplyTo）走闭包常量求值，被裁剪的产物上谓词翻译会换一条退路。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：谓词翻译的退路是编译表达式树。")]
    BulkWritePlan Explain();

    BulkWriteReport ExplainBulk(BulkWriteMethod? requested = null);

    [RequiresUnreferencedCode("执行前建文本要走全局过滤器的叠加（同 Explain 那句）。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：谓词翻译的退路是编译表达式树。")]
    Task<BulkWriteReport> ExecuteBulkAsync(CancellationToken ct = default);
}
