using QuickSql.Metadata;
using QuickSql.Sql;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.Gbase8s;

/// <summary>GBase 8s（Informix 血统）的 SQL 文本层（§9.4，第六家）。
/// <b>这一家是 §9.4 那句"三套分页实现"变四套的那一票</b>：T-SQL 的 <c>OFFSET…FETCH</c>、
/// PG 血统与 MySQL 血统与虚谷的 <c>LIMIT…OFFSET</c>、Oracle 血统的 <c>ROWNUM</c> 套壳，
/// 到这里多出第四种落点 —— <b>句首的限定词</b> <c>SELECT FIRST n SKIP m</c>【文档·IBM】。
/// 它<em>在</em>本句里面（不像套壳那样包一句），又<em>不</em>在句尾（不像那三套各占一条尾巴），
/// 所以共用层为前三套留的两个槽位都接不住它：<see cref="DialectBase.EmitPaging"/> 长在尾巴上，
/// <see cref="DialectBase.PagingWrapsSelect"/> 那一档要包整句。新增的第三个槽位
/// （<see cref="EmitPagingQualifier"/>）因此在六家里是<em>零覆写者</em>的一格 ——
/// 除本家以外没有任何一家需要它，而那五份黄金样本逐字未动就是这一句的见证。
///
/// <para><b>第四套分页带来的第一件实事不是文本，是参数序</b>。本家占位符是位置形 <c>?</c>，
/// "文本里第几颗"与"参数表里第几颗"必须一致；限定词落在<em>整句之最前</em>，
/// 所以 <c>FIRST</c>/<c>SKIP</c> 那两颗参数比投影与谓词都先登记 —— 编号序与文本序<b>恰好</b>同向，
/// 而这件事是"槽位选在句首"顺手保住的，不是设计出来又验证过的。反过来（尾巴形分页 + 位置形占位符）
/// 就已经不成立了，因为那时 <c>ORDER BY</c> 里的参数先登记。<b>这一格因此是本仓库第一处
/// "分页形状与参数绑定次序有耦合"的记录</b>，它记 §14 与 §13.5。</para>
///
/// <para>与已落地五族的分岔逐条，每一条都是一个覆写点或一处<em>刻意不覆写</em>（出处一律标
/// 【文档·IBM】，理由写在 provider 类注释第三段）：
/// <list type="bullet">
/// <item><b>参数占位符只有位置形</b>（<see cref="ParameterPrefix"/> 取 <c>'?'</c>，
/// 且<em>只有</em>这一支）【文档·IBM】：这一族的预编译语法里带名字的形（<c>@p0</c> / <c>:p0</c>）
/// 都不存在，主机变量是 ESQL/SPL 层的 <c>:var</c> 而不是 SQL 里的占位符。
/// 于是本家是六家里第一颗<b>名字不进文本</b>的：<see cref="EmitParameter"/> 覆写成
/// "登记参数 + 落地一颗 <c>?</c>"，共用 <see cref="DialectBase"/> 那一份编号纪律而只换落地那一步。
/// <b>这一格牵连的三处</b>：<c>ToLogSql</c> 在本家恒等（<c>Gbase8sValues</c> 类注释第二条）、
/// <c>DriverAcceptsPrefixedParameterNames</c> 在这一家<b>没有意义</b>（下面第三条）、
/// 函数模板的实参次序第一次要核对（<see cref="DialectBase.EmitFunction"/>，本家第一个消费者）。</item>
/// <item><b>没有 <c>N'</c> 宽字符字面量</b>：<see cref="PrefixUnicodeLiterals"/> 保持 false，
/// 而这一句在六家里的<em>含义</em>不同 —— 前几家是"这一家有那一形而本仓库不用"或"有且用"（T-SQL 用），
/// 这一家是"那一形压根不存在"【文档·IBM】，因为 codeset 是建库时定的、字面量不带的第二档
/// （<c>Gbase8sTypes</c> 第二条同一件事）。</item>
/// <item><b><see cref="DriverAcceptsPrefixedParameterNames"/> 保持 false 而今天<em>惰性</em></b>：
/// 那颗开关管的是"摘掉参数名前缀"，而本家给驱动的名字（<c>p0</c>，从 <c>?p0</c> 摘来）
/// 在位置形绑定里<em>不参与配对</em>。写出来不是为了行为，是为了让这一格在六家的分布可读
/// （false ∶ 六家里的五家同值而本家唯一"取值不影响任何行为"）。</item>
/// <item><b>表别名没有 <c>AS</c></b>（<see cref="SourceAliasKeyword"/> 落成一颗空格）【文档·IBM】：
/// 这一族的关联名（correlation name）直接跟在表名后面。<b>这一格与列别名不通用</b>：
/// <c>SELECT x AS y</c> 的 <c>AS</c> 在这一家是合法且常见的，所以只有 <c>FROM</c> 那一支省略 ——
/// 这一条与 <c>OracleDialect</c>/<c>XuguDialect</c> 同形（第三家），而它是三家各按一档出处写的
/// （文档 ∶ 文档 ∶ 产物）。</item>
/// <item><b>没有 <c>UPDATE … FROM</c>，所以 <see cref="SupportsJoinedWrites"/> 取 false</b>：
/// §12.5 的合并档在这一族整批退回一句一实体【文档·IBM】，<b>退出的族从四家长到五族</b>
/// （Oracle 血统、MySQL 血统、虚谷、本家，加上——按 provider 数——各自叶子的复件；发得出的只剩
/// T-SQL 一族与 PG 血统一族），而这一格真正被本家改变的是<em>缺法的种数</em>：
/// Oracle 血统是"目标不能起别名"、MySQL 血统是"能起别名但没有那个子句"、虚谷是"两样都没被证过"，
/// 而这一家是<b>压根没有多表更新的子句，却有 <c>WHERE EXISTS</c> 那条等价路</b>
/// 【文档·IBM】—— 所以"缺的是形状还是能力"这一问在这一家答"能力有、形状不对"，
/// 与 §12.4 第 2 项那句"整批退回不损失正确性只损失次数"同一格而<em>更贴近能接的那一边</em>。</item>
/// <item><b>布尔谓词、恒真/恒假谓词都不覆写</b>：取基类的 <c> = 1</c>/<c> = 0</c> 与
/// <c>1 = 0</c>/<c>1 = 1</c>，与 T-SQL、Oracle 血统、MySQL 血统、虚谷四家同形（六家里的四家，
/// PG 血统那五颗叶子一家 <c>IS TRUE</c>）。判据与本家 <c>Gbase8sTypes</c> 第一条同一格：
/// 布尔列落 <c>INTEGER</c>，所以 <c>= 1</c> 与列型同边，而"位只跟存储型走"那条规则（v0.35 第一笔立）
/// 在本家是<em>落地即套用</em>的第一例。</item>
/// </list></para>
///
/// <para><b>本类产出的每一条文本都没有在一台活库上跑过，而且本机没有任何一台可以跑的库</b>。
/// 这一句在虚谷那一家是一句会被翻掉的话（§13.5 记着它翻的过程），在本家它是<em>起点</em>：
/// 它不等改判，等第一台库。所以本文件不写"未实测而理由在文档"以外的任何强判据，
/// 而每一格的分岔都留了一句"重开触发"。§13.1 第 4 项那句"授权的实例里没有一家是 X"在本家
/// 要读成"授权清单里压根没有过这一族"—— 差额与逐格账记 §13.5 与 §14。</para></summary>
public sealed class Gbase8sDialect(Gbase8sTypes types, Gbase8sKeys keys, Gbase8sValues values, Gbase8sExpressions expressions)
    : DialectBase(IdentifierPolicy.Gbase8s, values, expressions)
{
    public override DbKind Kind { get; } = DbKind.Gbase8s;

    /// <summary>问号，而且是<em>唯一</em>的一支【文档·IBM】。这一格让六家的分布变成
    /// <b>3 家 <c>@</c> ∶ 2 家 <c>:</c> ∶ 1 家 <c>?</c></b>，而第三派到今天只有一颗 ——
    /// 它是这一份文件里最贵的一格：前缀错了在另五家红在"名字对不上"，在本家红在
    /// <b>值绑定到隔壁列</b>（位置形没有第二道校验），所以本家为这一颗前缀新立了
    /// <see cref="DialectBase.EmitFunction"/> 那道核对。<b>而那道核对量不到最贵的那一段</b>：
    /// 它管函数模板里的实参次序，管不到语句骨架的发射次序（骨架那一边由 <c>DialectBase</c>
    /// 单一出口保证，六家共用，所以那一边本来就不需要本家另证）。</summary>
    public override char ParameterPrefix => '?';

    public override string GetStoreType(ColumnContract column) => types.GetStoreType(column);

    /// <summary>取 false 的判据见类注释第五条。<b>这颗开关不是错误豁免</b>：共用层据此整批退回
    /// 一句一实体，而 <see cref="EmitJoinedSources"/> 仍然抛 —— 走到那里说明有人绕过了开关，
    /// 那种时候宁可红（与另四家同一条纪律，本家是第五份）。</summary>
    public override bool SupportsJoinedWrites => false;

    /// <summary>目标不起别名，所以它在 <c>SET</c> 与 <c>WHERE</c> 里<em>用自己的表名</em>引用自己
    /// （与 <c>OracleDialect</c>/<c>XuguDialect</c> 同一形状、同一句"刻意不带模式前缀"）。
    /// 这一格在本家的来路比另两家弱一档：<b>本家没有一份 shipped 产物可抄</b>，取的是
    /// "与另两家同形 + 手册里 <c>UPDATE</c> 那一页没有出现过目标关联名"两条合起来【文档·IBM】，
    /// 而"没出现"不等于"不许" —— 那一半不对称风险的论证在 <c>XuguDialect</c> 类注释第三条，
    /// 本家整个继承那份取向而不另证。</summary>
    public override string WriteTargetAlias(TableInfo table) => table.Name;

    protected override string SourceAliasKeyword => " ";

    /// <summary>与上面那颗 <see cref="WriteTargetAlias"/> <em>成对</em>（§4 那句话在四族上各落地一次，
    /// 这里是第四次）。基类默认出口是 <c>UPDATE 表 AS 别名</c>，本家按 <c>UPDATE 表 SET …</c>：
    /// <see cref="DialectBase.EmitDeleteTarget"/> 默认转给这一口，所以 <c>DELETE</c> 那半边跟着一起改对
    /// （两半同时被断言的纪律与 <c>OracleDialect</c>/<c>XuguDialect</c> 那两份同一条）。</summary>
    protected override void EmitUpdateTarget(SqlWriter writer, TableRef table) => EmitTableName(writer, table.Table);

    /// <summary><c>InsertOrUpdate</c> 在本家的发射：<b>没有 MERGE 语法，用 <c>UPDATE … WHERE</c> + <c>INSERT … SELECT …
    /// WHERE NOT EXISTS</c> 两条</b>（类注释第五条与 <see cref="EmitJoinedSources"/> 那条抛指的同一条等价路，
    /// 【文档·IBM】）。每行一对：<c>UPDATE 置值 WHERE 键=…</c>（命中就改，没命中 0 行）紧跟
    /// <c>INSERT … SELECT … FROM systables WHERE tabid = 1 AND NOT EXISTS (…键…)</c>（没命中才插）。
    /// <c>systables WHERE tabid = 1</c> 是这一族把"一颗字面 <c>SELECT</c> 落成一行"的惯用单行源，与本家
    /// <see cref="Gbase8sKeys"/> 那句 <c>SELECT DBINFO(…) FROM systables WHERE tabid = 1</c> 同一来路。</summary>
    ///
    /// <para><b>这一格比句尾与 MERGE 那几颗多背三处未证，逐条写清（本家一条文本都没在活库跑过、本机也没有可跑的库，
    /// 类注释第二段那句在这里是起点不是待翻的话）</b>：① 一条命令里放<b>多行的这一对语句 = 一个分号批</b>，而本家的
    /// 多语句批 <see cref="Providers.IBulkStrategy.SupportsMergedStatements"/> 取默认 false、没有过读数——
    /// <c>WHERE EXISTS</c> 这形状<em>天生</em>要两条语句一起走，退不开，除非引擎改两条往返（那是另一版的活）；
    /// ② 目标<em>不起别名</em>（<see cref="WriteTargetAlias"/>），所以三处目标引用都用表名本身；③ 参数是位置形
    /// <c>?</c>（<see cref="EmitParameter"/>），<b>登记次序必须等于文本次序</b>——这里全部由 <see cref="DialectBase.EmitExpression"/>
    /// 当场登记 + 落 <c>?</c>，一处不重排。同一行的键会在 <c>UPDATE WHERE</c>、<c>INSERT 的 SELECT 清单</c>、
    /// <c>NOT EXISTS</c> 三处各落一颗 <c>?</c>（各登记一颗同值的参数），语义对而参数偏多，是这一形状没 CTE 可共享行集的代价。</para>
    ///
    /// <para>重开触发一句话：连上一台活的 GBase 8s 跑绿这一对（尤其那颗分号批）。</para></summary>
    public override void Emit(in UpsertStatement upsert, SqlWriter writer)
    {
        var table = upsert.Table.Table;
        var settable = upsert.Settable.Count;

        for (var row = 0; row < upsert.Rows.Count; row++)
        {
            // 每一行是一对独立语句：行与行之间也要用分号隔开（一对内部 UPDATE ∶ INSERT 已经带了一颗分号）。
            if (row > 0)
                writer.Append("; ");

            var cells = upsert.Rows[row];

            // 一条：命中就改。SET 只并可写列，键只进 WHERE（改自己的键不是整行更新）。
            writer.Append("UPDATE ");
            EmitTableName(writer, table);
            writer.Append(" SET ");

            for (var column = 0; column < settable; column++)
            {
                if (column > 0)
                    writer.Append(", ");

                EmitIdentifier(writer, upsert.Settable[column].ColumnName);
                writer.Append(" = ");
                EmitExpression(cells[column]!, writer);
            }

            writer.Append(" WHERE ");
            EmitWhereKeys(writer, upsert, cells, settable);

            writer.Append("; INSERT INTO ");
            EmitTableName(writer, table);
            writer.Append(" (");
            EmitColumnList(writer, upsert);
            writer.Append(") SELECT ");

            for (var column = 0; column < upsert.Columns.Count; column++)
            {
                if (column > 0)
                    writer.Append(", ");

                EmitExpression(cells[column]!, writer);
            }

            writer.Append(" FROM systables WHERE tabid = 1 AND NOT EXISTS (SELECT 1 FROM ");
            EmitTableName(writer, table);
            writer.Append(" WHERE ");
            EmitWhereKeys(writer, upsert, cells, settable);
            writer.Append(")");
        }
    }

    /// <summary>键等式串（多键合取）：<c>k0 = ? AND k1 = ?</c>。键格在 <see cref="UpsertStatement.Columns"/>
    /// 里排在可写列之后，所以从 <c>settable + j</c> 取。</summary>
    void EmitWhereKeys(SqlWriter writer, in UpsertStatement upsert, IReadOnlyList<SqlExpression?> cells, int settable)
    {
        for (var key = 0; key < upsert.Keys.Count; key++)
        {
            if (key > 0)
                writer.Append(" AND ");

            EmitIdentifier(writer, upsert.Keys[key].ColumnName);
            writer.Append(" = ");
            EmitExpression(cells[settable + key]!, writer);
        }
    }

    /// <summary>整张写入清单（可写 ++ 键）的列名串，供 <c>INSERT INTO 表 (…)</c> 用。</summary>
    void EmitColumnList(SqlWriter writer, in UpsertStatement upsert)
    {
        for (var column = 0; column < upsert.Columns.Count; column++)
        {
            if (column > 0)
                writer.Append(", ");

            EmitIdentifier(writer, upsert.Columns[column].ColumnName);
        }
    }

    /// <summary>登记参数而<em>不</em>落地它的名字，只落一颗 <c>?</c>。这是本家与另五家在文本层
    /// <b>唯一一处结构性的不同</b>：那五家的参数在文本里是有形状的（<c>@p0</c> / <c>:p0</c>），
    /// 本家那一格只有一个字符。
    /// <b>编号仍然按基类那一条问</b>（<see cref="SqlWriter.NextParameterName"/>），所以 hint 与
    /// 内核侧的编号纪律一处不动 —— 改的只有"落地什么文本"这一步，而那是 <c>AppendParameter</c>
    /// 与 <c>RegisterParameter</c> 两颗口子的分界线（本家是后一颗的第一个消费者）。
    /// 顺序纪律：<b>先登记、后落文本</b>。反过来不会错，但会让"文本里有几颗 <c>?</c>"与
    /// "表里有几颗参数"在调试输出里对不上，而那一格对不上在本家<em>没有别的查得出它的地方</em>。</summary>
    protected override void EmitParameter(ParameterValue parameter, SqlWriter writer)
    {
        writer.RegisterParameter(Values.CreateParameter(writer.NextParameterName(), parameter.Value, parameter.Hint));

        writer.Append('?');
    }

    /// <summary>句首限定词：<c>FIRST n</c>（上限）与 <c>SKIP m</c>（偏移），两支各自独立可缺省。
    /// <b>只有 <c>SKIP</c> 时不补"无上限"哨兵，因为缺省那一支本身就是无上限</b>【文档·IBM】——
    /// 这一格让六家的分布长成 <c>2 家补哨兵 ∶ 4 家不需要</c>（补的那两颗是 MySQL 血统的 2^64−1 与
    /// 虚谷的 2147483647，两颗出处不是一类；不补的四家里 PG 血统那一族是"语法允许光写 OFFSET"，
    /// 本家与此同判而来路不同），而它是 §9.4 那句"套数变四套"之外<em>真正新长出来的那一格</em>。
    /// 次序：<c>FIRST</c> 在前、<c>SKIP</c> 在后【文档·IBM】，两数反着写是本家的语法错，
    /// 而类注释第二段说的"编号序 = 文本序"就是靠这一行守住的。
    /// <b>两支都缺省时整段不发</b>（与基类同判），此时本家与另五家在这一句上逐字同形。</summary>
    protected override void EmitPagingQualifier(SqlWriter writer, SelectStatement select)
    {
        if (select.Limit is null && select.Offset is null)
            return;

        if (select.Limit is { } limit)
        {
            writer.Append("FIRST ");

            EmitExpression(limit, writer);

            writer.Append(' ');
        }

        if (select.Offset is { } offset)
        {
            writer.Append("SKIP ");

            EmitExpression(offset, writer);

            writer.Append(' ');
        }
    }

    /// <summary>空体：本家的分页文本全在 <see cref="EmitPagingQualifier"/> 那一段。
    /// 两口都在同一个类里，成对覆写这条纪律与 <c>OracleDialect</c> 那颗
    /// <c>PagingWrapsSelect</c> ∶ 空 <c>EmitPaging</c> 完全同形 —— 改一处的人一定看得到另一处，
    /// 否则同一份分页会在句首与句尾各发一遍（那是<em>能绿着发错</em>的一类：两条子句都合法，
    /// 只是其中一条被写了两次）。</summary>
    protected override void EmitPaging(SqlWriter writer, SelectStatement select)
    {
    }

    /// <summary>转交给 <see cref="Gbase8sKeys.EmitReturning"/>，那里<em>抛</em>。
    /// 走这一格说明有人要回填主键，而本家的两档回填都不成立（<c>RETURNING</c> 只在
    /// ESQL/SPL 里配 <c>INTO</c> 宿主变量、CLI 收不到结果集；语句后 <c>DBINFO</c> 那一档
    /// 要的是多语句批，见 <see cref="Gbase8sKeys"/>）—— 与 Oracle 血统同一立场：
    /// 宁可当场红，也不发一条"看着像在回填"的语句。</summary>
    protected override void EmitReturning(SqlWriter writer, IReadOnlyList<ColumnContract> returning) => keys.EmitReturning(writer, returning);

    /// <summary>"任何表源都不接受"的<em>第五份</em>（前四份在 <c>OracleDialect</c>、<c>MySqlDialect</c>、
    /// <c>XuguDialect</c> 与 PG 血统那一份的<em>反面</em>——那一家是唯一发得出的）。
    /// 措辞刻意与那三份都不同：本家<em>不是</em>"有 MERGE 的语法而形状不对"，
    /// 是"有 <c>WHERE EXISTS</c> 的等价路而共用层不发它"。那一句要写进消息里，
    /// 因为现场读到这条错的人下一步就是找"这一家怎么多表更新"，而答案是有、只是不在这条路上。</summary>
    protected override void EmitJoinedSources(SqlWriter writer, string keyword, IReadOnlyList<JoinClause> joins, out SqlExpression? folded)
    {
        folded = null;

        if (joins.Count == 0)
            return;

        throw new QuickSqlException(
            $"{Kind} 的写语句不接受目标表之外的表源（{keyword.Trim()} 那一段有 {joins.Count} 个）：这一家没有 UPDATE … FROM 那种子句，"
            + "也没有把行集带进写语句的形状（§12.5 合并档发的是 (VALUES …) AS t(cols) 那一形）。"
            + "这一家能到同一目的地的是 UPDATE … WHERE EXISTS (SELECT …)【文档·IBM】，而共用层不发那种形状。"
            + "整批退回一句一实体由 SupportsJoinedWrites 决定（语义与合并档等价，§12.4 第 2 项），走到这里说明那颗开关被绕过了 —— "
            + "宁可报错也不能悄悄丢掉表源，那等于丢掉连接条件（§1.2 原则 4）。");
    }
}
