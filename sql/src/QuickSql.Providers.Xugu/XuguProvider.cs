using System.Data.Common;
using QuickSql.Sql;
using QuickSql.Values;

namespace QuickSql.Providers.Xugu;

/// <summary>虚谷的驱动槽位（§4，第五家；§9.1 那份清单里"自成一套"那一族的第一层，也是今天唯一一层）。
///
/// <para><b>这一族没有血统基类，也没有叶子</b>：§9.1 的清单里没有第二种挂在这家底下的库种，
/// 所以它是与 <c>QuickSql.Providers.SqlServer</c> 同档的<b>单层</b> provider（类是 <c>sealed</c>），
/// 而不是 <c>MySqlBase</c>/<c>OracleBase</c>/<c>PostgreSqlBase</c> 那一档。这一格的直接后果是
/// §2 那条"provider 之间禁止互引"在这里表现为：<see cref="XuguConnectorFactory"/> 是第五份逐字同形的拷贝。</para>
///
/// <para><b>构造期不反射</b>：<see cref="DbProviderFactory"/> 由 Core 的 <c>DriverRegistry</c> 按
/// <c>QuickSqlDrivers.json</c> 解析后注入，所以本程序集编译期不认识 <c>XuguClient</c>（§1.2 原则 3）。
/// 那颗程序集名与金仓、达梦、MySQL 三格同一处境：按 §9.1 的清单写，<b>从未在一台机器上解析成功过</b>，
/// 而且它是四颗里唯一一处<em>连"官方发布名"都没有两档出处</em>的一颗（本次读到的文档给的是驱动包名，
/// 与托管程序集名是否同名没有核过）—— 所以清单里同样<em>不写</em> <c>factoryType</c>，
/// 省略它走"扫导出类型里第一个 <c>DbProviderFactory</c> 子类"那条兜底。
/// 兜底在这一家格外靠得住一点：厂商参考页上写着 <c>XGProviderFactory.Instance</c>【文档】，
/// 也就是说那一颗工厂<em>类</em>的名字与它存在这件事是有出处的，缺的只是"程序集叫什么"。</para>
///
/// <para>各槽位共用同一批实例（与另四族同一条纪律）：<c>Expressions.Register</c> 的覆写必须同时对翻译器
/// 和方言生效，各 new 一份就会出现"注册了却不改行为"的幽灵 bug。</para>
///
/// <para><b>能力位：这一档声明 7 位，= 7 位离线文本 + 0 位仅文档</b>，与 <c>MySqlProviderBase</c>（5 = 5 + 0）、
/// 达梦那层（6 = 6 + 0）同属"每一位都指得出产出文本"那一档，而"0 位仅文档"在这里有了第三个实例。
/// <b>三家的位数是 7 ∶ 6 ∶ 5，这一家反而是最多的一档</b> —— 这一句要说反过方向才不误读：
/// 位多不代表这一族强，只代表它<em>今天被点名的方式</em>是"有产出的一格一格列"，
/// 而它把两派里各一颗（<c>Returning</c> 在金仓/Oracle 那一侧是抛、在这家是发；
/// <c>CaseSensitive_Identifiers</c> 在 MySQL 那家第一次不声明、在这里又回到声明）同时收进了一份掩码。
/// 逐位对 §4 那句"代码今天真的走到那一条语句"：
/// <c>Paging_LimitOffset</c> 对 <see cref="XuguDialect.EmitPaging"/>（它与金仓、MySQL 血统同形，
/// 而 <c>OFFSET</c> 单独出现时补的那颗哨兵与 MySQL 那颗<em>同形不同义</em>，见 <c>NoLimitSentinel</c>），
/// <c>Returning</c> 对 <see cref="XuguKeys.EmitReturning"/>（三档出处里最薄的一格，欠账写在同一个文件），
/// <c>Schemas</c> 对 <see cref="XuguSchemaReader"/> 的 <c>owner</c> 过滤，
/// <c>Guid_Char36</c> 对 <see cref="XuguTypes"/> 把 <c>Guid</c> 落 <c>CHAR(36)</c>（而这一家<em>有</em>
/// 原生 16 字节 <c>GUID</c> 那一型，两档出处各支持一支，取的是产物那一支），
/// <c>CaseSensitive_Identifiers</c> 对 <see cref="IdentifierPolicy.Xugu"/> 的 <c>CaseSensitive: true</c>，
/// <c>Comment_On_Column</c> 与 <c>Alter_Column_Precision</c> 对 <see cref="XuguCodeFirst"/> 那两句。</para>
///
/// <para><b>这一版顺手证伪了一条 §13.1 式的直觉：新增一族不必然把五家交集缩小。</b>
/// 四家交集那三颗（<c>Schemas</c>、<c>Comment_On_Column</c>、<c>Alter_Column_Precision</c>）这一家<em>三颗全有</em>，
/// 所以五家交集仍然是 <b>3 位</b>，与上一版逐字相同 —— 而它不是巧合：那三颗恰好都是
/// "共用层真的会发的那几种语句"，也就是说<b>交集量的是 <c>src/</c> 的覆盖面而不是库的能力面</b>。
/// 所以它不会被"新库少一格"推着走：<c>MySqlProviderBase</c> 那一档交出 <c>CaseSensitive_Identifiers</c>
/// 时交集没动（四家都是 3 位），这一家交出 <c>Boolean_Type</c> 与 <c>Identity_ExplicitInsert</c> 时它也没动。
/// "每接一族交集就小一格"这句直觉因此是错的，而它错的方式值得记：交集只在共用层
/// <em>新增一条发射路径</em>时才会变。</para>
///
/// <para>刻意<b>不</b>声明的，逐条给理由（与 <c>MySqlProviderBase</c>、<c>OracleProviderBase</c> 那两段同一条纪律：
/// 不声明比声明更需要写下来）：
/// <list type="bullet">
/// <item><c>Boolean_Type</c>：<b>这是五家里第一次"库明确有那一型而不声明"</b>——前四家不声明的理由都是
/// "那一型装得下别的值"或"压根没有"，而厂商文档给 <c>BOOLEAN</c> 明写了 <c>TRUE|FALSE|NULL</c> 三值【文档】。
/// 仍然不声明，因为这一位真正的语义是"谓词侧可以走 <c>IS TRUE</c>"（<see cref="DialectBase.EmitBooleanEquality"/>
/// 那一支），而本家把 CLR bool 落成 <c>TINYINT</c>——它装得下 2，<c>IS TRUE</c> 与 <c>= 1</c> 在同一列上
/// 不是同一判据（<c>IS TRUE</c> 落在整数列上也数到 1 是 BT-11 的实测，那正是 2 被"非零即真"判真的形状）。
/// 这里最初那句"两档出处都没出现过"已被 v0.32 的 BT 族驳回，理由档位从"没出处"换成"有出处而不用"，
/// 并由 v0.35 第一笔裁成规则：<b>位只跟存储型走，bool 落 <c>TINYINT</c> 期间永不声明</b>。三条完整理由写在
/// <see cref="XuguTypes.GetStoreType"/> 的第一臂。</item>
/// <item><c>Identity_ExplicitInsert</c>：<see cref="XuguKeys.SupportsExplicitIdentityInsert"/> 取 false，
/// 而这一家比另三家多一层：它<em>有</em>一颗管这件事的会话参数（<c>IDENTITY_MODE</c>【文档给名字与用途，
/// 语法本次没抄】），但那颗开关的形状是"改会话"而不是"发一条语句"，SPI 里 <c>IdentityInsertToggle</c>
/// 那一个口子接不住它。加上 Core 今天压根没有那条调用路径（四家已经把它记成 §14 的一格），
/// 所以这里既不声明也不覆写。</item>
/// <item><c>Upsert_MergeInto</c>：<c>MERGE INTO … USING … ON</c> 在这一家确实有语法（两份产物都发过它），
/// 但 <c>src/</c> 里一行都没发过 —— 与 <c>MySqlProviderBase</c> 那颗 <c>Upsert_OnDuplicate</c>、
/// Oracle 那一档那颗 <c>Upsert_MergeInto</c> 同一条判据。<b>而这一家是三颗里离"能接"最近的一颗</b>：
/// <see cref="XuguDialect.EmitJoinedSources"/> 那句抛里点名了它，缺的是 §12.5 那个形状能不能带进它那一子句。
/// <b>【本笔的一句】那句"同一条判据"今天对三颗中的两颗仍然成立、对 MySQL 那一颗换了理由</b>：
/// 那一颗的句子从今天起发得出去（<c>MySqlBulk.UpsertClause</c>），而位仍不声明——换成"走过了而位没有读者"
/// 那一条（见 <c>MySqlProviderBase</c> 类注释 <c>Upsert_OnDuplicate</c> 那一项的改判段，与本家
/// <c>MultiStatement</c> 那一条同形）。本家这一颗两半边都没动：既没发过句子，也没有读者。</item>
/// <item><c>Json_Type</c>：<b>本条在交账前改判过一次</b>。上一版这里写的是"这一族有没有 JSON 型【无出处】"，
/// 而本次读到的类型页里<em>有</em> JSON 那一档 —— 所以理由换成"没有发射口"：<c>Json&lt;T&gt;</c> 在
/// <c>src/</c> 里仍然是 0 处，而 §4 那句判据量的是代码今天走不走得到。<b>这一处改判本身记 §13.5</b>，
/// 它是这一版三处"文档推翻我们写过的话"之一（另两颗：<c>OFFSET</c> 哨兵、<c>text</c> 那颗守卫）。</item>
/// <item><c>Sequences</c>、<c>Cte</c>、<c>WindowFunctions</c>、<c>Count_Over_Window</c>、<c>TempTable</c>、
/// <c>Array_Type</c>、<c>RefCursor</c>、<c>SkipLocked</c>、<c>Lock_NoWait</c>、<c>Truncate_Cascade</c>：
/// 两档出处里要么没有、要么本次没读到，而代码里没有对应的发射口 —— 与另四族同一立场，
/// 声明一颗没有消费者的"支持"正是 §13.5 那本台账要避免的东西。</item>
/// <item><c>Guid_Raw16</c>：与 <c>CHAR(36)</c> 那颗是二选一（两边都有出处，见 <see cref="XuguTypes"/> 那一格），
/// 取文本那一支的代价与理由都在那里。</item>
/// <item><c>Paging_RowNum</c> 与 <c>Paging_OffsetFetch</c>：这一家两样都不是它的形状（<c>LIMIT…OFFSET</c>），
/// 声明了会让 §9.4 那三套分页实现凭空多一套 —— 第五家<em>不发明新套</em>，它就是第一套里的第三家
/// （金仓、MySQL 血统与这一家），套数从 v0.23 到今天没变过。</item>
/// <item><c>MultiStatement</c> 与 <c>MultiResultSets</c>：一次发送里塞 <c>;</c> 分隔的多条语句在这一家
/// 没有任何一份产物演示过，而 <see cref="XuguCodeFirst.EmitCreateTable"/> 尾部那条 <c> ; </c> 串联
/// 因此在<b>五家全都</b>只能走 §9.5 的脚本路径（这一格从上一版的"四家都躲不掉"变成"五家都躲不掉"）。</item>
/// <item><c>Bulk_Copy</c> 与 <c>Bulk_ArrayBind</c>：<see cref="Bulk"/> 交回 <see langword="null"/> 那一档，批量退回链自然从 L3 起（§12.1），所以这里报出去的是"没有高速通道"而不是
/// "有但没接"。判据与 <c>OracleProviderBase</c> 那一份同一格：文档没给参数个数上限、而 64K 那一档是
/// <em>行</em>的预算不是参数的颗数，两样都换不成一个可报的 <c>RowsPerStatement</c>，所以宁可走 100 的兜底。</item>
/// </list></para>
///
/// <para>每一位的出处记在 §13.5 那本台账里：本家族今天每一位都是"离线文本已钉 + 未实测"，
/// 因为授权实例里没有一家是虚谷（§13.1 第 4 项）。<b>而这一族的"未实测"比另四家多一层可核性</b>：
/// 它同时有厂商一手文档与两份 shipped 适配器，所以本仓库里每一格都写了它取自哪一档 ——
/// 那不能替代真库，但它让"重读哪一格"这件事第一次有目录可查。</para></summary>
[QuickSqlProvider(DbKind.Xugu)]
public sealed class XuguProvider : IDbProvider
{
    /// <summary>清单里的 <c>key</c>。<c>assembly</c> 按本次读到的文档写 <c>XuguClient</c>，
    /// 而它从未在一台机器上解析成功过（上一段有这一格的完整处境）。名字与
    /// <see cref="DbKind.Xugu"/> 的 <c>ToString().ToLowerInvariant()</c> 一致，所以 <c>key</c> 那一颗
    /// 不需要额外映射表。</summary>
    public const string DriverKey = "xugu";

