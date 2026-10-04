using System.Data.Common;
using FastSql.Sql;
using FastSql.Values;

namespace FastSql.Providers.Gbase8s;

/// <summary>GBase 8s 的驱动槽位（§4，第六家；§9.1 那份清单里"独立血统"那一族的第二层，
/// 与 <c>SqlServer</c>、<c>Xugu</c> 同档：没有血统基类、没有叶子）。
///
/// <para><b>这一族是单层，而"单层"在本家比在另两家多一层可说的</b>：§9.1 的清单里挂在 Informix 血统
/// 底下的库种只有一颗（就是这一家自己），所以它与 <c>FastSql.Providers.SqlServer</c>、
/// <c>FastSql.Providers.Xugu</c> 同一档，而不是 <c>MySqlBase</c>/<c>OracleBase</c>/<c>PostgreSqlBase</c>
/// 那一档。后果逐颗：<c>XuguConnectorFactory</c> 那一格说的"第六份逐字同形的拷贝"就是这里
/// （§2 禁跨 provider 互引，连接层那一段共用不了）；<b>而本家比虚谷那一家多一颗"零覆写"的分量</b>：
/// 虚谷落地时 §9.1 那份清单里已经有 PG 血统可以对照，本家对照的是<em>整个 Informix 血统</em>，
/// 而这一血统今天只有它一颗 —— 也就是说"没有可覆写的父类"在这一家不是选择的结果，
/// 是<em>没有对象</em>。这一格与 <c>ShenTong</c> 那条"有缝之族的第一颗零覆写壳"是两种零覆写：
/// 那一种是族里有基类而这一叶不接，本家是族里压根没有基类。</para>
///
/// <para><b>构造期不反射</b>：<see cref="DbProviderFactory"/> 由 Core 的 <c>DriverRegistry</c> 按
/// <c>FastSqlDrivers.json</c> 解析后注入，所以本程序集编译期不认识 <c>GBase.DataDB</c>（§1.2 原则 3）。
/// 那颗程序集名<em>只</em>来自 §9.1 那一行第四列，从未在一台机器上解析成功过，而且本家比另五家薄一层：
/// 连"官方发布名有两档出处"都没有，所以清单里<em>不写</em> <c>factoryType</c>（理由与达梦那一行逐字相同），
/// 省略它走"扫导出类型里第一个 <c>DbProviderFactory</c> 子类"那条兜底。<b>而这一家的兜底今天不可验证</b>：
/// 虚谷那一格至少写着"厂商参考页上有 <c>XGProviderFactory.Instance</c> 这个名字"【文档】，
/// 本家连那一颗工厂类的名字都没读到 —— 也就是说这一整条驱动路径（清单 → <c>AssemblyLoadContext</c> →
/// 扫工厂类）第一次有一个键是<em>纯按清单存在</em>的。<c>DriverPathTests</c> 那一支量的正是这一格，
/// 而它量的是"清单里这一行解析得开吗"，答案在拿到驱动包之前恒为"解析不开"。</para>
///
/// <para>各槽位共用同一批实例（与另五族同一条纪律）：<c>Expressions.Register</c> 的覆写必须同时对翻译器
/// 和方言生效，各 new 一份就会出现"注册了却不改行为"的幽灵 bug。</para>
///
/// <para><b>能力位：这一档声明 4 位，而它是六家里第一位数最少的一档 —— 更要紧的是它是第一颗
/// "分页有产出而一位都报不出"的</b>。<see cref="DbCapabilities"/> 那三分页位
/// （<c>Paging_LimitOffset</c>/<c>Paging_RowNum</c>/<c>Paging_OffsetFetch</c>）按字面各对应一种形状，
/// 而本家的形状是<em>句首限定词</em> <c>SELECT FIRST n SKIP m</c>（§9.4 那句"三套实现"到本家变四套），
/// 枚举里没有第四颗。于是 <see cref="Gbase8sDialect.EmitPagingQualifier"/> 每发一条 <c>FIRST</c>
/// 都在产出一份"能力位报不出"的文本 —— 这一格与本仓库立了六次的那条判据<em>不冲突而只是没覆盖</em>：
/// 判据说"不许报没有产出的位"，这里是有产出而不许报，因为报成任何一颗现有的都是撒谎。
/// 今天的实际代价是零（<c>grep -rn "Paging_" src/FastSql.Core</c> 只命中枚举定义那一行，
/// 共用层不按那三位门控任何行为，分页形状走 <see cref="ISqlDialect"/> 的发射口），
/// 而这颗"零"正是 §14 那一格的形状：位掩码从今天起<em>不能</em>被读成"这家的分页能力的完整读数"。
/// 逐位对 §4 那句"代码今天真的走到那一条语句"：<c>Schemas</c> 对 <see cref="Gbase8sSchemaReader"/>
/// 那道 <c>t.owner</c> 闸（而本家那一颗"模式"是所有者，见同一份类注释第三条），
/// <c>Alter_Column_Precision</c> 对 <see cref="Gbase8sCodeFirst.EmitAlterColumnPrecision"/> 那一句
/// <c>ALTER TABLE … MODIFY (…)</c>，<c>CaseSensitive_Identifiers</c> 对
/// <see cref="IdentifierPolicy.Gbase8s"/> 的 <c>CaseSensitive: true</c>，
/// <c>Guid_Char36</c> 对 <see cref="Gbase8sTypes"/> 把 <c>Guid</c> 落 <c>CHAR(36)</c>
/// （这一家<em>没有</em>原生 UUID 型，也没有取号函数【文档·IBM】，所以这一位在本家是<b>唯一</b>答案
/// 而不是两档出处各支持一支的选择 —— 与虚谷那一格正相反）。</para>
///
/// <para><b>这一版把"新增一族不必然把交集缩小"那条例证推进成了第一次真正的缩小</b>：
/// 上一版（虚谷）三颗全有、交集仍是 3 位，本家有<b>两</b>颗没有 —— <c>Comment_On_Column</c>
/// （这一家的语句清单里没有 <c>COMMENT ON</c> 那一条，列注释走 Core 的"跳过并 WARN"退路，
/// 见 <see cref="Gbase8sCodeFirst.TryEmitColumnComment"/>）与那颗报不出的分页位。
/// 于是六家交集从 <b>3 位</b> 掉到 <b>2 位</b>：<c>Schemas</c> ∶ <c>Alter_Column_Precision</c>。
/// <b>而掉的那一颗恰好是"共用层真的会发的那几种语句"之外的一颗</b>，所以 v0.32 那条读法
/// （"交集量的是 <c>src/</c> 的覆盖面而不是库的能力面"）到这一版<em>仍然成立而不再充分</b>：
/// 覆盖面变了是因为这一家<em>发不出</em>那条语句，而不是因为共用层没那条路径。两句一起记 §13.5。</para>
///
/// <para>刻意<b>不</b>声明的，逐条给理由（与另五族同一条纪律：不声明比声明更需要写下来）：
/// <list type="bullet">
/// <item>三分页位：见上面第四段，这一家四颗里三颗的形状都不是它的，而第四颗没有位。</item>
/// <item><c>Returning</c>：<see cref="Gbase8sKeys.EmitReturning"/> 抛。这一家 <c>RETURNING</c> 只存在于
/// ESQL/SPL 里配 <c>INTO</c> 宿主变量【文档·IBM】，CLI 收不到那个结果集，所以"库里有这一词"
/// 与"这一位可报"在本家是两件事 —— 与金仓那颗"基类抛而不报"同一判据、不同因。</item>
/// <item><c>Boolean_Type</c>：本家<b>有</b> <c>BOOLEAN</c> 那一型【文档·IBM】而不报，走的是 v0.35 第一笔
/// 裁的那条规则：<b>位只跟存储型走</b>，而 <see cref="Gbase8sTypes.GetStoreType"/> 把 CLR bool 落
/// <c>INTEGER</c>（那是这一家第<b>三</b>家做同一种选择、第三条不同的理由，理由逐颗写在那一臂旁边）。
/// 这一格是那条规则第一次由<em>新落地的家</em>套用第二遍。</item>
/// <item><c>Identity_ExplicitInsert</c>：<see cref="Gbase8sKeys.SupportsExplicitIdentityInsert"/> 取 false，
/// 而"false 说的是哪件事"在本家与另三家不同条：那三家说的是"库里要一颗开关而我们没接"，
/// 本家的 <c>SERIAL</c> 压根不禁止直接写值【文档·IBM】，所以这里 false 说的是
/// "<em>不需要</em>开关，而 SPI 只有开关那一格"。<b>这是本仓库第一颗"false 而理由是它用不上"的位</b>。
/// 与 <see cref="Gbase8sKeys.IdentityInsertToggle"/> 交回 null 同一句。</item>
/// <item><c>MultiStatement</c>：<see cref="Gbase8sKeys.EmitPostStatementKeyRead"/> 那一句要接在
/// <c>INSERT</c> 的<em>同一条命令文本</em>尾巴上（会话级 <c>DBINFO</c> 只在此刻问得着刚插的那一颗），
/// 而"一条命令里塞两条语句"在这一家的 CLI 侧今天<b>没有任何一份材料证明它可行</b>【文档没写过、
/// 产物没有、活库没有】。所以这一位不报，而 <see cref="Gbase8sKeys"/> 那颗
/// <c>SupportsPostStatementKeyRead</c> 为 true 的<em>前提</em>正是这一格 —— 两处必须一起改，
/// 记 §14 与 <c>DialectSql</c> 那条分句路径。</item>
/// <item><c>Upsert_*</c>、<c>Sequences</c>、<c>Cte</c>、<c>WindowFunctions</c>、<c>Count_Over_Window</c>、
/// <c>Json_Type</c>、<c>Array_Type</c>、<c>RefCursor</c>、<c>SkipLocked</c>、<c>Lock_NoWait</c>、
/// <c>TempTable</c>、<c>Truncate_Cascade</c>：手册里读到过名字而 <c>src/</c> 一行没发过，
/// 要么压根没读到 —— 与另五族同一立场，声明一颗没有消费者的"支持"正是 §13.5 那本台账要避免的东西。
/// <b>本家这一列比另五家长</b>：手册里 <c>SEQUENCE</c> 与 <c>SKIP LOCKED</c> 都读到了名字
/// 【文档·IBM】，而"读到名字"离"发得出这条语句"还差一份语法页，本层不跨这一步。</item>
/// <item><c>Guid_Raw16</c>：与 <c>CHAR(36)</c> 那颗在本家<em>不是二选一</em>（没有原生 16 字节那一型），
/// 所以这一位是第四种情形：<b>另一支候选在这一家压根不存在</b>，与 <see cref="Gbase8sTypes.GetLengthSemantics"/>
/// 那条"分档那一支没有对象"同一形。</item>
/// <item><c>Bulk_Copy</c> 与 <c>Bulk_ArrayBind</c>：<see cref="Bulk"/> 交回 <see langword="null"/>，
/// 批量退回链自然从 L3 起（§12.1）。这一格与另五家同判而本家多一句：手册里那一族"数组绑定 /
/// 批量装载"的名字（<c>INSERT … IN</c> 一类）本次没有读到可报的<em>颗数上限</em>，
/// 而 <c>IBulkStrategy.ParameterBudget</c> 要的正是一个颗数 —— 所以这里报出去的是"没有高速通道"
/// 而不是"有但没接"，与 <c>OracleProviderBase</c> 那一份同一档。</item>
/// </list></para>
///
/// <para>每一位的出处记在 §13.5 那本台账里，而本家的档位<em>与另五家不同一级</em>：另五家的"离线文本已钉 +
/// 未实测"至少还有厂商一手文档或 shipped 适配器，本家全部格子标的是【文档·IBM】——
/// GBase 8s 自己的手册本次一页都没有，而"Informix 的即 GBase 8s 的"这一条是<b>借来的前提</b>
/// （§9.1 那一行第四列说这一家与 Informix <c>同源</c>，同源到什么程度本次没有材料）。
/// 所以本层的正确读法不是"这六族里最弱的一族"，而是：<b>它是第一层"整层的正确性挂在一颗未验证的
/// 等价关系上"的 provider</b>。重开条件只有一条，写在 §14 那一行的末尾：拿到一份 GBase 8s 手册，
/// 或一台库。</para></summary>
[FastSqlProvider(DbKind.Gbase8s)]
public sealed class Gbase8sProvider : IDbProvider
{
    /// <summary>清单里的 <c>key</c>。<c>assembly</c> 按 §9.1 那一行第四列写 <c>GBase.DataDB</c>，
    /// 而它从未在一台机器上解析成功过（上一段有这一格的完整处境）。名字与
    /// <see cref="DbKind.Gbase8s"/> 的 <c>ToString().ToLowerInvariant()</c> 一致，所以 <c>key</c> 那一颗
    /// 不需要额外映射表 —— <b>这一条在本家是<em>真的</em>省了一格</b>：另五家有几个键要靠
    /// <c>$comment</c> 说明"名字与库种的对应是按清单写的"，本家是六家里第一个"名字就是自己"而
    /// 不需要解释的一颗（因为它没有血统、没有别名、也没有第二家共享同一个基类名）。</summary>
    public const string DriverKey = "gbase8s";

