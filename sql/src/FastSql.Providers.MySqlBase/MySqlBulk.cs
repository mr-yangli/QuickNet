using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using FastSql.Providers;

namespace FastSql.Providers.MySqlBase;

/// <summary>批量高速层的现状（§12.1，第四家）。<b>【本笔改判 —— 原文照抄在前，改判在后】</b>
/// 原文：「与两族同一档：只报预算，高速通道不接，所以 <c>Bulk_Copy</c> 能力位不声明」——
/// 今天这一家接上了 L1：<see cref="ResolveCopyChannel"/> 交得出 <see cref="MySqlBulkCopyChannel"/>，
/// 能力位跟着在同一家 provider 基类里声明。原文里那句「五家里只有 <c>SqlServerProvider</c> 声明
/// <c>Bulk_Copy</c>，另四家……都只报预算」从本笔起读成历史（另四家变另三家：金仓、Oracle 血统、虚谷）。</summary>
///
/// <para><b>L1 的形状与 T-SQL 那一家有两格不同</b>：装载类是 <c>MySqlBulkCopy</c>（驱动把 <c>LOAD DATA LOCAL INFILE</c>
/// 包成流式上传），映射按<em>源序号 ↔ 目标名</em>而不是双名；而且它欠连接串一颗 <c>AllowLoadLocalInfile=true</c>
/// 才走得动——那道闸门由通道自己的 <c>UnavailableBecauseOf</c> 在选层之前点名【实测·对照量具 m1∶m2，
/// 补参前两条批量路径同抛 NotSupportedException，补参后 24 格全绿】。成员形状逐颗在量具里以强类型写法跑通过
/// 2 000 行 × 7 列与 20 000 行（MySqlConnector 2.6.2 / MySQL 8.4.8）。
/// <b>而"换成反射写法"这一句今天读来太轻</b>：那一支驱动在 C# 调用面与反射面之间有四格形状差
/// （可选参数、<c>ValueTask&lt;MySqlBulkCopyResult&gt;</c> 返回值、构造子重载、没有 <c>Dispose</c> 面），
/// 照 T-SQL 那一份的手法写出来会在真库上把把交回 <see langword="null"/> 而<em>静默</em>整批退 L3 ——
/// 失败形状与"这一家根本没有装载类"完全同形，离线册一根探针都看不见。逐颗读数与修法写进
/// <see cref="MySqlBulkCopyChannel"/> 的改判段【实测·真库 8.4.8 集成册第一趟】。</para>
///
/// <para><see cref="ParameterBudget"/> 取 <b>65535</b>：那一条 <c>INSERT</c> 走的是预处理语句，而这一家的
/// 文档在"Prepared Statement 限制"那一节给出的占位符个数上限就是它（协议里那颗计数字段只有两字节宽）。
/// 出处与金仓那一族同档：那是<b>协议/服务端上限</b>而不是某个驱动的实测上限（驱动可能更早报错），
/// 这一格在活库上重测过文本层（量具与集成册都按它分块），判据见 §13.5 的出处台账。两处已知的口径噪声也记在同一格：
/// 厂商文档里这颗数另有一个"65,536"的写法（差的那一颗是 <c>0</c> 号位），今天没有一台库能分辨，
/// 所以取小的那个 —— 报小了只多几次往返，报大了会得到真库上一句"参数过多"（§9.1 那一条量级判断在此原样适用）。</para>
///
/// <para><b>这一家比另两族多一道闸门，而它不在参数个数上</b>：整条语句的字节数受 <c>max_allowed_packet</c>
/// 管（会话与部署两侧的数，默认值随版本从 4 MB 到 64 MB 不等）。65,535 颗参数在 <c>LONGBLOB</c> 表上
/// 可以先撞包体上限再撞占位符上限，所以这个数字<em>只</em>回答"能塞多少颗参数"，不回答"能发多大一条语句"。
/// <see cref="MySqlValues"/> 因此拒绝把 <c>byte[]</c> 内联进文本（那一档不受任何预算管）。
/// <b>【v0.31】"孤例"这一档没了，但方向不是这一家被追上，而是第五家带进来一颗同类闸门</b>：
/// 虚谷厂商文档给一行的记录大小上限 64K【文档】，也是"字节而不是个数"的那一种，
/// 而它<em>没有</em>任何槽位承接（<c>ColumnContract</c> 只有列宽没有行预算）——
/// 两颗的差别是这一家的那一颗至少能折算到列上（65535 / 4 = 16383，已经写进 <c>MaxChars</c>），
/// 第五家那一颗今天连折算的落点都没有。并排记 §14。</para>
///
/// <para>与第三家对照着读才有意思：那一族今天交回 <see langword="null"/>（库侧与驱动侧各有一个上限，
/// 没有一台活库能告诉哪一个先生效），于是分块走 <c>StatementEmitter.ConservativeParameterBudget</c>
/// 那一份 100 的兜底；这一族因为有一个明确的协议数字而敢报。<b>报出来的直接后果就是块数</b> ——
/// 那一份兜底与 65,535 之间差 655 倍，所以这一族的加入把 §13.1 里"三家的块数互不相同（3 / 1 / 50）"
/// 那句最强证据<em>说成了假话</em>：同一批 600 行在它与金仓各是一块。v0.30 已把它重数成两句仍然成立的话 ——
/// 块数 3 / 1 / 50 / <b>1</b>，可数的判据换成预算 2100 / 32767 / 100 / 65535 与每语句行数
/// 270 / 4212 / 12 / 8425（<c>DialectParityTests</c> 逐家钉死）。
/// <b>【v0.31】那两行各自再加一格，而"互不相同"死得更彻底</b>：第五家也没有批量策略（<c>Bulk</c> 为 null），
/// 于是拿到与达梦同一份兜底 —— 块数 3 / 1 / 50 / 1 / <b>50</b>（50 也出现了两次）、
/// 预算 2100 / 32767 / 100 / 65535 / <b>100</b>、每语句行数 270 / 4212 / 12 / 8425 / <b>12</b>。
/// 五家里<b>两处重复</b>，而重复的两处都落在"没有真预算"那一边 —— 所以这一格真正被钉住的性质是
/// "兜底分支有消费者，而且已经有两个"，不是任何一位数。</para>
public sealed class MySqlBulk : IBulkStrategy
{
    readonly Assembly _driver;

