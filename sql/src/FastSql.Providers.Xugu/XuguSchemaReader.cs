using System.Data.Common;
using FastSql.Metadata;
using FastSql.Providers;

namespace FastSql.Providers.Xugu;

/// <summary>从 <c>ALL_COLUMNS</c> 读结构（§4 里差异最大的那一格，第五家）。与四族同一纪律：
/// 这里只有 ADO.NET 抽象层的虚调用，本文件不认识任何驱动类型（§1.2 原则 3）。
///
/// <para><b>【v0.32 第二格改判】本文件的每一条 SQL 现在都在一台活库上跑过了</b>。初稿那一句
/// "整份读法没有在一台活库上跑过（现场没有授权的虚谷实例）"到本版不再成立：出处是三份日志（同一台
/// <c>XuguDB 12.0.0</c>，下面每颗判断旁边点名其中的行）—— <c>obj/logs/v32-recon-meta-1.log</c>
/// 是先跑的一轮目录问答加上<em>改前</em>那份读侧快照，<c>obj/logs/v32-recon-meta2-1.log</c> 是为
/// "19 型对照表"专建的一张表上逐项问来的取值形，<c>obj/logs/v32-recon-meta-2.log</c> 是本文件
/// 改完之后<em>原地重跑同一轮问答</em>留下的改后快照（M-20 那八行、M-21 那五行）。第三份的存在理由：
/// 判断改过了就要让库再答一次，而不是让注释自己说"现在对了" —— §12.8 那条"文案改过而数字没重跑，
/// 那一版就不算交完账"反过来同样成立。仍然成立的是初稿那半句的另一半：
/// 跑过<em>这一份</em>读法不等于这一家的目录被读完了 —— <c>ALL_COLUMNS</c> 有 27 颗字段（S-09 数的），
/// 本文件只读其中六颗，而没读的那 21 颗里至少有两颗（<c>IS_SERIAL</c>、<c>COMMENTS</c>）是 §9.5
/// 的 <c>doctor</c> 会用得上的，缺口记在下面第三条 bullet 末与 §14。</para>
///
/// <para>与初稿的定位最要紧的一处差别：<b>初稿的字段名与 join 形状是"半份产物 + 三族同形推断"拼出来的，
/// 而拼出来的那一份里有两颗字段名不存在、两颗挂在了没有它们的表上</b>。活库给的是：<c>ALL_COLUMNS</c>
/// 没有 <c>COLUMN_NAME</c>（那颗叫 <c>COL_NAME</c>）、没有 <c>DATA_TYPE</c>（叫 <c>TYPE_NAME</c>）、也<em>没有</em>
/// <c>OWNER</c> 与 <c>TABLE_NAME</c>（E10049 字段表达式非法，K-02/K-15），它是按 <c>TABLE_ID</c> 挂在
/// <c>ALL_TABLES</c> 上的 —— 所以初稿那条"本文件不 join <c>ALL_TABLES</c>"的取舍被库推翻了：
/// <b>不 join 就连表名都问不到</b>。初稿从产物里抄到的那三颗表名（<c>ALL_COLUMNS</c>/<c>ALL_TABLES</c>/
/// <c>ALL_SCHEMAS</c>）与那几颗列名（<c>COL_NO</c>、<c>NOT_NULL</c>、<c>COMMENTS</c>、<c>IS_SERIAL</c>）
/// 则逐颗印上了【产物】那一档，而 <c>ALL_SCHEMAS</c> 那一颗本版<em>真的</em> join 上了（第二条 bullet）。</para>
///
/// <para>初稿那三处"与另三家不同形"到本版的判决，逐条标出处：
/// <list type="bullet">
/// <item><b>目录里存的是折叠后的大写</b>【产物→<b>实测</b>】：与 <c>OracleSchemaReader</c> 同一条纪律，绑进去的值要
/// <em>自己折</em>（<see cref="Fold"/>），因为参数值不进 SQL 文本、<c>AppendIdentifier</c> 那条折叠路在这里够不着。
/// 漏掉这一步的后果也同 §9.2 那句"查询 100% 报表不存在"，只是发生在 <c>doctor</c> 侧：读成"这张表没有列"。
/// 两半现在都量到了：不带引号建的 <c>FastSqlRecon33</c> 在目录里是 <c>"FASTSQLRECON33"</c>（K-07），
/// 而<em>模式名不折成大写就直接查不到</em>（M2-07 交回 0 行，同一句只把名字换成 <c>SYSDBA</c> 交回 19 行）。
/// <b>而这一家比 Oracle 那一族多一层</b>：那颗折叠是 <c>COMPATIBLE_MODE</c> 决定的（见
/// <see cref="IdentifierPolicy.Xugu"/>），所以在 POSTGRESQL 兼容档上目录里是小写 —— 这一层本次<em>没量过</em>，
/// 而本版把"没量过"升级成了"<b>在这一台上问不出</b>"【实测·不可问】：五颗候选读法逐颗发过
/// （<c>all_variables</c> / <c>props</c> / <c>all_props</c> / <c>all_db_info</c> / <c>CHARSET()</c>，
/// N-01c…f 在 <c>obj/logs/v32-recon-cjk-utf8-2.log</c>）全部报"表或视图不存在 / 字段函数不存在"，
/// 而唯一在场的那一颗 <c>all_databases</c> 交出 32 颗字段里没有兼容位（有 <c>CHAR_SET="GBK"</c>、
/// <c>TIME_ZONE="GMT+08:00"</c>，见同一跑的 N-01b）。它仍然是【推断】，而这一版的措辞更准了：
/// 在那一档上 <b>本文件读成"这张表不存在"</b>，是读不到而不是读错，与初稿同一方向。
/// 现场要问这一格得换路子（厂商工具或配置文件），那一步不在 §13.17 授权的只读范围内。</item>
/// <item><b>模式为 null 说的是"当前会话用户"</b>【推断→<b>实测，但落点换了三处</b>】：初稿写的是
/// <c>c.owner = NVL(:owner, USER)</c>，而那一颗 <c>owner</c> 字段在这一家<em>压根不存在</em> ——
/// 所以它不是"写法不同"而是引用了不存在的字段。实测的形状分三颗：<c>NVL</c> 带一颗绑定 <c>NULL</c>
/// 在这家好用（M2-04 与 M2-05 两支各 19 行）、<c>USER</c> 那颗会话函数能进 <c>WHERE</c>（M-07），
/// <c>ALL_TABLES.SCHEMA_ID</c> 与 <c>ALL_SCHEMAS.SCHEMA_ID</c> 对得上而名字在 <c>ALL_SCHEMAS.SCHEMA_NAME</c>
/// 那一颗（M-00/M-05b）。三颗合起来就是下面 <see cref="ColumnsSql"/> 里那道闸，而它<em>真的在挡</em>：
/// 模式名给一个不存在的，同一句交回 0 行（M2-06 —— 那一句是这道闸唯一的判据）。默认那一支也与建侧核过了：
/// <c>XuguCodeFirst</c> 不带模式建出去的 <c>FASTSQLORDER</c>，落在 <c>SCHEMA_NAME = 'SYSDBA' = USER</c>
/// 那一颗模式下（M-05b）。初稿那句"错了的后果是读不到而不是读错"到本版<em>仍然成立，而已经被排掉</em>。</item>
/// <item><b>宽度、数值精度与时刻精度共用一颗字段，而且要拆包</b>【产物→<b>实测，而初稿的拆法读反了</b>】：
/// 打包是<em>真的</em>，可重数的三颗点在 <c>obj/logs/v32-recon-meta2-1.log</c> 的 M2-01：
/// <c>NUMERIC(18,4)</c> → <c>SCALE=1179652</c>、<c>NUMERIC(20,0)</c> → <c>1310720</c>、
/// <c>NUMERIC(5,2)</c> → <c>327682</c>，也就是 <c>SCALE = 精度 × 65536 + 标度</c>。初稿 <see cref="Unpack"/>
/// 把两支<em>对调</em>了（它给 <c>NUMERIC(18,4)</c> 还回 <c>NUMERIC(4,18)</c>；改前的快照在 meta-1 的
/// M-20 <c>ORD=3</c>（<c>AMOUNT</c>）那一行与 meta2-1 的 M2-20 <c>ORD=6</c>（<c>C_DEC18</c>），
/// 改后同一个 <c>AMOUNT</c> 列在 <c>obj/logs/v32-recon-meta-2.log</c> 的 M-20 <c>ORD=3</c> 读回
/// <c>NUMERIC(18,4)</c>），本版按实测改回。<b>而"要不要打包"这一问已经不再需要猜</b>：型名那颗本身就说了
/// 是不是数值型，所以下面按 <c>NUMERIC</c>/<c>DECIMAL</c> 拆、按其余型名直读，那颗阈值只剩
/// "拆包的基"这一个身份（<see cref="PackedBase"/>）。初稿那句"厂商文档说宽度上限 60000 而阈值是 65536，
/// 两数相邻可能只是巧合"也一并改判：<b>65536 是基，不是巧合</b>，两支不撞是构造出来的必然 ——
/// 那条"读出来的吻合"现在是读出来的<em>解释</em>。
/// <b>产物那份查询里还有的 <c>IS_SERIAL</c> 与 <c>COMMENTS</c> 两颗这里仍然不读</b> —— 不是漏，是
/// <see cref="SchemaColumn"/> 这一份 record 没有承接它们的槽位（它只有名字、型、两个上限、可空性与序号）。
/// 两颗的<em>取值形</em>本版量到了（M2-03 那 19 行：布尔 <c>IS_SERIAL=False</c> 与可写文本的
/// <c>COMMENTS=NULL</c>），于是 <c>doctor</c> 在这一家读不出"这一列是不是自增"与"库里那一列写着什么注释"，
/// 而后者恰好是 §9.5 那条"契约与库对账"里唯一一处注释能落地的地方 —— 两颗一起记 §14，
/// 缺口现在带得上那一句"库侧读得出、我们侧没槽位"。</item>
/// </list></para>
///
/// <para>【v0.32 第二格新增，初稿没有这一条】<b>这一家的目录里 <c>VARCHAR</c>、<c>TEXT</c>、<c>CHAR</c>
/// 三型都存成同一个型名 <c>"CHAR"</c></b>（M2-01 那 19 行里的第 9/10/11 三行），分开它们的不是型名而是
/// <c>VARYING</c> 那颗布尔与 <c>SCALE</c> 里有没有宽度：<c>VARCHAR(9)</c> 是 <c>CHAR</c>+<c>True</c>+9、
/// <c>CHAR(7)</c> 是 <c>CHAR</c>+<c>False</c>+7、<c>TEXT</c> 是 <c>CHAR</c>+<c>True</c>+<c>-1</c>。
/// 三处后果一起记在这里：① <c>VARYING</c> 是这颗解析器的关键字，<em>不带引号读不出来</em>
/// （E19260 unexpected VARYING，meta-1 的 M-02），要写成 <c>c."VARYING"</c>（M2-01/M2-02 两绿）；
/// ② <c>TEXT</c> 与"不写宽度的 <c>VARCHAR</c>"在这一家的目录里<em>是同一个东西</em>，本文件按建侧
/// 那一支回 <c>TEXT</c>（<c>XuguTypes.GetStoreType</c> 给无宽度的 <c>string</c> 发的就是 <c>TEXT</c>），
/// 这一格是<em>选出来的</em>不是读出来的，点名在 <see cref="StoreType"/> 的注释里；
/// ③ 初稿"目录里只有一个宽度"那一句现在要说得更准：<b>只有一个<em>数</em>，而它有三个身份</b>
/// （宽度 / 打包过的精度∶标度 / 时刻精度），判身份靠型名。</para>
///
/// <para>还有一颗同批量到的同义字：<c>INT</c> 与 <c>TIMESTAMP(3)</c> 建出去，目录里回来的是
/// <c>INTEGER</c> 与 <c>DATETIME</c>（M2-01 第 2/16 行）。前者本文件折回 <c>INT</c>（与
/// <c>PostgreSqlSchemaReader</c> 把 <c>character varying</c> 折回 <c>varchar</c> 同一条纪律：
/// 读侧还回来的字形要与建侧发出去的那一份同一，否则 §9.5 的比对里每颗 <c>INT</c> 列都是一条假红）；
/// 后者<em>不折</em>，因为"它是不是 <c>DATETIME</c> 的同义字"只有 M2-01 这一颗样本说了话，而把它写成
/// 一条映射就是替库做主 —— 后果是别人手工建的 <c>TIMESTAMP</c> 列在 <c>doctor</c> 里报成
/// <c>DATETIME</c>，一条可指认、方向已知的差，记 §14。</para>
///
/// <para>与 §10.4 那一格直接相关的结论【本版按实测收窄】：<b>这一族的目录里问不出第二个数（字节）</b>，
/// 所以量纲探测在这里是退化实现（<see cref="ProbeLengthSemanticsAsync"/>），这一家与金仓同档而不是与
/// Oracle 血统同档 —— 三档在这一格上现在是 <b>2 家两把尺子都从目录里读</b>（Oracle 血统靠 <c>CHAR_USED</c>
/// 那一颗字，MySQL 血统靠同一行的 <c>CHARACTER_OCTET_LENGTH</c>）∶ <b>1 家</b>要靠类型名与排序规则推断（T-SQL）
/// ∶ <b>2 家</b>压根问不出第二个数（金仓、这一家）—— <b>2 ∶ 1 ∶ 2 加满五家</b>。
/// <b>MySQL 血统那颗"行级预算"不算第四档</b>：它是同一张视图里多出来的<em>另一个问题</em>
/// （一行之和而不是一列之上限），在"量纲的出处"这一格上它站在"两颗都摆着"那一边 ——
/// 本条初稿把它单列成一档，那是把"多一颗数"读成了"少一颗数"，本版交账时改掉。
/// 而"问不出字节"这一格现在多了一颗实测的旁证：<c>LENGTHB('甲公司')=9</c> 对 <c>LENGTH('甲公司')=5</c>
/// （<c>obj/logs/v32-recon-funcs-2.log</c> 的 F-10/F-11）—— 这一家<em>有</em>字节这把尺子，只是它量的
/// 是<em>值</em>而不是<em>列</em>，所以列上的第二个数仍然读不出来。</para></summary>
public sealed class XuguSchemaReader : ISchemaReader
{
    /// <summary>数值列那颗共用字段的<em>拆包基</em>：<c>SCALE = 精度 × 65536 + 标度</c>。
    /// 【v0.32 第二格实测】三颗可重数的点：1179652 = 18×65536+4、1310720 = 20×65536+0、
    /// 327682 = 5×65536+2（<c>obj/logs/v32-recon-meta2-1.log</c> 的 M2-01/M2-09）。
    ///
    /// <para>初稿这一颗叫 <c>PackedThreshold</c>，出处是"产物那份表里被打包过的那一颗明显大于任何宽度值，
    /// 而两者之间的空档没有解释"，并特意记下"厂商文档给的列宽上限是 60000【文档】而阈值取 65536
    /// （2 的 16 次方），两数相邻可能只是巧合"。本版那个"巧合"被解释掉了：65536 就是<em>基</em>，
    /// 空档是构造出来的必然 —— 宽度 ≤ 60000 落在非打包那一支，任何精度 ≥ 1 的数值列 ≥ 65536 落在打包那一支。
    /// 名字本版改成 <c>PackedBase</c>：一颗阈值与一个进制在数值上相同，而<b>只有后者要求 <c>%</c>
    /// 也参与读法</b> —— 初稿读反正是这一步没做到（它把余数给了精度、把商给了标度）。</para>
    ///
    /// <para>本版之后它只剩一个用途：<see cref="Unpack"/> 只在 <c>NUMERIC</c>/<c>DECIMAL</c> 两支里被查，
    /// 而查它是为了<em>挡一件库没说过不会发生的事</em>（万一某一档 <c>NUMERIC</c> 存的是未打包的标度）。
    /// 落空的后果可指认：那一颗数值列的精度读不出来，<c>doctor</c> 报"目录说 NUMERIC"。</para>
    const int PackedBase = 65536;

