using System.Data.Common;
using QuickSql.Providers;
using QuickSql.Sql;
using QuickSql.Values;

namespace QuickSql.Providers.MySqlBase;

/// <summary>MySQL 血统的驱动槽位（§4 的能力继承链：第四档）。MySQL 本尊 / MariaDB / TiDB 共享这一份，
/// 各自只覆写真正分岔的那几处 —— 与另两档同一形状，而今天的现实是<b>这一档还没有叶子</b>：
/// <c>QuickSql.Providers.MySql</c> 那一层壳不覆写任何一格（与金仓那 33 行同一处境，
/// 而 MariaDB 的 <c>RETURNING</c> 与 TiDB 的 <c>AUTO_ID_CACHE</c> 是两颗已经点名的将来覆写者）。
/// <b>这一句在 v0.35 第三、四两笔后作废了一半，原文留着记当时的分布</b>：这一档今天挂着三颗 provider——
/// MySQL 本尊（零覆写，就是上一句说的处境）与 TiDB、OceanBase 两颗分叉叶子（同样零覆写，
/// "等 TiDB 那天再虚化"两处都裁成了"仍不虚化"）。<b>而"两颗将来覆写者"到今天一颗都没到场</b>：
/// MariaDB 这一档已从 <c>DbKind</c> 撤下、本仓库不立（撤档理由与重开条件记设计文档 §14），<c>AUTO_ID_CACHE</c> 那格 TiDB 叶子没领。
///
/// <para><b>构造期不反射</b>：<see cref="DbProviderFactory"/> 由 Core 的 <c>DriverRegistry</c> 按
/// <c>QuickSqlDrivers.json</c> 解析后注入，所以本程序集编译期不认识 <c>MySqlConnector</c>（§1.2 原则 3）。
/// 那一颗程序集名与达梦那一格同一处境：按 §9.1 的清单写的，从未在一台机器上解析成功过，
/// 所以清单里同样<em>不写</em> <c>factoryType</c>。</para>
///
/// <para>各槽位共用同一批实例（与另四族同一条纪律）：<c>Expressions.Register</c> 的覆写必须同时对翻译器
/// 和方言生效，各 new 一份就会出现"注册了却不改行为"的幽灵 bug。</para>
///
/// <para><b>能力位：这一档只声明 5 位，是五档里最少的一档</b>，而"少"本身就是这一版的产出。
/// <b>【本笔改判：5 位变 6 位，加的正是 <c>Bulk_Copy</c> 那一颗——原文的判据没有翻，是新代码走到了那一条】</b>
/// 位判据仍是"代码今天真的走到那一条"：通道接线在 <see cref="MySqlBulk.ResolveCopyChannel"/>，
/// 真库读数在对照量具 m1∶m2（详见 <c>Bulk</c> 槽位那一段的改判），所以这一颗从"库里都有但代码没发射口"
/// 那一档搬进"有产出可指"那一档；搬进来之后它成了本档<em>唯一一颗不是 SQL 文本</em>的位，
/// 台账里那一行因此要按"6 位 = 5 位离线文本 + 1 位装载通道"重数（通道量的是驱动 API，那一份有对照量具
/// m1∶m2 的真库读数；<em>provider 自己那条反射装配今天没跑过集成册</em>，所以台账里它落的是"仅厂商文档"
/// 而不是"已见证"），不再是"6 = 6 + 0"的形状
/// （达梦那一份的 6 颗全是文本，两位同为 6 而组成不同）。"是五档里最少的一档"那半句不跟着翻：
/// 达梦那一份也是 6 颗，这一档与它并列最少而不是唯一最少（这一点本条上一版已经错过一次，口径按 §13.5 走）。
/// 判据按 §4 那一句"代码今天真的走到那一条语句"逐位对：
/// <c>Paging_LimitOffset</c> 对 <see cref="MySqlDialect.EmitPaging"/>（五家里这一套尾巴三家共用，
/// 而"只有 <c>OFFSET</c>"那一格三家三样：金仓可以光写 <c>OFFSET</c>，这一家补 <c>2^64-1</c>，
/// 第五家补厂商写死的 <c>2147483647</c>），<c>Guid_Char36</c> 对 <see cref="MySqlTypes"/> 把 <c>Guid</c> 落
/// <c>CHAR(36)</c>，<c>Schemas</c> 对 <see cref="MySqlSchemaReader"/> 的 <c>table_schema</c> 过滤，
/// <c>Comment_On_Column</c> 对 <see cref="MySqlCodeFirst.TryEmitColumnComment"/>（它是 <c>MODIFY COLUMN</c>
/// 而不是独立语句，仍然"这一列有注释"这一位为真），<c>Alter_Column_Precision</c> 对那句 <c>MODIFY COLUMN</c>。
/// 五位<em>全部</em>有产出文本可指，所以 §13.5 那本台账里这一档今天记的是 5 位 = 5 位离线文本 + 0 位仅文档。
/// <b>【本笔改判】这一句的三颗数从今天起读作 6 = 5 + 1：多出来的那一位没有文本可指，可指的是那颗反射口，
/// 于是本家走出"0 位仅文档"那一档</b>——此刻站在那一档里的是达梦、神通、Oracle、虚谷、GBase 8s 五本，
/// 而本家与 sqlite 那本各带 1 位、同数而异因（那一家是"行为量过而发不出"，本家是"通道接好而没跑集成册"）。
/// 下面那句"'0 位仅文档'是第二家，不是唯一一家"因此只作为 v0.35 那一刻的分布留着。
/// <b>"0 位仅文档"是第二家，不是唯一一家</b>（本条上一版写作"四档里唯一一个"，交账时按台账重数被推翻：
/// 达梦那一份是 6 = 6 + 0，两家同为 0，而金仓那份是 14 = 7 + 7；v0.31 的第五家是 7 = 7 + 0，
/// 于是"零"这一档有了第三个实例，而它偏偏是三份零里位数最大的那一个 —— 这一档的零与"强弱"无关，
/// 判据量的是代码走没走到，不是库里有没有）—— 而这一档那"0 位仅文档"不是能力强，是<b>敢声明的少</b>：
/// 与第三家同一把尺攒出来的掩码，自然同一颗零。</para>
///
/// <para>刻意<b>不</b>声明的，逐条给理由（与 <c>OracleProviderBase</c> 那一段同一条纪律：不声明比声明更需要写下来）：
/// <list type="bullet">
/// <item><c>Returning</c>：没有可作为结果集发出的 <c>OUTPUT</c>/<c>RETURNING</c>，
/// <see cref="MySqlKeys.EmitReturning"/> 是抛的（缺的是"语句之后回读"那一格，§14）。</item>
/// <item><c>Boolean_Type</c>：<c>bool</c> 落 <c>TINYINT(1)</c>，而那一型装得下 <c>2</c>，
/// 所以谓词侧走的是共用的 <c>= 1</c>。声明这一位等于让 <see cref="DialectBase.EmitBooleanEquality"/>
/// 走金仓那一支，产出与 <see cref="MySqlDialect"/> 类注释第五条的判断相反 —— 与 Oracle 血统同一条不声明的理由，
/// 而这一家比那一族更像"有布尔型"（它有 <c>BOOL</c> 这个<em>名字</em>），所以这一格曾经是落地四档里最容易误声明的一位。
/// <b>v0.31 把这一格的位次让出去了</b>：第五家有厂商明写的 <c>BOOLEAN</c> 三值型【文档】而同样不声明
/// （判据是"<c>IS TRUE</c> 那一支两档出处都没出现过"，见 <c>XuguProvider</c> 类注释第一条），
/// 所以"最容易误声明"从今天起说的是那一家。</item>
/// <item><c>CaseSensitive_Identifiers</c>：<b>两个离线家族都声明过的那一位，第一次有人不声明</b>（T-SQL 那一档
/// 从来不在其列 —— 它靠方括号而不是靠大小写敏感，见 §4 第一份掩码），而它恰好把 §13.1
/// 那句"两个离线家族只在这四位上重叠"改写成三位：这一家列名一律不区分大小写，表名的那颗开关
/// （<c>lower_case_table_names</c>）<em>建库时定死而不在库里</em>（它管的是文件系统）。
/// 引号照加（<see cref="IdentifierPolicy.MySql"/> 的 <c>QuoteAlways</c>）与"标识符大小写敏感"是两件事：
/// 前者防保留字，后者说的是解析器的判等规则。
/// <b>【v0.31】那句"两个离线家族都声明过"到五家要说成三个</b>：第五家把这一位<b>收回了声明侧</b>
/// （<c>XuguProvider</c> 类注释把这一格写成了"在 MySQL 这档不声明、在第五档又回来了"，与这里正相反向），
/// 所以这一位今天的分布是 <b>3 声明（金仓、这一族、第五家）∶ 2 不声明（T-SQL、这一家）</b>，判据可重数：
/// <c>grep -rn "CaseSensitive_Identifiers" src/ --include=*.cs | grep -v "///"</c> 交出 4 行 =
/// 一颗枚举值 + 三份掩码（这句 grep 剔 <c>///</c> 是必要的：不剔就会被数进来的正是本段与那三处类注释）。
/// 而"第一次有人不声明"那半句的位次<em>没有</em>动 —— 它说的是 v0.30 那一刻的分布，不是今天。</item>
/// <item><c>Upsert_OnDuplicate</c>：<c>ON DUPLICATE KEY UPDATE</c> 在这一家确实存在（而这个名字在 <c>src/</c> 里
/// 今天的<em>全部</em>踪迹就是 <c>DbCapabilities</c> 那一颗枚举值与 §4 那行列举，一个消费方都没有 ——
/// 上一版本条写作"§12.5 那张分派表里唯一为这一家留过位置的一格"，交账时按 grep 推翻：那张表不存在），
/// 但 <c>src/</c> 里一行都没发过。声明它等于把它当成已完成 ——
/// 与 Oracle 那一档不声明 <c>Upsert_MergeInto</c> 同一条判据。
/// <b>【本笔改判 —— 原文照抄在前（上面三行），改判在后】</b>最后那句"一行都没发过"从今天起不成立：
/// <c>MySqlBulk.UpsertClause</c> 发的是这一形（<c>VALUES (…) AS `fs`(…) ON DUPLICATE KEY UPDATE</c> 那一段句尾，
/// 形状在 8.4.8 上量过，见 <c>obj/logs/v40-upsert-shapes2.log</c>）。这颗位仍<em>不</em>翻成声明，理由与本家
/// <c>MultiStatement</c> 那一条改判后立的是同一颗：<b>整份 <c>src/</c> 没有一颗读者按这一位分派</b>，而写侧选层
/// 走的是策略槽（<c>IBulkStrategy.UpsertClause</c> 交回 null 就是这一家不接）——位与槽是两面，本笔只动后者，
/// 前者仍然等 §14 那本"没人读的能力位"统一清账。与 §4 那句"代码今天真的走到"的张力因此是<em>两条判据撞在一起</em>：
/// 走到的是槽，清账的是位，本笔把这一格从"没走过"改成"走过了而位不动"，与 <c>MultiStatement</c> 并成同一形状的第二个实例。</item>
/// <item><c>Cte</c>、<c>WindowFunctions</c>、<c>Count_Over_Window</c>、<c>Json_Type</c>、<c>TempTable</c>：
/// 库里都有（8.0 起 <c>WITH</c> 与窗口函数是真的，<c>JSON</c> 是原生型），但代码里没有对应的发射口。
/// <b>这一格与 <c>PostgreSqlProviderBase</c> 那一档在同一判据下给出不同答案</b>，那条不一致从 v0.23 起就记在
/// §4 与 §14 等一次统一清账 —— 这一档跟的是那句判据而不是那一档的掩码。</item>
/// <item><c>MultiStatement</c> 与 <c>MultiResultSets</c>：一次发送里塞 <c>;</c> 分隔的多条语句在这一家
/// 要驱动显式开启（<c>AllowUserVariables</c>/<c>AllowLoadLocalInfile</c> 之外的那一颗），而"要开一颗连接串参数才成立"
/// 的能力位按 §4 判据不算声明得起来。同另两档同一条不声明（【v0.31】那句"另两档"今天是<b>另三档</b>：
/// <c>grep -rn "MultiStatement" src/ --include=*.cs | grep -v "///"</c> 在整个 <c>src/</c> 下只命中两处 ——
/// <c>DbCapabilities.cs:41</c> 那颗枚举与 <c>SqlServerProvider.cs:43</c> 那一处声明，所以五家里
/// <em>只有 T-SQL 一家</em>声明它，另四家（金仓、Oracle 血统、这一家、第五家）共用这一条"不声明"）。
/// 顺带一句：
/// <c>MySqlCodeFirst.EmitCreateTable</c> 尾部那条 <c> ; </c> 串联因此在五家里<em>都</em>只能走 §9.5 的脚本路径，
/// 这一格从"两家躲不掉"变成"四家都躲不掉"，v0.31 再变成"五家都躲不掉"（<c>XuguCodeFirst</c> 同一条串联）。
/// <b>【本笔改判 —— 原文照抄在前，改判在后】</b>原文那句"要驱动显式开启（<c>AllowUserVariables</c>/
/// <c>AllowLoadLocalInfile</c> 之外的那一颗）"没有真库读数背书，而背书今天已经在自己家里跑了若干版：
/// 多值 INSERT 带 <c>; SELECT LAST_INSERT_ID()</c> 的 postRead 回填支每轮集成册都在活 MySQL 上绿【实测】，
/// 那条串里没有为多语句放行任何东西 —— 所以"要开一颗连接串参数"这一句按原文留着记当时的口径，读数上它不成立。
/// 这颗位仍<em>不</em>翻成声明，理由换了：不是"开不起来"，而是<b>整份 <c>src/</c> 没有一颗读者按这一位分派</b>
/// （原文自己数过：全库只命中枚举与 T-SQL 的声明两处），而 §12 批量写链的 Merged 档表态走的是
/// <c>IBulkStrategy.SupportsMergedStatements</c> 那颗策略槽（本家已表态 true，读数就是刚才那句 postRead）——
/// 位与槽是两面：位是公开的能力清单，槽是写侧选层的判据，本笔只动后者，前者等 §14 那本"没人读的能力位"
/// 统一清账。</item>
/// </list></para>
///
/// <para>每一位的出处记在 §13.5 那本台账里：本家族今天每一位都是"离线文本已钉 + 未实测"，
/// 因为授权实例里没有一家是这个血统（§13.1 第 4 项）。<b>【本笔的两格订正】</b>这句的"未实测"半边
/// 从 v0.37 起就不成立了（活 MySQL 上集成册每轮都绿，原文留着记当时的分布）；而本笔新加的
/// <c>Bulk_Copy</c> 一颗要按两半写：<b>驱动 API 本身有真库读数</b>（对照量具 m1∶m2 的强类型两条批量路径），
/// <b>provider 里这条反射装配还没在集成册跑过</b>（测试册补跑之前，它是"成员形状已证、装配路径未证"那一档，
/// 与 §13.5 里虚谷那一行"解析连通已见证而本程序集文本未跑"同一种前后两段）。</para></summary>
public abstract class MySqlProviderBase : IDbProvider
{
    readonly MySqlTypes _types = MySqlTypes.Instance;
    readonly MySqlValues _values = MySqlValues.Instance;
    readonly MySqlExpressions _expressions = new();
    readonly MySqlKeys _keys = new();
    readonly MySqlDialect _dialect;

