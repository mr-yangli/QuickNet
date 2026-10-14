using QuickSql.Metadata;
using QuickSql.Sql;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.Sqlite;

/// <summary>SQLite 的 SQL 文本层（§9.4，第七家）。<b>本文件是七份方言里第一份分岔点逐格挂活库读数的</b>：
/// 另六族的分岔点里总有几格拿的是厂商文档或产物读数，而这里下面每一条都带一个探针编号
/// （SQLite 3.46.1 ∶ Microsoft.Data.Sqlite 9.0.0，两颗版本现场读出于 A0/A1/H1）。
/// 这句只到"问过"为止，两处例外与 <c>SqliteProvider</c> 类抬头那一句同一条：其一，那些编号量的是
/// <em>探针手写的语句</em>，本 provider 今天还没有把一条自己产出的文本发出去跑过（整格见本 csproj 的
/// <c>Description</c> 与 内建表 <c>BuiltinDrivers</c> 本家注）；其二，<c>DbCapabilities</c> 那一份清单里有
/// 几颗本轮没设点位。
/// 限定的话与 <c>SqliteTypes</c> 类抬头同一句：量的是这一对，不是"这一族的所有部署"。
///
/// <para><b>【§9.4 那句"三套分页"的套数本家不动，动的是其中一套的内部】</b>
/// 本家的文本与第一套（PG 血统那一份）<em>逐字同形</em>：<c>LIMIT n OFFSET m</c>。
/// 分岔在缺省那一支：<b>本家不许光写 <c>OFFSET</c></b>【实测·A4/31/317】，而 PG 血统那一家光写
/// <c>OFFSET</c> 合法（<c>PostgreSqlDialect</c> 类注释第二条正是靠这一格省掉哨兵的）。于是
/// "要不要补一颗无上限哨兵"的分布从 <c>2 家补 ∶ 4 家不补</c> 变成 <b>3 ∶ 4</b>，
/// 而本家那颗哨兵是<b>三派语义里的第三派</b>：MySQL 血统写 <c>18446744073709551615</c>（厂商上限）、
/// 虚谷写 <c>2147483647</c>（那一家自己那型写死的数），两颗都是"挑一颗装得下的最大数"；
/// 本家写 <c>-1</c>，它不是一颗<em>数量</em>而是库认得的<em>"不设上限"记号</em>——单独出现就收【实测·832】，
/// 挂在 <c>OFFSET</c> 前面也收而不报错【实测·32 那颗 <c>LIMIT -1 OFFSET 2</c> 交回空集】，
/// 后面带 <c>OFFSET</c> 时同样成立、连偏移那颗数很大时也成立【实测·315/315b/408】。
/// 这一格所以值得写这么长：它是"同一套文本形状里两家实现不通用"的第一例，而 §9.4 那句按套数记的账
/// 从今天起要读成"套数 ∶ 每套里的实现数"。</para>
///
/// <para><b>【占位符：本家是七家里唯一一颗"前缀可以四选一"的】</b>四种记号 <c>@</c> ∶ <c>:</c> ∶
/// <c>$</c> ∶ <c>#</c> 在同一条语句里各绑定成功【实测·第 2 轮 113】，也就是说这一族的<em>驱动与引擎</em>
/// 都不挑前缀，而另六家的那颗前缀是被驱动定死的。这里取 <c>'@'</c> 是为了与 SQL Server、PG 血统、
/// MySQL 血统同形（共用层 <c>SqlLogging.ToLogSql</c> 的替换按"前缀 + 字母"找占位符，同形少一处特判）。
/// 分布因此从 <c>3 @ ∶ 2 : ∶ 1 ?</c> 长成 <b><c>4 @ ∶ 2 : ∶ 1 ?</c></b>。
/// <see cref="DriverAcceptsPrefixedParameterNames"/> 取 true，是这颗开关<em>第二个</em>为真的家，
/// 而两家的来路不同：T-SQL 那一家是两段历史（v0.19–v0.31 原样递、v0.32 之后也绿），本家是当场四颗问的
/// 【实测·E1/E2b/O/414】。<b>但本家这一位不是"两侧都认"的全部</b>：具名两侧都认，而<b>匿名那颗
/// <c>?</c> 驱动直接拒</b>【实测·225】，尽管引擎自己支持 <c>?1</c> 那一族【实测·N】。
/// "引擎支持而驱动不支持"这一格在本仓库是第一次有实测，它记 §14 与 内建表 <c>BuiltinDrivers</c> 本家注：
/// 换一颗直接绑 <c>sqlite3_bind_*</c> 的驱动，本家的前缀取向就不是唯一答案了。</para>
///
/// <para><b>【三格零覆写，而它们各自是别的家覆写过的那三格】</b>恒假/恒真谓词（<c>1 = 0</c> ∶
/// <c>1 = 1</c>，基类默认）在本家第一次拿到实测【实测·2110/2111】——六家里这两格要么被 PG 血统改写成
/// 布尔字面量、要么"没覆写而也没人问过"，本家是七家里第一颗<b>基类默认值被正面量过</b>的。
/// 布尔比较同理：基类的 <c> = 1</c> 在本家成立【实测·H0/335/607/831】，而本家是七家里唯一一颗
/// <b><c> = 1</c> 与 <c> IS TRUE</c> 两种写法都合法且同判</b>的家（PG 血统只有后一支、其余几家只有前一支），
/// 所以"位只跟存储型走"那条规则（v0.35 第一笔立）在这里落地时<em>没有</em>被迫二选一。
/// 第三格是写语句的目标别名：<see cref="EmitUpdateTarget"/>、<see cref="EmitDeleteTarget"/>、
/// <see cref="WriteTargetAlias"/> 三口全用基类默认，而本家是<b>两半都能带别名</b>的家
/// 【实测·2100 UPDATE 带别名 ∶ 2104 DELETE 带别名 ∶ 2101 别名与表名同名】——
/// T-SQL 要为它重写整条语句，Oracle 血统、MySQL 血统、虚谷、GBase 8s 四族反而要把它关掉。</para>
///
/// <para><b>【<see cref="SupportsJoinedWrites"/> 取 false，而本家的"缺"是两半各缺一次】</b>
/// 这与 <c>XuguDialect</c> 那句"两样都没被证过"不同，本家两半都被问过：
/// <list type="bullet">
/// <item><c>UPDATE … FROM</c> <b>在</b>【实测·1311-1314/2108】，普通表源、命名子查询、
/// <c>UNION</c>、甚至裸 <c>(VALUES …)</c> 都能当源（后者【实测·2109】，隐式列名是
/// <c>column1..N</c>【实测·1200/1207】）。</item>
/// <item><c>DELETE … FROM</c> 与 <c>DELETE … USING</c> <b>都不在</b>【实测·1317/1318，两形各一次语法错】。</item>
/// <item>真正的挡点是<b>列名清单</b>：<c>AS t(cols)</c> 这一形在本家被拒，无论后面跟的是
/// <c>VALUES</c> 还是子查询【实测·1201-1205，五形五拒；2107 是 UPDATE 侧的第六次】。
/// 而 §12.5 的合并档发的正是 <c>(VALUES …) AS t(cols)</c> —— 源发得出、名字发不出，
/// 于是那一档在本家<em>整形不可用</em>。</item>
/// </list>
/// 所以本家的 false 读作"<b>能力在两半之间摊开，而共用层的形状两头都不落在它有的那一侧</b>"：
/// UPDATE 半边有子句、缺的是给列改名，DELETE 半边两样都缺。<see cref="EmitJoinedSources"/>
/// 因此仍然抛（走到那里说明开关被绕过），而抛的话里给的是两条<em>本家量过的</em>等价路：
/// <c>UPDATE … SET v = (SELECT … ) </c> 的标量子查询形（子查询源本身合法，2108）与
/// <c>WHERE EXISTS</c> 的关联形【实测·2106】—— 这一点与 GBase 8s 那一份最大的分别是：
/// 那一家两条路都只有【文档】，本家两条都量过。</para>
///
/// <para><b>【一颗共用层的硬编码在本家是语法错，而它今天不可达】</b>
/// <c>DialectBase.Emit(InsertStatement)</c> 里那一支"<b>null 格落 <c>DEFAULT</c> 关键字</b>"
/// （<c>DialectBase.cs:173</c>）在本家发不出去：<c>INSERT … VALUES (DEFAULT, DEFAULT)</c> 是
/// 语法错【实测·2158】。它今天不构成缺陷，因为 <c>InsertEngine.RowsOf</c> 每一格都参数化
/// （那一段的注释自己写着"这一版不产生 DEFAULT 格"），也就是说七个方言族<em>没有任何一家</em>
/// 今天走到那一行。所以本家<em>不</em>为它新立槽位（一颗没有调用点的开关，与 §14 那几条"为虚函数而虚函数"
/// 同一判据），只把这一格记在 §14 与本家注里，重开触发写得很具体：<b>§16 的 <c>IgnoreColumns</c>
/// 或任何"部分列插入"落地时，本家那一格要说的是"多行 <c>VALUES</c> 里无法表达列默认值"</b> ——
/// 单行的退路是 <c>INSERT … DEFAULT VALUES</c>（整行默认），它接不住逐列选择。
/// 而"绑一颗 <c>NULL</c> 代替 <c>DEFAULT</c>"这一手在本家<em>看着能用而语义不同</em>：
/// 带 <c>NOT NULL DEFAULT 'd'</c> 的列上两者差一整个约束【实测·2145 那一列能用 ∶ 348 绑一颗 <c>NULL</c> 上去报
/// <c>NOT NULL constraint failed</c>】，
/// 只有自增那一档恰好同解（显式 <c>NULL</c> 由库补号【实测·210/211】）。</para>
///
/// <para>最后两格是"本家不发而问得出"的：<see cref="PrefixUnicodeLiterals"/> 保持 false，
/// 而本家的假<em>最硬</em> —— <c>N'…'</c> 在这一族是语法错【实测·P】，另六家的假最多是"没有那一形"
/// 或"有而不取"。以及一颗 SPI 装不下的：<b>本家能用的语法有几成取决于引擎版本</b>
/// （<c>RETURNING</c> 要 3.35+、<c>IS DISTINCT FROM</c> 要 3.39+、<c>DROP COLUMN</c> 要 3.35+，
/// 而这三道门槛今天都由现场那颗 3.46.1 跨过【实测·A1】）—— <c>DbCapabilities</c> 里
/// 没有"库版本"那一格，所以本文件里所有"能用"的读数都自带一个没地方声明的前提，整格记 §14。
/// 同一条门槛还挡住了两颗确实存在的语法：<c>STRICT</c> 表【实测·2153/2154】与生成列
/// 【实测·2155/2156】，本家都不发（前者会把我们"文本存定点"的写法直接拒掉，后者契约里没有对应格）。</para></summary>
public sealed class SqliteDialect(SqliteTypes types, SqliteKeys keys, SqliteValues values, SqliteExpressions expressions)
    : DialectBase(IdentifierPolicy.Sqlite, values, expressions)
{
    public override DbKind Kind { get; } = DbKind.Sqlite;

    /// <summary>类注释第二段：四种记号里挑了一颗，而这一颗是被<em>共用层的便利</em>选中的，不是被库要求的。</summary>
    public override char ParameterPrefix => '@';

    /// <summary>本家驱动收带前缀的参数名【实测·E1/E2b/O/414 四颗各一次】，所以执行器不必摘前缀，
    /// 那笔每颗参数一次的 <c>Substring</c>（§12.8 那五枚钉的量）在本家不用付。
    /// 与 T-SQL 同值而来路更短：那一家是两段历史，本家是一次现场。</summary>
    public override bool DriverAcceptsPrefixedParameterNames => true;

    public override string GetStoreType(ColumnContract column) => types.GetStoreType(column);

    /// <summary>定点存 TEXT 而 SUM/AVG 返回 INTEGER —— SQLite 存储类排序 INTEGER &lt; TEXT
    /// 让 <c>1700 &gt; '1000'</c> 得 <c>0</c>【实测·v0.37 排查】。在比较表达式入口把 decimal 参数
    /// 包 <c>CAST(@p AS NUMERIC)</c>，库会把另一侧的 TEXT 列值按数值比较（<c>NUMERIC</c> 是 CAST 专用名，
    /// 与 DDL 的 <c>TEXT</c> 不冲突）。MAX/MIN 保留列的 TEXT 类型，两侧同型不受影响，本方法只对比较运算符生效。</summary>
    protected override SqlExpression PrepareForEmission(SqlExpression node)
        => node is SqlBinaryExpression binary && IsComparison(binary.Operator)
            ? WrapDecimalParameters(binary)
            : node;

    static SqlBinaryExpression WrapDecimalParameters(SqlBinaryExpression binary)
    {
        var left = IsDecimalParameter(binary.Left) ? new CastExpression(binary.Left, "NUMERIC") : binary.Left;
        var right = IsDecimalParameter(binary.Right) ? new CastExpression(binary.Right, "NUMERIC") : binary.Right;

        return ReferenceEquals(left, binary.Left) && ReferenceEquals(right, binary.Right)
            ? binary
            : new SqlBinaryExpression(binary.Operator, left, right);
    }

    static bool IsComparison(BinaryOperator op) => op
        is BinaryOperator.Equal or BinaryOperator.NotEqual
        or BinaryOperator.LessThan or BinaryOperator.LessThanOrEqual
        or BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual;

    static bool IsDecimalParameter(SqlExpression expr)
        => expr is ParameterValue { Value: decimal };

    /// <summary>类注释第五段：UPDATE 半边有子句而列名清单发不出，DELETE 半边两样都没有。</summary>
    public override bool SupportsJoinedWrites => false;

    /// <summary>句尾 <c>RETURNING</c>（<see cref="DialectBase.ReturningPrecedesValues"/> 的默认值就是这一边），
    /// 与 PG 血统同形而本家四宿主都有实测（<c>SqliteKeys</c> 类注释第一段）；
    /// 文本本体归 <see cref="SqliteKeys.EmitReturning"/>，与另六家同一条分工。</summary>
    protected override void EmitReturning(SqlWriter writer, IReadOnlyList<ColumnContract> returning) => keys.EmitReturning(writer, returning);

    /// <summary><c> LIMIT n OFFSET m</c>，与 <c>PostgreSqlDialect</c> 那一份<b>逐字同形而差一颗哨兵</b>：
    /// 那一家两支各自可缺省，本家光写 <c>OFFSET</c> 是语法错【实测·A4/31/317】，所以有偏移而无上限时
    /// 必须先落一颗 <c>LIMIT -1</c>【实测·315/315b/408】。
    /// <b>哨兵取 <see cref="InlineLiteral"/> 而不是参数</b>：它与 <c>MySqlDialect.NoLimitSentinel</c>
    /// 那颗同形同判（写进文本的常数不参与绑定，而它<em>不是</em>用户给的任何数），也与本家类注释第二段
    /// 那句"不是一颗数量"一致。
    /// 两支都缺省时整段不发；<see cref="DialectBase.EmitPagingOrdering"/> 在这里不调用，
    /// 因为 <see cref="DialectBase.PagingRequiresOrdering"/> 保持 false【实测·34：无 <c>ORDER BY</c> 的
    /// <c>LIMIT</c> 直接通过】。</summary>
    protected override void EmitPaging(SqlWriter writer, SelectStatement select)
    {
        if (select.Limit is null && select.Offset is null)
            return;

        writer.Append(" LIMIT ");

        EmitExpression(select.Limit ?? new InlineLiteral("-1"), writer);

        if (select.Offset is { } offset)
        {
            writer.Append(" OFFSET ");

            EmitExpression(offset, writer);
        }
    }

    /// <summary>与 <c>Gbase8sDialect</c> 那一份同一立场（<b>发得出子句而不发得出列名清单，等于发不出这一档</b>），
    /// 措辞按本家改写：那一家是"压根没有多表更新的子句"，本家是有子句而 <c>AS t(cols)</c> 被拒。
    /// 消息里两条等价路都有实测，这是它比另五份多出来的一格 —— 读到这条错的人下一步就是问
    /// "那这一家怎么写"，本家能答"有两条路、都量过，只是共用层不发"。</summary>
    protected override void EmitJoinedSources(SqlWriter writer, string keyword, IReadOnlyList<JoinClause> joins, out SqlExpression? folded)
    {
        folded = null;

        if (joins.Count == 0)
            return;

        throw new QuickSqlException(
            $"{Kind} 的写语句接不下 §12.5 合并档那一形（{keyword.Trim()} 那一段有 {joins.Count} 个表源）：这一家的 "
            + "UPDATE … FROM 只认源自己带出来的列名，而 (VALUES …) 出来的隐式名是 column1..N，给它改名那一形 "
            + "AS t(cols) 在本家被拒（五形五拒，2107 是 UPDATE 侧的第六次）；DELETE 那半边连 FROM/USING 子句都没有。"
            + "本家量过的两条等价路是 UPDATE … SET v = (SELECT … FROM 源 WHERE …) 的标量子查询形与 "
            + "… WHERE EXISTS (SELECT 1 FROM 源 WHERE …) 的关联形，而共用层不发那两种形状。"
            + "整批退回一句一实体由 SupportsJoinedWrites 决定（语义与合并档等价，§12.4 第 2 项），走到这里说明那颗开关被绕过了 —— "
            + "宁可报错也不能悄悄丢掉表源，那等于丢掉连接条件（§1.2 原则 4）。");
    }

    /// <summary><c>InsertOrUpdate</c> 在本家的发射：<b>复用共用层那颗 <c>INSERT … VALUES</c>（列清单、VALUES 形状与
    /// 参数登记与 <c>InsertRange</c> 同一处真相），本家只补它后面那一段 <c>ON CONFLICT … DO UPDATE SET 列 = excluded.列</c></b>。
    ///
    /// <para><b>为什么句尾写在这里、而不是 PG 血统那一族的 <c>IBulkStrategy.UpsertClause</c> 槽</b>：本家整颗
    /// <c>Bulk</c> 交回 <see langword="null"/>（§12.3 那句"没有可举证的参数预算 ⇒ 不报一颗假数字"，与那颗句尾槽无关地
    /// 一起被挡在外面），所以它拿不到那颗槽——不是因为不支 upsert（库确实有 <c>ON CONFLICT</c>，四臂各量过
    /// 【实测·第 2 轮 40 ∶ 41 ∶ 42 ∶ 43 ∶ 46】），而是因为槽长在另一颗接口上。于是内核那一条"先问句尾、交不出就交给
    /// 方言整语句 emit"的分岔把本家送到了这里。<b>这一形与 PG 血统那一份 <c>PostgreSqlBulk.UpsertClause</c> 逐字同形</b>
    /// （同一句 <c>ON CONFLICT (键) DO UPDATE SET … = excluded.…</c>），两家各写一份是因为 §2 那条"provider 之间禁止互引"
    /// 让本家引用不到那一份——将来若把句尾从策略槽挪进方言层，两家能并成一条出路（§14）。</para>
    ///
    /// <para><b>档位口径</b>：这一句是本仓库第一条<em>由方言产出、发向 SQLite 的 upsert 文本</b>。它到"文本层已钉 +
    /// 同形语法有活库旁证（PG 那一支 + 本类注释那四臂探针）"为止；<c>InsertOrUpdateEngine</c> 经这一颗发出的<b>那一条</b>
    /// 具体语句还没有在一台活 SQLite 上跑过一次（探针量的是手写语句，不是本层产出），集成册补一场才算结清（§14、task #11）。</para></summary>
    public override void Emit(in UpsertStatement upsert, SqlWriter writer)
    {
        // 共用层那份 INSERT：本家不发 Returning（回填那一档不在这条链上），所以 Returning 留空 = 逐字一条普通 INSERT … VALUES。
        var insert = new InsertStatement { Table = upsert.Table, Columns = upsert.Columns, Rows = upsert.Rows };

        base.Emit(in insert, writer);

        writer.Append(" ON CONFLICT (");

        for (var i = 0; i < upsert.Keys.Count; i++)
        {
            if (i > 0)
                writer.Append(", ");

            EmitIdentifier(writer, upsert.Keys[i].ColumnName);
        }

        writer.Append(") DO UPDATE SET ");

        for (var i = 0; i < upsert.Settable.Count; i++)
        {
            if (i > 0)
                writer.Append(", ");

            var column = upsert.Settable[i].ColumnName;

            EmitIdentifier(writer, column);
            writer.Append(" = excluded.");
            EmitIdentifier(writer, column);
        }
    }
}
