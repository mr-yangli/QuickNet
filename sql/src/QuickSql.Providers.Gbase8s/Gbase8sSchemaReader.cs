using System.Data.Common;
using QuickSql.Metadata;
using QuickSql.Providers;

namespace QuickSql.Providers.Gbase8s;

/// <summary>从 <c>systables</c> ∶ <c>syscolumns</c> ∶ <c>systypes</c> 三张系统表读结构
/// （§4，第六家）。与五族同一纪律：这里只有 ADO.NET 抽象层的虚调用，本文件不认识任何驱动类型
/// （§1.2 原则 3）。
///
/// <para><b>【本文件最重要的一条：它是这一族唯一一份"今天连一次都没有被走到"的代码】</b>
/// 另五家的读侧各有一份可指认的处境：T-SQL、Oracle 血统、PG 血统、MySQL 血统那四份在授权实例上跑过，
/// 虚谷那一份在 v0.32 有过三份日志（改前问答、19 型对照表、改后原地重跑，出处逐颗点名在
/// <c>XuguSchemaReader</c> 的类注释里）。本家一份都没有：<b>本机没有任何一台 GBase 8s</b>，
/// 而这一格又必须有一条真连接才会被执行 —— 所以它是这一层十一个文件里唯一一份
/// <em>既没有黄金样本、也没有离线用例、也没有活库</em>的东西。三处后果一起记在这里：
/// ① 下面每一条 SQL 的<em>字段名</em>与 <em>join 形状</em>都是【文档·IBM】，其中没有一颗拿到过
/// 比"手册那一页写着这个名字"更强的出处；② <see cref="ProbeLengthSemanticsAsync"/> 的读数只有本文件
/// 自己的读法可以证明，所以它进 <b>§10.4 那张表的"问不出"那一档</b>，与金仓、虚谷同档，
/// 而本家那一档是<em>推</em>出来的同档；③ 重开成本与另五家不同：另五家改这一格要先有一份新日志，
/// 本家改它第一次要有的是这台库。</para>
///
/// <para><b>【本文件第一次让"绑参数的顺序"成为契约】</b>五族的目录读侧都用<em>具名</em>占位符
/// （<c>:tbl</c>、<c>@tbl</c>），所以那两三颗 <c>Parameters.Add</c> 写颠倒也只是读得别扭；本家只有
/// 位置形 <c>?</c>（<see cref="Gbase8sDialect.ParameterPrefix"/>），名字进不了文本，于是
/// <b>那几行的先后顺序就是 SQL 语义的一部分</b> —— 颠倒它不会报错，只会把表名绑到所有者位上，
/// 然后交回 0 行、被读成"这张表不存在"。这是 §5.8 那句"禁静默退化"在本文件唯一的落点，
/// 所以 <see cref="ColumnsSql"/> 旁边逐颗点名了每一位接什么。
/// 与主链路的关系要说准：<c>AdoExecutor.Bind</c> 那颗摘前缀的口子（v0.32 立的）在本文件用不上，
/// 因为这里发的参数<em>从来就不带名字</em> —— 六族里只有本家不需要那一手，而理由不是它做对了，
/// 是它压根没有名字可摘。</para>
///
/// <para><b>【模式这一格在本家不是命名空间，是所有者】</b>§9.1 那一行第四列的读法落到目录上第一次
/// 换了身份：另五家的"模式"是树的一层（<c>ALL_TABLES.OWNER</c>、<c>information_schema.tables.TABLE_SCHEMA</c>、
/// <c>sys.objects.schema_id</c>），本家的 <c>systables.owner</c> 是<em>建这张表的那个用户</em>，
/// 而 <c>CREATE SCHEMA AUTHORIZATION user</c> 说的正是同一件事（一条语句、一个名字、没有第三层）。
/// 后果有两颗，都进了下面的 SQL：① <c>tabname</c> 只在<em>所有者之内</em>唯一，所以不加所有者那道闸
/// 会把两张同名的表并成一份读数 —— 那是 <c>XuguSchemaReader</c> 类注释第二条初稿怕的那一处混淆，
/// 在本家它<em>真的</em>可能发生；② "未点名所有者"这一支不能用 <c>COALESCE(?, CURRENT USER)</c> 表达：
/// 位置形 <c>?</c> 在文本里没有类型，而绑进去的是 <see cref="DBNull"/>，那颗 <c>COALESCE</c> 的型推导
/// 在这一家问不出【推断】，所以本文件改成<em>发两条文本</em>（每格一对，只差那一颗谓词），
/// 把默认那一支写进 SQL 而不是写进绑定值。这一手在五族都没有对应物，它是位置形占位符带进来的
/// 第三件后果（前两件：<see cref="Gbase8sValues"/> 类注释的恒等日志与顺序契约）。</para>
///
/// <para><b>折叠方向：本文件是六家里第一份把绑进去的名字折成<em>小写</em>的</b>（<see cref="Fold"/>）。
/// 另两家折大写的理由在这里逐字成立而方向相反（目录里存的是折叠后的形，参数值不进 SQL 文本，
/// 所以 <c>AppendIdentifier</c> 那条折叠路够不着）。它顺带暴露一颗 SPI 的窄：
/// <see cref="IdentifierPolicy.FoldToUpperCase"/> 是一颗布尔，只能表达"折大写"与"不折"，
/// 表达不了"折小写" —— 所以本家的策略那颗取 <c>false</c>（发射层恒加引号，用不到折叠），
/// 而真正的折叠规则只能写在<em>本文件的读侧</em>。这一格记 §14，重开条件是"哪一家需要不靠引号活着"。</para>
///
/// <para><b>两颗读不出的字段，逐颗给后果</b>：① <c>DECIMAL</c> 的<em>标度</em>。手册把
/// <c>syscolumns.width</c> 说成"字符型是长度、定点型是位数"，而那颗专门装标度的字段在本次读到的材料里
/// 对"精度还是标度"的指代没有读定，所以本文件<em>不选它</em>：<c>DECIMAL(10,2)</c> 在这里读回
/// <c>DECIMAL(10)</c>，<c>doctor</c> 会报一条真差而它是读法造成的。宁缺不错，与
/// <c>PostgreSqlSchemaReader</c> 那颗 <c>TIMESTAMP→DATETIME</c> 不折同一判据；② 自增。
/// <c>SERIAL</c> 在目录里读回的是 <c>INTEGER</c> 加一颗挂在序列上的 <c>DEFAULT</c>【文档·IBM】，
/// 也就是说这一家把"这一列会自增"存在<em>默认值</em>上而不是型名上，而 <see cref="SchemaColumn"/>
/// 没有承接默认值的槽位（它只有名字、型、两颗上限、可空性与序号）—— 于是本家的 <c>doctor</c>
/// 读不出"这一列是不是自增"，与虚谷那颗 <c>IS_SERIAL</c> 同一格缺口而<em>不同因</em>
/// （那一家是"库给了值而我们没槽位"，本家是"值压根不在列上"）。两颗一起记 §14。</para></summary>
public sealed class Gbase8sSchemaReader : ISchemaReader
{
    /// <summary>"这一列可以存空值"编码在 <c>coltype</c> 的高位而不是另一颗字段里【文档·IBM】：
    /// 那一份读法是 <c>coltype = 基本型号 + 256 × 可空 + 512 × 有默认值</c>。
    /// 所以判可空只看 <see cref="NullableBit"/> 那<em>一颗位</em>，不看整颗数的高字节奇偶 ——
    /// 后者在"还有一位装别的东西"时会读错，而这里没有读到"没有别的位"。
    /// 基本型号那颗在 <see cref="BaseTypeMask"/>，join 用它（<c>MOD(coltype, 256)</c> 就是这颗掩码的
    /// SQL 形，两处各写一遍是 §12.8 那条"同一颗数两处不许分岔"的反面选择：一处是 C#、一处是文本，
    /// 抽不出公共表达式，所以两颗旁边各点一次对方）。</summary>
    const int NullableBit = 256;

