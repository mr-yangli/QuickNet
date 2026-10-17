using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;

namespace QuickSql.Providers.Xugu;

/// <summary>虚谷的 DDL（§9.2、§10.4，第五家）。与查询文本共用同一个 <see cref="IdentifierPolicy.Xugu"/>，
/// 而这一格在这一家<b>比在另四家更吃紧</b>：那一家会把不带引号的名字折成 <c>COMPATIBLE_MODE</c> 决定的字形
/// （默认档是大写），所以双引号一漏，建出来的表名与查的表名就不是同一个东西 —— 与 PG 系那一句
/// "<c>QuickSqlOrder</c> 变成 <c>quicksqlorder</c>"是同一件事的两种方向。P0 只产出文本与黄金样本比对，
/// <b>不执行</b>。
///
/// <para>五处与四族不同，逐条标出处（三档判据见 <see cref="XuguExpressions"/> 类注释第二段）：
/// <list type="bullet">
/// <item><b>模式为 null 时什么都不写</b>【推断·与 Oracle 血统同形】：这一家的默认模式是"当前会话用户"，
/// 与 <c>OracleCodeFirst</c>/<c>MySqlCodeFirst</c> 那两家的处理一致，而<em>不是</em>因为这一族有 <c>DUAL</c>
/// 就跟着 Oracle 走 —— 理由是补一个假名字（<c>dbo</c>/<c>public</c>/<c>"SYSTEM"</c>）会造出一个
/// "看着限定了其实不存在"的模式，而裸名在两家的语义都是"由连接决定"。
/// <b>这一格今天没有出处</b>：两份产物的建表文本都<em>不带模式限定</em>，所以观察到的只是"没人写过限定"，
/// 不是"限定不许"。它的 §9.2 后果与 MySQL 那一家同一格：落在哪个模式由连接串决定而不是由 provider 决定。</item>
/// <item><b>自增是列上的一个子句，形状与 T-SQL、达梦逐字同形</b>：<c>IDENTITY(1,1)</c>【产物】
/// （两家 shipped 适配器都这么发，而它是这一族少见的"两份产物在这一格上完全一致"）。
/// 这一档有意思的地方是<b>它与自己的血统相反</b>：这一家别处（<c>SYSDATE</c>、<c>DUAL</c>、四参 <c>INSTR</c>）
/// 像 Oracle 那一族，而 Oracle 那一族的自增写法是 <c>GENERATED … AS IDENTITY</c>。
/// <b>这里刻意不留 <c>IdentityClause</c> 覆写点</b>：§9.1 那份清单里没有第二种挂在这家底下的库种，
/// 而"不留一颗没人覆写的虚函数"这条纪律已经立过两次（<c>OracleKeys.EmitReturning</c> 那一口刻意不是
/// <c>virtual</c>；<c>MySqlCodeFirst.TableOptions</c> 那颗是 <c>const</c>，见 <c>MySqlProvider</c> 类注释第二条）。
/// <b>同一族的 <c>IdentityClause</c> 不是这条纪律的实例</b>（本条上一版把它点了名，交账时按代码推翻）：
/// 那一颗在 <c>MySqlCodeFirst.cs:61</c> 是 <c>virtual</c> 而今天<em>没有覆写者</em>（MySQL 系那一族还没有叶子），
/// 它与 <c>PostgreSqlCodeFirst.cs:30</c> 那颗同名虚口是同一档遗留，一起记在 §14。
/// 【v0.36 第二笔复点：这一档"遗留"的读数更硬了——MySQL 系在 v0.35 挂上 TiDB／OceanBase 两颗叶子
/// （"还没有叶子"自此作废，原句留着记当时的分布），PG 系在本笔挂上 openGauss，三颗都裁成"不覆写"；
/// 而 PG 那一颗连覆写<em>入口</em>都没有：<c>PostgreSqlProviderBase</c> 构造直 new 具体 CodeFirst，
/// <c>CreateCodeFirst</c> 那道缝三族血统里只有 Oracle 一家留了。"零覆写者"从此要读成两种：
/// MySQL 那颗粒是有人可覆写而没人覆写，PG 这颗粒是从来没有可覆写的路。】</item>
/// <item><b>改精度是一条语句，而可空性必须在同一条里重述</b>【推断·与 T-SQL 同形】：
/// <c>ALTER TABLE … ALTER COLUMN &lt;列&gt; &lt;型&gt; &lt;NULL|NOT NULL&gt;</c>。五家在这一槽上的分布因此是
/// <c>4 家一句 ∶ 1 家两句串联</c>（金仓那一家 <c>TYPE</c> 不碰 <c>NOT NULL</c>，要独立一句）。
/// <b>这是本文件里出处最薄的一格</b>：文档那一侧本次没有读到 <c>ALTER COLUMN</c> 的语法页，
/// 两份产物里也没有改列的文本（ORM 的迁移段不走这两条路），所以它是"跟着会把可空性一起重置的那一族写"——
/// 也就是<em>取最保守的那一形</em>：重述可空性在两种库行为下都不会出错（要么被这句覆盖成契约值，
/// 要么本来就是这句该说的值），而不重述在 T-SQL 那一形上是静默改约束。真库第一天要跑的第一条就是它，
/// 记 §13.5 与 §14。</item>
/// <item><b>列注释是原生语法</b>：<c>COMMENT ON COLUMN … IS</c>【文档】，所以
/// <see cref="TryEmitColumnComment"/> 返回 true 而不是走 Core 那条"跳过并 WARN"的退路。
/// 这一格让五家的分布变成 <c>4 家原生 ∶ 1 家走存储过程</c>（只有 T-SQL 要 <c>sp_addextendedproperty</c>），
/// 而它是五家里少见的"新家族没有把分布拆细而是并入多数派"的一格。<b>它同时是本族第二处
/// "文档与产物在这一格上说的同一句话"</b>（第一处是 <c>''</c> 翻倍那一条，见 <see cref="XuguValues"/>）。</item>
/// <item><b>全文索引要抛，而这一次的"抛"有文档</b>：厂商那一页列的索引种类是普通、<c>UNIQUE</c>、<c>BITMAP</c>
/// 【文档】—— <c>BITMAP</c> 是这一族在这一格上多出来的那一支（四家里没有一家有它），而
/// <c>IndexContract</c> 的枚举里没有那一颗。所以"这一家有第四种索引而契约上没有那一格"记进 §14，
/// 与 <c>WM_CONCAT</c>（<see cref="XuguExpressions"/> 末段）同一批。反过来 <c>FullText</c> 那一颗在这里
/// <em>没有</em>语法，而它是三份"抛"里<b>第一家</b>有厂商文档正面依据的（另两家的措辞都是"要另一套东西"：
/// PG 系那句给的是访问方法，Oracle 血统那句给的是 <c>CTXSYS.CONTEXT</c> 域索引 —— 那一族<em>有</em>全文检索，
/// 只是不长在这一颗关键字上；这里读到的是"文档列了三种而里面没有全文"）。同一格在
/// <c>MySqlCodeFirst.EmitCreateIndex</c> 里那一段 <c>switch</c> 注释上立过一次（它写的是"三份抛里唯一一份有正面依据"）。</item>
/// <item><b><c>rowversion</c> 是这一家 DDL 拒收的第一颗名字</b>【实测·否定其可用】（v0.34 第二笔，
/// <see cref="EmitColumnDefinition"/> 里那一道）：这一族对 <c>[Column(TypeName = …)]</c> 的<em>通例</em>是
/// "人写的名字原样发、provider 不翻译也不校验"——今天仍然如此，<c>rowversion</c> 是第一颗、也是唯一一颗
/// 被点名的例外。它不是"这一家认不认这个词"的问题：认（RV-01a/b 建得成、目录读作 <c>ROWVERSION</c>、
/// RV-11d→f 会自增），问题是<em>发出去之后本仓库用不了它</em>（读即 <c>DataOutOfBindException</c>，
/// RV-08/A-06）。同一道口子管住 <c>EmitAddColumn</c> 与 <c>EmitAlterColumnPrecision</c>：三支都走
/// <c>Definition</c>。清单与逐条出处见 §14 的 rowversion 行与 §9.2 的 v0.34 段。</item>
/// </list></para>
///
/// <para>一处与 <c>MySqlCodeFirst</c> 正好相反的形状：<b>本族的建表文本尾部什么都没有</b>——没有
/// <c>ENGINE = …</c> 也没有 <c>DEFAULT CHARSET = …</c>。
/// <b>【实测·正问，v0.32 第二格改判；原判【推断】】</b>：这一句今天不是"按缺席读"出来的 ——
/// <c>obj/logs/v32-recon-cjk-plain-3.log</c> 的 N-08 三颗把 MySQL 血统那两家用的表尾写法照抄进这一家的
/// <c>CREATE TABLE</c>，三颗<em>全部被解析器驳回</em>，而且驳回的话说得没有第二种读法：
/// <c>ENGINE = INNODB</c> 交 <c>[E19260 L1 C41] unexpected IDENT, expecting end of file</c>、
/// <c>DEFAULT CHARSET = UTF8</c> 交 <c>unexpected DEFAULT, expecting end of file</c>、
/// <c>CHARACTER SET UTF8</c> 交 <c>unexpected CHARACTER, expecting end of file</c> ——
/// 三颗的"期望"都是<em>end of file</em>，即这一家的建表语法在列清单之后就<em>收尾</em>了，
/// 表级那一整段选项不存在（不是"这一家只有一个存储引擎"，而是"这一家压根没有引擎那一格"，
/// 前者仍然只是它的厂商侧解释、留在【文档】档之外）。所以本族不发那两颗不是省事，是<em>发不出去</em>。
/// 字符集那一半的替代则是<em>在连接串上</em>（<c>CHAR_SET=</c>，见 <see cref="XuguTypes"/>），
/// 而本版把它量穿了：<c>all_databases.CHAR_SET</c> 在这一台库上是 <c>GBK</c>，连接串多带一颗
/// <c>CHARSET=UTF8</c> 就换一种数法（§13.5 的 CHARSET 那一格）。
/// 于是 §9.3 那句"默认必须映射到宽的一侧"在这一家<b>不由 DDL 保证</b>，也<em>不能</em>由 DDL 保证：
/// 同一份建表文本在 UTF8 与 GBK 两个连接上会建出两批字节宽度不同的列，而 provider 看不见那一颗参数。
/// 这是那颗"连接串参数改变方言假设"缺口的<b>第三个实例的同一格</b>（第三颗是 <c>CHAR_SET</c> 本身），记 §14。</para>
///
/// <para>索引语句与建表同批用 <c> ; </c> 串起来，与金仓那一家同一形状：本族没有声明
/// <c>MultiStatement</c>（§14 的能力位里没有一颗是这一版新点的），所以这一串要么由执行器拆、
/// 要么走 §9.5 那条"生产默认走脚本"的路子。文本里不掺换行：黄金样本要跨平台逐字符比对。</para></summary>
public sealed class XuguCodeFirst(XuguTypes types) : ICodeFirstDialect
{
    public IdentifierPolicy Identifiers => IdentifierPolicy.Xugu;