    readonly XuguTypes _types = XuguTypes.Instance;
    readonly XuguValues _values = XuguValues.Instance;
    readonly XuguExpressions _expressions = new();
    readonly XuguKeys _keys = new();
    readonly XuguDialect _dialect;

    public XuguProvider(DbProviderFactory driverFactory)
    {
        Connectors = new XuguConnectorFactory(driverFactory);
        CodeFirst = new XuguCodeFirst(_types);
        _dialect = new XuguDialect(_types, _keys, _values, _expressions);
    }

    public DbKind Kind => DbKind.Xugu;

    /// <summary>位掩码本体，与七位各自的产出文本都写在类注释第四段。它不是 <c>virtual</c> 而是一句直给：
    /// 这一族今天没有叶子，而"不留一颗没人覆写的虚函数"这条纪律已经立过三次。</summary>
    public DbCapabilities Capabilities =>
        DbCapabilities.Paging_LimitOffset | DbCapabilities.Returning | DbCapabilities.Schemas |
        DbCapabilities.Guid_Char36 | DbCapabilities.CaseSensitive_Identifiers |
        DbCapabilities.Comment_On_Column | DbCapabilities.Alter_Column_Precision;

    public IConnectorFactory Connectors { get; }

    public ISqlDialect Dialect => _dialect;

