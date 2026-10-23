using System.Data.Common;
using QuickSql.Metadata;
using QuickSql.Providers.PostgreSqlBase;

namespace QuickSql.Providers.DuckDb;

/// <summary>DuckDB 的驱动槽位（§9.1 的 PG 系那一叠里的第七颗叶子，v0.39 的 0.39.0-beta.21 落笔）。
///
/// <para>它是 PG 血统里<b>第一颗真正改了血统产出的叶子</b>：此前六颗（金仓/openGauss/GaussDB/瀚高/
/// GBase 8c/PG 本尊）在文本层全部零覆写、产出逐字同形，而 DuckDB 与血统的分岔<b>是厂商文档明写的</b>
///【文档·DuckDB，方言与 PG 兼容见
/// <see href="https://duckdb.org/docs/current/sql/dialect/postgresql_compatibility.html"/>】，所以
/// 本家不是一层空壳 —— 覆写的三格全部有文档出处，逐格记在对应类注释里：
/// <list type="bullet">
/// <item><b>类型表</b>：<see cref="DuckDbTypes"/>（<c>DOUBLE</c>/<c>TIMESTAMP</c>/<c>TIMESTAMPTZ</c>/
/// <c>TIME</c> 那四格换名，出处 URL 在类型表类注释）。</item>
/// <item><b>DDL</b>：<see cref="DuckDbCodeFirst"/>（身份列换形态：句外序列 + <c>DEFAULT nextval</c>，
/// 出处 URL 在那一格）。</item>
/// <item><b>"建库"整格不存在</b>：<see cref="CanCreateDatabase"/> 落 false、
/// <see cref="EnsureDatabaseExistsAsync"/> 抛 <see cref="NotSupportedException"/> —— 本家是内嵌引擎，
/// "库"就是连接串里那颗文件，与 SQLite 同档；没有服务端，就没有"连到某座默认库去 CREATE DATABASE"
/// 这一条路可走。这一格<b>不是"没读数所以不赌"</b>，是"这一问在本家没有对象"（与 SQLite 那一本台账
/// 量到的"少一类问题"同一形状）。</item>
/// </list></para>
///
/// <para><b>本家没有覆写的那些格子</b>，是<b>贴着文档对完之后裁"同形"</b>的：分页 <c>LIMIT/OFFSET</c>
/// 与血统同形（两子句独立，不要求 ORDER BY）、布尔谓词 <c>IS TRUE</c>/<c>IS FALSE</c> 与 <c>false</c>/
/// <c>true</c> 字面量、句尾 <c>RETURNING</c>（键回读）、<c>ON CONFLICT</c> 档位【文档·DuckDB】、
/// <c>COMMENT ON COLUMN/TABLE</c>、<c>ALTER COLUMN … TYPE</c> 双句 —— 这些在
/// <see href="https://duckdb.org/docs/current/sql/statements/insert.html"/>、
/// <see href="https://duckdb.org/docs/current/sql/meta/information_schema.html"/> 那几张同家属表里
/// 对得上血统的形状。而它们的<b>代码实际上来自血统</b>（本家没重发一份），所以那几句在 §14 那
/// "方言紧随 PG 但不是 PG"的差额里记半边：文本层走的是血统的代码，库侧收不收全部归未实测。</para>
///
/// <para><b>驱动</b>：内建表那颗键的 assembly 名是 <c>DuckDB.NET.Data</c>（官方 ADO.NET 驱动，
/// NuGet 包按原生伴件分档），不写 factoryType 走兜底扫描 —— 与金仓/达梦那一档同一判据
///（名字是厂商文档口径、从未在任何机器上解析成功过【文档·DuckDB：驱动页见
/// <see href="https://duckdb.org/docs/current/dev/dotnet.html"/>】；本机没有引擎、也没有驱动包）。
/// "已适配"三句在本家一句都够不上：整层是【文档·DuckDB】口径 + 未实测台账。</para></summary>
[QuickSqlProvider(DbKind.DuckDb)]
public sealed class DuckDbProvider : PostgreSqlProviderBase
{
    /// <summary>内建表里的 <c>key</c>（值照 <c>DbKind.DuckDb.ToString().ToLowerInvariant()</c> 落）。</summary>
    public const string DriverKey = "duckdb";

    /// <summary><see cref="PostgreSqlProviderBase"/> 那道缝的第一格：类型表换本家自己的
    /// <see cref="DuckDbTypes"/>。为什么类型表要换而方言不够，缺口在
    /// <see cref="PostgreSqlProviderBase"/> 的 <c>Types</c> 抬头里写全了（CAST 那条路读的是属性）。</summary>
    public override ITypeMap Types => DuckDbTypes.Instance;

