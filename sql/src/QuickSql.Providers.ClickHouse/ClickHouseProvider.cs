using System.Data.Common;
using QuickSql.Sql;
using QuickSql.Values;

namespace QuickSql.Providers.ClickHouse;

/// <summary>ClickHouse 的驱动槽位（§9.1 的第 22 颗库种；与 <c>SqlServer</c>、<c>Xugu</c>、<c>Gbase8s</c>、
/// <c>Sqlite</c> 同档的单层 provider：没有血统基类、没有叶子，<b>没有一颗可覆写的父类</b>）。
///
/// <para><b>构造期不反射</b>：<see cref="DbProviderFactory"/> 由 Core 的 <c>DriverRegistry</c> 按
/// 内建表 <c>BuiltinDrivers</c> 解析后注入，所以本程序集编译期不认识 <c>ClickHouse.Client</c>（§1.2 原则 3）。
/// 那颗程序集名按厂商文档口径写【文档·ClickHouse：官方 .NET 客户端见
/// <see href="https://clickhouse.com/docs/integrations/language-clients/dotnet/"/>】，与 duckdb 行同档：
/// 本机没有引擎、驱动包也从未被解析成功过，内建表里<em>不写</em> <c>factoryType</c>，
/// 省略它走"扫导出类型里第一个 <c>DbProviderFactory</c> 子类"那条兜底。</para>
///
/// <para>各槽位共用同一批实例（与已落地的每一族同一条纪律）：<c>Expressions.Register</c> 的覆写必须同时
/// 对翻译器和方言生效，各 new 一份就会出现"注册了却不改行为"的幽灵 bug。</para>
///
/// <para><b>能力位：声明 7 位</b>。历届差距要读清：GBase 8s 那家"分页有产出而一位都报不出"是因为
/// 句首限定词在枚举里没有位；本家的分页形状恰好就是枚举里现成的那颗 —— <c>LIMIT/OFFSET</c> 两子句
/// 各自独立、不要求 ORDER BY【文档·ClickHouse，SELECT 页】。逐位对 §4 那句"代码今天真的走到那一条语句"：
/// <c>Paging_LimitOffset</c> 对 <see cref="ClickHouseDialect.EmitPaging"/> 那两段尾巴文本；
/// <c>Boolean_Type</c> 对 <see cref="ClickHouseTypes"/> 把 CLR bool 落原生 <c>Bool</c>、
/// <see cref="ClickHouseDialect.EmitBooleanEquality"/> 落 <c>= true</c>/<c>= false</c>
///（布尔是本家的真类型【文档·ClickHouse：数据类型页 Bool 那一行】）；
/// <c>Guid_Raw16</c> 对 <see cref="ClickHouseTypes"/> 把 <c>Guid</c> 落原生 <c>UUID</c>
///（16 字节【文档·ClickHouse：UUID 数据类型页】，与 PG 血统那颗同判同因）；
/// <c>Schemas</c> 对 <see cref="ClickHouseSchemaReader"/> 那道 <c>database =</c> 闸 —— 本家的"模式"
/// 在库里叫 <c>database</c>，<c>CREATE DATABASE</c> 是一条独立的语句【文档·ClickHouse】；
/// <c>Alter_Column_Precision</c> 对 <see cref="ClickHouseCodeFirst.EmitAlterColumnPrecision"/> 那一句
/// <c>ALTER TABLE … MODIFY COLUMN</c>【文档·ClickHouse，alter 页】；
/// <c>CaseSensitive_Identifiers</c> 对 <see cref="IdentifierPolicy.ClickHouse"/> 的 <c>CaseSensitive: true</c>
///（官方 identifiers 页写明标识符区分大小写）；<c>Comment_On_Column</c> 对
/// <see cref="ClickHouseCodeFirst.TryEmitColumnComment"/> 那一句 <c>ALTER TABLE … COMMENT COLUMN</c>
/// 与读侧 <c>system.columns.comment</c> 两半（发射与读回都在活库 26.9.21.9 上逐字回放过，
/// 出处 replay-5，§13.5 第十七本）。</para>
///
/// <para>刻意<b>不</b>声明的，逐条给理由（与已落地的每一族同一条纪律：不声明比声明更需要写下来）：
/// <list type="bullet">
/// <item><c>Transactions</c>：§6 (3) 那颗表态位，本家取"没有"。它说的是"内核写路径不开事务、
/// 逐句按库的 autocommit 直发"，而"多条不原子"是这一表态的诚实条款；公开 <c>BeginTransaction</c>
/// 路径按这颗位缺位当场抛 —— <b>严禁静默假事务</b>的纪律与 <see cref="IDbProvider.CanCreateDatabase"/>
/// 那颗 false 先例同一形状。</item>
/// <item><c>Returning</c>/<c>Returning_OnlyExpression</c>：<see cref="ClickHouseKeys.EmitReturning"/> 抛。
/// 写入那一路的官方语句清单里没有 RETURNING/OUTPUT 任何一支【文档没逐页回读，本笔如写成"有"】；
/// 宁可当场红，也不发一条"看着像在回填"的语句，与 GBase 8s 同档。</item>
/// <item><c>Upsert_*</c>：INSERT 变体条文里没有 MERGE、没有 ON CONFLICT、也没有 ON DUPLICATE
///【文档没逐页回读，本笔如写成"有"】；整语句 upsert 走基类默认抛（<see cref="DialectBase.Emit(in UpsertStatement, SqlWriter)"/>），
/// 句尾 <c>IBulkStrategy.UpsertClause</c> 那颗槽随 <see cref="Bulk"/> 交 null 一起缺席。</item>
/// <item><c>Sequences</c>/<c>Identity_ExplicitInsert</c>：本家没有"由库递增的列"那一格【文档没逐页回读】，
/// 契约整型主键在 <c>GetStoreType</c> 里得到 Int32 就此终结，没有第二出去处。
/// <b>它顺带记一条本笔已知的缺口</b>：内核 <c>InsertEngine</c> 把自增列剔出 INSERT 列清单
///（<c>InsertEngine.cs:566</c> 那一手）在本家会把缺省 0 静默嵌进库 —— 那是 §5.8 已记账的一格，
/// 送值要动内核 SPI，本笔不拦，整格记 §14。</item>
/// <item><c>Bulk_Copy</c>/<c>Bulk_ArrayBind</c>：<see cref="Bulk"/> 交回 <see langword="null"/>，
/// 批量退回链自然从 L3 起（§12.1）。</item>
/// <item><c>MultiStatement</c>/<c>MultiResultSets</c>/<c>TempTable</c>/<c>Json_Type</c>/<c>Array_Type</c>/
/// <c>Cte</c>/<c>WindowFunctions</c>/<c>Count_Over_Window</c>/<c>SkipLocked</c>/<c>Lock_NoWait</c>/
/// <c>RefCursor</c>/<c>Truncate_Cascade</c>：<c>src/</c> 一行没发过 —— 声明一颗没有消费者的"支持"
/// 正是 §13.5 那本台账要避免的东西。</item>
/// </list></para>
///
/// <para>每一位的出处记在 §13.5 第十七本台账里，而本家的档位与已落地的每一族同形：<b>整层
/// 【文档·ClickHouse】口径、未实测</b>，本机没有任何一台引擎。重开条件只有一条：拿到一台可跑的引擎。</para></summary>
[QuickSqlProvider(DbKind.ClickHouse)]
public sealed class ClickHouseProvider : IDbProvider
{
    /// <summary>内建表里的 <c>key</c>。名字与 <see cref="DbKind.ClickHouse"/> 的 <c>ToString().ToLowerInvariant()</c>
    /// 一致，所以 <c>key</c> 那一颗不需要额外映射表 —— 与 gbase8s 那一档同一句："名字就是自己"。</summary>
    public const string DriverKey = "clickhouse";