    /// <summary>自增写法（类注释第二条）：<c>const</c> 而不是 <c>virtual</c>，理由也写在那里。</summary>
    const string IdentityClause = " IDENTITY(1,1)";

    public void EmitCreateTable(SqlWriter writer, EntityContract contract)
    {
        writer.Append("CREATE TABLE ");
        EmitTable(writer, contract.Table);
        writer.Append(" (");

        var parts = new List<string>(contract.Columns.Length + 1);

        foreach (var column in contract.Columns)
            parts.Add(Definition(column));

        if (contract.PrimaryKey.Length > 0)
        {
            var key = string.Join(", ", contract.PrimaryKey.Select(c => Identifiers.Quote(c.ColumnName)));

            parts.Add($"CONSTRAINT {Identifiers.Quote("PK_" + contract.Table.Name)} PRIMARY KEY ({key})");
        }

        // 括号在上面已经开了；尾部没有表选项（类注释末段第一条）。
        writer.Append(string.Join(", ", parts)).Append(')');

        foreach (var index in contract.Indexes)
        {
            writer.Append(" ; ");
            EmitCreateIndex(writer, contract.Table, index);
        }
    }

    public void EmitAddColumn(SqlWriter writer, TableInfo table, ColumnContract column)
    {
        writer.Append("ALTER TABLE ");
        EmitTable(writer, table);
        writer.Append(" ADD COLUMN ").Append(Definition(column));
    }