    IBulkCopyChannel? _channel;

    public MySqlBulk(DbProviderFactory driverFactory)
    {
        // 与 SqlServerBulk 同一条纪律：构造期零反射，只记"工厂住在哪个程序集"，第一次真取通道才找装载类。
        _driver = driverFactory.GetType().Assembly;
    }

    public int ParameterBudget => 65_535;

    /// <summary>已表态（本笔）。读数不是文档转述：本仓库在活 MySQL 上<em>已经在发</em>一条命令文本里塞
    /// 两句的语句 —— 多值 INSERT 带 <c>; SELECT LAST_INSERT_ID()</c> 的回填支（<c>InsertEngine</c> 的
    /// postRead 分支，集成册每轮都在跑它）。执行侧那半边的配合（跳批处理首个结果集）写在
    /// <c>AdoExecutor.WriteWithResultAsync</c> 的注里。L1 通道接上之后这一颗不改判：装载通道与
    /// Merged 档是两条独立的路，各自的实测各自的数。
    /// <b>本笔补的是<em>这条链自己</em>的读数</b>：四句 UPDATE 并成一条命令在本家的授权实例上落库成功、
    /// 逐列读回一致【实测·真库 8.4.8，集成册 <c>BulkWritePathTests</c>，<c>obj/logs/v40-my-bulkwrite-2.log</c>】。
    /// 顺带一条与"极致性能"那句诉求相左的数：200 行整行更新，合并档 701 ms、逐条 871 ms——<b>省的是往返，
    /// 省不了那 200 句的执行</b>，而这一台实例的往返本来就只有 1 ms 上下。</summary>
    public bool SupportsMergedStatements => true;