    /// <summary>【v0.32 第二格整条改写】这一条 SQL 的<b>四颗字段名里有两颗不存在、两处形状不对</b>，
    /// 而它此前唯一的出处是"半份产物 + 三族同形推断"。活库量到的 <c>ALL_COLUMNS</c> 是 27 颗字段（S-09），其中：
    /// <list type="bullet">
    /// <item>没有 <c>COLUMN_NAME</c>，那一颗叫 <c>COL_NAME</c>（E10049 字段表达式非法）；</item>
    /// <item>没有 <c>DATA_TYPE</c>，型名那颗叫 <c>TYPE_NAME</c>（同一条报错）；</item>
    /// <item>没有 <c>OWNER</c> 也没有 <c>TABLE_NAME</c> —— 这一张视图按 <c>TABLE_ID</c> 挂在
    /// <c>ALL_TABLES</c> 上，模式那颗在 <c>ALL_TABLES</c> 这边是 <c>SCHEMA_ID</c>/<c>USER_ID</c> 两颗
    /// <em>数字</em>（M-05），名字在 <c>ALL_SCHEMAS.SCHEMA_NAME</c>（M-00）。类注释初稿写"本文件不 join
    /// <c>ALL_TABLES</c> 与 <c>ALL_SCHEMAS</c>"（理由是"join 只会把'表不存在'与'这张表没有列'混成同一个
    /// null 答案"），那个取舍今天被库推翻了：<b>不 join 这两张就连表名都问不到</b>，而初稿怕的那一处混淆
    /// 现在由 M2-06 那道模式闸分掉了一半（表名同而模式不同 → 0 行而不是并成一份）。</item>
    /// <item><c>VARYING</c> 必须带引号（它是这颗解析器的关键字，M-02 那一红），而<em>不带它就分不出
    /// <c>VARCHAR</c> 与 <c>CHAR</c></em> —— 初稿那份"半份出处"里根本没有这一颗。</item>
    /// </list>
    /// 新写形是这台库答应的形状，每一颗都点得出日志：<b>join 走 <c>TABLE_ID</c> 与 <c>SCHEMA_ID</c></b>
    /// （M2-04 起 19 行全部读回），<b>模式那道闸是 <c>s.schema_name = NVL(:sch, UPPER(USER))</c></b>
    /// —— 给 <c>NULL</c> 走右支 = 当前会话用户（M2-04），给名字走左支（M2-05），给一个不存在的名字交回
    /// <em>0 行</em>（M2-06）。初稿那句"模式为 null 时绑 <c>DBNull</c> 而 SQL 里用 <c>NVL</c> 接住"的
    /// <em>手法</em>原样留着，它只是换到了真实存在的那颗字段上。</summary>
    const string ColumnsSql = """
        SELECT c.col_no, c.col_name, c.type_name, c.scale, c.not_null, c."VARYING"
        FROM all_columns c, all_tables t, all_schemas s
        WHERE t.table_name = :tbl AND c.table_id = t.table_id AND s.schema_id = t.schema_id
          AND s.schema_name = NVL(:sch, UPPER(USER))
        ORDER BY c.col_no
        """;

