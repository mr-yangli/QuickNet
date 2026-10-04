using FastSql.Metadata;
using FastSql.Providers;
using FastSql.Sql;

namespace FastSql.Providers.Sqlite;

/// <summary>SQLite 的建表与改表（§9.2 的 <c>ICodeFirstDialect</c>，第七家）。<b>本文件是本仓库第一份
/// "每一条要发的 DDL 形状都在活库上发过一次"的建表实现</b>：另六族的 DDL 出处以厂商文档与 shipped 产物
/// 为主（<c>Gbase8sCodeFirst</c> 类注释那三处【文档·IBM】是这批里最典型的一格），而下面每一支都有
/// 第二十八到第三十轮的编号【实测】。要连 <c>SqliteTypes</c> 类抬头那句限定一起读：量的是
/// "SQLite 3.46.1 ∶ Microsoft.Data.Sqlite 9.0.0" 这一对，不是这一族的所有部署。
///
/// <para><b>【第一条：自增在本家换的不是后缀，是整条键】</b>本仓库另六家在这一格上分三派
/// （<c>IDENTITY(1,1)</c> ∶ <c>AUTO_INCREMENT</c> ∶ <c>GENERATED … AS IDENTITY</c>），三派都是
/// "型名之后追一句子句"，而表级 <c>PRIMARY KEY</c> 那一支照旧发。本家两样都不行：
/// <list type="bullet">
/// <item><b>自增只能是内联主键的一部分</b>：内联 <c>INTEGER PRIMARY KEY AUTOINCREMENT</c> 建得成
/// 【实测·2802 的 <c>notnull=1 ∶ pk=1</c>，目录回显 2803 那句原文】，而同一张表上再发一条表级主键——
/// 无论那一把键是复合【实测·305】、单列具名【实测·3009】还是 <c>CONSTRAINT … PRIMARY KEY (自增列, 别的列)</c>
/// 【实测·2806】——都交同一句 <c>table "x" has more than one primary key</c>。<b>"看着只是多写一处键"
/// 在这一族是语法级互斥</b>，所以 <see cref="EmitCreateTable"/> 里那颗 <c>inlineKey</c> 判的是
/// "这一把键已经由列定义发出去了"，而不是"要不要省一句"。</item>
/// <item><b>型名必须是那颗 <c>INTEGER</c></b>：<c>BIGINT PRIMARY KEY AUTOINCREMENT</c> 被正面拒
/// 【实测·306 ∶ 2807 同一句 <c>AUTOINCREMENT is only allowed on an INTEGER PRIMARY KEY</c>】，
/// 而本家的 <c>long</c> 键按 <c>SqliteTypes.GetStoreType</c> 得到的正是 <c>BIGINT</c>。所以自增那一支
/// 把型名换成 <c>INTEGER</c>，而<em>这不是把键弄窄</em>：这一档的存储就是 64 位 rowid，一颗 30 亿
/// 写进去原样读回、下一号是 3000000001【实测·310/310b】。<b>本仓库在这里第一次需要
/// "契约里的 CLR 宽度与发出的型名脱钩"而脱钩是安全的</b>——安全由那两条读数撑着，而不是由
/// "INTEGER 在这一族只是 affinity 配方"这句话撑着（那句只说宽度不强制，没说值装得下）。</item>
/// <item><b>取 <c>AUTOINCREMENT</c> 而不是裸 <c>INTEGER PRIMARY KEY</c>，理由是一颗对照</b>：
/// 两形都会自动补号，分岔在删掉最大号之后 —— 裸那一形<em>把那个号还回去</em>【实测·2809：删掉 2
/// 之后下一号是 <c>2</c>】，带 <c>AUTOINCREMENT</c> 那一形<em>只增</em>【实测·2810：删掉 50 之后
/// 下一号是 <c>51</c>】。另六家的自增列语义都是"只增"（序列对象自己记着 <c>seq</c>，本家的
/// <c>sqlite_sequence</c> 也是【实测·56/57】），所以本家要的是后者：<b>发裸形会让一句
/// <c>DELETE</c> 之后的 <c>INSERT</c> 复用刚被删掉的号，而那是这一族<em>独家</em>的行为差</b>。
/// 代价也在这儿：这一档要求那张表存在 <c>sqlite_sequence</c> 那一行，而 SPI 里没有"读下一号"的那一格
/// （<c>SqliteKeys</c> 类注释第三段末）。</item>
/// </list></para>
///
/// <para><b>【第二条：模式限定符在索引上换边，而换边不是风格】</b>本仓库六家的
/// <c>EmitCreateIndex</c> 都写成 <c>CREATE INDEX "名字" ON 模式.表 (…)</c>（限定在<em>表</em>那一侧）。
/// 本家那个位置不能限定，四颗各自独立问到同一句语法错【实测·1301 ∶ 2826 ∶ 2919 ∶ 4604】，
/// 而把限定<em>挪到索引名那一侧</em>不但能过、还会落进正确的那个库里：
/// <c>CREATE INDEX side.ix_s30b ON s30 ("V")</c> 成功后，索引出现在 <c>pragma_index_list('s30','side')</c>
/// 里【实测·3005 ∶ 3006】。<b>第四十六轮把这一句换成本文件真正发的那一形重跑了一次</b>：先
/// <c>ATTACH DATABASE '…' AS app</c>【实测·4600 ∶ 4601 的 <c>pragma_database_list</c> 三行 = main(0) ∶ app(2)】、
/// 表只建在 <c>app</c> 里【实测·4602】，然后同一条带引号的
/// <c>CREATE INDEX "app"."IX_t46_CreatedAt" ON "t46" ( "CreatedAt" ASC )</c> 建得成【实测·4603】，
/// 索引落在 <c>app</c> 库里【实测·4605 的 <c>app.sqlite_master</c> ∶ 4606 的 <c>pragma_index_list('t46','app')</c>】，
/// 而<em>两半都限定</em>的那一形是语法错【实测·4604】。
/// <b>为什么必须换而不是"干脆两边都不限定"</b>：不带限定的
/// <c>CREATE INDEX ix ON s30 (…)</c> 会去 <c>main</c> 里找表并报 <c>no such table: main.s30</c>
/// 【实测·3002】—— 而"只在 main 那一档解析裸表名"这半句在 4603 之后要说得更准：<b>建索引这一句里
/// 裸表名解析的是"索引名那个限定词所指的库"，不是永远的 <c>main</c></b>（4603 的表只存在于 <c>app</c>，
/// 而它在名字限定为 <c>app</c> 的那一句里被认出来了）。反过来，<em>同一张表在查询那一侧的裸名也认得出来</em>
/// 【实测·4608】，所以这一格是 <c>CREATE INDEX</c> 独有的解析规则，不是"这一族不解析 ATTACH 库里的裸名"。
/// 还有一格要记：库里存下来的那条 DDL 文本<em>把名字上的限定词抹掉了</em>（4605 的
/// <c>sql</c> 列原文是 <c>CREATE INDEX "IX_t46_CreatedAt" ON "t46" ( … )</c>），所以
/// <c>SqliteSchemaReader</c> 拿 <c>sqlite_master.sql</c> 与"我们发出的那一串"逐字比的时候，
/// 限定形永远对不上——比的是<em>存在性</em>，不是文本。
/// 今天集成跑的是单文件（<c>Schema</c> 为空），
/// 那条路走不到，但形状在这里必须先量对，否则第一天用到 ATTACH 就是一句语法错。
/// <b>本家也是七家里唯一一颗"限定名可以发而限定表不许发"的</b>：另六家那两半都发得出。</para>
///
/// <para><b>【第三条：本文件六处"抛"里有五处带的是库自己说过的原话】</b>另有两处（自增列撞表级主键、
/// 非整数族上的自增）的红话在第一条里已经引过，那里是"为什么这一支换掉了键"的出处，本条只补下面两处
/// 发语句前的闸门：<see cref="EmitAddColumn"/> 与 <see cref="EmitAlterColumnPrecision"/>。唯一一处
/// <em>没有</em>库原话的是 <see cref="EmitCreateIndex"/> 的全文那一档，它的出处是枚举式的
/// （2816/2817/2818 三形都建得成而里面没有全文），读法与本文件其余几颗不同，写在全段末尾：
/// <list type="bullet">
/// <item><see cref="EmitAddColumn"/> 在两种列上抛：自增列【实测·312b ∶ 2813 同一句
/// <c>Cannot add a PRIMARY KEY column</c>】与非空列【实测·313 ∶ 2811 同一句
/// <c>Cannot add a NOT NULL column with default value NULL</c>】。第二颗的退路量到了同一条语句的活的那一半：
/// 带上 <c>DEFAULT 0</c> 就建得成【实测·3010】，而 <c>ColumnContract</c> 里没有"列默认值"那一格
/// （§14 新记的一颗：<b>这一族的 <c>ADD COLUMN</c> 需要一个本仓库无法表达的子句</b>）。
/// 所以这里抛不是"保守"，是因为发出去<em>恒红</em>，而红话里能把那颗缺的子句点名——库自己只会说
/// "default value NULL"，那是更容易读错的一句。</item>
/// <item><see cref="EmitAlterColumnPrecision"/> 抛，而这一句要分两层读：<b>语法层</b>本家没有
/// 改列的语句【实测·F4 ∶ 2815 同一句 <c>near "ALTER": syntax error</c>】；<b>语义层</b>这一问在本家
/// <em>没有对象</em>——声明宽度从不参与存储【实测·4001 ∶ 4002】，把 <c>VARCHAR(4)</c> 改成 <c>VARCHAR(9)</c>
/// 在库里改不动任何东西（<c>4</c> 那一条今天装得下 20 个字符，<c>9</c> 也一样）。
/// 于是 <c>Alter_Column_Precision</c> 那颗能力位在本家<b>不声明</b>，Core 也就不会走到这一句，
/// 抛是那道闸被绕过时的兜底。真正要改型状的那一族操作在这一族是"建新表、拷数据、换名"，
/// 而它的形状本次没有读数（<c>RENAME TO</c> 那一颗是活的【实测·AG】，其余没有），不在这一版建模（§14）。</item>
/// </list></para>
///
/// <para><b>【第四条：本文件只有一颗 <see cref="TryEmitColumnComment"/> 交 false，而交集掉的那一位不是它】</b>
/// 那一颗 false 是第二次（第一次是 <c>Gbase8sCodeFirst</c>）：那一家
/// 的原因是"注释在目录表里而不是在语句里"，本家更硬——<c>COMMENT</c> 这个词在本族是语法错
/// 【实测·F5 ∶ 2828 同一句 <c>near "COMMENT": syntax error</c>】，而这一族<em>没有任何</em>列注释的落处：
/// <c>sqlite_master</c> 回显的是引擎自己规范化的 DDL 文本（列按顺序追加、表级约束排在最后
/// 【实测·3013】），所以"把注释夹进尾巴再靠目录读回来"这条路不通。
/// <b>"模式"那一格在本家要反过来读，而这一处是本笔起稿时写错过的地方</b>：本家<em>声明</em>
/// <c>Schemas</c>，尽管它发不出 <c>CREATE SCHEMA</c>（那是语法错【实测·2824】，建到不存在的模式上报
/// <c>unknown database</c>【实测·2825】，而"这一族的模式"实际是一颗 attach 进来的文件【实测·3000/3001】，
/// 由连接会话决定而不是由 provider 决定）。理由是那颗位在七家语义里量的从来不是"模式对象可由 provider 建出"，
/// 而是<b>"限定名发得出而库认得它"</b>：本家三处限定都有现场读数——<see cref="EmitTable"/> 发
/// <c>main.</c> ∶ <c>side.</c> 限定建表【实测·3001】、带限定的 <c>ALTER</c> 与 <c>DROP TABLE</c> 都收
/// 【实测·1308 ∶ 1310】、<see cref="EmitCreateIndex"/> 把限定挪到索引名那一侧而它落进正确的库
/// 【实测·3005 ∶ 3006】，读侧 <c>pragma_table_info(@tbl, @sch)</c> 两档各交回自己的表【实测·3705 ∶ 3706】。
/// 于是 §13.5 那句"每接一家交集就小一格"走到本家<b>掉一位而不是清空</b>：上一笔还剩
/// <c>Schemas</c> ∶ <c>Alter_Column_Precision</c> 两颗，本家只丢后者（<see cref="EmitAlterColumnPrecision"/>
/// 抛，两层理由在第三条），那颗 <c>Comment_On_Column</c> 的 false 是第二次而它<em>不在交集里已经一笔了</em>。
/// 这一格读法有两层，都要留下：
/// <b>其一</b>，交集史上第一次只剩一颗，而<b>剩下的那颗<em>自己正在变形</em></b>——七家里有两家是在
/// "那一格不是 provider 能建的对象"的库里声明它的（<c>Gbase8sCodeFirst</c> 的模式是所有者、本家的模式
/// 是挂载的文件），所以"交集里还有这一位"不等于"跨族建表迁移可以按这一位发"。
/// <b>其二</b>，本家丢的那一颗与上一笔丢的那一颗缺法不同：<c>Gbase8sCodeFirst</c> 掉的是"这一家发不出
/// 那条语句"，本家掉的这颗是<b>发不出<em>而且</em>没有对象</b>——语法层没有改列的语句【实测·F4 ∶ 2815】、
/// 语义层声明宽度从不参与存储【实测·4001 ∶ 4002】。所以 §13.5 那条"交集量的是 <c>src/</c> 的覆盖面而不是
/// 库的能力面"到本家不需要修正，而它多了一层：<b>覆盖面这一回缩水的理由完完全全在库那一侧</b>。</para>
///
/// <para><b>【第五条：本家第一次让"多语句串"这一格有了活证】</b>六家共同的形状是在
/// <c>EmitCreateTable</c> 尾部用 <c> ; </c> 串索引，而六家<em>都没有读数</em>说这一串收不收
/// （<c>Gbase8sCodeFirst</c> 类注释末段把这一颗交给"第一台库"）。本家它量过了，而且两半都量：
/// 建表加索引两句一串走 <c>ExecuteReader</c> 成立【实测·2820/2908/2909】，走 <c>ExecuteNonQuery</c>
/// 同样成立【实测·2915/2916】，三句一串（建表 + 普通索引 + 唯一索引）也成立【实测·2822/2912/2913】。
/// <b>尾句不带分号也会被执行</b>【实测·2908 ∶ 2910 ∶ 2912 三对，索引全部出现在 <c>index_list</c> 里】，
/// 这一点值得单记：驱动并不是按分号切完再逐条跑，最后那句没有终止符也照样走。
/// 另注意 DDL 的 <c>ExecuteNonQuery</c> 交回 <c>affrows = 0</c>【实测·2914/2915/2917】——
/// 与 <c>SqliteKeys</c> 类注释第二段那颗"回填不改变计数"是同一颗开关的两个方向，
/// 共用层若在 DDL 之后判 <c>Affrows</c> 会一律看到 0，本家它有解释。</para>
///
/// <para><see cref="EmitCreateIndex"/> 的方向两颗关键字都发（<c>ASC</c> ∶ <c>DESC</c>）【实测·307 ∶ 2816】，
/// 与虚谷那一家同数而理由不同：那一家是"产物在这里比文档话多"，本家是当场建成的索引在
/// <c>pragma_index_list</c> 里看得见【实测·2819】。部分索引（<c>WHERE</c> 那一支）这一族确实有
/// 【实测·2818】，而 <c>IndexContract</c> 里没有那一颗——与 <c>XuguCodeFirst</c> 类注释第五条那颗
/// <c>BITMAP</c> 同一格，只是本家那一支更常用。全文索引继续抛，而抛的话这次要说清一件事：
/// 这一族有 <c>fts5</c> 那一整族虚表，<b>本仓库对它一条读数都没有</b>（第 28 到 30 轮问的是 DDL 主干），
/// 所以"这一家有全文而没有那条语句"这句在别家是【文档】档、在本家它是【实测】(没有那条语句)
/// 加【未量】(那一族虚表)两颗并排。</para></summary>
public sealed class SqliteCodeFirst(SqliteTypes types) : ICodeFirstDialect
{
    public IdentifierPolicy Identifiers => IdentifierPolicy.Sqlite;