    protected MySqlProviderBase(DbKind kind, DbProviderFactory driverFactory)
    {
        Kind = kind;
        Connectors = new MySqlConnectorFactory(driverFactory);
        CodeFirst = new MySqlCodeFirst(_types);
        _dialect = new MySqlDialect(kind, _types, _keys, _values, _expressions);

        // 与 SqlServerProvider 同一条接线：装载类（MySqlBulkCopy）不在工厂的抽象面上，只能从同一支驱动程序集里
        // 按名字取，所以这里交工厂而不是交名字；构造期仍然一次反射都没有（见 MySqlBulk 的抬头）。
        Bulk = new MySqlBulk(driverFactory);
    }

    public DbKind Kind { get; }

    /// <summary>子类要改的是<b>加</b>或<b>减</b>这一档，而不是另起一份 —— 另起一份就等于把 §4 那条继承链作废。</summary>
    public virtual DbCapabilities Capabilities => FamilyCapabilities;

    /// <summary>这一档的位掩码本体（逐位理由见类注释）。声明给子类，也声明给 §13.5 那本出处台账
    /// （它按 provider 数"未实测"的位）。<b>少</b>是这一档的姿态：~~TiDB 那颗叶子将来要加的是
    /// <c>Paging_LimitOffset</c> 之外的一格，不是这里减一格。~~【v0.35 第四笔：这句预测落空了——
    /// TiDB（第三笔）与 OceanBase（本笔）两颗分叉叶子都到场，一颗格都没加、也没减，<c>Capabilities</c>
    /// 逐字继承 <c>FamilyCapabilities</c>（见证是 <c>TiDbDialectTests</c> 与 <c>OceanBaseDialectTests</c>
    /// 各那条同名断言）。"同一位的分叉库共用一份掩码"今天由三颗叶子（本尊 + 两颗分叉）而不是由一句预测背书；
    /// 加/减只在方言文本真的分岔时才发生，而这一族到今天没为任何一颗分叉叶子改过掩码。】
    /// <b>【本笔加了一颗 <c>Bulk_Copy</c>，而且是"分叉叶子共用一份掩码"那句的第一次自我改写】</b>
    /// 位加在族档上、三颗叶子一起领：TiDB 与 OceanBase 共享同一支 MySqlConnector、同一份通道代码，
    /// 加在叶子上的那一种"逐家表态"今天没有读数可分——量具只证到了 MySQL 本尊那一台，
    /// 另两家的 <c>LOAD DATA LOCAL</c> 是否等价于本家的实现【未实测】，缺口与 OceanBase 双模那一格并排记 §14。</summary>
    protected static DbCapabilities FamilyCapabilities =>
        DbCapabilities.Paging_LimitOffset | DbCapabilities.Schemas | DbCapabilities.Guid_Char36 |
        DbCapabilities.Comment_On_Column | DbCapabilities.Alter_Column_Precision | DbCapabilities.Bulk_Copy;