    /// <summary>§10.4 的量纲探测，只读目录视图：不建临时表、不写数据（§9.5 那句"生产库常禁 DDL 权限"）。
    /// 它比 <see cref="ColumnsSql"/> 少读四颗字段，因为探测只问"这一列的量纲是什么形"，要的是<em>型名</em>
    /// 与<em>那颗共用字段</em>两颗。
    /// <b>【本版把 <c>VARYING</c> 从这一条里摘掉了】</b>初稿与本版的头一版都在这儿读它，理由是
    /// "CHAR 与 VARCHAR 连有没有宽度语义都要靠它分开"；跑完整场实测后那一颗只剩一个身份：
    /// <em>分开 <c>CHAR</c> 的两副面孔</em>（定长还是变长，见 <see cref="StoreType"/> 的前三支），
    /// 而"变长与否"不参与"有没有宽度语义"这一判（<c>VARCHAR(9)</c> 与 <c>CHAR(7)</c> 都有宽度、
    /// <c>CLOB</c> 没有，M2-01 逐行）。留在探测里就是一颗读了不用的字段，而它还要占掉引号那一手
    /// （不带引号 E19260，M-02）—— 摘掉。
    /// <b>返回结果里的 <c>CollationName</c> 那一格是空串，这一处是刻意与 <see cref="OracleSchemaReader"/> 分岔的</b>：
    /// 那一族把 <c>NLS_LENGTH_SEMANTICS</c> 塞进那一格，是因为"量纲的出处"真的在那颗会话参数上；
    /// 而这一族的目录里没有那样一颗字段（宽度单位由厂商文档规定、不由列记着），塞一颗名字相近而含义不同的
    /// 东西进去正是 §9.5 那句"doctor 要报的是出处"要避免的事。
    /// 于是 <c>doctor</c> 在这一家能报"声明 200 字符"，报不出"这一列最多 800 字节"。
    /// <b>而这一格现在有实测的旁证说那第二个数<em>不是没量具</em>而是没落在列上</b>：值上的字节数量得到
    /// （<c>LENGTHB('甲公司')</c> → 9，F-11），列上的字节宽度读不回（<c>ALL_COLUMNS</c> 那 27 颗里没有
    /// 那样一颗，而 <c>SCALE</c> 在 <c>VARCHAR(9)</c> 上答回的是 9 —— 字符数，M2-01 第 9 行）。
    /// 目录里另有一颗 <c>COLLATOR</c>（S-09 那 27 颗之内，实测值 <c>NULL</c>）本文件<em>不</em>拿去填
    /// <c>CollationName</c>：它给 <c>null</c> 说的是"这几颗列没登记排序规则"，与填一颗空串在同一次读里
    /// 是同一份信息，而<em>哪一列会有值</em>本文件没量过 —— 记 §14。</summary>
    const string ProbeSql = """
        SELECT c.type_name, c.scale
        FROM all_columns c, all_tables t, all_schemas s
        WHERE t.table_name = :tbl AND c.table_id = t.table_id AND c.col_name = :col
          AND s.schema_id = t.schema_id AND s.schema_name = NVL(:sch, UPPER(USER))
        """;