    /// <summary>一条语句把型与可空性一起重写（类注释第三条）。读的是 <c>NullableInStore</c> 而不是
    /// <c>IsNullable</c>：与另四族同一句判据（v0.24 全站那条规则），漏掉它就会把值归库给的列改回可空。</summary>
    public void EmitAlterColumnPrecision(SqlWriter writer, TableInfo table, ColumnContract column)
    {
        writer.Append("ALTER TABLE ");
        EmitTable(writer, table);
        writer.Append(" ALTER COLUMN ").Append(Definition(column));
    }

    public bool TryEmitColumnComment(SqlWriter writer, TableInfo table, ColumnContract column)
    {
        writer.Append("COMMENT ON COLUMN ");
        EmitTable(writer, table);
        writer.Append('.').Append(Identifiers.Quote(column.ColumnName)).Append(" IS ");
        writer.AppendStringLiteral(column.Comment ?? string.Empty, unicode: false);

        return true;
    }

    public void EmitCreateIndex(SqlWriter writer, TableInfo table, IndexContract index)
    {
        // 类注释第五条：文档给的那三种索引里没有全文那一档，所以这里抛而不是降格成普通索引
        // （T-SQL 那一族降格，是一处"看起来建成了"的形状；这一家连降格都不必，因为那一页压根没有它）。
        if (index.Kind == IndexKind.FullText)
            throw new QuickSqlException($"虚谷没有 CREATE FULLTEXT INDEX 这种写法：厂商文档列的索引种类是普通、UNIQUE、BITMAP 三种（§9.2）。"
                + "今天请自己建普通索引承担这个职责，或等全文检索那一格接上（缺的不是关键字而是整条 tsvector/解析器链路，与 PG 系那一句同一档）。"
                + "另注意这一家有的 BITMAP 在契约的 IndexKind 里没有那一颗，所以这一族多出来的那一支今天也发不出去（§14）。");

        writer.Append(index.Kind == IndexKind.Unique ? "CREATE UNIQUE INDEX " : "CREATE INDEX ");
        writer.Append(Identifiers.Quote(index.Name)).Append(" ON ");
        EmitTable(writer, table);
        writer.Append(" (");

        for (var i = 0; i < index.ColumnNames.Length; i++)
        {
            if (i > 0)
                writer.Append(',');

            writer.Append(' ').Append(Identifiers.Quote(index.ColumnNames[i]));

            // 方向照四家的多数派写出来。这一家认不认 ASC/DESC【无独立出处】，而两家产物的建索引文本
            // 都<em>带了</em>方向，所以这是本文件里少见的"产物在这格上比文档话多"的一处 ——
            // 它的后果也便宜：方向写错是索引形状不对，不是建表失败。
            writer.Append(index.Descending ? " DESC" : " ASC");
        }

        writer.Append(" )");
    }

