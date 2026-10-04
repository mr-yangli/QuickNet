using System.Data.Common;
using FastSql.Providers;
using FastSql.Sql;
using FastSql.Values;

namespace FastSql.Providers.OracleBase;

/// <summary>Oracle 血统的驱动槽位（§4 的能力继承链：第三档）。达梦/Oracle 本尊/人大金仓的那一支 Oracle 模式
/// 共享这一份，各自只覆写真正分岔的那几处 —— 今天有覆写者的只有一处（<see cref="CreateCodeFirst"/>，
/// 达梦的 <c>IDENTITY(1,1)</c>）。
///
/// <para><b>【v0.37 第三笔】上面那一句今天算半兑现，而"半"字要留住</b>：那三个名字里被举出来的第二颗
/// （Oracle 本尊）第一次有了自己的程序集与叶子（<c>FastSql.Providers.Oracle</c>），所以"共享这一份"
/// 从一句按血统写的规划变成一份可跑的账；第三颗（金仓 Oracle 模式）照旧没有落地，那一句因此仍然
/// 是前瞻而不是清单。本笔真正改口的是覆写者那一格：它从"今天只有一处、只有一个覆写者"读成
/// <b>"只有一处、只有一个覆写者，而这一族的三颗叶子里两颗选择不走它"</b>——第五笔（神通）第一次让
/// "零覆写"在这一族成为一种裁决而不是既成事实，本笔第一次让那个裁决在<b>血统自己那一家</b>上重演一次，
/// 理由还相反（神通是"没有读数所以不赌"，本尊是"基类那份默认本来就是它家的话"）。
/// 可跑的见证在 <c>OracleDialectTests</c> 那两格：叶与影子探针逐字同形，且叶子的
/// <c>CodeFirst</c> 实例类型就是 <c>OracleCodeFirst</c> 本身。</para>
///
/// <para><b>构造期不反射</b>：<see cref="DbProviderFactory"/> 由 Core 的 <c>DriverRegistry</c> 按
/// <c>FastSqlDrivers.json</c> 解析后注入，所以本程序集编译期不认识 <c>Oracle.ManagedDataAccess</c>
/// 也不认识达梦那一份（§1.2 原则 3）。</para>
///
/// <para>各槽位共用同一批实例（与另两族同一条纪律；【v0.31】另三家今天也守这一条，五家无一例外）：
/// <c>Expressions.Register</c> 的覆写必须同时对翻译器
/// 和方言生效，各 new 一份就会出现"注册了却不改行为"的幽灵 bug。</para>
///
/// <para><b>能力位：这一档只声明 6 位，比 PG 那一档少 8 位，而少的那几位里有三位是判据本身的问题。</b>
/// §4 给的判据是"代码今天真的走到那一条语句"，逐位对：
/// <c>Paging_RowNum</c> 对 <see cref="OracleDialect.EmitWrappedPaging"/>，<c>Guid_Char36</c> 对
/// <see cref="OracleTypes"/> 把 <c>Guid</c> 落 <c>CHAR(36)</c>（这一位从 v0.10 立项起第一次有 provider
/// 真的走到它），<c>Schemas</c> 对 <see cref="OracleSchemaReader"/> 的 <c>owner</c> 过滤，
/// <c>CaseSensitive_Identifiers</c> 对 <see cref="IdentifierPolicy.OracleFamily"/>，
/// <c>Comment_On_Column</c> 对 <see cref="OracleCodeFirst.TryEmitColumnComment"/>，
/// <c>Alter_Column_Precision</c> 对那句 <c>MODIFY</c>。</para>
///
/// <para>刻意<b>不</b>声明的，每一条都比声明更需要理由：
/// <list type="bullet">
/// <item><c>Returning</c>：这一家没有可作为结果集发出的 <c>OUTPUT</c>/<c>RETURNING</c>，而
/// <see cref="OracleKeys.EmitReturning"/> 是抛的（§12.4 第 1 项那条降级链在这一家只走了一半，缺的是槽位，见 §14）。</item>
/// <item><c>Boolean_Type</c>：<c>bool</c> 落 <c>NUMBER(1)</c>，于是 <c>WHERE flag = 1</c> 是这里的正常写法，
/// 声明它就等于让 <see cref="DialectBase.EmitBooleanEquality"/> 走错分支。</item>
/// <item><c>Identity_ExplicitInsert</c>：与 <see cref="IKeyStrategy.SupportsExplicitIdentityInsert"/> 同一条事实的两个写法。</item>
/// <item><c>Upsert_MergeInto</c>：<c>MERGE INTO</c> 在这一家确实存在（它就是 <see cref="ISqlDialect.SupportsJoinedWrites"/>
/// 为 false 时唯一的那条退路），但这里一行都没发过 —— 声明了就是把它当成已完成，而 §12.5 末尾那一格还空着。
/// <b>【v0.31】那句"唯一的那条退路"到五家要读成"退路之一"</b>：这颗位今天有三家会用到它，而两家有语法
/// （这一族与第五家，<c>XuguProvider</c> 那条 <c>Upsert_MergeInto</c> 说它<em>两份产物都发过</em>）、
/// 一家换的是另一颗位（MySQL 血统的 <c>Upsert_OnDuplicate</c>，<c>MySqlProviderBase</c> 类注释里已经列过）。
/// 所以"为 false 时的退路"这一档在五家里是<b>两颗位 ∶ 三家</b>，而不是这一族独一份的一条。</item>
/// <item><c>MultiStatement</c> 与 <c>MultiResultSets</c>：与 PG 系同一条理由（一次发送里塞 <c>;</c> 分隔的多条语句
/// 在这一家同样不成立），而 <c>OracleCodeFirst.EmitCreateTable</c> 尾部那条 <c> ; </c> 串联因此只能走 §9.5 的脚本路径。</item>
/// <item><c>Cte</c>、<c>WindowFunctions</c>、<c>Count_Over_Window</c>：库里都有，但代码里没有 ——
/// <c>CountMethod.CountOverWindow</c> 在 <c>QueryEngine</c> 里今天就是那句"未接入"的抛，而 WITH 没有任何发射口。
/// <b>PG 那一档把这三位声明了</b>（v0.22 按 §4 原稿继承），于是两档在同一个判据下给出不同答案。本版本不动那一档
/// （改它等于同时改两份掩码、台账计数与一句"原稿就是这么写的"），把这一格不一致记进 §4 与 §14，等一次统一清账。</item>
/// </list></para>
///
/// <para>每一位的出处记在 §13.5 那本台账里：本家族今天每一位都是"厂商文档或离线文本 + 未实测"，
/// 因为授权实例里没有一台是这个血统（§13.1 第 4 项）。</para></summary>
public abstract class OracleProviderBase : IDbProvider
{
    readonly OracleTypes _types = OracleTypes.Instance;
    readonly OracleValues _values = OracleValues.Instance;
    readonly OracleExpressions _expressions = new();
    readonly OracleKeys _keys = new();
    readonly OracleDialect _dialect;
    ICodeFirstDialect? _codeFirst;