    const int BaseTypeMask = 255;

    /// <summary>点给了所有者时的读法。<b>两颗参数的顺序就是语义</b>（类注释第二条）：
    /// 第一位接<em>表名</em>、第二位接<em>所有者</em>，且都只出现一次 —— 位置形没有"复用一颗参数"
    /// 这一手，所以本条里 <c>tabname</c> 不能像具名族那样写两遍 <c>:tbl</c>。
    /// <c>tabtype = 'T'</c> 那道闸是这一族特有的：本家的目录里表、视图、系统表共用 <c>systables</c>
    /// 一张【文档·IBM】，不筛的话一张与表同名的视图会进同一份读数，而 <c>doctor</c> 拿它比契约会报
    /// "这一列不存在"。<b>视图那一档今天在本仓库不可问</b>（没有活的映射目标会建在视图上），
    /// 所以这道闸挡的是"别人库里的巧合"，与 <c>SqlServerSchemaReader</c> 那颗 <c>type = 'U'</c> 同一格。</summary>
    const string ColumnsSql = """
        SELECT c.colno, c.colname, ty.name, c.coltype, c.width
        FROM "informix".syscolumns c, "informix".systables t, "informix".systypes ty
        WHERE t.tabname = ? AND t.owner = ? AND t.tabtype = 'T'
          AND c.tabid = t.tabid AND ty.tabid = MOD(c.coltype, 256)
        ORDER BY c.colno
        """;