    /// <summary>类注释第一条第三项：取 <c>AUTOINCREMENT</c> 的代价是它依赖 <c>sqlite_sequence</c>，
    /// 取它的理由是裸形会复用刚删掉的号（2809 ∶ 2810 那一对对照）。写成 <c>const</c> 而不是
    /// <c>virtual</c>，与 <c>XuguCodeFirst.IdentityClause</c> 同一条纪律，而本家这一颗<em>比那一家短一半</em>：
    /// 它只给子句，那颗必需的 <c>INTEGER</c> 由 <see cref="IdentityStoreType"/> 给（306 ∶ 2807 拒的正是型名那一格）。</summary>
    const string IdentityClause = " PRIMARY KEY AUTOINCREMENT";

    public void EmitCreateTable(SqlWriter writer, EntityContract contract)
    {
        writer.Append("CREATE TABLE ");
        EmitTable(writer, contract.Table);
        writer.Append(" (");

        // 内联自增键已经自带 PRIMARY KEY，而本家那一颗与表级主键互斥（305/2806/3009 三颗同一句红话）。
        var inlineKey = contract.PrimaryKey.Length == 1 && contract.PrimaryKey[0].IsIdentity
            ? contract.PrimaryKey[0]
            : null;

        if (inlineKey is null && contract.PrimaryKey.Length > 0 && contract.Columns.Any(c => c.IsIdentity))
            throw new FastSqlException("SQLite 不能把自增列放进表级主键：内联的 "
                + "INTEGER PRIMARY KEY AUTOINCREMENT 与表级 PRIMARY KEY 在这一族是互斥的两颗（305 ∶ 2806 ∶ 3009，"
                + "三颗都报 table has more than one primary key）。今天请让自增列单独作主键，"
                + "或把并发/业务那把复合键改成不含自增列——库没有「第二把键」这一格，缺的不是本家的发射而是这一族的语法。");

        var parts = new List<string>(contract.Columns.Length + 1);

        foreach (var column in contract.Columns)
            parts.Add(Definition(column));

        if (contract.PrimaryKey.Length > 0 && inlineKey is null)
        {
            var key = string.Join(", ", contract.PrimaryKey.Select(c => Identifiers.Quote(c.ColumnName)));

            // 具名表级主键这一形在本家成立（2805 的复合键 ∶ 3007/3008 的单列键，目录里 pk 序号逐颗对得上）。
            parts.Add($"CONSTRAINT {Identifiers.Quote("PK_" + contract.Table.Name)} PRIMARY KEY ({key})");
        }

        writer.Append(string.Join(", ", parts)).Append(')');

        foreach (var index in contract.Indexes)
        {
            writer.Append(" ; ");
            EmitCreateIndex(writer, contract.Table, index);
        }
    }

