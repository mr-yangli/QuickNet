using QuickSql.Metadata;
using QuickSql.Sql;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.OracleBase;

/// <summary>Oracle 血统的 SQL 文本层（§9.4 那三套实现里的第三套）。与已落地的两族逐条对照，
/// 每一条都是一个覆写点，而且<em>这一族的分岔比第二族更深</em> —— 第二族只差在句尾的子句上，
/// 这一族连"一句 SELECT 长什么样"都不同（"两族"与"比第二族更深"都是 v0.23 的口径，那时落地的只有两家；
/// 今天这一列五行，而"更深"那一句仍然成立：v0.30、v0.31 两家都在"尾巴"那一套里，只有这一族要<em>把整句包住</em>。
/// 下面六条里有三条的位次在 v0.31 变了 —— 冒号前缀、<c>UPDATE … FROM</c> 取 false、可空布尔的 <c>= 0</c> 事故，
/// 变成的数字各写在自己那一格（前一条在下面的 <see cref="ParameterPrefix"/>，后两条在
/// <c>MySqlChangeGuard</c> 类注释第二段与 <c>OracleValues</c>），不在这里重复计数）：
/// <list type="bullet">
/// <item><b>分页是包两层的，不是加尾巴的</b>：<c>ROWNUM</c> 是<em>在同一层里先于 ORDER BY 赋号</em>的伪列，
/// 所以 <c>… ORDER BY x WHERE ROWNUM &lt;= 5</c> 这种写法既不存在也得不到那一页 —— 要"排序后再截"就必须把
/// 排好序的那一句降成内层表源。共用层因此多了一个 <see cref="DialectBase.PagingWrapsSelect"/> 开关：
/// 只给 <see cref="EmitPaging"/> 一个覆写点是不够的，那一条路是"尾巴"，而这一族要的是"整句被包住"。</item>
/// <item><b>表别名没有 <c>AS</c></b>（<see cref="SourceAliasKeyword"/> 落成一颗空格）：那一家只在<em>列</em>
/// 别名上接受 <c>AS</c>。这不是版式偏好，是过不过解析器。</item>
/// <item><b>写语句的目标不能起别名</b>（<see cref="DialectBase.WriteTargetAlias"/> 与
/// <see cref="EmitUpdateTarget"/> 成对覆写）：T-SQL 靠 <c>UPDATE [t0] … FROM 表 AS [t0]</c>、
/// PG 系靠 <c>UPDATE 表 AS t0</c>，这一族两样都没有，引用自己只能用表名。
/// 这一格是 §4 那句"目标别名由方言在建 AST 之前定下来"的来路 —— 它不是重构出来的，是这一家逼出来的。</item>
/// <item><b>没有 <c>UPDATE … FROM</c></b>：<see cref="SupportsJoinedWrites"/> 取 false，
/// §12.5 的合并档在这一族整桶退回一句一实体（等价性与代价写在那里）。发射口 <see cref="EmitJoinedSources"/>
/// 仍然抛：那是给手工构造的 AST 与将来的 <c>MERGE INTO</c> 留的落点，不是给 <c>SaveChanges</c> 走的日常路。</item>
/// <item><b>布尔与 uuid 都不是原生类型</b>（§9.3）：<c>IS TRUE</c> 这一家没有，所以共用层那句
/// <c> = 1</c>/<c> = 0</c> 在这里是对的形状，§10.5 那类"可空布尔列上 <c>= 0</c> 匹配不到 NULL"的事故
/// 也跟着回来了。恒假谓词因此只能是 <c>1 = 0</c> 而不是 <c>false</c> —— 这一家没有布尔字面量。</item>
/// <item><b>标识符发射前折成大写</b>（<see cref="IdentifierPolicy.OracleFamily"/>）：建表与查询共用同一个
/// 归一口，所以 §9.2 那句"默认全大写而代码里写小写 → 查询 100% 报表不存在"在这一族是构造上不可能，
/// 而不是靠人记得加引号。</item>
/// </list>
/// 类不是 <c>sealed</c>：达梦与神通在它上面只覆写真正分岔的那几处（§4 的能力继承链）。
/// <b>本类的产出文本没有在一台真库上跑过</b> —— 现场没有授权的 Oracle 血统实例，见 §13.5 的出处台账。
/// 因此下面每一处"这一家不认 X"都是厂商文档口径，第一次真连库时要按 §13.5 那本台账逐位改判。</summary>
public class OracleDialect(DbKind kind, OracleTypes types, OracleKeys keys, OracleValues values, OracleExpressions expressions)
    : DialectBase(IdentifierPolicy.OracleFamily, values, expressions)
{
    public override DbKind Kind { get; } = kind;

    /// <summary>这一族的参数占位符是冒号（与 T-SQL 的 <c>@</c> 分岔，而它恰好与"Oracle 里 <c>@</c> 是
    /// 数据库链接运算符"不冲突 —— 那颗符号在这两家各说各的话，正说明它必须由方言给而不是共用层猜）。
    /// <b>v0.31 起冒号那一档不再是这一族独一份</b>：第五家同前缀（<c>XuguDialect.ParameterPrefix</c>），
    /// 于是这一格是 <b>2 家 <c>:</c> ∶ 3 家 <c>@</c></b>，而两家的<em>证据强度</em>不同一条：
    /// 这一族有厂商写法本身，那一家的出处只是一枚产物里的 <c>:XuguParam</c> 参数名。
    /// "各说各的话"那句要按<em>档</em>读、不按<em>家</em>读：同一颗 <c>@</c> 在这一族是链接运算符、
    /// 在 T-SQL 是占位符，这一条不因多了两家而变。<b>顺带记一处空白</b>：金仓那一族那颗 <c>@</c>
    /// 在它自己的文件里<em>没有注释出处</em>（<c>PostgreSqlDialect.cs:40</c> 是一行光秃秃的覆写），
    /// 所以 3 那一边的三家里只有一家写过依据 —— 本版没有替它补，那是另一族的事。</summary>
    public override char ParameterPrefix => ':';

    public override string GetStoreType(ColumnContract column) => types.GetStoreType(column);

    /// <summary>没有"把另一张表带进写语句"的那种语法（见 <see cref="EmitJoinedSources"/> 那一条）。</summary>
    public override bool SupportsJoinedWrites => false;

    /// <summary>目标不起别名，所以它在 <c>SET</c> 与 <c>WHERE</c> 里<em>用自己的表名</em>引用自己。
    /// 刻意不带模式前缀：<c>UPDATE sch."ORDERS" SET … WHERE sch."ORDERS"."Id" = :p</c> 在这一家不合法，
    /// 目标一旦限定到模式，引用侧就只认那个不带模式的名字。</summary>
    public override string WriteTargetAlias(TableInfo table) => table.Name;

    protected override string SourceAliasKeyword => " ";

    protected override bool PagingWrapsSelect => true;

    /// <summary>转交给 <see cref="OracleKeys.EmitReturning"/>。这一家到得了这里就说明有人要回填主键，
    /// 而那里的答案永远是抛 —— 文案要点名"缺的是哪个槽位"，因为"Oracle 没有 RETURNING"是一句
    /// 会让现场去找 <c>RETURNING … INTO</c> 的错话（那一条要的是 OUT 绑定，本 SPI 表达不了）。</summary>
    protected override void EmitReturning(SqlWriter writer, IReadOnlyList<ColumnContract> returning) => keys.EmitReturning(writer, returning);

    /// <summary>走包装那一支时 <c>Emit</c> 在 <see cref="DialectBase.EmitWrappedPaging"/> 处就返回了，
    /// 所以能到这里的一条必然没有分页。<b>这一口是抽象口的形状要求，不是一条分支</b>：
    /// 把它写成"到了这里就抛"会是死代码，因为 <see cref="PagingWrapsSelect"/> 与它由同一个类给出，
    /// 谁改了开关就一定看得到这里。</summary>
    protected override void EmitPaging(SqlWriter writer, SelectStatement select)
    {
    }

    /// <summary>两三层的 <c>ROWNUM</c> 分页（§9.4 第三套）。层数是<b>两档而不是三档</b>：
    /// <list type="bullet">
    /// <item>只有 <c>Take</c>：包一层就够 —— <c>SELECT * FROM (排好序的内层) WHERE ROWNUM &lt;= n</c>。
    /// 外层不再需要那个伪列，于是这一档<em>不产生多余的列</em>。</item>
    /// <item>带 <c>Skip</c>：必须再包一层，因为 <c>WHERE ROWNUM &gt; m</c> 直接写在内层永远为空
    /// （第 1 行拿到 1 时比较失败就不继续编号）。中间那层把 <c>ROWNUM</c> 落成一颗真列，
    /// 外层才有东西可比。<b>代价是外层多带一列</b>，而它不进任何投影：全仓库物化按序号取数
    /// （<c>FieldCount</c> 与 <c>GetOrdinal</c> 在 <c>src/</c> 里各 0 处），所以那一列没有槽位可占。</item>
    /// </list>
    /// 上界写成 <c>(:skip + :take)</c> 而不是在 CLR 里加好：两侧都是<em>参数</em>，加完就不是参数了 ——
    /// 那是把一条共享游标的语句换成一条按值独享的语句，而这一档的调用方恰恰是分页（同一句 repeated）。
    /// <c>ROWNUM</c> 与 <c>&lt;=</c> 之间不加括号：编号在谓词里、不在比较里，包起来只会多一层读不出的噪音。</summary>
    protected override void EmitWrappedPaging(in SelectStatement select, SqlWriter writer)
    {
        if (select.Offset is null)
        {
            writer.Append("SELECT * FROM (");
            EmitSelectBody(select, writer);
            writer.Append(") WHERE ROWNUM <= ");

            // 那颗感叹号有闸门背书：DialectBase.Emit 只在 Limit 与 Offset 至少一个非空时才走到这里，
            // 所以"Offset 为空"这一支里的 Limit 必然非空 —— 少那一道闸门这里就得改成抛。
            EmitExpression(select.Limit!, writer);

            return;
        }

        writer.Append("SELECT * FROM (SELECT ");
        EmitIdentifier(writer, PageSource);
        writer.Append(".*, ROWNUM ");
        EmitIdentifier(writer, RowNumber);
        writer.Append(" FROM (");

        EmitSelectBody(select, writer);

        writer.Append(") ");
        EmitIdentifier(writer, PageSource);

        // 只有 Take 的份里没有这一道界，所以这一支的层数与中间谓词是成对出现的。
        if (select.Limit is { } limit)
        {
            writer.Append(" WHERE ROWNUM <= (");
            EmitExpression(select.Offset, writer);
            writer.Append(" + ");
            EmitExpression(limit, writer);
            writer.Append(')');
        }

        writer.Append(") WHERE ");
        EmitIdentifier(writer, RowNumber);
        writer.Append(" > ");

        EmitExpression(select.Offset, writer);
    }

    /// <summary>内层表源与那颗编号列的名字。<b>带 <c>$</c> 是因为它不能与任何业务列同名</b>：
    /// 外层引的是 <c>"QUICKSQL$PAGE".*</c>，撞名的那一列会被库判成"歧义"，而那是一条库里的错而不是这里的错。
    /// 两颗名字都过 <see cref="DialectBase.EmitIdentifier"/>：折叠与引号由策略给，所以声明处与引用处
    /// 不可能写出两个字形（§9.2 那条"共用一份实现"在这一格上是同一件事）。</summary>
    const string PageSource = "QUICKSQL$PAGE";

    const string RowNumber = "QUICKSQL$ROWNUM";

    /// <summary>ANSI 那一份是 <c>UPDATE 表 AS 别名</c>，T-SQL 是 <c>UPDATE 别名 … FROM 表 AS 别名</c>，
    /// 这一族两个都不是：目标位只有一张表，别名那颗参数无处安放。
    /// <c>DELETE</c> 共用这一口（<see cref="DialectBase.EmitDeleteTarget"/> 默认转给它），
    /// 因为这一家的 <c>DELETE FROM 表</c> 与 <c>UPDATE 表</c> 在"目标位不接受别名"这一条上同形。</summary>
    protected override void EmitUpdateTarget(SqlWriter writer, TableRef table) => EmitTableName(writer, table.Table);

    /// <summary>这一族没有 <c>UPDATE … FROM</c> 也没有 <c>DELETE … USING</c>，所以带表源的写语句
    /// 在这里<em>抛</em>而不是悄悄丢掉那些表源：一条少了行集的合并 UPDATE 会把同一个值写给整张表，
    /// 那是 §1.2 原则 4 里最坏的一种静默退化。<see cref="EmitUpdateTarget"/> 与它是同一条分岔的两半
    /// （不能起别名 ⇒ 也不能带别的表进来），<see cref="DialectBase.EmitJoinedSources"/> 那一格本来就是
    /// 为"这一家怎么带别的表"留的覆写点，这里给出的答案是"带不了，等 MERGE INTO"。</summary>
    protected override void EmitJoinedSources(SqlWriter writer, string keyword, IReadOnlyList<JoinClause> joins, out SqlExpression? folded)
    {
        folded = null;

        if (joins.Count == 0)
            return;

        throw new QuickSqlException(
            $"{Kind} 的写语句不接受额外的表源：这一家没有 {keyword.Trim()} 这种子句，"
            + "带别的表的更新要写成 MERGE INTO … USING (…) ON (…) WHEN MATCHED THEN …，本版本未接（§12.5 末尾那一格）。"
            + "日常路径不会到这里 —— SupportsJoinedWrites 为 false 时 SaveChanges 整桶退回一句一实体，"
            + "语义与合并档等价（§12.4 第 1 项），代价是那 " + joins.Count + " 组表源所代表的行各占一次往返。");
    }

    /// <summary>源在本家起的别名（目标另起 <c>t0</c>）。两家都是<em>不带 <c>AS</c></em> 的表别名——这与
    /// <see cref="SourceAliasKeyword"/> 那句"这一家只在列别名上接受 <c>AS</c>"同源，MERGE 的两个别名都是表位。</summary>
    const string MergeTarget = "t0";

    const string MergeSource = "qsrc";

    /// <summary><c>InsertOrUpdate</c> 在本血统的发射：<b>Oracle 的 <c>MERGE INTO … USING (SELECT … FROM dual UNION ALL …) …</c></b>。
    /// 这一家没有 <c>(VALUES …)</c> 那种行集构造子，多行只能写成一颗 <c>SELECT … FROM dual</c> 的 <c>UNION ALL</c> 串
    /// （每颗格是一枚参数、以一个列别名命名，后面的 <c>ON</c> ∶ <c>SET</c> ∶ <c>INSERT VALUES</c> 都引那别名）；
    /// <c>ON</c> 与两支的去向由共用层那份 <see cref="DialectBase.EmitMergeTail"/> 发，不收分号。
    /// 目标位起别名（<c>MERGE INTO 表 t0</c>）——这一家只有 <c>UPDATE</c> 的目标不能起别名，<c>MERGE</c> 能。</summary>
    ///
    /// <para><b>档位口径</b>：这一句是本血统第一条产出的 <c>MERGE</c> 文本，形态是厂商文档口径（Oracle MERGE 语法），
    /// 而本仓库<em>没有任何一台授权 Oracle 血统实例</em>跑过它（类注释第二段那句"本类产出没有在一台真库上跑过"
    /// 对这一格同样成立）。它到"文本层已钉 + 未实测台账"为止，达梦 ∶ 神通两家在这一颗上同档同因（共用本类产出）。
    /// 另一格 Oracle MERGE 的老坑——<c>WHEN MATCHED UPDATE SET</c> 不能改 <c>ON</c> 里用到的那一列（ORA-38104）——
    /// 本形状构造上不会踩：键只进 <c>ON</c> 与 <c>INSERT</c>，<c>SET</c> 只有可写列。重开条件一句话：连上一台 Oracle
    /// 血统库跑绿这一句（§14、集成册补一场）。</para></summary>
    public override void Emit(in UpsertStatement upsert, SqlWriter writer)
    {
        writer.Append("MERGE INTO ");
        EmitTableName(writer, upsert.Table.Table);
        writer.Append(' ');
        EmitIdentifier(writer, MergeTarget);

        // USING (SELECT :p0 AS "col0", :p1 AS "col1" FROM dual UNION ALL SELECT …) qsrc
        writer.Append(" USING (");

        for (var row = 0; row < upsert.Rows.Count; row++)
        {
            if (row > 0)
                writer.Append(" UNION ALL ");

            writer.Append("SELECT ");

            var cells = upsert.Rows[row];

            for (var column = 0; column < cells.Count; column++)
            {
                if (column > 0)
                    writer.Append(", ");

                EmitExpression(cells[column]!, writer);
                writer.Append(" AS ");
                EmitIdentifier(writer, upsert.Columns[column].ColumnName);
            }

            writer.Append(" FROM dual");
        }

        writer.Append(") ");
        EmitIdentifier(writer, MergeSource);

        EmitMergeTail(writer, MergeTarget, MergeSource, upsert, semicolon: false);
    }
}