    /// <summary>未点所有者那一支：<c>t.owner = CURRENT USER</c> 而不是绑一颗 <c>NULL</c>
    /// （类注释第三条末说了为什么不走 <c>COALESCE</c>）。它与 <see cref="ColumnsSql"/> 只差
    /// <em>谓词的形状</em>，语义上两条形在同一次读里必须给出同一个答案，所以本文件把它们并排放，
    /// 改一条要看另一条 —— <c>OracleSchemaReader</c> 那一族用 <c>NVL(:owner, USER)</c> 把两支合在
    /// 一条里，那是具名形才有的奢侈。</summary>
    const string ColumnsSqlForCurrentUser = """
        SELECT c.colno, c.colname, ty.name, c.coltype, c.width
        FROM "informix".syscolumns c, "informix".systables t, "informix".systypes ty
        WHERE t.tabname = ? AND t.owner = CURRENT USER AND t.tabtype = 'T'
          AND c.tabid = t.tabid AND ty.tabid = MOD(c.coltype, 256)
        ORDER BY c.colno
        """;

    /// <summary>§10.4 的量纲探测：只读目录，不建临时表、不写数据（§9.5 那句"生产库常禁 DDL 权限"）。
    /// 它比 <see cref="ColumnsSql"/> 少读两颗字段（序号与 <c>coltype</c>）、少一道 <c>tabtype</c> 闸
    /// （一列之上不必再筛，而筛了更贵），剩下的两颗恰好是"量纲这一判"要的全部：型名认身份、
    /// <c>width</c> 是唯一那颗数。<b>两颗形各有自己的颗数</b>（点名的那条三颗、默认那条两颗，
    /// 顺序都是表名→〔所有者〕→列名），而位置形没有"跳过一位"这一手，所以调用点必须按
    /// <em>选了哪条文</em>决定绑几颗 —— 这一格比 <see cref="ColumnsSql"/> 那一格更容易错一处：
    /// 那里绑多了会报"参数过多"（驱动挡得往），这里若把列名绑到所有者位上，没人挡。</summary>
    const string ProbeSql = """
        SELECT ty.name, c.width
        FROM "informix".syscolumns c, "informix".systables t, "informix".systypes ty
        WHERE t.tabname = ? AND t.owner = ? AND c.colname = ?
          AND c.tabid = t.tabid AND ty.tabid = MOD(c.coltype, 256)
        """;

    /// <summary>探测的默认所有者那一支，与 <see cref="ColumnsSqlForCurrentUser"/> 同一对、同一理由。
    /// 这里<em>没有</em>把两条合并成一条 <c>COALESCE</c> 的冲动：上一段已经说了那颗 <c>NULL</c>
    /// 在位置形里推不出型，而探测这一格比列表那一格更容易撞上它（它每次读一列都带一颗可能为 null 的
    /// 所有者，而列名的位置在第三位 —— 顺序错一位的话 <c>COALESCE</c> 还会替我们兜住，位置形连这一样都不兜）。</summary>
    const string ProbeSqlForCurrentUser = """
        SELECT ty.name, c.width
        FROM "informix".syscolumns c, "informix".systables t, "informix".systypes ty
        WHERE t.tabname = ? AND t.owner = CURRENT USER AND c.colname = ?
          AND c.tabid = t.tabid AND ty.tabid = MOD(c.coltype, 256)
        """;