    readonly Gbase8sTypes _types = Gbase8sTypes.Instance;
    readonly Gbase8sValues _values = Gbase8sValues.Instance;
    readonly Gbase8sExpressions _expressions = new();
    readonly Gbase8sKeys _keys = new();
    readonly Gbase8sDialect _dialect;

    public Gbase8sProvider(DbProviderFactory driverFactory)
    {
        Connectors = new Gbase8sConnectorFactory(driverFactory);
        CodeFirst = new Gbase8sCodeFirst(_types);
        _dialect = new Gbase8sDialect(_types, _keys, _values, _expressions);
    }

    public DbKind Kind => DbKind.Gbase8s;

    /// <summary>位掩码本体，四位各自的产出文本写在类注释第四段，不声明的十二段理由在第五段。
    /// 它不是 <c>virtual</c> 而是一句直给：这一族今天没有叶子，而"不留一颗没人覆写的虚函数"
    /// 这条纪律已经立过三次，本家是第四次。
    /// <b>位数为六家最少（4，对 5/6/7 三家），而这条读法要按类注释第四段修正一次</b>：
    /// 它少的那两颗里有一颗是"发了文本而枚举没位"，所以这颗 4 不是"这一族只会四件事"，
    /// 是"这一族有四件事我们能拿代码指认"。</summary>
    public DbCapabilities Capabilities =>
        DbCapabilities.Schemas | DbCapabilities.Alter_Column_Precision |
        DbCapabilities.CaseSensitive_Identifiers | DbCapabilities.Guid_Char36;

    public IConnectorFactory Connectors { get; }

    public ISqlDialect Dialect => _dialect;

    public ITypeMap Types => _types;

    public IValueBinder Values => _values;

    public IExpressionRules Expressions => _expressions;

    public ICodeFirstDialect CodeFirst { get; }

    public ISchemaReader Schema { get; } = new Gbase8sSchemaReader();

    public IKeyStrategy Keys => _keys;

    public IConcurrencyStrategy Concurrency { get; } = new Gbase8sConcurrency();

    public IChangeGuard ChangeGuard { get; } = new Gbase8sChangeGuard();

    /// <summary>没有批量策略（类注释倒数第二条）。这一格与 <c>OracleProviderBase</c> 同一档而不同因：
    /// 那一族的兜底是"驱动有数组绑定而这一族没有"，这一家是"读到过名字而没读到可报的颗数"。</summary>
    public IBulkStrategy? Bulk => null;

    /// <summary>GBase 8s（Informix 血统）的建库要走 <c>ontape</c> / <c>dbaccess</c> 等管理工具，
    /// 不是 SQL 语句能做的事，显式交 false。</summary>
    public bool CanCreateDatabase => false;
}