    readonly ClickHouseTypes _types = ClickHouseTypes.Instance;
    readonly ClickHouseValues _values = ClickHouseValues.Instance;
    readonly ClickHouseExpressions _expressions = new();
    readonly ClickHouseKeys _keys = new();
    readonly ClickHouseDialect _dialect;

    public ClickHouseProvider(DbProviderFactory driverFactory)
    {
        Connectors = new ClickHouseConnectorFactory(driverFactory);
        CodeFirst = new ClickHouseCodeFirst(_types);
        _dialect = new ClickHouseDialect(_types, _keys, _values, _expressions);
    }

    public DbKind Kind => DbKind.ClickHouse;

    /// <summary>位掩码本体，七位各自的产出文本写在类注释第三段，不声明的逐条在第四段。</summary>
    public DbCapabilities Capabilities =>
        DbCapabilities.Paging_LimitOffset | DbCapabilities.Boolean_Type | DbCapabilities.Guid_Raw16 |
        DbCapabilities.Schemas | DbCapabilities.Alter_Column_Precision | DbCapabilities.CaseSensitive_Identifiers |
        DbCapabilities.Comment_On_Column;

    public IConnectorFactory Connectors { get; }

    public ISqlDialect Dialect => _dialect;

    public ITypeMap Types => _types;

    public IValueBinder Values => _values;

    public IExpressionRules Expressions => _expressions;

    public ICodeFirstDialect CodeFirst { get; }

    public ISchemaReader Schema { get; } = new ClickHouseSchemaReader();

    public IKeyStrategy Keys => _keys;

    public IConcurrencyStrategy Concurrency { get; } = new ClickHouseConcurrency();

    public IChangeGuard ChangeGuard { get; } = new ClickHouseChangeGuard();

    /// <summary>没有批量策略（类注释第四条）。这一格与 <c>Gbase8sProvider</c> 同一档：没有装载通道的读数，
    /// 批量退回链自然从 L3 起（§12.1）。</summary>
    public IBulkStrategy? Bulk => null;

    /// <summary>本家的"建库"要走 <c>CREATE DATABASE</c> 语句本身【文档·ClickHouse，CREATE DATABASE 语句页】，
    /// 而 <see cref="EnsureDatabaseExistsAsync"/> 那一格的形状是"连上某个已存在的锚点、缺库才补一句"。
    /// 本家连接串里那颗 database 当场就要在场 —— 没有"连一个不带库名的锚点库再补建"的路可走，
    /// 所以显式交 false；<see cref="EnsureDatabaseExistsAsync"/> 不覆写，谁绕过这一颗 false 来问
    /// 都在接口默认的那句 <see cref="NotSupportedException"/> 上停（与 DuckDb 同案而不同因：
    /// 那一家是"建库这一问没有对象"，这一家是"要建得有，但锚点那一格没有路"）。
    /// 口径与 SQLite 的 true 先例对读：<b>"能建"要配"建得了"的那条路</b>，本家连那条路的第一段
    /// （锚点不是库）都没有。</summary>
    public bool CanCreateDatabase => false;
}