    public async Task<SchemaTable?> ReadTableAsync(DbConnection connection, TableInfo table, CancellationToken ct = default)
    {
        // "用哪条文"与"绑几颗"必须由同一个答案决定，所以这里先落一颗可空的 owner 而不是就地 if：
        // 那条 pattern 顺手把可空性交了 —— Fold 的签名不接受 null，这是六份 Fold 里唯一一颗要调用点自证的。
        var owner = table.Schema is { Length: > 0 } named ? named : null;

        using var command = Command(connection, owner is null ? ColumnsSqlForCurrentUser : ColumnsSql);

        // 顺序即语义（类注释第二条）：① 表名 ② 所有者（只有点名那一支才有这一颗）。
        command.Parameters.Add(Parameter(command, Fold(table.Name)));

        if (owner is not null)
            command.Parameters.Add(Parameter(command, Fold(owner)));

        var columns = new List<SchemaColumn>();

        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return null;

            do
            {
                var name = reader.GetString(1);
                var storeType = reader.GetString(2);
                // 两颗数字都走<b>强类型取数口</b>（<c>GetInt32</c>），与另五家在目录上同一颗读法：
                // <c>syscolumns.coltype</c> 与 <c>width</c> 在厂商的目录定义里都是 INT【文档·IBM】，
                // 而 §12.8 第 8 行那颗 <c>Convert.</c> 钉的正是"别顺手加一次运行期转换"——本家在这一格
                // 一颗不添。真库到手时若某一形交回的是 Int16，红的会是这一句而不是下游的判据。
                var colType = reader.GetInt32(3);
                var width = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4);

                // 两颗上限的<em>方向</em>是本文件与五族分岔最大的一格：这一家目录里那颗数的单位是
                // 字节（§10.4 六家里第一颗恒 Byte，出处见 Gbase8sTypes.GetLengthSemantics），
                // 所以填 MaxBytes 而 MaxChars 留 null（<see cref="MaxChars"/>）。另五家恰好相反 ——
                // 它们把字符数填在前一格，字节那一格要么另有字段、要么恒 null。
                // "这一列是不是可空"从 coltype 的那一位读（<see cref="IsNullable"/>），
                // 而"这一列是不是自增"读不出：它在这一家不在 coltype 上（类注释末段第 ② 条）。
                columns.Add(new SchemaColumn(name, StoreType(storeType, width),
                    MaxChars(storeType, width), width, IsNullable(colType), ReadOrdinal(reader)));
            }
            while (await reader.ReadAsync(ct).ConfigureAwait(false));
        }

        return new SchemaTable(table, columns);
    }

    public async Task<LengthSemanticsReading?> ProbeLengthSemanticsAsync(DbConnection connection, TableInfo table, string columnName, CancellationToken ct = default)
    {
        var owner = table.Schema is { Length: > 0 } named ? named : null;

        using var command = Command(connection, owner is null ? ProbeSqlForCurrentUser : ProbeSql);

        // 三颗的顺序即语义（类注释第二条）：① 表名 ② 所有者（可空，见 ReadTableAsync 那一格里
        // 为什么这里要先落一颗变量）③ 列名。
        command.Parameters.Add(Parameter(command, Fold(table.Name)));

        if (owner is not null)
            command.Parameters.Add(Parameter(command, Fold(owner)));

        command.Parameters.Add(Parameter(command, Fold(columnName)));

        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        var storeType = reader.GetString(0);
        var width = reader.IsDBNull(1) ? -1 : reader.GetInt32(1);

        // 与 Xugu 那一支同一条改判：只有"有宽度语义的那几型"且"目录真给了那颗数"的才报长度，
        // 其余一律 -1。上一族撞上的那颗是 DATETIME(3)（目录里那颗数是精度），本家撞上的会是
        // DECIMAL（那颗是位数）与 DATETIME/INTERVAL 两族（本家它们的全名里带字段范围，
        // 见 BaseTypeName），所以这一道闸在这一家不是抄来的形式。
        //
        // 三格各自的读法，逐格点名（本家是六家里唯一一处"填的是后那一格"的）：
        //   CollationName 给空串 —— 这一家的排序规则挂在目录的 collid 上而不是列上，本文件不 join
        //     那张表（它要的是"这一列的量纲"，那颗表答的是"这一列怎么比"），与 Xugu 那一格同一判据。
        //   DeclaredLength 与 DefinitionByteLength 在这里是同一个数，而它跟金仓/虚谷那个
        //     "两侧填同一个数"不是一件事：那两家填的是"字符数、第二个数问不出"，本家填的是
        //     "字节数、第一个数压根不存在"。两形同码而异义，所以 CharSemanticsProven 取 false ——
        //     那是这一格里唯一诚实的取值：它不是"没证实"，是"证实的是反方向"。
        var declared = IsText(storeType) && width > 0 ? width : -1;

        return new LengthSemanticsReading(string.Empty, declared, declared, false);
    }

    /// <summary>把目录给的两颗（型名、<c>width</c>）合成一份列定义字形。纪律与另五家同一条：
    /// <b>还回来的字形要与 <see cref="Gbase8sTypes.GetStoreType"/> 发出去的那一份同一</b>，
    /// 否则 <c>doctor</c> 里每颗字符列都是一条假红。逐支：
    /// <list type="bullet">
    /// <item>字符两支（<c>CHAR</c>/<c>VARCHAR</c>）还原宽度 —— 在这一家它是<em>字节</em>，
    /// 而建侧 <c>Gbase8sTypes.String</c> 写的也正是字节（"字符数 × 4"），两侧同量纲所以能对上。
    /// <b>这是本仓库第一次出现"读回的宽度与写出的宽度单位相同、而都不等于契约的字符数"</b>：
    /// 对账那一层要不要换算，是 §10.4 那颗缺口在本家的具体形状（另五家两侧同单位、同数值）。</item>
    /// <item>定点一支只还原位数：标度那颗字段不读（类注释末段第 ① 条），所以这里还回来的是
    /// <c>DECIMAL(10)</c> 而不是 <c>DECIMAL(10,2)</c>。也不写"猜一个 0" —— 那是把"问不出"写成"答案是零"。</item>
    /// <item>大对象两支（<c>CLOB</c>/<c>BLOB</c>）与其余所有型一律原样还型名：前者因为那颗数是库的
    /// 块上限而不是声明值，后者因为它们本来不带声明形。</item>
    /// </list>
    /// 一颗本家特有的：<c>SERIAL</c> 读不回 <c>SERIAL</c>（自增在默认值上，见类注释末段第 ② 条），
    /// 目录里那颗型名是 <c>INTEGER</c>，而本方法<em>不</em>把它折成 <c>SERIAL</c> —— 折了才是造假。
    /// 另两颗同义字的处理相反：<c>INT</c> 与 <c>INTEGER</c>、<c>INT8</c> 与 <c>BIGINT</c> 在本家
    /// 都是手册明写的同义字【文档·IBM】，本文件<em>不折</em>，因为建侧发的就是长形
    /// （<see cref="Gbase8sTypes.GetStoreType"/>），还回来一个 <c>INT</c> 才是那条假红的来源。</summary>
    static string StoreType(string storeType, int? width)
        => BaseTypeName(storeType) switch
        {
            "CHAR" when width > 0 => $"CHAR({width})",
            "VARCHAR" when width > 0 => $"VARCHAR({width})",
            "DECIMAL" when width > 0 => $"DECIMAL({width})",

            // 没有宽度语义的型，目录里那颗 width 可能是 0、可能压根不给：两种都不还原。
            _ => storeType,
        };

    /// <summary>字符上限在这一家<em>一律留 null</em>，因为目录里那颗数的单位是字节而不是字符
    /// （见 <see cref="ReadTableAsync"/> 里那一段），把它抄进字符那一格会让"契约说 200 字符、
    /// 目录说 800"被读成一条宽度差。<b>这与 <c>PostgreSqlSchemaReader</c>/<c>XuguSchemaReader</c>
    /// 那两颗恒 null 的 <c>MaxBytes</c> 是同一颗纪律的镜像</b>：那两家是"问不出第二个数所以留空"，
    /// 本家是"问不出第一个数所以留空"，两侧都不替库补一个换算。换算只在建侧
    /// <see cref="Gbase8sTypes.ApplyStorageDefaults"/> 发生一次，那是我们自己的声明而不是库的读数。
    /// 签名与 <see cref="StoreType"/> 逐颗对齐（那颗 <c>storeType</c> 在本家确实不参与判断，
    /// 留着是为了让"两颗上限出自同一份读法"在签名上看得出来 —— 与 <c>XuguSchemaReader.MaxBytes</c>
    /// 同一处取舍）。</summary>
    static int? MaxChars(string storeType, int? width) => null;

    /// <summary>可空性只看 <see cref="NullableBit"/> 那<em>一颗位</em>，而<em>不看</em>
    /// <see cref="BaseTypeMask"/> 那半（另一半见 <see cref="ColumnsSql"/> 的 join）。
    /// 这一格是六家里最省事的一颗：另五家要么读一颗 <c>NOT_NULL</c> 布尔、要么读一颗 <c>'Y'/'N'</c>
    /// 文本（还要接住三种取值形，见 <c>XuguSchemaReader.IsNullable</c> 那一段），本家它藏在型号里。
    /// <b>方向按"那位为真即可空"读，读反的后果是假的</b>：它会把可空列报成非空，
    /// 而 <c>doctor</c> 那层报的是"契约写得比库宽"这一类可指认的差，不像"表不存在"那样静默 ——
    /// 也就是说这一颗读反仍然会漏，只是漏得看得见。【文档·IBM】：这一颗位本次没有逐页回读，
    /// 拿到库时第一位就问它（与 <see cref="Gbase8sDialect"/> 类注释末段那颗"整层的零实测"同一个成本）。</summary>
    static bool IsNullable(int colType) => (colType & NullableBit) != 0;

    /// <summary>序号：<c>syscolumns.colno</c> 是<em>库给的</em>列序，本文件不改它。与
    /// <c>XuguSchemaReader</c> 那颗 <c>COL_NO</c> 同一判据（"折成 1 基就是造假，比对按名字不按它"），
    /// 而这一家比那一家多一颗可指认的疑点：<c>colno</c> 从 0 还是从 1 起，本次没有读定【文档·IBM，
    /// 未逐页回读】，所以这里的取值<em>不进入任何判据</em>，只作为"目录给的序号"原样带出去。
    /// 取数口与上面两颗数字同一颗（<c>GetInt32</c> 而不是 <c>Convert.ToInt32</c>，理由见
    /// <see cref="ReadTableAsync"/> 里那一段，以及 §12.8 第 8 行那颗 <c>Convert.</c> 钉）。</summary>
    static int? ReadOrdinal(DbDataReader reader) => reader.IsDBNull(0) ? null : reader.GetInt32(0);

    /// <summary>"有宽度语义的字符型"：<c>CHAR</c>、<c>VARCHAR</c>、<c>LVARCHAR</c>，而<b>不含</b>
    /// <c>CLOB</c>/<c>BLOB</c>。<b>三型里第三颗是这一家特有的</b>：另五家的字符型只有定长与变长两档，
    /// 本家有 <c>LVARCHAR</c> 那一档（它带宽度、不是大对象【文档·IBM】），不认它会让
    /// <c>doctor</c> 在 <c>LVARCHAR(2000)</c> 上报"这一列问不出量纲"。
    /// <c>CLOB</c> 这里<em>不</em>认，与 <c>XuguSchemaReader.IsText</c> 相反（那一家认 <c>CLOB</c>
    /// 是因为建侧会在超宽时发它）—— 本家建侧超宽发的也是 <c>CLOB</c>（<c>Gbase8sTypes.String</c>
    /// 无宽度那一支），所以这一颗是<b>本家与虚谷在同一判据上第一次取反</b>：<b>这一家的大对象
    /// 没有可报的声明宽度</b>（那颗 <c>width</c> 在 <c>CLOB</c> 上给的是库的块上限，不是用户写的数），
    /// 报出来是一条看着像答案的假数。方向与 <see cref="StoreType"/> 的第三支同一句。
    /// 与守卫那侧的关系也点一次：<see cref="Gbase8sChangeGuard"/> 关的是 <c>CLOB</c>/<c>BLOB</c>/
    /// <c>BYTE</c> 三颗<b>不可比</b>，这一颗判的是<b>可不可量</b>，两判在 <c>LVARCHAR</c> 上分岔
    /// （可比、也可量），在 <c>TEXT</c> 上也分岔（可比、不可量）—— 所以两处不共用一份名单。</summary>
    static bool IsText(string storeType)
        => BaseTypeName(storeType) is "CHAR" or "VARCHAR" or "LVARCHAR";

    /// <summary>型名的<em>基名</em>：切在第一个空格或括号之前。为什么要切 —— 本家的 <c>systypes.name</c>
    /// 对 <c>DATETIME</c> 与 <c>INTERVAL</c> 两族给的是<em>带字段范围</em>的全名
    /// （<c>DATETIME YEAR TO FRACTION</c>、<c>INTERVAL DAY TO SECOND</c> 那一形【文档·IBM】），
    /// 不切的话这两族在 <see cref="StoreType"/> 的 switch 里永不相认，于是 <c>doctor</c>
    /// 对每一颗时刻列都报一条"型不认识"。与 <c>Gbase8sChangeGuard.BaseType</c> 同一判据同一写法，
    /// 两处各写一遍：<b>provider 之内可以互引</b>（§2 禁的是跨 provider），而这一颗不值得为一个
    /// <c>IndexOfAny</c> 造一份公共物 —— 两处的<em>下游</em>不同（这里认型族、那里比大对象名），
    /// 抽出来只会把两颗不同的判据并成一颗看起来通用的。</summary>
    static string BaseTypeName(string storeType)
    {
        var cut = storeType.IndexOfAny(['(', ' ']);

        return (cut < 0 ? storeType : storeType[..cut]).Trim().ToUpperInvariant();
    }

    /// <summary>目录里的名字是<em>小写</em>形（本族不带引号折向小写），所以绑进去的也必须是那一形。
    /// <b>这一句在六家里是第一次方向朝下</b>：<c>OracleSchemaReader</c> 与 <c>XuguSchemaReader</c>
    /// 各有一颗做同一件事而还回来 <c>ToUpperInvariant</c>，判据完全相同（参数值不进 SQL 文本，
    /// 所以 <c>AppendIdentifier</c> 那条折叠路在这里够不着；漏掉这一步的后果是"这张表不存在"
    /// 而不是"读错了"）。
    /// <para>而本家这一颗比那两家多一层可说的：<b>它是六家里唯一一处"加引号与不加引号在目录侧同解"</b>
    /// —— 库里存的是小写，未加引号的写法折出来的也是小写，两者撞上了。于是 §9.2 那句
    /// "建出来的表与查的表必须是同一张"在本家对<em>未加引号</em>的建表也成立，而那一句在
    /// Oracle/虚谷两族只靠恒加引号成立。这一格是 <see cref="IdentifierPolicy.Gbase8s"/> 那条
    /// "<c>FoldToUpperCase: false</c> 在本家永远不会被走到"的<em>反面读数</em>：折叠规则确实不会被
    /// 发射层走到，但它会被目录读侧走到 —— 只是走的是本文件里手写的那一份，而不是那颗布尔。</para>
    /// <para><c>Schema</c> 为 null 那一支在这里<em>不</em>绑 <see cref="DBNull"/>：见类注释第三条末。
    /// 于是本方法是六份 <c>Fold</c> 里唯一一颗<b>不接受 null</b> 的 ——
    /// 调用点必须先看过 <c>table.Schema</c> 再决定绑不绑它，那两道 <c>if (hasOwner)</c> 不是防御，
    /// 是本颗签名的要求。</para></summary>
    static string Fold(string identifier) => identifier.ToLowerInvariant();

    static DbCommand Command(DbConnection connection, string sql)
    {
        var command = connection.CreateCommand();

        command.CommandText = sql;

        return command;
    }

    /// <summary>名字<em>不给</em>：<c>ParameterName</c> 留在驱动的默认值上，而文本里那一颗是裸问号。
    /// 这是五族那一手（"文本带前缀而对象名不带"）在本家的<em>退化形</em>：这里两侧都没有名字，
    /// 所以本方法比 <c>XuguSchemaReader.Parameter</c> 少一颗入参，而少的那一颗正是那一家用一整段
    /// 注释解释的"名字要摘前缀"。摘不动，因为压根没穿上。</summary>
    static DbParameter Parameter(DbCommand command, string value)
    {
        var parameter = command.CreateParameter();

        parameter.Value = value;

        return parameter;
    }
}