    public string Describe => "L1 MySqlBulkCopy（LOAD DATA LOCAL 流式上传，按源序号↔目标名映射，" +
                              "白名单之外的列整批退 L3；连接串欠一颗 AllowLoadLocalInfile=true，缺它选层时就退而不是执行时炸）" +
                              "+ L3 多值 VALUES + L5 逐条；参数预算 65535（预处理语句占位符上限）；" +
                              "另一道闸门 max_allowed_packet 管的是字节而不是个数，不在本策略内；Merged 档已表态（UpdateRange 的整行批在本家真库跑通过）；" +
                              "Upsert 档已接（一句 INSERT … AS `fs` … ON DUPLICATE KEY UPDATE，8.0.19+ 的行别名写法，本尊 8.4.8 量过）";

    /// <remarks>惰性形状与 <c>SqlServerBulk</c> 逐字同一条：无锁，命中进字段，落空每次重查——
    /// 落空的下一步是退文本层付 N 次往返，为它省一次类型查找没有意义。
    /// 与 T-SQL 那一家的差别在命中之后还有一道连接串闸门（<c>UnavailableBecauseOf</c>），
    /// 判它的是内核里的选层而不是这里。</remarks>
    [RequiresUnreferencedCode("取通道是一次按类型名的反射查找（MySqlBulkCopyChannel.TryResolve），裁剪后的驱动交回 null，阶梯因此从 L3 起。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：反射解析装载类需要运行时生成代码。")]
    public IBulkCopyChannel? ResolveCopyChannel() => _channel ??= MySqlBulkCopyChannel.TryResolve(_driver);

    /// <summary>一句 upsert 的句尾：<c>VALUES (…) AS `fs`(…) ON DUPLICATE KEY UPDATE 列 = `fs`.列</c>
    /// （2026-10-07 第二条裁决，MySQL 血统那一家接的写法）。
    /// <para><b>行别名那一形而不是老的 <c>VALUES(col)</c></b>：8.4.8 上老写法仍然收下而每句带两条 Warning 1287
    /// （厂商写明将在后续版本移除），行别名写法收下而 <c>SHOW WARNINGS</c> 为空【实测·探针 <c>obj/probe-upsert</c>，
    /// <c>obj/logs/v40-upsert-shapes2.log</c>】。代价是一个版本地板：<c>AS</c> 给行取别名是 8.0.19 起的东西，
    /// 而这一颗挂在共用的 <c>MySqlBulk</c> 上、三颗叶子（MySQL ∶ TiDB ∶ OceanBase）一起领——本仓库只证到了
    /// MySQL 本尊那一台 8.4.8，另两家的 <c>ON DUPLICATE KEY UPDATE</c> 行别名是否等价【未实测】，
    /// 与 <c>LOAD DATA LOCAL</c> 那一家两叶子同档，缺口并排记 §14。</para>
    /// <para>别名清单按 <see cref="UpsertColumns.ValueOrder"/> 写而不是按可写列写：那一串必须与内核拼出的
    /// <c>INSERT</c> 列清单逐位同序，错一位就把值写进别人的列。别名本身过 <see cref="UpsertColumns.Identifier"/>
    /// 取号，本家不手拼反引号。</para></summary>
    public string? UpsertClause(UpsertColumns columns)
    {
        var alias = columns.Identifier("fs");

        var builder = new StringBuilder("AS ").Append(alias).Append('(');

        for (var column = 0; column < columns.ValueOrder.Count; column++)
        {
            if (column > 0)
                builder.Append(", ");

            builder.Append(columns.ValueOrder[column]);
        }

        builder.Append(") ON DUPLICATE KEY UPDATE ");

        for (var column = 0; column < columns.Settable.Count; column++)
        {
            if (column > 0)
                builder.Append(", ");

            builder.Append(columns.Settable[column]).Append(" = ").Append(alias).Append('.').Append(columns.Settable[column]);
        }

        return builder.ToString();
    }
}