    protected OracleProviderBase(DbKind kind, DbProviderFactory driverFactory)
    {
        Kind = kind;
        Connectors = new OracleConnectorFactory(driverFactory);
        _dialect = new OracleDialect(kind, _types, _keys, _values, _expressions);
    }

    public DbKind Kind { get; }

    /// <summary>子类要改的是<b>加</b>或<b>减</b>这一档，而不是另起一份 —— 另起一份就等于把 §4 那条继承链作废。</summary>
    public virtual DbCapabilities Capabilities => FamilyCapabilities;

    /// <summary>这一档的位掩码本体（逐位理由见类注释）。声明给子类，也声明给 §13.5 那本出处台账。</summary>
    protected static DbCapabilities FamilyCapabilities =>
        DbCapabilities.Paging_RowNum | DbCapabilities.Guid_Char36 | DbCapabilities.Schemas |
        DbCapabilities.CaseSensitive_Identifiers | DbCapabilities.Comment_On_Column |
        DbCapabilities.Alter_Column_Precision;

    public IConnectorFactory Connectors { get; }

    public ISqlDialect Dialect => _dialect;

    public ITypeMap Types => _types;

    public IValueBinder Values => _values;

    public IExpressionRules Expressions => _expressions;

    /// <summary>懒建而不是在构造函数里 <c>new</c>：这一颗是本家族唯一的<em>子类覆写点</em>
    /// （达梦的 <c>IDENTITY(1,1)</c>），而基类的构造函数里调虚函数时子段的字段初始化器还没跑完。
    /// 与 <c>PostgreSqlProviderBase</c> 那一颗 <c>CodeFirst { get; }</c> 的区别就在这一个覆写点上 ——
    /// 那一档今天没有覆写者，所以它可以直接在构造函数里建。</summary>
    /// <remarks>【v0.36 第五笔】本口有了第二颗叶子，而它<b>选择不走这道缝</b>：<c>ShenTongProvider</c>
    /// 不继承 <c>CreateCodeFirst</c>，于是"零覆写"第一次是<em>选择</em>而不是结构既成 ——
    /// 另七颗零覆写叶挂在 PG 与 MySQL 两族，那两族<em>从来没有过</em>可覆的路（构造里直 <c>new</c> 具体类型）。
    /// 于是"这一颗是本家族唯一的子类覆写点"这句仍真（覆写者仍只达梦一颗），而它不再等于"这一族的叶子都要走它"。
    /// 缝等的因此是<em>差异</em>而不是<em>人</em>：神通真实例到场先问"这台库认不认
    /// <c>GENERATED BY DEFAULT AS IDENTITY</c>"，那一问的答案才是这道缝的第二份见证（§9.1 行 902、§14 行 2044）。
    /// <b>【v0.37 第三笔】本口今天有第三颗叶子，它也不走，而它不走的理由与第二颗相反</b>：本尊
    /// （<c>FastSql.Providers.Oracle</c>）不覆写是因为缝里那句默认值<em>本来就是它自家的标准语法</em>，
    /// 走缝等于改自己的话——于是这一族"有缝的两颗叶子"今天都在场而都不走，一档从一次记例变成一个形状。
    /// 同一段里那句"另七颗零覆写叶"本笔重数过：今天那两族是<b>九颗</b>（PG 六颗 ∶ MySQL 三颗），
    /// 差额两颗是 v0.36 第六笔与 v0.37 第二笔各添一颗而都没回来复点这一句，差额记在这里而不悄悄改掉；
    /// 上面那句"覆写者仍只达梦一颗"则一字不改地仍然真，这正是本笔的要害：<b>覆写者数与住户数脱钩</b>。</remarks>
    public ICodeFirstDialect CodeFirst => _codeFirst ??= CreateCodeFirst(_types);

