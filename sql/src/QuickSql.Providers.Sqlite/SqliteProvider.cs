using System.Data.Common;
using QuickSql.Sql;
using QuickSql.Values;

namespace QuickSql.Providers.Sqlite;

/// <summary>SQLite 的驱动槽位（§4，第七家；§9.1 那张表"内核血统"那一列里第三颗自研——
/// <c>SqlServer</c> 那一行写的是"自研 T-SQL"、<c>虚谷 Xugu</c> 那一行写的是"自研"，本行与后一颗同档）。
/// <b>本家与另六家的分别是：它的断言是一格一格向活库问过再写下来的</b>——这一句只到"问过"为止，
/// 而且它有两处明写的例外（下面那份清单里 <c>Array_Type</c> 与 <c>RefCursor</c> 那一颗本轮没有设点位），
/// 最后一段写着它还没到哪一步。
///
/// <para><b>这一族没有血统基类，也没有叶子</b>：§9.1 那张表里凡把"内核血统"写成别家内核名字的行
/// （Oracle 系 ∶ PG 系 ∶ <c>PG 9.2 内核</c> ∶ Informix ∶ MySQL 兼容 ∶ MySQL / Oracle 双模，加上末行明写的
/// "PG 或 MySQL 派生"）里没有一行写的是 SQLite，而本家自己也不派生谁 —— 两头都是空的，
/// 于是它与 <c>QuickSql.Providers.SqlServer</c>、<c>QuickSql.Providers.Xugu</c>、
/// <c>QuickSql.Providers.Gbase8s</c> 同档：<b>单层</b> provider，类是 <c>sealed</c>，十一颗子 SPI 各一份实现。
/// §2 那条"provider 之间禁止互引"在这里的表现为 <see cref="SqliteConnectorFactory"/> 是第七份逐字同形的拷贝。</para>
///
/// <para><b>构造期不反射</b>：<see cref="DbProviderFactory"/> 由 Core 的 <c>DriverRegistry</c> 按
/// 内建表 <c>BuiltinDrivers</c> 解析后注入，所以本程序集编译期不认识 <c>Microsoft.Data.Sqlite</c>（§1.2 原则 3）。
/// <b>而这一家把内建表注释 里那句"公开包走默认探测路径"那一支第一次走通了</b>：另三颗单层家里，
/// T-SQL 那一家是 P0 主靶、程序集由 <c>tests/QuickSql.Tests.Integration</c> 的引用带进输出目录；
/// 虚谷那一家在 v0.32 结算日解析成功过，走的是另一支——把厂商那两颗 DLL 手动摆进
/// <c>&lt;输出目录&gt;/drivers/</c>（§9.1 那一行第五列有整段账）；GBase 8s 那一家到今天仍然<em>从未在
/// 一台机器上解析成功过</em>（它类注释第一段）。本家既不用摆文件也不用写 <c>path</c>：
/// <c>restore</c> 得到、<c>SqliteFactory.Instance</c> 拿到、工厂当场造出
/// <c>SqliteConnection</c> 与 <c>SqliteCommand</c>【实测·A0 ∶ A0b】，程序集名与版本现场读回
/// <c>Microsoft.Data.Sqlite 9.0.0.0</c>、引擎 <c>sqlite_version() = 3.46.1</c>【实测·A0c ∶ A1】。
/// 也就是说内建表里那颗 <c>factoryType</c> 在本家<em>写得出实名</em>；另两家只能省略它走
/// "扫导出类型里第一个 <c>DbProviderFactory</c> 子类"那条兜底，而虚谷那一家连实名都读到过仍省着 ——
/// 那道差额记在 <see cref="DriverKey"/> 那一段。</para>
///
/// <para>各槽位共用同一批实例（与另六族同一条纪律）：<c>Expressions.Register</c> 的覆写必须同时对翻译器
/// 和方言生效，各 new 一份就会出现"注册了却不改行为"的幽灵 bug。</para>
///
/// <para><b>能力位：这一档声明 5 位</b>，位数与 <c>MySqlProviderBase</c> 同一档，census 因此长成
/// <c>17 ∶ 14 ∶ 7 ∶ 6 ∶ 5 ∶ 5 ∶ 4</c>（<c>SqlServer ∶ PostgreSqlBase ∶ Xugu ∶ OracleBase ∶
/// MySqlBase ∶ 本家 ∶ Gbase8s</c>）。<b>本家与另六家不同的不是位数而是档位</b>：另六家里只有虚谷那一本
/// 在 §13.5 翻过 <c>Live</c>（四位，而每位都靠人把基线文本逐句敲给那台库），其余五家每一位后面最多跟
/// "离线文本已钉"；本家每一位后面跟的是一颗活库当场交回的东西，逐位对 §4 那句"代码今天真的走到那一条语句"：
/// <c>Paging_LimitOffset</c> 对 <see cref="SqliteDialect.EmitPaging"/>（<c>LIMIT … OFFSET …</c>，
/// 而缺上限那一档它发的是 <c>LIMIT -1</c> 这颗"不设上限"记号而不是厂商上限数
/// 【实测·A3 ∶ 32 ∶ 315/315b/408】，三派语义里的第三派，见 <c>SqliteDialect</c> 类注释第三格），
/// <c>Returning</c> 对 <see cref="SqliteKeys.EmitReturning"/>（<c>INSERT … RETURNING</c> 多行带参数都活
/// 【实测·第 2 轮 105 ∶ 106 ∶ 404 ∶ 1319 ∶ 1013】，且 <c>UPDATE</c>/<c>DELETE</c> 两臂同判），
/// <c>Identity_ExplicitInsert</c> 对 <see cref="SqliteKeys.SupportsExplicitIdentityInsert"/>
/// （一颗 100 写进去下一号是 101【实测·54 ∶ 55】，31 亿那颗同判【实测·310/310b】），
/// <c>Schemas</c> 对 <see cref="SqliteCodeFirst"/> 的限定名发射（建表带 <c>main.</c> ∶ <c>side.</c>
/// 【实测·3001】、带限定的 <c>ALTER</c> 与 <c>DROP TABLE</c> 都收【实测·1308 ∶ 1310】、索引那一颗的限定
/// 挪到索引名侧而它落进正确的库【实测·3005 ∶ 3006】，读侧 <c>pragma_table_info(@tbl, @sch)</c> 两档各交
/// 自己的表【实测·3705 ∶ 3706】），<c>MultiStatement</c> 对 <see cref="SqliteCodeFirst.EmitCreateTable"/>
/// 尾部那条 <c> ; </c> 串（建表 + 普通索引 + 唯一索引三句一串收、尾句不带分号也执行
/// 【实测·2820 ∶ 2822 ∶ 2912/2913 ∶ 2908/2910】）。</para>
///
/// <para><b>这一段的后两件事都要单独说。</b>其一，<c>MultiStatement</c> 是<b>七家里第二份声明</b>
/// （第一份是 <c>SqlServerProvider.cs:43</c>），而本家是<b>第一份背后站着活库读数的</b>。它也不是一份
/// "谁都没看过的礼物"：这一格在 <c>src/</c> 里早就数过 —— <c>OracleCodeFirst.cs:26-28</c> 那句写着
/// <b>五家的 <c>CodeFirst</c> 全都产出过 <c> ; </c> 串，而五家里只有 T-SQL 一家声明这一位</b>；
/// 另几家不声明的理由各家不同，而且有两家给的口径比"没演示"更硬：PG 血统那一份与 Oracle 那一份同因
/// （<c>PostgreSqlProviderBase.cs:21</c> ∶ <c>OracleProviderBase.cs:52</c>，扩展协议下不合法），
/// MySQL 血统那一份要的是<em>一颗连接串参数开启</em>（<c>MySqlProviderBase.cs:72-73</c>），
/// 虚谷那一份才是"没有任何一份产物演示过"（<c>XuguProvider.cs:84</c>），GBase 8s 那一份指的是键回读那句
/// 要接在建表句之后（<c>Gbase8sProvider.cs:76</c>）。本家不需要借用其中任何一句：连"建新表 → 拷数据 →
/// 删旧 → 改名 → 重建索引"这一整串换型序列都是一条文本走通的
/// 【实测·4310 ∶ 4312 ∶ 4315 三段，四步的读回在 4313 ∶ 4314】。其二，<b>新增一族第一次把交集扣到只剩
/// 一颗</b>：上一版是两位（<c>Schemas</c> ∶ <c>Alter_Column_Precision</c>，<c>Gbase8sProvider.cs:56</c>
/// 那句），本家交出后一位，丢的那颗是 <c>Alter_Column_Precision</c>
/// （<see cref="SqliteCodeFirst.EmitAlterColumnPrecision"/>
/// 抛，理由两层都写在 <c>SqliteCodeFirst</c> 类注释第三条【实测·F4 ∶ 2815】）。
/// 剩下的那颗<em>自己正在变形</em>：七家今天都声明 <c>Schemas</c>，而这一格在两家手里指的不是同一样东西 ——
/// GBase 8s 那一颗是"所有者"（<c>Gbase8sProvider.cs:44</c>），本家那一颗是<em>挂进来的另一个文件</em>：
/// <c>CREATE SCHEMA</c> 发不出【实测·2824】，而 <c>main.</c> ∶ <c>side.</c> 两侧确实都限得住
/// 【实测·3000/3001 ∶ 3005 ∶ 3006】。所以"交集里还有这一位"不等于"跨族建表迁移可以按这一位发"，
/// 这一句与 §13.5 那本新台账（第十五本）并排读。</para>
///
/// <para>刻意<b>不</b>声明的，逐条给理由（与另六家那几段同一条纪律：不声明比声明更需要写下来）：
/// <list type="bullet">
/// <item><c>Boolean_Type</c>：<c>INTEGER</c> affinity 之下 <c>BOOLEAN</c> 与 <c>INTEGER</c> 是同一颗存储
/// 【实测·610 那一行】，库里没有"只有两值"那一型；<see cref="SqliteTypes.GetStoreType"/> 落 <c>INTEGER</c>
/// 而谓词侧交回 <c>0</c>/<c>1</c> 整数【实测·第 2 轮 104 ∶ 1112】。<b>v0.35 第一笔那条裁断在这里成立得
/// 最干净</b>：位只跟存储型走，而这一族的存储型清单里压根没有布尔那一档。</item>
/// <item><c>Guid_Raw16</c> 与 <c>Guid_Char36</c>：<b>本家是七家里第一颗两颗 Guid 位都不声明的家</b>，
/// 理由是它两支都不是库里的事：<c>Guid</c> 落 <c>TEXT</c> 而那颗列上没有任何约束
/// （36 字符的文本，<c>GetGuid</c> 读回成立【实测·336 ∶ 337】，绑成 <c>byte[16]</c> 也进得去而读回是
/// <c>Byte[]</c>【实测·V ∶ X】）。<c>CHAR(36)</c> 那一支要的是"库认得这一型"，本家它是 TEXT 的别名；
/// <c>RAW16</c> 那一支更压根没有对应声明。这一格与达梦、MySQL 血统、GBase 8s 三家同宽而不同因，
/// 逐字在 <see cref="SqliteTypes"/> 那一处臂的注释里。</item>
/// <item><c>Upsert_OnConflict</c>：<b>这是七家里最别扭的一颗不声明</b> —— 库确实有
/// <c>INSERT … ON CONFLICT DO UPDATE</c>，四臂各量过（更新、不动、无目标、表达式目标报错
/// 【实测·第 2 轮 40 ∶ 41 ∶ 42 ∶ 43 ∶ 46】，带 <c>RETURNING</c> 的那一臂在 130），而 <c>src/</c> 里
/// <c>.OnConflict(</c> 是<em>零处</em>（<c>OnConflict</c> 一词全仓库三处命中，全在注释里）。
/// §4 判据量的是代码走不走得到，所以不声明；它与 <c>MySqlProviderBase</c> 那颗 <c>Upsert_OnDuplicate</c>、
/// Oracle 那一档那颗 <c>Upsert_MergeInto</c> 是同一条判据的第三个实例，只是这一家连"语法形状"都与
/// PG 血统那一支同形，最容易让人误以为已经接上了。
/// <b>【本笔的两句】上面那两句各有一处从今天起要改读</b>：① "同一条判据"对 MySQL 那一颗换了理由
/// （句子发得出去了而位没有读者，见 <c>MySqlProviderBase</c> 类注释那一项的改判段）；
/// ② <c>ON CONFLICT</c> 那一个大写形状今天在本仓库<em>有了</em>发射口——在 PG 血统那一家
/// （<c>PostgreSqlBulk.UpsertClause</c>），本家仍然零处。于是"三处命中，全在注释里"那一句从今天起
/// 只对<em>本家</em>成立，跨家读要按"PG 血统有一处文本发射、其余各家仍只在注释里"。
/// 本家要接这一档缺的还是那一颗<em>链上的入口</em>：<c>UpsertClause</c> 那颗槽本家没覆写，
/// 而它与 SQLite 那一形逐字同形【实测·第 2 轮 40 ∶ 41 ∶ 42】——接一家不必改其余各家，那是下一笔的一行。</item>
/// <item><c>Comment_On_Column</c>：<c>COMMENT</c> 这个词在本族是语法错【实测·F5 ∶ 2828】，而这一族
/// <em>没有任何</em>列注释的落处（<c>sqlite_master</c> 回显的是引擎规范化的 DDL 文本【实测·3013】），
/// 所以 <see cref="SqliteCodeFirst.TryEmitColumnComment"/> 是本文件唯一一颗交 <see langword="false"/>
/// 的槽位方法。这一颗让交集从两位掉到…… <b>没掉</b>：那颗位在上一版就已经只剩两家（见上面第二段），
/// 完整算账在 <c>SqliteCodeFirst</c> 类注释第四条。</item>
/// <item><c>Alter_Column_Precision</c>：<see cref="SqliteCodeFirst.EmitAlterColumnPrecision"/> 抛，
/// 而它在七家里是<b>第一次"这一问在本家没有对象"那一档</b>：这一族没有改列型状的语句，
/// 而"型状"本身在库里就不存在（声明名只是一颗 affinity 配方，宽度与标度从不参与存储
/// 【实测·4001 ∶ 4002】）。交集被这一颗扣掉的那一位就是它。</item>
/// <item><c>CaseSensitive_Identifiers</c>：<see cref="IdentifierPolicy.Sqlite"/> 取
/// <c>CaseSensitive: false</c>，因为带引号写错大小数照样解析得到、而"只差大小写的两张表"是同一张
/// 【实测·第 2 轮 11 ∶ 12 ∶ 16 ∶ 19 ∶ 20 ∶ 18 那颗 <c>table mixedcase already exists</c>】。
/// 与虚谷那一颗 <c>true</c> 相反一侧。</item>
/// <item><c>Sequences</c>：<c>CREATE SEQUENCE</c> 是语法错【实测·第 2 轮 107】，自增只有
/// <c>INTEGER PRIMARY KEY AUTOINCREMENT</c> 一形，而它那颗序列藏在 <c>sqlite_sequence</c> 表里、
/// 要到第一次自增插入才存在【实测·D6 ∶ 56】。</item>
/// <item><c>Cte</c>、<c>WindowFunctions</c>、<c>Count_Over_Window</c>：三颗库都会
/// （<c>WITH</c> 与 <c>COUNT(*) OVER ()</c>【实测·第 2 轮 102 ∶ 1014 ∶ 836】，
/// <c>ROW_NUMBER() OVER</c>【实测·2144】），而 <c>src/</c> 里既没有 <c>WITH</c> 的发射口，
/// <c>Count_Over_Window</c> 那一档今天又是在 <c>QueryEngine</c> 里直接抛的那一颗 —— 与另六家同一立场。</item>
/// <item><c>SkipLocked</c>、<c>Lock_NoWait</c>：<c>SELECT … FOR UPDATE</c> 是语法错
/// 【实测·第 2 轮 103 ∶ 2164 同一句 <c>near "UPDATE": syntax error</c>】—— 这一族<em>没有行级锁那一层</em>，
/// 两颗位没有可指的语句。<see cref="SqliteConcurrency"/> 类注释第四条整段是这一格。</item>
/// <item><c>Truncate_Cascade</c>：<c>TRUNCATE</c> 在本族是语法错【实测·93b ∶ 2161】，清空只有
/// <c>DELETE FROM</c>，而"级联"那一半没有对象。</item>
/// <item><c>TempTable</c>：<c>CREATE TEMP TABLE</c> 是活的【实测·第 2 轮 109】，而且本仓库已经量到它
/// 与 <c>main</c> 同名时的那颗遮蔽【实测·3805 ∶ 3909 ∶ 3910 ∶ 3911 ∶ 3912】—— 不声明是因为
/// <c>src/</c> 里没有建临时表的发射口，而那颗遮蔽正是 §14 记它的原因。</item>
/// <item><c>Json_Type</c>：<c>json_extract</c> 与 <c>json()</c> 现场可用【实测·第 2 轮 101 ∶ 1015】，
/// 而 <c>Json&lt;T&gt;</c> 在 <c>src/</c> 里仍是 0 处 —— 与 <c>XuguProvider</c> 那颗同一句改判后的口径。</item>
/// <item><c>Array_Type</c> 与 <c>RefCursor</c>：这一族没有数组那一型，也没有服务端过程与游标那一档对象；
/// 本轮没有为这两格设点位（引擎那一侧的答案要看编译开关，【推断】），而代码里同样没有发射口。</item>
/// <item><c>MultiResultSets</c>：<b>本家量得到它</b>（一条文本里三颗 <c>SELECT</c> 交出三串
/// 【实测·4405】，而"多份靠 <c>NextResult</c> 读完"这句是<b>探针自己的读取循环</b>、不是任何一颗读数的内容），
/// 它没有发射口：<c>src/</c> 里唯一那处 <c>NextResult</c> 是"跳到第一颗带列的结果集"
/// （<c>AdoExecutor.cs:172</c>），它<em>容忍</em>多份而从不读第二份的行列。与上一颗 <c>MultiStatement</c>
/// 正好成对：那一颗声明，是因为那条串今天真的发出去；这一颗不声明，是因为没有任何一条路径产出过
/// 多结果集查询。</item>
/// <item><c>Bulk_Copy</c> 与 <c>Bulk_ArrayBind</c>：<see cref="Bulk"/> 交回 <see langword="null"/>，
/// 批量从 L3 逐行链起（§12.1）。那颗驱动没有"一次交一批"的公开口，而 2100 那一类参数预算在这家
/// 也不是约束（参数个数上限现场读的是驱动侧那颗常量，见 <see cref="SqliteDialect"/> 的预算一格），
/// 所以这里既没有高速通道可报、也没有预算可报。</item>
/// </list></para>
///
/// <para><b>还有一句必须写在本家而不是别家：provider 不报引擎版本门槛。</b>本家五位里三颗带着版本下限
/// —— <c>RETURNING</c> 要 3.35 以上、<c>DROP COLUMN</c> 要 3.35 以上、<c>IS DISTINCT FROM</c> 要 3.39 以上
/// 【文档·SQLite 发布页，三颗门槛本轮没有从引擎侧读到】，而现场那台是 3.46.1【实测·A1】、
/// 托管侧 <c>Microsoft.Data.Sqlite 9.0.0</c>【实测·A0c】。<b>这一族的部署是可以差得很远的一族</b>
/// （单文件内嵌、编译开关、是否带 ICU），所以"这一位支持"在本家天然带着一句"在这台 3.46.1 上支持"。
/// 换一台老引擎时这些文本会当场报语法错，而 provider 不会预告 —— 整格记 §14，
/// 它比另六家那句"版本没读"更要紧，因为本家的读数全部来自一颗具体的引擎版本。</para>
///
/// <para>每一位的出处记在 §13.5 那本台账里，而本家给那本台账带来的是<b>第十五本</b>，也是它第一次不是
/// <c>declared ∶ offline ∶ 未实测</c> 那一种三元组：<b>本家每一位的第三档都是活库读数</b>
/// （编号排到 48 的四十三份留档报告、上千颗带编号的点位，逐颗能在
/// <c>obj/sqlite-probe*/probe*-report.txt</c> 里对读）。
/// <b>【v0.37 第四笔】第三档今天又升了一级：集成册那一格已经绿过</b>——
/// <c>QUICKSQL_TEST_PROVIDER=sqlite</c> 那一跑是 <c>95 ∶ 0 红 ∶ 14 跳</c>
/// （<c>obj/logs/v37-sqlite-int-4.log</c>），于是台账里分页、句尾 <c>RETURNING</c> 与建表那一串三行翻成
/// <c>Live</c>，而这三行的见证<em>没有任何一行手写文本参与</em>：前两颗跑的是 <c>QueryEngine</c> 与
/// <c>InsertEngine</c> 自己拼出来的语句，第三颗跑的是 <c>CodeFirst</c> 产出的 <c>CREATE TABLE … ; CREATE INDEX …</c>
/// 整串（发它的是集成夹具 <c>Database.cs:812</c> 那一句 <c>ExecuteNonQuery</c>，见证是事后翻库：那颗
/// <c>IX_QuickSqlOrder_CreatedAt</c> 在场，而全仓库只有这一条路径会建它）。<b>但"本层产出的每一条文本都在
/// 活库上跑过"这一句今天仍然不成立</b>，而它不成立的只剩两格，两格的原因不同：<b>带模式限定的那一形 DDL</b>
/// 留在 <c>OfflineText</c>，因为夹具那十一张表全建在 <c>main</c> 里（契约里没有模式，<c>main.</c> ∶ <c>side.</c>
/// 那两句是探针手写的）；<c>Identity_ExplicitInsert</c> 留在 <c>VendorDoc</c>，因为这一位在 <c>src/</c> 里
/// 连一句可发的文本都没有。上一版在本格写的"<c>CodeFirst</c> 的产物没有执行口"由本笔作废，四处同源措辞一起改口。
/// 逐位的档位与各自的原因记在 <c>CapabilityLedgerTests</c> 本家那一段，不在这里重述。</para></summary>
[QuickSqlProvider(DbKind.Sqlite)]
public sealed class SqliteProvider : IDbProvider
{
    /// <summary>内建表里的 <c>key</c>。<c>assembly</c> 写 <c>Microsoft.Data.Sqlite</c>、<c>factoryType</c>
    /// 写 <c>Microsoft.Data.Sqlite.SqliteFactory</c> —— 后一颗名字是<em>现场读到的</em>而不是文档口径
    /// （出处就是第二段那四颗 A0 系列）。按"名字读过而内建表不写"这一格本家排第三：<c>sqlserver</c> 那一行
    /// 从第一天就写着实名，虚谷在 v0.32 结算日也当场读到过工厂实名（<c>XuguClient.XGProviderFactory</c>，
    /// §9.1 那一行第五列）而它的内建表行照旧省着那颗，<b>本行是第一次把现场读到的实名写进内建表</b>。
    /// 名字与 <see cref="DbKind.Sqlite"/> 的 <c>ToString().ToLowerInvariant()</c> 一致，
    /// 所以 <c>key</c> 不需要额外映射表。</summary>
    public const string DriverKey = "sqlite";