    /// <summary>本家 <c>ADD COLUMN</c> 只有"可空、无键、无唯一"那一档是活的（2812 ∶ 2813 ∶ 2814 ∶ 2811
    /// 四颗：带 <c>NULL</c> 的过，带主键、带 <c>UNIQUE</c>、带 <c>NOT NULL</c> 而无 <c>DEFAULT</c> 的三颗各报一句），
    /// 所以这里<em>先</em>挡两颗、把第三颗交给库。挡在发射点而不是建完之后：见类注释第三条第一项。</summary>
    public void EmitAddColumn(SqlWriter writer, TableInfo table, ColumnContract column)
    {
        // 可空性读 stored 而不是裸契约值，与另六族同一句纪律（v0.24 全站那条）。本家的
        // ApplyStorageDefaults 今天不改这一颗位（它只改 Unicode / LengthSemantics / MaxBytes / StoreTypeName），
        // 所以这一读法现在是等价的；写成 stored 是因为"等价"这件事归那颗方法管，不归这里管。
        var stored = types.ApplyStorageDefaults(column);

        if (column.IsIdentity)
            throw new FastSqlException($"SQLite 不能往已有表加自增列（{column.ColumnName}）：这一族的自增必须是"
                + "内联主键，而 ALTER TABLE ADD COLUMN 明确不收主键那一支（312b ∶ 2813 同一句 "
                + "Cannot add a PRIMARY KEY column）。今天请建新表并搬数据，或把这一列做成应用给值的普通列。");

        if (!stored.NullableInStore)
            throw new FastSqlException($"SQLite 不能往已有表加非空列（{column.ColumnName}）：库里那一格需要一个"
                + "非 NULL 的默认值来填已有行，而契约里没有「列默认值」那一颗（313 ∶ 2811 报 "
                + "Cannot add a NOT NULL column with default value NULL ∶ 同一条语句带 DEFAULT 0 就成立，实测 3010）。"
                + "今天请先以可空列加上、再回填、约束那一格在这一族改不回去。");

        writer.Append("ALTER TABLE ");
        EmitTable(writer, table);
        writer.Append(" ADD COLUMN ").Append(Definition(column));
    }