    public async Task<SchemaTable?> ReadTableAsync(DbConnection connection, TableInfo table, CancellationToken ct = default)
    {
        using var command = Command(connection, ColumnsSql);

        command.Parameters.Add(Parameter(command, "tbl", Fold(table.Name)));
        command.Parameters.Add(Parameter(command, "sch", Fold(table.Schema)));

        var columns = new List<SchemaColumn>();

        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return null;

            do
            {
                // 序号在第一格（跟着产物那份查询的 <c>COL_NO</c>），列名在第二格 —— 而它必须是折叠过的那一份：
                // 契约侧比对的是 C# 成员映射出的列名，读回来一个 "name" 就对不上。
                // 【v0.32 第二格实测】<c>COL_NO</c> 从 <b>0</b> 起数而不是从 1（M-01 那八行是 0…7，
                // 而另四族的 <c>ordinal_position</c> 是 1 基），<c>SchemaColumn</c> 那一格只是
                // "目录给的序号"，比对按名字不按它，所以这一格<em>不折成 1 基</em> —— 折了才是造假。
                var name = reader.GetString(1);
                var dataType = reader.GetString(2);
                var scale = reader.GetInt32(3);
                var varying = reader.GetBoolean(5);

                columns.Add(new SchemaColumn(name, StoreType(dataType, varying, scale),
                    MaxChars(dataType, scale), MaxBytes(dataType, scale), IsNullable(reader.GetValue(4)), reader.GetInt32(0)));
            }
            while (await reader.ReadAsync(ct).ConfigureAwait(false));
        }