    readonly SqliteTypes _types = SqliteTypes.Instance;
    readonly SqliteValues _values = SqliteValues.Instance;
    readonly SqliteExpressions _expressions = new();
    readonly SqliteKeys _keys = new();
    readonly SqliteDialect _dialect;

    public SqliteProvider(DbProviderFactory driverFactory)
    {
        Connectors = new SqliteConnectorFactory(driverFactory);
        CodeFirst = new SqliteCodeFirst(_types);
        _dialect = new SqliteDialect(_types, _keys, _values, _expressions);
    }

    public DbKind Kind => DbKind.Sqlite;

    /// <summary>位掩码本体，五位各自的产出文本与读数都写在类注释第四段；不声明的十四档逐条在第五段。
    /// 它不是 <c>virtual</c> 而是一句直给：这一族今天没有叶子，而"不留一颗没人覆写的虚函数"这条纪律
    /// 已经立过四次。</summary>
    public DbCapabilities Capabilities =>
        DbCapabilities.Paging_LimitOffset | DbCapabilities.Returning | DbCapabilities.Identity_ExplicitInsert |
        DbCapabilities.Schemas | DbCapabilities.MultiStatement | DbCapabilities.Transactions;

    public IConnectorFactory Connectors { get; }

    public ISqlDialect Dialect => _dialect;