    string Definition(ColumnContract column)
    {
        var writer = new SqlWriter(new(), [], ':');

        EmitColumnDefinition(writer, column);

        return writer.ToString();
    }

    void EmitColumnDefinition(SqlWriter writer, ColumnContract column)
    {
        var stored = types.ApplyStorageDefaults(column);

        var storeType = types.GetStoreType(stored);

        // rowversion 在这一家拒收（v0.34 第二笔，§14「要不要拒收」那一格按「全部完善」的整批授权定为 ①）。
        // 判据全是本仓库自己的读数：库里收得下这句 CREATE、也真的会自增（RV-01a/b/c 与 RV-11d→f，
        // v32-recon-rvbool-5.log），可那一列的值取不回来 —— 带它的投影一取行就抛 System.DataOutOfBindException
        // （RV-08b/08d/09e，与集成层 A-06/A-06c 同一颗红，v32-recon-mat-4.log）。
        // 所以原样发出去建出的是一列"DDL 绿、读即红"的版本戳，§5.8 禁的正是这个形状；抛在 DDL 这一侧，
        // 是因为那是错误唯一的便宜出口 —— 落在读侧就是整表不可用，落在写侧就是每次保存撞库。
        // 抛点选在 Definition 而不是 GetStoreType：那一颗还有非 DDL 的读者（ChangeGuard.Compares、
        // SchemaReader 对账），拒收只属于"我们要不要建它"这一问。
        if (string.Equals(storeType.Trim(), "rowversion", StringComparison.OrdinalIgnoreCase))
            throw new QuickSqlException("虚谷不建 rowversion 列：库里建得成、会自增，但驱动读不回那一列的值"
                + "（带它的 SELECT 一取行就抛 System.DataOutOfBindException，RV-08/A-06 两批同一颗红，§14）。"
                + "这一家请改用应用给值的并发列：[ConcurrencyToken] 而不带 [Column(TypeName = \"rowversion\")]，"
                + "比较式与写侧与另四家逐字同形（XuguConcurrency），缺的只是「由库递增」这一格 —— 那一格等的是 SPI，不是改名。");

        writer.Append(Identifiers.Quote(column.ColumnName)).Append(' ').Append(storeType);

        if (column.IsIdentity)
            writer.Append(IdentityClause);

        writer.Append(stored.NullableInStore ? " NULL" : " NOT NULL");
    }

    /// <summary>模式为空就不写限定（类注释第一条）。与 <c>MySqlCodeFirst</c> 那一份逐字同形，
    /// 而与 T-SQL/PG 两家的 <c>dbo</c>/<c>public</c> 分岔 —— 那两家的默认模式是一个<em>人人都有的公共模式</em>，
    /// 这一家的默认模式是<em>当前用户</em>，补假名字的效果因此不是"落到公共区"而是"落到一个可能不存在的模式"。</summary>
    void EmitTable(SqlWriter writer, TableInfo table)
    {
        if (table.Schema is { Length: > 0 } schema)
        {
            writer.Append(Identifiers.Quote(schema)).Append('.');
        }

        writer.Append(Identifiers.Quote(table.Name));
    }
}