        return new SchemaTable(table, columns);
    }

    public async Task<LengthSemanticsReading?> ProbeLengthSemanticsAsync(DbConnection connection, TableInfo table, string columnName, CancellationToken ct = default)
    {
        using var command = Command(connection, ProbeSql);

        command.Parameters.Add(Parameter(command, "tbl", Fold(table.Name)));
        command.Parameters.Add(Parameter(command, "sch", Fold(table.Schema)));
        command.Parameters.Add(Parameter(command, "col", Fold(columnName)));

        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        var dataType = reader.GetString(0);
        var scale = reader.GetInt32(1);

        // 与金仓那一族的"两侧填同一个数"同一条退路，理由也同一条：填 0 会让 §9.5 的 doctor 报成
        // "这一列长度 0"，那是把"问不出答案"写成"答案是没有"。而这一家比金仓多一层可说的东西：
        // 单位<em>是</em>字符这一条有文档（见 XuguTypes 类注释第一条），只是第二个数（字节）在目录里没有。
        //
        // 【v0.32 第二格改判两处】① 上一版这里写的是 <c>width ?? packed.Declared ?? -1</c>，也就是
        // "只要那颗共用字段有数就拿去当长度" —— 于是 <c>DATETIME(3)</c> 那一列探测回来 DECLARED=3、
        // 一颗 <c>INTEGER</c> 回来 -1（改前那一场的五行在 meta-1 的 M-21）。本版只认<em>有宽度语义的那几型</em>
        // 且<em>真的写了宽度</em>的那一颗数，其余一律 -1（问不出）：同一颗 <c>CREATEDAT</c> 现在回
        // DECLARED=-1 BYTES=-1，而 <c>CUSTOMERNAME</c> 仍然回 64/64（改后五行在 meta-2 的 M-21）。
        // ② <c>CharSemanticsProven</c> 那一格仍然传"这一型是有宽度语义的字符型"
        // 而不是"量纲被实测证实过"：单位是字符这一条在本家是【文档】级、列上那颗数<em>是</em>字符数
        // （M2-01 的 <c>VARCHAR(9)</c> → 9 是第一次有库在旁边印证），可这一格在五家没有一家被读成"实测"，
        // 改判不该在这一格上偷偷升档 —— 那一格在金仓那一家说的是同一件事（它按类型名给）。
        var declared = MaxChars(dataType, scale) ?? -1;

        return new LengthSemanticsReading(string.Empty, declared, declared, IsText(dataType));
    }

    /// <summary>数值列那颗共用字段的拆包（类注释第三条）：<c>SCALE = 精度 × <see cref="PackedBase"/> + 标度</c>。
    /// 只在 <c>NUMERIC</c>/<c>DECIMAL</c> 两支被调用，因为<b>要不要打包是型名说的，不是数的大小说的</b> ——
    /// 上一版反过来（它先看数落在哪一支，再看型名认身份），于是<em>两颗一起读反</em>：
    /// 商给了标度、余数给了精度（1179652 → 精度 4、标度 18）。
    /// 返回 <c>null</c> 说的是"这一颗数不像打包形，不敢拆"，此时 <see cref="StoreType"/> 落到"原样还型名"
    /// 那一支，也就是<em>精度读不出来</em>而不是读成一个错的。</summary>
    static (int Precision, int Scale)? Unpack(int packed)
        => packed >= PackedBase ? (packed / PackedBase, packed % PackedBase) : null;

    /// <summary>把目录给的三颗字段（型名、<c>VARYING</c>、<c>SCALE</c>）合成一份列定义字形。
    /// <b>这里的纪律是"还回来的字形要与 <c>XuguTypes.GetStoreType</c> 发出去的那一份同一"</b>，
    /// 与 <c>PostgreSqlSchemaReader.StoreType</c> 把 <c>character varying</c> 折回 <c>varchar</c> 同形，
    /// 而逐支的出处是 <c>obj/logs/v32-recon-meta2-1.log</c> 的 M2-01（19 型对照表，本版逐行读过）。
    /// <b>本方法自己那一跑的改后快照在 <c>obj/logs/v32-recon-meta-2.log</c> 的 M-20</b>：那八行现在读回
    /// <c>INT</c>（<c>ORD=0</c>，改前是 <c>INTEGER</c>）、<c>VARCHAR(64)</c>（<c>ORD=1</c>，改前是
    /// <c>CHAR(64)</c>）、<c>NUMERIC(18,4)</c>、<c>DATETIME(3)</c>、<c>TEXT</c>（<c>ORD=7</c>，改前是
    /// <c>CHAR(-1)</c>）—— 也就是下面每一支都各有一颗列在旁边答一次，注释不再只指向"库给的字段"而断了
    /// "我们合成的字形"那一半。
    ///
    ///
    /// <para><c>scale &gt; 0</c> 那一半闸门是本版新增的判据【实测】：<c>SCALE = -1</c> 说的是
    /// "这一型没有声明值"（<c>INTEGER</c>/<c>TINYINT</c>/<c>FLOAT</c>/<c>CLOB</c>/<c>BLOB</c>/<c>DATE</c>
    /// 全给 -1，M2-01），而上一版不认识它 —— 它给 <c>TEXT</c> 那一列还回过 <c>CHAR(-1)</c>、给
    /// <c>MaxChars</c> 填过 <c>-1</c>（改前两颗样本：meta2-1 的 M2-20 <c>ORD=11</c> 那一行
    /// <c>C_TEXT</c>，和 meta-1 的 M-20 <c>ORD=7</c> 那一行 <c>NOTE</c>）。改后同一颗 <c>NOTE</c>
    /// 读回 <c>TEXT</c> 且 <c>CHARS=&lt;null&gt;</c>（meta-2 的 M-20 <c>ORD=7</c>）。
    /// <b>把"没有声明"读成一个负的声明，
    /// 是这一格里最坏的一种错</b>：它不红，它给 §9.5 一条看着像答案的数。</para>
    ///
    /// <para>两支<em>选出来的</em>而不是读出来的，逐条点名：① <c>CHAR</c> + <c>VARYING=True</c> + 无宽度
    /// 回 <c>TEXT</c> —— 在这一家的目录里 <c>TEXT</c> 与"不写宽度的 <c>VARCHAR</c>"是同一个东西
    /// （M2-01 的 C_TEXT 与 C_VARCHAR 只差一颗宽度），本文件跟着建侧那一支（<c>XuguTypes</c> 给无宽度的
    /// <c>string</c> 发 <c>TEXT</c>）；② <c>INTEGER</c> 折回 <c>INT</c>（同义字【文档】，且本仓库的 DDL
    /// 发的就是 <c>INT</c>）。而 <c>TIMESTAMP→DATETIME</c> 那一颗同义字<em>不折</em>，理由写在类注释末段。</para>
    ///
    /// <para>时刻两支的精度限定词本版<em>还原</em>了：<c>TIME(3)</c> 与 <c>DATETIME(3)</c> 建出去、
    /// 目录里 <c>SCALE=3</c> 回来（M2-01 的 <c>ORD=14</c>/<c>ORD=15</c> 两行），于是初稿那句"精度限定词在
    /// 这里不还原、doctor 会报契约要 TIME(3) 而目录说 TIME"【无出处】不再成立 —— 那一颗【无出处】本版
    /// 改判为【实测】，而它<em>已经被本方法自己答实过一次</em>：<c>FASTSQLORDER</c> 的 <c>CREATEDAT</c>
    /// 改前读回裸 <c>DATETIME</c>（meta-1 的 M-20 <c>ORD=5</c>）、改后读回 <c>DATETIME(3)</c>
    /// （meta-2 的同一行）。<c>DATE</c> 不给精度（-1）而它本来也没有精度那一档【文档】，两支不撞。</para></summary>
    static string StoreType(string dataType, bool varying, int scale) => dataType.ToUpperInvariant() switch
    {
        // 字符三型共用一个型名，分开它们的是 VARYING 与有没有宽度（类注释第四条）。
        "CHAR" when varying && scale > 0 => $"VARCHAR({scale})",
        "CHAR" when varying => "TEXT",
        "CHAR" when scale > 0 => "CHAR(" + scale + ')',

        // 数值：拆得开就还原成 NUMERIC(p,s)，拆不开（不是打包形）就原样还型名 —— 精度读不出，不猜。
        "NUMERIC" or "DECIMAL" when Unpack(scale) is { } packed => $"{dataType}({packed.Precision},{packed.Scale})",

        // 时刻：这一支的 scale 是直读的精度（不是打包），所以 > 0 才带限定词，0 与 -1 都还裸型名。
        "TIME" or "DATETIME" when scale > 0 => $"{dataType}({scale})",

        // INTEGER→INT 那一颗同义字折在这里（与 PostgreSql 那一族把 catalog 名折回建侧名同一条纪律）。
        "INTEGER" => "INT",

        _ => dataType,
    };

    /// <summary>字符上限只对"有宽度语义"的那几型有意义；<c>NUMERIC</c> 的精度不是字符数，
    /// 带进去会让 <c>ColumnContract.FitsBytes</c> 那类判据读出一个假上限（与另三家同一条纪律）。
    /// 【本版】<c>scale &gt; 0</c> 那一半是新加的闸门：<c>-1</c> 说的是"没有声明"而不是"声明了 -1"，
    /// 上一版没有这一句，它把 <c>TEXT</c> 的宽度报成了 -1（同一颗样本：meta2-1 的 M2-20 <c>ORD=11</c>）。
    /// 而那一颗 <c>varying</c> 参数本版<em>也摘掉了</em>：判"有没有宽度语义"只看型名（见 <see cref="IsText"/>
    /// 末段），留着它会让读法看起来依赖一颗其实不参与判断的字段。</summary>
    static int? MaxChars(string dataType, int scale)
        => IsText(dataType) && scale > 0 ? scale : null;

    /// <summary>字节上限在这一家问不出来（见 <see cref="ProbeLengthSemanticsAsync"/> 那句注释），
    /// 所以<em>一律留 null</em>。这与 <c>MySqlSchemaReader</c> 那一格是同一条纪律而不是同一件事：
    /// 那一族有一个真实存在的字节闸门（只是它在行上不在列上），这一家压根没有第二个数可读。
    /// 【本版】签名与 <see cref="MaxChars"/> 逐颗对齐（只少了那颗不参与判断的 <c>varying</c>），
    /// 为的是"这两颗上限来自同一份读法"这一件事在签名上看得出来。</summary>
    static int? MaxBytes(string dataType, int scale) => null;

    /// <summary>"有宽度语义的字符型"在这一家 = 型名 <c>CHAR</c>（字符三型同源，见类注释第四条）、
    /// <c>CLOB</c>、<c>TEXT</c>。
    /// <b>为什么要认 <c>CLOB</c> 而不只认 <c>CHAR</c></b>：建侧在宽度超过 60000 时发的就是 <c>CLOB</c>
    /// （<c>XuguTypes.MaxVarcharChars</c> 那一支），而它在目录里的字形 M2-01 已经量到
    /// （型名 <c>CLOB</c>、<c>SCALE=-1</c>、<c>VARYING=False</c>）—— 认得它，<c>doctor</c> 才说得出
    /// "这一列是超宽文本"而不是"这一型不认识"。<c>TEXT</c> 那一颗<em>读不到</em>（库不存它，见上面第 ② 条），
    /// 列进来只为"别人建的表里如果真写着 <c>TEXT</c>"这一格不落到"不认识"，方向是放宽而不是收紧。
    /// <b>本版不再看 <c>varying</c> 那一颗</b>：它在 <c>CLOB</c>/<c>BLOB</c> 上都给 <c>False</c>（M2-01），
    /// 拿它当"是文本"的判据会把 <c>BLOB</c> 也带进来 —— 那颗布尔在本文件只用于<em>分开 CHAR 的两副面孔</em>
    /// （<see cref="StoreType"/> 的前三支），不参与"有没有宽度语义"这一判。</summary>
    static bool IsText(string dataType)
        => dataType.Equals("CHAR", StringComparison.OrdinalIgnoreCase)
           || dataType.Equals("CLOB", StringComparison.OrdinalIgnoreCase)
           || dataType.Equals("TEXT", StringComparison.OrdinalIgnoreCase);

    /// <summary>目录里那一颗叫 <c>NOT_NULL</c>【产物给的列名】，所以"为真"就是非空 —— 方向由名字给，
    /// 不用猜。取值<em>字形</em>初稿没有出处，所以文本 <c>Y</c> 与数字 <c>1</c> 两形都算真。
    ///
    /// <para><b>【v0.32 第二格实测】活库给的是第三种：CLR <c>Boolean</c></b>（<c>all_columns.NOT_NULL</c>
    /// 在 M-01 那八行里逐颗是 <c>False</c>/<c>True</c>，出处
    /// <c>obj/logs/v32-recon-meta-1.log</c>；同一形在 catalog-2 的 K-08 那一行也出现过）。
    /// 于是初稿那一句 <c>GetString(4)</c> 在这一家会直接抛 <c>InvalidCastException</c> —— 读侧现在拿
    /// <c>GetValue</c>，把三形都接住：<b>布尔</b>是实测到的那一形，<c>Y</c>/<c>1</c> 文本与数字两形是初稿
    /// 那句话留下的余量（换 <c>COMPATIBLE_MODE</c> 或换版本时不至于静默读反），而
    /// <b>除此之外的一形照旧抛</b> —— 这一条与初稿同：宁可红在"这一份读侧全部重读"，
    /// 不要红在"doctor 多报一条这一列可以写空值"（§9.5：doctor 是报给人的，不是替库执行约束的）。</para>
    ///
    /// <para>目录那一格给 <c>null</c> 时读成"可空"：这是<em>保守</em>方向（多放行一次写入，
    /// 由库自己的约束去挡），而实测的 27 行里没有一行是 <c>null</c>。</para>
    static bool IsNullable(object? notNull) => notNull switch
    {
        bool flag => !flag,
        string text => text is not ("Y" or "1"),
        null => true,
        _ => Convert.ToInt64(notNull) == 0,
    };

    /// <summary>目录里的名字是大写形（见类注释第一条），所以绑进去的也必须是大写形。
    /// 【v0.32 第二格实测】不折的那一形库确实不收：<c>all_tables.TABLE_NAME</c> 里
    /// 不带引号建的 <c>FastSqlRecon33</c> 存的是 <c>"FASTSQLRECON33"</c>（K-07），带引号建的小写名存的才是小写（K-10）；
    /// 模式那颗同一条纪律：<c>'sysdba'</c> 交回 0 行而 <c>'SYSDBA'</c> 交回 19 行（M2-07 对 M2-05）。
    ///
    /// <para>初稿说 null 原样留 <see cref="DBNull"/>，接的是 SQL 里那颗 <c>NVL(:owner, USER)</c>；
    /// 本版那一颗 <c>NVL</c> 仍然在场，只是换到了真实存在的 <c>s.schema_name</c> 上（见
    /// <see cref="ColumnsSql"/>），所以 <c>Schema</c> 为 null 这一支从"兜底"变回了<em>有语义的那一条路</em>：
    /// 绑 <see cref="DBNull"/> → 走 <c>UPPER(USER)</c> → 与建侧（<see cref="XuguCodeFirst"/> 那句
    /// "模式为空就不限定"）指向同一个默认，而 M2-04 就是这一支答回来的那 19 行。</para></summary>
    static object Fold(string? identifier)
        => identifier is { Length: > 0 } name ? name.ToUpperInvariant() : DBNull.Value;

    static DbCommand Command(DbConnection connection, string sql)
    {
        var command = connection.CreateCommand();

        command.CommandText = sql;

        return command;
    }

    /// <summary>【v0.32 第二格整颗改判】参数名<em>不带</em>前缀：<c>ParameterName = "tbl"</c>，
    /// 而 SQL 文本里那一颗仍然是 <c>:tbl</c>。
    ///
    /// <para>初稿这一句写的是"前缀是冒号而不是 <c>@</c>，五家在这一格上是 <c>2 家 : ∶ 3 家 @</c>"，
    /// 那是把<em>文本里</em>的那一颗当成了<em>参数对象上</em>的那一颗 —— 两件事在另四族一直是同一颗字符串，
    /// 所以从来没有人把它们分开看过。分开是在这台真库上被逼的：带冒号的名字交回
    /// E10036「未绑定变量 TBL」，摘掉冒号同一句就绿（<c>obj/logs/v32-recon-meta-1.log</c> 的 M-01 绿 /
    /// M-03 红，一次只动名字这一颗变量；而 catalog-2 的 K-16/K-18/K-19 与 D-14 那四红全压在同一个原因上，
    /// 本段就是去把那一整场的红重问一遍）。同一条分岔在主链路上由 <c>AdoExecutor.Bind</c> 收
    /// （Core 的 <c>SqlWriter.NextParameterName()</c> 交回的是带前缀的那一颗，v0.32 第二格在驱动边界摘掉），
    /// <b>而本文件绕不开那一条路</b>：目录读侧不发 <c>AdoParameter</c>，它自己 <c>command.CreateParameter()</c>，
    /// 所以那一颗摘前缀在这里必须再写一遍。两处是同一个事实的两个落点，改一处不改另一处
    /// 就会留下"主链路绿、doctor 红"这种最难查的形状 —— 记 §14 与 <c>doctor</c> 那一格。</para>
    ///
    /// <para>另两颗名字上的坑一起记在这里：① 名字不能叫 <c>table</c>，那颗关键字在解析期就被驳回
    /// （E19260 unexpected TABLE，K-02b/K-03 那一组），所以下面三调用交的是 <c>tbl</c>；
    /// ② 带 <c>@</c> 的写法在这一族连<em>文本</em>都不认（B-03/B-04 两红），所以 <c>ParameterPrefix</c>
    /// 取冒号那一支的理由仍然成立 —— 只是它管的是文本，不是参数对象的名字。</para>
    static DbParameter Parameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();

        parameter.ParameterName = name;
        parameter.Value = value;

        return parameter;
    }
}