    /// <summary>不可达：本家不声明 <c>Alter_Column_Precision</c>（类注释第三条第二项）。走到这里说明
    /// 那颗位被绕过，抛的话要说清"不是没实现，而是这一问在本家没有对象"。</summary>
    public void EmitAlterColumnPrecision(SqlWriter writer, TableInfo table, ColumnContract column)
        => throw new FastSqlException("SQLite 没有改列型状的语句（F4 ∶ 2815 都是 near \"ALTER\": syntax error），"
            + "而且这一问在这一族没有对象：声明宽度从不参与存储（实测 4001 ∶ 4002），改宽度改不动库里的任何东西。"
            + "真正要换型的那一族操作在这一族是建新表、拷数据、改名，其形状本次没有逐颗读数（§14）。");

    /// <summary>不发任何东西，返回 false。理由与后果在类注释第四条：<b>这一族的 <c>COMMENT</c> 是语法错</b>
    /// （F5 ∶ 2828），而目录回显的是引擎规范化的 DDL 文本【实测·3013】，所以也没有"塞进原文再读回来"的退路。
    /// Core 那一条"跳过并 WARN"的退路在本家与 GBase 8s 那一家同形状而不同缺法。</summary>
    public bool TryEmitColumnComment(SqlWriter writer, TableInfo table, ColumnContract column) => false;

