using System.Data.Common;
using FastSql.Metadata;
using FastSql.Providers;

namespace FastSql.Providers.Sqlite;

/// <summary>从 <c>pragma_table_info</c> 这一张<em>表值函数</em>读结构（§4，第七家）。与六族同一纪律：
/// 这里只有 ADO.NET 抽象层的虚调用，本文件不认识任何驱动类型（§1.2 原则 3）。
///
/// <para><b>【本文件最重要的一条：本家的目录不认标识符，它认文本】</b>六族的目录是一张视图，
/// 表名与列名都是<em>谓词里的值</em>；本族的目录是一颗函数，<b>表名是它的参数</b>而列名仍然是谓词里的值。
/// 于是同一个文件里并存两种比法，而且两形的判据相反：
/// <list type="bullet">
/// <item>函数参数走引擎自己的名字解析 —— <b>大小写不敏感</b>：<c>pragma_table_info('R34')</c> 对声明成
/// <c>r34</c> 的表照样交回四颗列【实测·3404】，<c>xinfo('T41')</c> 同【实测·4113】。所以本文件<em>没有</em>
/// <c>Fold</c> 那颗：七家里三颗折方向的（Oracle 血统与虚谷折大写、GBase 8s 折小写）都在别家，
/// 本家与 T-SQL、PG 血统、MySQL 血统同边。</item>
/// <item>行集里的 <c>"name"</c> 是一颗普通文本列，等号比的是<b>字面</b>：<c>WHERE "name" = 'v'</c> 对声明成
/// <c>V</c> 的列交回 0 行【实测·4109】，而同一个 <c>v</c> 加 <c>COLLATE NOCASE</c> 就交回那一行
/// 【实测·4110】。所以 <see cref="ProbeSql"/> 里那一颗 <c>COLLATE NOCASE</c> 不是风格：它把列名的比法
/// 搬到与表名同一档，也就是<em>引擎自己解析标识符的那一档</em>。这一手有已知的边界，见下面第四条末。</item>
/// </list>
/// 这一格在六族没有对应物，因为六族的两处都是谓词、都比字面。本家的分裂是"目录是一段可调用代码"
/// 而不是"目录是一张表"带来的。</para>
///
/// <para><b>【"表不存在"与"这表没有列"在本家是同一份读数，而它不可利用】</b>函数对一张不存在的表
/// 交回<em>空集而不是报错</em>【实测·3402】，所以 <see cref="ReadTableAsync"/> 那条 <c>return null</c>
/// 字面上覆盖两种形状。另六家靠系统视图的谓词天然分辨（没有那一行就是没有那张表），本家分辨不了 ——
/// 除非再问一次 <c>pragma_table_list</c>。这里不补那一次问句，因为多出来的那一档压根不存在：
/// <c>CREATE TABLE t ()</c> 在本家是<b>语法错</b>【实测·3503】，任何一张表都至少有一颗列，
/// 所以"存在而读不到列"这一支是<em>空的一支</em>。这是本文件唯一一处"静默退化"的入口由库的语法关掉的，
/// 而它<em>不是</em>免费的：它把代价挪到了下面第二条那一格。</para>
///
/// <para><b>【真正的静默入口是名字里装点】</b>把 <c>main.w40</c> 这样的字形绑进函数参数，
/// 本家交回 0 行【实测·4007】，两点都绑也一样【实测·4008】，而<em>同一条表用数据侧去读是读得到的</em>
/// 【实测·4009 交回那一行的值】。也就是说：契约里的表名一旦自带点分，目录这一侧会安静地说"没有这张表"，
/// 而写与比对那一侧还在用它。这一格不补守卫（六族同样不补，且本仓库的 <see cref="TableInfo"/> 把
/// Schema 与 Name 拆成两颗、点分从来不是它们的合法形），记 §14。与之对照：数据侧对不存在的表是<b>响的</b>
/// （<c>no such table</c>【实测·3801】）而目录侧是静的（3402）—— 本家的"表存在性"这一问，
/// 只有目录这一条路会不吭声。</para>
///
/// <para><b>【列名那一颗 <c>COLLATE NOCASE</c> 的边界，量出来了】</b>NOCASE 只折 ASCII：
/// 声明成 <c>ä</c> 的列，用 <c>Ä</c> 去比在 <c>COLLATE BINARY</c> 与默认两形下都交回 0 行
/// 【实测·4111 ∶ 4111b】，用声明那一形才命中【实测·4112】；而库自己的大小写函数也是 ASCII-only
/// （<c>upper('ü') = 'Ü'</c> 为 0【实测·第4轮 111、112】）。所以本文件给列名的那一档是
/// "与引擎的标识符解析同宽"而<em>不</em>是"与非限定名在 SQL 里的解析完全一致"——
/// 后者的边界本次没读定【无出处】。这一格与 <c>SqliteChangeGuard</c> 那颗"CI 风险在本家为量出来的 0"
/// 共用同一颗事实（比较默认逐字节），一个在守卫的等号上，一个在目录的名字上。</para>
///
/// <para><b>【那道"表 ∶ 视图"的闸本家没有，而且不是漏了】</b><c>SqlServerSchemaReader</c> 的
/// <c>type = 'U'</c> 与 <c>Gbase8sSchemaReader</c> 的 <c>tabtype = 'T'</c> 挡的都是"一张与表同名的视图
/// 混进同一份读数"。本家这个形状<em>构造不出来</em>：视图已存在时建同名表报
/// <c>view v37 already exists</c>，反向报 <c>table s37 already exists</c>【实测·3701 ∶ 3702】——
/// 同一库里表与视图共用一个名字空间，所以那道闸后面没有要挡的东西。
/// 而补上它反而<em>亏</em>：要筛就得按名字去 <c>pragma_table_list</c> 里等值比那一列，
/// 那颗比较是大小写敏感的【实测·3608 用 <c>R36A</c> 交回 0 行】，于是换来一条假阴性（表在、读成不存在），
/// 而目录侧本来读得到（3404）。分辨表与视图的能力本家有、且量过（<c>table_list</c> 的 <c>type</c>
/// 那颗对 <c>v37</c> 给 <c>'view'</c>【实测·3713】，对 <c>r34</c> 给 <c>'table'</c>【实测·3407】），
/// 这里不用它，是本文件今天唯一一处"能问而不问"的取舍。</para>
///
/// <para><b>【未点 schema 这一支不是无害的默认值：它会换对象】</b>本家的"模式"是<em>数据库文件</em>
/// （一条连接一个 <c>main</c>，加上 <c>ATTACH</c> 进来的那些，§9.1 那一行第四列在本家的读法见
/// <c>SqliteConnectorFactory</c>）。要点有两颗，都在探针上量到：
/// <list type="bullet">
/// <item>函数的第二 arg 绑 <see cref="DBNull"/> 时，等价于<em>不点</em>而不等价于 <c>main</c>：
/// 对 <c>main</c> 里的表两形同解【实测·3504 ∶ 3405】，而对一张 temp 表，不点交回它、点 <c>main</c>
/// 交回 0 行【实测·3601 ∶ 3507b】。所以一份文本就够（另加一颗可空绑值），本家不需要 GBase 8s
/// 那种"每格发两条文"的手法 —— 那一手是位置形占位符的代价，本家有具名形（<see cref="SqliteDialect.ParameterPrefix"/>）。</item>
/// <item>更硬的一颗：<b>temp 库里的同名表遮住 <c>main</c> 的表，而且遮的不只是目录</b>。目录侧
/// 不点交回 temp 的三列、点 <c>main</c> 交回两列【实测·3704 ∶ 3705】；数据侧同样打到 temp ——
/// <c>SELECT b FROM s37</c>（<c>b</c> 只在 <c>main</c> 那张上）报 <c>no such column</c>
/// 【实测·3902】，未点的 <c>INSERT</c> 落进 temp【实测·3904 ∶ 3905 ∶ 3906 两张各一行】，
/// 而未点的 <c>DROP TABLE</c> 打掉的是 temp、<c>main</c> 那张活得好好的【实测·3909 ∶ 3910 ∶ 3911，
/// 同轮之后目录又交回 <c>main</c> 的两列【实测·3912】】。</item>
/// </list>
/// 后果落在本文件与发射层的关系上：<b>本家"建出来的表与查的表必须是同一张"（§9.2）这一条，
/// 靠的是读侧与写侧<em>用同一颗 <see cref="TableInfo.Schema"/></em>，而不是靠库的默认解析。
/// 谁要是替对方补一颗 <c>'main'</c>，另一侧没补，两边就在同一台库上指着两张不同的表，
/// 而且只有 temp 那一格会显形。<see cref="SqliteCodeFirst"/> 未给模式时发的是裸名，
/// 所以这里也发裸名（绑 <see cref="DBNull"/>）。</para>
///
/// <para><b>【四颗读不出的字段，逐颗给后果与出处】</b>本家的函数只有六颗字段
/// （<c>cid ∶ name ∶ type ∶ notnull ∶ dflt_value ∶ pk</c>【实测·3501 那一行打出的列名清单】，
/// 用 <c>pragma_table_xinfo</c> 才多第七颗 <c>hidden</c>【实测·3401 ∶ 3502】），
/// 六颗对着 <see cref="SchemaColumn"/> 的六个槽位，缺口如下：
/// <list type="bullet">
/// <item><b>宽度没有独立字段</b>：它只活在 <c>type</c> 那颗文本里（<c>VARCHAR(4)</c> 原样回显
/// 【实测·4003】，小写声明就回显小写【实测·4004】）。所以本文件的 <see cref="DeclaredChars"/> 是从文本里
/// <em>读</em>出那颗数，而不是从一颗数字字段里取 —— 七家里独一份。取出的数进 <c>MaxChars</c>，
/// 而 <c>MaxBytes</c> 恒 null（<see cref="MaxBytes"/>）。它参与不参与存储是另一件事，见末段。</item>
/// <item><b>排序规则不在列上</b>：本 build 里问不出"这一列用哪一颗排序规则"——
/// <c>pragma_collation_list</c> 存在而<em>只收 0 颗入参</em>（那颗报错把 arity 说了出来【实测·3509】），
/// <c>collation_list('t')</c> 那颗函数不存在【实测·3510】，<c>pragma_collations</c> 那张表不存在【实测·3511】。
/// 一颗列上的 <c>COLLATE NOCASE</c> 只在<em>建表文本</em>里看得见【实测·3502（pragma 只给 <c>TEXT</c>）
/// ∶ 3512（文本里带着 <c>COLLATE NOCASE</c>）】。于是 <c>CollationName</c> 在本家恒空串，
/// 而 <c>SqliteChangeGuard</c> 讨论的那颗"某列声明成 NOCASE 就变 CI"的风险，<b>读侧看不见</b>。
/// 本文件不解析那段文本：它要的是列的形状，而解析别人的 DDL 是另一件事（记 §14）。</item>
/// <item><b>自增读不出</b>：<c>INTEGER PRIMARY KEY AUTOINCREMENT</c> 在目录里是
/// <c>type='INTEGER'</c>、<c>pk=1</c>、<c>dflt_value</c> 为空【实测·3401 第一行】，
/// "会自增"那一事实只存在于建表文本里【实测·3408】。而 <see cref="SchemaColumn"/> 没有承接默认值或文本的槽位
/// —— 与 <c>Gbase8sSchemaReader</c> 那颗 <c>SERIAL</c> 同一格缺口而<b>不同因</b>：那一家把值挂在默认字段上，
/// 本家压根没把这件事写成任何一颗字段值。</item>
/// <item><b>生成列读不到（这是选择，不是缺口）</b>：同一个库上同一张表，<c>table_info</c> 交回四列而
/// <c>table_xinfo</c> 交回五列，多出来的正是那颗 <c>GENERATED ALWAYS AS</c> 的列【实测·3501 ∶ 3401】。
/// 本文件选前者，理由是 <c>hidden</c> 那颗今天只量到过 <c>0</c> 与 <c>2</c> 两值【实测·3401】，
/// 第三值长什么样没读定【无出处】，而"要不要把一列挡在读数外"这件事交给<em>库自己的定义</em>
/// 比交给我们没读全的一颗位更安全。代价逐颗点名：<c>doctor</c> 在本家看不见生成列，
/// 所以"契约里多写了一颗生成列"这一条差异读不出来（记 §14，与虚谷那颗 <c>IS_SERIAL</c>、
/// GBase 那颗标度同一档）。</item>
/// </list>
/// 还有一颗是<b>不该读</b>的：<c>dflt_value</c>。它在驱动的行集元数据里报 <c>Byte[]</c>
/// 【实测·4102 那一颗字段的 <c>GetFieldType</c>】而在 SQL 层是文本（<c>typeof</c> 交回 <c>text</c>、
/// 值连引号一起回显【实测·4114】）。两形自相矛盾，而本仓库不需要它的任何一种读法 ——
/// <see cref="SchemaColumn"/> 无槽位，所以本文件一颗不取。</para>
///
/// <para><b>【那颗声明宽度到底意味着什么：三档"问不出"在本家是第四种来路】</b>本仓库 §10.4 那张表里
/// 金仓与虚谷是"问不出第二个数"、GBase 8s 是"第一个数压根不存在而第二个数是字节"、本家是
/// <b>两个数都不参与存储</b>：<c>VARCHAR(4)</c> 装进 20 个字符【实测·4001 ∶ 4002 交回 <c>20</c>】、
/// <c>CHAR(2)</c> 装进 10 个【实测·4001 ∶ 4002 交回 <c>10</c>】、<c>NVARCHAR(6)</c> 装进 10 个
/// 【实测·4006】，而 <c>NUMERIC(10,2)</c> 收一句文本并原样留着（<c>typeof</c> 为 <c>text</c>
/// 【实测·4002】）。也就是说声明的那颗数<em>没有任何一环会去用它</em>，于是"它是字符数还是字节数"
/// 这一问在本家<b>连可以观察的行为都没有</b> —— 正也证不成、反也证不成。<see cref="LengthSemanticsReading"/>
/// 的 <c>CharSemanticsProven</c> 因此取 <c>false</c>，而两颗长度填同一个数：那个数是<em>我们自己写进 DDL 的那一颗</em>
/// （<see cref="SqliteTypes.GetStoreType"/> 的 <c>string</c> 分支发 <c>VARCHAR(n)</c>，目录原样还）。
/// 这里要说准它有什么价值：它让 <c>doctor</c> 能比"契约说 200 ∶ 库声明 200"这一对<em>自述</em>，
/// 比不了"库容得下 200" —— 后者在本家不是一个事实。</para>
///
/// <para>取数口纪律与六家同一条：<see cref="ReadTableAsync"/> 用强类型口而不 <c>Convert.</c>
/// （§12.8 第 8 行那颗钉）。本家的数字字段在驱动里是 <c>Int64</c>【实测·4102 的运行时型清单】，
/// 而那颗 <c>GetInt32</c> 在 <c>Int64</c> 列上<em>过</em>【实测·4103 ∶ 4104 ∶ 4107 三形都交回数】，
/// 所以本文件与六家写同一颗 <c>GetInt32</c>。同一条列上 <c>GetBoolean</c> 也可用【实测·4106】而这里不选：
/// <c>notnull</c> 那一颗除了 0 与 1 还会不会是别的值，本次没读定【无出处】，
/// 而 <c>== 0</c> 与非零判定在 <c>GetInt32</c> 上是同一件事、在 <c>GetBoolean</c> 上不是。</para></summary>
public sealed class SqliteSchemaReader : ISchemaReader
{
    /// <summary>列表那一格。<b>一颗绑值两种语义</b>：<c>@tbl</c> 进函数参数（标识符解析，大小写不敏感，
    /// 3404），<c>@sch</c> 绑 <see cref="DBNull"/> 时等于"不点"（3504 ∶ 3601，与类注释第五条）。
    /// <c>ORDER BY "cid"</c> 那颗不可换成 <c>"pk"</c>：复合键上两序不同解——声明顺序是
    /// <c>B, A, C</c> 而 <c>pk</c> 给 <c>2, 1, 0</c>【实测·3604】，按 <c>pk</c> 排会把契约的列序读乱。
    /// 这里<em>没有</em> <c>tabtype</c> 那道闸，理由与读数见类注释第四条。</summary>
    const string ColumnsSql = """
        SELECT "cid", "name", "type", "notnull"
        FROM pragma_table_info(@tbl, @sch)
        ORDER BY "cid"
        """;