    /// <summary>血统那道缝的又一行，方向与前两格相反：<b>把 PG 那份完整性码表退回"判不出"</b>。
    /// DuckDB 说的是 PG 的线协议，可约束违例在它自己那一侧报的不是 PG 的 SQLSTATE 家数
    /// （23505 / 23502 / 23503 / 23514 这一套是服务端的行为，不是协议规定的），
    /// 而本家到今天没有一条真库用例撞过其中任何一颗。§8.4 的判据是"误分类比不分类坏"——
    /// 拿别人的码表猜自己家，猜中的结果是用户把一句约束错误读成"重复提交"再点一次。
    /// 翻这一格的条件写在这里：集成册那条 <c>IntegrityErrorTranslationPathTests</c> 在本家跑绿
    /// （它今天跑在 SQL Server 与 PG 上），就把这一行删掉跟着血统走。</summary>
    public override IntegrityViolation? ClassifyIntegrityError(DbException error) => null;

    public DuckDbProvider(DbProviderFactory driverFactory)
        : base(DbKind.DuckDb, driverFactory)
    {
    }

    /// <summary>血统那道缝的第二格：DDL 换本家的 <see cref="DuckDbCodeFirst"/>（身份列换形态那一份）。
    /// 参数 <paramref name="types"/> 是血统传上来的那份 <see cref="PostgreSqlTypes"/>，本家不接它 ——
    /// DDL 与类型表是同命的，换 DDL 就换同一份类型表。</summary>
    protected override ICodeFirstDialect CreateCodeFirst(PostgreSqlTypes types) => new DuckDbCodeFirst(DuckDbTypes.Instance);

    /// <summary>血统那道缝的第三格：方言是 <see cref="DuckDbDialect"/> —— 文本层与血统的差额只有
    /// <c>GetStoreType</c> 那一颗读的表；血统的方言类本来 <c>public</c> 可继承（不是 <c>sealed</c>），
    /// 本家的子类只覆写真正分岔的那一档。</summary>
    protected override PostgreSqlDialect CreateDialect(
        DbKind kind, PostgreSqlTypes types, PostgreSqlKeys keys, PostgreSqlValues values, PostgreSqlExpressions expressions)
        => new DuckDbDialect(kind, keys, values, expressions);

    /// <summary><b>"建库"这一问在本家没有对象</b>：内嵌引擎没有服务端，"库"就是连接串里那颗文件，
    /// 打开即有（与 SQLite 同档）。表的门在 <c>SchemaSyncer</c> 那一句话里（按这颗位绕开
    /// <see cref="EnsureDatabaseExistsAsync"/>），所以这一格落 false 是<b>可执行的那一半</b>。</summary>
    public override bool CanCreateDatabase => false;

    /// <summary>血统那一份的整格直达<b>不可达</b>：进门第一道闸（<see cref="CanCreateDatabase"/>）
    /// 已经是 false，而虚口保真问题的另一半是"谁绕过闸来问" —— 答案是抛：调用方拿一句
    /// <see cref="NotSupportedException"/>，消息里写明"本家的库是那颗文件"，而不是静默走到血统那句
    /// 连 <c>postgres</c> 锚点库的查询（那一句在本家既没有锚点库也没有连接面）。</summary>
    public override Task EnsureDatabaseExistsAsync(string connectionString, CancellationToken ct = default)
        => throw new NotSupportedException(
            "DuckDB 是内嵌单文件引擎：\"库\"就是连接串里那颗文件，没有\"建库\"这一操作（血统 PG 的 EnsureDatabaseExistsAsync 在本家不可达）。");
}

/// <summary>本家的文本层：血统 <see cref="PostgreSqlDialect"/> 的子类，只覆写
/// <c>GetStoreType</c> 一格 —— 换读 <see cref="DuckDbTypes"/> 那份表。这一格开成覆写而不是
/// "方言另起一份"的理由写全：血统方言的其余各格（分页两子句、布尔谓词两形、恒假字面量、
/// <c>UPDATE … FROM</c> 的折并、句尾 RETURNING）在 DuckDB 的文档口径里<b>同形</b>【文档·DuckDB】，
/// 另起一份就会把那一批"同形"变成一堆再也不随血统走的复件；覆写则它们跟着血统同一份改动走。
///
/// <para><b>基类构造参数里那颗 <c>PostgreSqlTypes</c> 本类不收</b>（传 null 是刻意的）：血统方言
/// 只有 <c>GetStoreType</c> 读它，而这一格已由本类覆写补位 —— null 传进去的每一条路径都在覆写层的
/// 后面。各共享槽位（keys/values/expressions）仍经血统那道缝逐格递入。</para></summary>
sealed class DuckDbDialect(DbKind kind, PostgreSqlKeys keys, PostgreSqlValues values, PostgreSqlExpressions expressions)
    : PostgreSqlDialect(kind, null!, keys, values, expressions)
{
    public override string GetStoreType(ColumnContract column) => DuckDbTypes.Instance.GetStoreType(column);
}