    /// <summary>DDL 那一格的覆写点。参数把家族共用的类型映射递过去，是为了让覆写者不必自己去拿单例 ——
    /// 一旦哪天 <c>OracleTypes</c> 也要按库种分叉，这里就是那根线。</summary>
    protected virtual ICodeFirstDialect CreateCodeFirst(OracleTypes types) => new OracleCodeFirst(types);

    public ISchemaReader Schema { get; } = new OracleSchemaReader();

    public IKeyStrategy Keys => _keys;

    public IConcurrencyStrategy Concurrency { get; } = new OracleConcurrency();

    public IChangeGuard ChangeGuard { get; } = new OracleChangeGuard();

    /// <summary><b>没有预算位可言，所以这里是 <see langword="null"/></b>：§12.1 那颗可空槽的语义就是
    /// "这一家还没有能举证的预算数字"。分块公式于是走 <c>StatementEmitter.ConservativeParameterBudget</c>
    /// 那一份 100 的兜底（它自己的注释写着"没有任何库种事实可依"），代价是批量 INSERT 多几次往返 ——
    /// 而 <c>InsertEngine.Explain()</c> 会把块数说出来，不静默。
    ///
    /// <para>为什么不抄一个数字：另外那三家的预算各有出处（T-SQL 是驱动里那条 2100 的硬上限，金仓那一族是
    /// 协议里 <c>Parse</c>/<c>Bind</c> 消息的 int16，MySQL 血统那一族是预处理语句的 65535 颗占位符），而这一族的
    /// 绑定变量上限在两个层面各有一个数
    /// （库侧与驱动侧），今天没有一台活库能告诉我哪一个先生效（§13.5 台账里这一行是"未实测"）。
    /// 报大了会得到真库上一句"参数过多"，报小了只是多几次往返 —— 两者的成本差一个量级，所以先取兜底，
    /// 抬到真库上限的那次实测与判据记在 §14。<b>v0.31 起这一支有第二个住户</b>：<c>XuguProvider.Bulk</c>
    /// 同样交回 null（两档出处里既没有参数个数上限、64K 那一档又是<em>行</em>的预算），
    /// 于是"没有可报数字"这一档从孤例变成两族，而 <c>DialectParityTests</c> 的块数账因此出现两个 50。</para></summary>
    public virtual IBulkStrategy? Bulk => null;

    /// <summary>Oracle 血统不支持传统意义上的 <c>CREATE DATABASE</c>（Oracle 的"库"是实例级 PDB，
    /// 达梦同理）。显式交 false，与接口默认值相同而意图更清楚：不是"还没实现"，是"这一族没有这个概念"。</summary>
    public bool CanCreateDatabase => false;
}