    /// <summary>量纲探测那一格：只取 <c>type</c> 一颗（本家的宽度只活在文本里，4003），
    /// 比 <see cref="ColumnsSql"/> 少三颗字段、少一道排序。
    /// <b><c>COLLATE NOCASE</c> 那一个是本文件唯一一颗"改比较规则"的加法</b>，它的来路与边界都在
    /// 类注释第一、四条：没有它，契约里写 <c>name</c> 而库声明 <c>NAME</c> 的一颗列会读成"问不出"
    /// （4109）；有了它，非 ASCII 的名字仍然读成"问不出"（4111 ∶ 4111b）。
    /// 与 <see cref="ColumnsSql"/> 同一颗表名解析，所以两处都吃同一份 <c>(@tbl, @sch)</c> 绑值。</summary>
    const string ProbeSql = """
        SELECT "type"
        FROM pragma_table_info(@tbl, @sch)
        WHERE "name" = @col COLLATE NOCASE
        """;

    public async Task<SchemaTable?> ReadTableAsync(DbConnection connection, TableInfo table, CancellationToken ct = default)
    {
        // 空串当"未点"处理，不绑进去。这颗与 Gbase8sSchemaReader 里同形的 <c>is { Length: > 0 }</c>
        // 是两种来路：那一家是因为位置形 <c>?</c> 推不出型，本家是因为空串点了一颗<em>存在的问句</em>——
        // pragma_table_info('t', '') 报 unknown database ''【实测·3505】，它不静默。
        var schema = table.Schema is { Length: > 0 } named ? named : null;

        using var command = Command(connection, ColumnsSql);

        command.Parameters.Add(Parameter(command, "@tbl", table.Name));
        command.Parameters.Add(Parameter(command, "@sch", schema));

        var columns = new List<SchemaColumn>();

        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return null;

            do
            {
                var name = reader.GetString(1);
                var declaredType = reader.GetString(2);

                // 两颗数字都走 GetInt32（4103 ∶ 4104 那两条读数：本驱动在 Int64 列上接受它）。
                // notnull 的方向按"那位为 0 才可空"读【实测·3501 四行 ∶ 3603 ∶ 3605 三形给的 0/1】，
                // 而读它之前要知道一件事：库<em>报的是声明形状而不是强制形状</em>——
                // INTEGER PRIMARY KEY 不带 NOT NULL 时这里给 0【实测·3603】，
                // 而同一个约束在 WITHOUT ROWID 表上给 1【实测·3605】，两形都原样带出去、一折不折。
                // 序号取 cid 而不取 pk（3604 那一对读数是本方法唯一的理由）。
                columns.Add(new SchemaColumn(name, declaredType, DeclaredChars(declaredType), MaxBytes(declaredType),
                    reader.GetInt32(3) == 0, reader.GetInt32(0)));
            }
            while (await reader.ReadAsync(ct).ConfigureAwait(false));
        }