    public void EmitCreateIndex(SqlWriter writer, TableInfo table, IndexContract index)
    {
        if (index.Kind == IndexKind.FullText)
            throw new FastSqlException("SQLite 没有 CREATE FULLTEXT INDEX 这一句：28 到 30 轮把建索引的三种形都问过了"
                + "（普通、UNIQUE、带 WHERE 的部分索引都能建，实测 2816/2817/2818），里面没有全文那一档。"
                + "这一族确实有 fts5 那一族虚表，但本仓库对它一条读数都没有，所以这里不发——"
                + "那一格与 §14 的虚表/生成列并排（3012 量到 ADD COLUMN 收生成列而契约没有那颗，同一个形状）。"
                + "今天请建普通索引承担这个职责。");

        writer.Append(index.Kind == IndexKind.Unique ? "CREATE UNIQUE INDEX " : "CREATE INDEX ");

        // 类注释第二条：模式限定只能挂在这颗名字上，表那一侧挂它是语法错（3002 ∶ 3005 一对正反读数）。
        if (table.Schema is { Length: > 0 } schema)
            writer.Append(Identifiers.Quote(schema)).Append('.');

        writer.Append(Identifiers.Quote(index.Name)).Append(" ON ");

        // 这里刻意不调 EmitTable：那一句会写出 模式.表，而本家的建索引不收限定表名（1301 ∶ 2826 ∶ 2919）。
        writer.Append(Identifiers.Quote(table.Name));
        writer.Append(" (");

        for (var i = 0; i < index.ColumnNames.Length; i++)
        {
            if (i > 0)
                writer.Append(',');

            writer.Append(' ').Append(Identifiers.Quote(index.ColumnNames[i]));

            // 两颗都发【实测·2816】：这一族认 ASC 也认 DESC，所以不必像 Gbase8sCodeFirst 那样只写一半。
            writer.Append(index.Descending ? " DESC" : " ASC");
        }

        writer.Append(" )");
    }