    public ITypeMap Types => _types;

    public IValueBinder Values => _values;

    public IExpressionRules Expressions => _expressions;

    public ICodeFirstDialect CodeFirst { get; }

    public ISchemaReader Schema { get; } = new SqliteSchemaReader();

    public IKeyStrategy Keys => _keys;

    public IConcurrencyStrategy Concurrency { get; } = new SqliteConcurrency();

    public IChangeGuard ChangeGuard { get; } = new SqliteChangeGuard();

    /// <summary>没有批量策略（类注释倒数第二条）。这一格与 <c>XuguProvider</c>、<c>Gbase8sProvider</c>
    /// 同一档而同因：那颗驱动给不出一个可报的高速通道，也给不出一个可报的参数预算。</summary>
    /// <remarks><b>【本笔表态】</b>批量阶梯铺开时这一家两档都不接，而它比另两 null 家多一格"看着近、实际远"：
    /// <b>L1</b> —— 这一族没有装载类形状的 API（它的高速面是"一条事务包住一批语句"，那是执行编排不是装载通道，
    /// §12.1 的阶梯里根本没有那一层），预算那颗照旧报不出；<b>Merged</b> —— 默认 false，尽管这一家的驱动
    /// <em>看起来</em>收得下分号批（活实例在场、集成册跑绿过），"看起来收得下"与"仓库发出去的一条批被它逐句执行、
    /// 行数按句回报"之间差的是一场没跑过的读数 —— 与 MySQL 那颗 true 的对照正落在这里：那一颗有 postRead
    /// 回填支每轮绿背书，本家这一格今天只有推断。重开条件一句话：集成册补一场多语句批，读数当场改判。</remarks>
    public IBulkStrategy? Bulk => null;

    /// <summary>SQLite 打开连接即创建文件，所以支持自动建库。</summary>
    public bool CanCreateDatabase => true;

    /// <summary>打开再关闭一次连接：文件不存在时驱动会自动创建，已存在时是空操作。
    /// 不建表、不写任何内容 —— 那是 <c>SchemaSyncer</c> 的职责。</summary>
    public async Task EnsureDatabaseExistsAsync(string connectionString, CancellationToken ct = default)
    {
        await using var connection = Connectors.CreateConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
    }
}