    public IConnectorFactory Connectors { get; }

    public ISqlDialect Dialect => _dialect;

    public ITypeMap Types => _types;

    public IValueBinder Values => _values;

    public IExpressionRules Expressions => _expressions;

    public ICodeFirstDialect CodeFirst { get; }

    public ISchemaReader Schema { get; } = new MySqlSchemaReader();

    public IKeyStrategy Keys => _keys;

    public IConcurrencyStrategy Concurrency { get; } = new MySqlConcurrency();

    public IChangeGuard ChangeGuard { get; } = new MySqlChangeGuard();

    /// <summary><b>【本笔改判 —— 原文照抄在前，改判在后】</b>原文：「与另两档同一档：只有预算、没有高速通道。
    /// 这一家的高速层是 <c>LOAD DATA LOCAL INFILE</c>（驱动专有 API 而且要连接串参数放行），P2 才接。」
    /// 今天接上了：<see cref="MySqlBulk.ResolveCopyChannel"/> 交得出反射版的 <c>MySqlBulkCopy</c> 通道，
    /// 那颗「要连接串参数放行」没有抹掉而是搬了位置——由通道的 <c>UnavailableBecauseOf</c> 在<em>选层之前</em>
    /// 点名（缺 <c>AllowLoadLocalInfile=true</c> 时 Auto 落在 L3 并把那句话写进 Reason，而不是执行当场抛；
    /// 落在 L3 不算降级，所以 <c>BulkDegraded</c> 那一位不派 —— 两件事的分别是本笔真库读数改的第三颗）。
    /// 成员形状与两条批量路径都有真库读数【实测·对照量具 m1∶m2，MySqlConnector 2.6.2 / MySQL 8.4.8：
    /// 强类型写法跑通 2 000 行 × 7 列与 20 000 行，补参前两条路径同抛 NotSupportedException】。
    /// <b>而"量具绿"不等于"仓库里这一条绿"</b>：反射版第一趟在真库上把把交回 <see langword="null"/>（四颗形状
    /// 读错，逐颗见 <see cref="MySqlBulkCopyChannel"/> 的改判段），修好后集成册那一册在本家全绿；
    /// 2 000 行走 L1 与走 L3 的耗时差复跑三趟读到的是 67 ∶ 108、68 ∶ 105、58 ∶ 107（毫秒，
    /// 同一台 8.4.8 同一颗驱动）——<b>本仓库不把这组数钉成"约一倍"</b>：那句"快约一倍"在新读下落到 1.5 到 1.8 倍
    /// 之间，而 §13.4 那句"不要把一次现场读数钉成断言"量的就是这一格【实测·三趟
    /// <c>obj/logs/v40-my-bulk-repeat-{1,2,3}.log</c>；更早那一趟 <c>obj/logs/v40-my-bulkcopy-3.log</c> 按原文留着不删，
    /// 它读的是同一件事的更早一次，而它那句"约一倍"从今天起是本档的一处历史读数】。
    /// <see cref="DbCapabilities.Bulk_Copy"/> 那颗位跟着在本档的掩码里声明（§4 判据"代码今天真的走到那一条"，
    /// 这里走到的是装载 API 而不是文本，同一判据 T-SQL 那一档先走过）。
    /// 与第三家不同的那半句照旧：那一档交回 <see langword="null"/> 走 100 颗的兜底，这一档有协议级数字可报 ——
    /// 两处选择的差别直接落在块数上，写在 <see cref="MySqlBulk"/> 而不在这里重复。</summary>
    public IBulkStrategy? Bulk { get; }