    string Definition(ColumnContract column)
    {
        var writer = new SqlWriter(new(), [], '@');

        EmitColumnDefinition(writer, column);

        return writer.ToString();
    }

    void EmitColumnDefinition(SqlWriter writer, ColumnContract column)
    {
        var stored = types.ApplyStorageDefaults(column);

        // 自增那一支在这里整颗换掉型名与键（类注释第一条第二项：BIGINT 被 306/2807 正面拒绝）。
        if (column.IsIdentity)
        {
            writer.Append(Identifiers.Quote(column.ColumnName)).Append(IdentityStoreType(column)).Append(IdentityClause);

            writer.Append(stored.NullableInStore ? " NULL" : " NOT NULL");

            return;
        }

        writer.Append(Identifiers.Quote(column.ColumnName)).Append(' ').Append(types.GetStoreType(stored));

        writer.Append(stored.NullableInStore ? " NULL" : " NOT NULL");
    }

    /// <summary>自增列的型名：本家它是那颗 <c>INTEGER</c> 而不是 CLR 宽度对应的那一颗
    /// （<c>long</c> → <c>BIGINT</c> 会被库拒，实测 306/2807）。<b>判据取"算出来的型名里有没有 INT"</b>：
    /// 有则说明这一列在契约里就是整数族，换成 <c>INTEGER</c> 只改声明字形而不改存储（310 那颗 30 亿是这一句的见证）；
    /// 没有则<em>抛</em>——一颗文本列上的自增在本家没有形状，而库里那一格今天已经证明不收（306 报的正是这一档）。
    /// 人手工写的 <c>[Column(DbTypes = "…")]</c> 在这里同样被换掉，理由与 <c>Gbase8sCodeFirst.SerialFor</c>
    /// 那一段逐字相同：<c>IsIdentity</c> 是这一列更近的事实，而写下的那个名字在自增列上本来就不成立。</summary>
    string IdentityStoreType(ColumnContract column)
    {
        var declared = types.GetStoreType(types.ApplyStorageDefaults(column));

        if (!declared.Contains("INT", StringComparison.OrdinalIgnoreCase))
            throw new FastSqlException($"SQLite 的自增列必须是整数那一族（{column.ColumnName} 的存储型是 {declared}）："
                + "AUTOINCREMENT is only allowed on an INTEGER PRIMARY KEY（实测 306 ∶ 2807 两颗同一句，后者是 BIGINT）。"
                + "整数族（byte/int/long/uint 与它们的枚举底）会被本家统一换成那颗 INTEGER，"
                + "因为 AUTOINCREMENT 只认那一个字形而存储仍是 64 位（实测 310/310b）。");

        return " INTEGER";
    }

    /// <summary>模式为空就不写限定，与 <c>XuguCodeFirst</c>/<c>MySqlCodeFirst</c> 那两份同形，
    /// 而本家这一格多一层【实测】：<b>限定在 <c>CREATE TABLE</c> 与 <c>ALTER TABLE</c> 上都能发</b>
    /// （3001 在 attach 进来的库里建成 ∶ 1308 带限定改表成功），<em>只有建索引不收</em>（见
    /// <see cref="EmitCreateIndex"/>）。所以本方法的默认形状不是"这一族不认限定"，而是"限定在这里是
    /// 一颗会话级事实"——<c>main</c> 永远在，别的名字要连接串或 <c>ATTACH</c> 先把它带进来
    /// 【实测·2825：<c>unknown database s28</c>】。</summary>
    void EmitTable(SqlWriter writer, TableInfo table)
    {
        if (table.Schema is { Length: > 0 } schema)
        {
            writer.Append(Identifiers.Quote(schema)).Append('.');
        }

        writer.Append(Identifiers.Quote(table.Name));
    }
}