    public ITypeMap Types => _types;

    public IValueBinder Values => _values;

    public IExpressionRules Expressions => _expressions;

    public ICodeFirstDialect CodeFirst { get; }

    public ISchemaReader Schema { get; } = new XuguSchemaReader();

    public IKeyStrategy Keys => _keys;

    public IConcurrencyStrategy Concurrency { get; } = new XuguConcurrency();

    public IChangeGuard ChangeGuard { get; } = new XuguChangeGuard();

    /// <summary>没有批量策略（类注释倒数第三条）。这一格与 <c>OracleProviderBase</c> 同一档而不同因：
    /// 那一族的兜底是"驱动有数组绑定而这一族没有"，这一族是"两档出处都给不出一个可报的颗数"。</summary>
    /// <remarks><b>【本笔表态】</b>批量阶梯铺开时这一家两档都不接：<b>L1</b> —— 虚谷的装载面本次
    /// 一页手册都没读到（比"读到了但口径不合"还薄一层），而这一族的驱动程序集虽然解析成功过、
    /// provider 文本却从没在它那儿跑过（§13.5 那句前后两段在本家原文就写着）；<b>Merged</b> ——
    /// 默认 false，本家没有任何一条多语句批的现场可对。重开条件与 Oracle 那一段同一条尺：先有能读出
    /// "行数口径"的装载 API，再谈接线。</remarks>
    public IBulkStrategy? Bulk => null;

    /// <summary>虚谷的建库模型与接口默认值不同（需要厂商工具或 DBA 操作），显式交 false。</summary>
    public bool CanCreateDatabase => false;
}