    public bool CanCreateDatabase => true;

    /// <summary>这一家认的库名键。<b>实测（MySqlConnector 2.6.0）就两颗</b>：<c>db</c>、<c>DB</c>、
    /// <c>init catalog</c>、<c>InitialCatalog</c>、<c>initial_catalog</c>、双空格写法一律按「未知选项」抛，
    /// 只有 <c>Database</c> 与 <c>Initial Catalog</c> 能用 —— 列多出来的那颗不会错，但它是一次永远命不中的比。</summary>
    static readonly ConnectionStringCatalog Catalog = new("Database", "Initial Catalog");

    /// <summary>MySQL 血统支持 <c>CREATE DATABASE IF NOT EXISTS</c>，一条语句搞定。
    /// 目标库名从连接串读（<see cref="Catalog"/>），连库时把这些键全部摘掉（不指定库也能连上 MySQL 服务器）。
    /// <para>曾经只读 <c>Database</c> 一颗，于是本项目的写法 <c>Initial Catalog=…</c> 在它眼里等于没库名，
    /// 这一格走的是「串里没库名就不建库」那一岔 —— <b>静默跳过</b>，症状推迟到调用方去连那座不存在的库时才现形，
    /// 报出来的是 <c>Unknown database</c>，而报错里没有「本应建库」这件事。</para></summary>
    public async Task EnsureDatabaseExistsAsync(string connectionString, CancellationToken ct = default)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };

        var dbName = Catalog.Read(builder);

        if (dbName is null) return;

        // 两颗键一起摘：驱动自己会同义词归一，留一颗等于把「哪一颗说了算」交给解析顺序
        // （实测 <c>Database=AAA;Initial Catalog=BBB</c> 两颗并存时驱动不抛，取后出现的那颗）。
        Catalog.Clear(builder);

        await using var connection = Connectors.CreateConnection(builder.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = Connectors.CreateCommand(connection);
        cmd.CommandText = $"CREATE DATABASE IF NOT EXISTS `{dbName.Replace("`", "``")}`";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