        return new SchemaTable(table, columns);
    }

    public async Task<LengthSemanticsReading?> ProbeLengthSemanticsAsync(DbConnection connection, TableInfo table, string columnName, CancellationToken ct = default)
    {
        var schema = table.Schema is { Length: > 0 } named ? named : null;

        using var command = Command(connection, ProbeSql);

        command.Parameters.Add(Parameter(command, "@tbl", table.Name));
        command.Parameters.Add(Parameter(command, "@sch", schema));
        command.Parameters.Add(Parameter(command, "@col", columnName));

        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        // 三格各自的读法（本家与 GBase 8s 落在同一档而不同因，见类注释末段）：
        //   CollationName 给空串 —— 这一列的排序规则不在目录的字段里（3509 ∶ 3510 ∶ 3511 三问都不给）。
        //   两颗长度填同一个数，而它是我们从 DDL 里读回来的自述，不是库的容量（4001 ∶ 4002）。
        //   CharSemanticsProven 取 false：没有任何一环用这颗数，所以正反都无从观察。
        var declared = DeclaredChars(reader.GetString(0)) ?? -1;

        return new LengthSemanticsReading(string.Empty, declared, declared, false);
    }

    /// <summary>声明宽度：<b>从型名文本里读出来的一颗数</b>，七家里只有本家这么办（另六家各有自己的
    /// 数字字段，见 <c>Gbase8sSchemaReader</c> 的 <c>width</c> 与 <c>PostgreSqlSchemaReader</c> 那一对）。
    /// 判据两处：基名含 <c>CHAR</c>（本家自己发的只有 <c>VARCHAR(n)</c> 这一形带宽度【实测·4003】，
    /// 而 <c>TEXT</c> 那一形不带【实测·3502 ∶ 4005】—— 用 <c>CHAR</c> 收尾判会把 <c>CHARACTER VARYING(50)</c>
    /// 认进来而把 <c>NUMERIC(10,2)</c> 挡在外面【实测·4003 第三行】），以及括号里恰好是一颗整数
    /// （两颗数的那一形是精度与标度，不是宽度）。<b>读不出就交回 null，不猜</b>：型名是空串时
    /// （无声明形的列【实测·4005】与库自己的表【实测·3810】两形都给 <c>''</c>）走的是同一条出口。
    /// 与 <see cref="ProbeLengthSemanticsAsync"/> 共用本方法，所以两处那颗数的读法不可能分岔。</summary>
    static int? DeclaredChars(string declaredType)
    {
        if (!BaseTypeName(declaredType).Contains("CHAR", StringComparison.Ordinal))
            return null;

        var open = declaredType.IndexOf('(');
        var close = open < 0 ? -1 : declaredType.IndexOf(')', open + 1);

        if (close < 0)
            return null;

        return int.TryParse(declaredType.AsSpan(open + 1, close - open - 1), out var chars) && chars > 0
            ? chars
            : null;
    }

    /// <summary>字节上限在这一家<b>恒 null</b>：目录里没有第二个数，而声明的那一颗也不参与存储
    /// （4001 ∶ 4002）。<b>这与 <c>Gbase8sSchemaReader.MaxChars</c> 那颗恒 null 是同一颗纪律的两面</b>——
    /// 那一家问得出字节而问不出字符，本家两问都问不出，两侧都不替库补一个换算。
    /// 签名与 <see cref="DeclaredChars"/> 逐颗对齐，理由与那一家同一句（让"两颗上限出自同一份读法"
    /// 在签名上看得出来）。</summary>
    static int? MaxBytes(string declaredType) => null;

    /// <summary>型名的<em>基名</em>：切在第一个空格或括号之前，折成大写。折大写<em>只服务本文件的判断</em>
    /// （<see cref="DeclaredChars"/> 的那一颗 <c>CHAR</c>），不作用于交回的读数 —— 交回的 <c>StoreType</c>
    /// 是目录原样给的那串【实测·4004：小写声明回显小写】。为什么必须切：本家的声明形带括号
    /// （<c>VARCHAR(4)</c>）也可能带空格（<c>CHARACTER VARYING(50)</c>），不切则第二形永不相认。
    /// 为什么必须折：<c>Contains</c> 走 <c>Ordinal</c> 而声明侧的大小写由建表的人决定（4004 那一行就是
    /// 照抄小写），本家的 <see cref="IdentifierPolicy"/> 恒加引号、不折列名，所以读侧要自己折一次。
    /// 与 <c>Gbase8sSchemaReader.BaseTypeName</c> 同一判据同一写法，两处各写一遍（§2 禁的是跨 provider，
    /// 而这一颗不值得为一次 <c>IndexOfAny</c> 造公共物）。</summary>
    static string BaseTypeName(string declaredType)
    {
        var cut = declaredType.IndexOfAny(['(', ' ']);

        return (cut < 0 ? declaredType : declaredType[..cut]).Trim().ToUpperInvariant();
    }

    static DbCommand Command(DbConnection connection, string sql)
    {
        var command = connection.CreateCommand();

        command.CommandText = sql;

        return command;
    }

    /// <summary>名字<em>要给</em>，而且带前缀给：本驱动对没有名字的参数是直接拒的
    /// （<c>ParameterName must be set.</c>【实测·225】），而探针里每一颗绑值都是带 <c>@</c> 交进去的
    /// 且全部命中（3401 起）。这与另六家那一手（文本带前缀而对象名不带）不同形：
    /// 本家不需要"摘前缀"那颗口子，但需要"给名字"这一颗动作——两颗都是驱动侧的规定，
    /// 而 <c>AdoExecutor.Bind</c> 那条路走的是命令对象的默认名，本文件的参数名必须与 SQL 文本里
    /// 那三颗一一对上（<c>@tbl ∶ @sch ∶ @col</c>），<b>这一处写错是响的而 GBase 8s 那一处是静的</b>：
    /// 具名形绑错名字会报缺参数，位置形只会把值放错位。</summary>
    static DbParameter Parameter(DbCommand command, string name, string? value)
    {
        var parameter = command.CreateParameter();

        parameter.ParameterName = name;
        parameter.Value = (object?)value ?? DBNull.Value;

        return parameter;
    }
}
