using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using QuickSql.Metadata;
using QuickSql.Sql;
using QuickSql.Sql.Ast;
using QuickSql.Values;

namespace QuickSql.Providers;

/// <summary>驱动 SPI（§4）。替代 FreeSql 的 <c>DbProvider</c> 巨类：每个子接口只回答一类问题，
/// provider 之间只能继承公共血统基类，不得互引内部实现。</summary>
public interface IDbProvider
{
    DbKind Kind { get; }

    DbCapabilities Capabilities { get; }

    /// <summary>ADO.NET 连接/命令/参数，反射绑定驱动（§1.2 原则 3：加载可以反射，调用不行）。</summary>
    IConnectorFactory Connectors { get; }

    ISqlDialect Dialect { get; }

    ITypeMap Types { get; }

    IValueBinder Values { get; }

    IExpressionRules Expressions { get; }

    ICodeFirstDialect CodeFirst { get; }

    ISchemaReader Schema { get; }

    IKeyStrategy Keys { get; }

    IConcurrencyStrategy Concurrency { get; }

    /// <summary>变更守卫的比较形状（§12.4 第 2 项）。合并语句靠它把「一行都没变的行」挡在 <c>UPDATE</c> 之外，
    /// 而"怎么挡"只有两形（一句 <c>IS DISTINCT FROM</c> 对三段 <c>OR</c>），今天的分布是 <b>1∶4</b>
    /// （金仓那一族一句，T-SQL、Oracle 血统、MySQL 血统与虚谷四家三段；第四家落进来的理由与前三家不同一条，
    /// 见 <c>MySqlChangeGuard</c>，第五家又不同一条，见 <c>XuguChangeGuard</c>），所以它归 provider，
    /// 与 <see cref="Concurrency" /> 同一个道理。<b>这一格从今天起是"一家对多数派"</b>：
    /// 五家里四家走三段，共用层那个三段形状因此不再需要为少数派开分支。</summary>
    IChangeGuard ChangeGuard { get; }

    /// <summary>为 null 时批量退回链自然从 L3 起（§12.1）。</summary>
    IBulkStrategy? Bulk { get; }

    /// <summary>该 provider 是否能自动创建数据库（<c>CREATE DATABASE</c> 或等价操作）。
    /// 默认 false：大多数 provider 需要连接到一个已存在的服务器才能建库，而连接串里通常已经指定了库名。
    /// SQLite 是例外：打开连接即创建文件，所以它覆写为 true。</summary>
    bool CanCreateDatabase => false;

    /// <summary>确保目标数据库已存在；不存在则创建。只在 <see cref="CanCreateDatabase"/> 为 true 时有意义。
    /// 默认抛 <see cref="NotSupportedException"/>：不支持建库的 provider 不应被调用此方法。
    /// <para>调用时机：<c>SchemaSyncer</c> 在 Execute 模式下、打开主连接之前调用，
    /// 确保后续建表语句不会因库不存在而失败。</para></summary>
    Task EnsureDatabaseExistsAsync(string connectionString, CancellationToken ct = default)
        => throw new NotSupportedException(
            $"Provider {GetType().Name} 不支持自动创建数据库（{nameof(CanCreateDatabase)}=false）。" +
            "请先手动建库，或改用支持建库的 provider（如 SQLite）。");
}

/// <summary>连接与命令的创建。实现方允许在构造期反射定位驱动类型，之后一律走缓存委托。</summary>
public interface IConnectorFactory
{
    DbConnection CreateConnection(string connectionString);

    DbCommand CreateCommand(DbConnection connection);

    /// <summary>驱动是否已经就位。缺失时给出可执行的修复建议，而不是 <c>NullReferenceException</c>。</summary>
    bool TryEnsureLoaded(out string? failure);
}

public interface ITypeMap
{
    string GetStoreType(ColumnContract column);

    /// <summary>填上 provider 才知道的事实：<see cref="ColumnContract.Unicode" />、
    /// <see cref="ColumnContract.LengthSemantics" />、<see cref="ColumnContract.MaxBytes" />、
    /// <see cref="ColumnContract.StoreTypeName" />。量纲由库决定，不由注解决定（§10.4）。</summary>
    ColumnContract ApplyStorageDefaults(ColumnContract column);

    /// <summary>存储类型名 → CLR 类型。读结构（<see cref="ISchemaReader"/>）与 <c>doctor</c> 用。</summary>
    bool TryGetClrType(string storeTypeName, out Type? clrType);

    /// <summary>该库上这个列的长度量纲（§10.4）。</summary>
    LengthSemantics GetLengthSemantics(ColumnContract column);
}

public sealed record FunctionMapping(string Template, bool RequiresNonNegativeLength = false)
{
    /// <summary>把实参填进模板。<c>{0}</c> 是第一个实参。</summary>
    public string Format(params string[] arguments) => string.Format(Template, arguments);
}

/// <summary>C# 方法/运算符 → SQL 函数（§5.8）。映射不到就抛 <c>UntranslatableExpressionException</c>，
/// 绝不静默客户端求值。</summary>
public interface IExpressionRules
{
    /// <summary><paramref name="key"/> 有两种形态，都由 Core 侧统一生成、不接受调用方手拼：
    /// 符号键是 <c>Fn.Key("str.substring", 3)</c> 出来的 <c>str.substring/3</c>（<b>元数是键的一部分</b>，
    /// 否则 <c>Math.Round(x)</c> 与 <c>Math.Round(x,2)</c> 会共用一个模板，多余实参被 string.Format 静默丢掉）；
    /// 覆写键是 <c>System.String.ToUpper</c> / <c>System.DateTime.get_Year</c> 这类方法全名。</summary>
    bool TryGetFunction(string key, out FunctionMapping mapping);

    /// <summary>覆写点：现场遇到某库的函数名不一样时注册，不改代码。</summary>
    void Register(string key, FunctionMapping mapping);

    string ConcatOperator { get; }

    /// <summary><c>LIKE</c> 的转义符。SQL Server 只有 ESCAPE 子句、无 <c>\</c> 默认转义。</summary>
    char LikeEscapeCharacter { get; }

    bool SupportsRegex { get; }
}

/// <summary>建表/改表/注释/大小写（§9.2）。P0 只产出 DDL 文本与黄金样本比对，不执行。</summary>
public interface ICodeFirstDialect
{
    IdentifierPolicy Identifiers { get; }

    void EmitCreateTable(SqlWriter writer, EntityContract contract);

    void EmitAddColumn(SqlWriter writer, TableInfo table, ColumnContract column);

    void EmitAlterColumnPrecision(SqlWriter writer, TableInfo table, ColumnContract column);

    /// <summary>返回 false 表示这一家没有列注释这一档：Core 不重试、不改走别的形状，而是把那一列记进
    /// 同步结果的 <c>NotJudged</c> 清单并指名（§9.5；<see cref="TryEmitTableComment"/> 同一取向）。
    /// <b>它必须幂等</b>：§9.5 的备注同步会在"备注已经存在而值变了"的列上重发这一句，
    /// 只发 <c>ADD</c> 的那一家在第二趟会撞"属性已存在"。</summary>
    bool TryEmitColumnComment(SqlWriter writer, TableInfo table, ColumnContract column);

    /// <summary>表级备注。<b>默认 false = 这一家不接</b>（新增 SPI 槽位一律带默认值，否则接一家要改六家，
    /// 与 <see cref="IDbProvider.CanCreateDatabase"/>、<see cref="IBulkStrategy.ResolveCopyChannel"/> 同一手法）。
    /// 返回 true 的家在 Core 那里同时得到"能读回表备注"的义务（<see cref="SchemaTable.Comment"/>），
    /// 否则写进去的备注下一趟判不出差别 —— 那是单向发射，不是同步。</summary>
    bool TryEmitTableComment(SqlWriter writer, TableInfo table, string? comment) => false;

    void EmitCreateIndex(SqlWriter writer, TableInfo table, IndexContract index);

    /// <summary>删掉库里<em>读回来</em>的那一条索引。<b>默认 false = 这一家不接</b>（新增 SPI 槽位一律带默认值，
    /// 接一家不必改六家，与 <see cref="TryEmitTableComment"/> 同一手法）。
    /// <para><b>Core 只在调用方显式开了"允许删索引"那一档时才问这一颗</b>：默认形态下结构同步
    /// 永不 <c>DROP INDEX</c>——它会当场拖慢正在跑的查询，是这一档里最难回滚的动作，而契约里没有这条索引
    /// 可能只是"这一册实体没覆盖那张表的全部查询"，不代表它没人用。没开的时候库里多出的那一条只进
    /// 同步结果的"要人工"清单并指名。</para>
    /// <para><b>接了这一颗的家同时欠着"读得回索引清单"的义务</b>（<see cref="SchemaTable.Indexes"/>）：
    /// 读不回来的那一家里"库里多出一条"根本判不出来，而按"契约里没有"去删就是把<em>读不出</em>读成"没有"、
    /// 删掉全部索引。那条形状由 <see cref="SchemaTable.Indexes"/> 的 null 挡在计划层，不靠这里。</para></summary>
    bool TryEmitDropIndex(SqlWriter writer, TableInfo table, SchemaIndex index) => false;
}

/// <summary>系统表读结构（§4 里差异最大的部分）。</summary>
public interface ISchemaReader
{
    Task<SchemaTable?> ReadTableAsync(DbConnection connection, TableInfo table, CancellationToken ct = default);

    /// <summary>§10.4 的量纲探测：只读，不建临时表、不写数据（生产库常禁 DDL 权限）。</summary>
    Task<LengthSemanticsReading?> ProbeLengthSemanticsAsync(DbConnection connection, TableInfo table, string columnName, CancellationToken ct = default);

    /// <summary>"库里这一列"与"契约说的这一列"是不是同一个形状。列级结构同步（§9.5）的判据位。
    ///
    /// <para><b>它归 provider 而不是 Core，只因为一件事</b>：类型名怎么折、量纲按字符还是字节、精度藏在哪颗
    /// 目录字段里，全是这一家自己的目录说了算。Core 拿 <see cref="SchemaColumn.StoreType"/> 文本与
    /// <see cref="ITypeMap.GetStoreType"/> 的发射形直接比，今天必然假红：发射侧一律大写而目录一律小写，
    /// <c>DATETIME2(3)</c> 发出去而目录回 <c>datetime2</c> 加一颗标度，<c>DECIMAL</c> 在别人手工建的表上
    /// 回 <c>numeric</c>，而 GBase 8s 的 <c>(n)</c> 是字节不是字符。跨家比文本更是没有这回事。</para>
    ///
    /// <para>三态里的 <b>null 是"这一家今天判不了"</b>，不是"判过了、一样"：那一格进同步结果的"判不了"清单并指名，
    /// 既不响也不假装同步过（§12.2）。默认实现交回 null，所以六家在本版一个字节都不必改。</para>
    ///
    /// <para>可空性<em>不在</em>这一颗的职责里：那是一格布尔，家与家同义，Core 自己比
    /// （比的是 <see cref="ColumnContract.NullableInStore"/>，不是 <see cref="ColumnContract.IsNullable"/>）。</para></summary>
    bool? ColumnShapeMatches(ColumnContract expected, SchemaColumn actual) => null;

    /// <summary>库里这一列<em>现在</em>有没有 NULL 行。只有结构同步要把一列<em>收紧</em>成
    /// <c>NOT NULL</c> 时才问（§9.5，判据见 SchemaSyncResult.SkippedNullabilityTightening 那一格）。
    ///
    /// <para><b>为什么归 provider 而不是 Core 自己发那句 <c>SELECT</c></b>：这一句要的是这一家的
    /// 标识符字形（双引号 / 反引号 / 方括号）与这一家的"取第一行"写法（<c>LIMIT</c> ∶ <c>TOP 1</c> ∶
    /// <c>FETCH FIRST</c>），Core 手里没有这两格，猜出来的文本在六家之一是语法错。与
    /// <see cref="ProbeLengthSemanticsAsync"/> 同一个处境，也只读、不写。</para>
    ///
    /// <para><b>三态的 <c>null</c> 是"这一家没接"</b>，不是"没有 NULL"：那一格 Core 照今天那样把
    /// <c>ALTER</c> 发出去，并在同步结果的"判不了"清单里点名这一列——脏表上那一发仍然会以库的拒绝收尾，
    /// 那是没接这一颗的家的既有行为，不靠默认值假装修好了。接了一家的默认值就<em>不该</em>是
    /// <see langword="false"/>：那等于对所有未接的库宣布"这张表干净"，而这句话只有库答得起。</para></summary>
    Task<bool?> ColumnHasNullsAsync(
        DbConnection connection,
        TableInfo table,
        string columnName,
        CancellationToken ct = default) => Task.FromResult<bool?>(null);
}

/// <summary>库里读回的一列。<b>后头那几格全可空，而空的意思是"这一家读不出"</b>，不是"库里没有"——
/// 所以每一格在 <see cref="ISchemaReader"/> 那一家支持时必须给真值（列备注没有就是空串，
/// 自增不是就是 <see langword="false"/>），不给就是放弃那一档 diff。</summary>
/// <param name="Name">列名。</param>
/// <param name="StoreType">归一到<em>本家建侧字形</em>的类型文本（各家 reader 自己折长名，
/// 所以它只保证同家自比；跨家、跨"故意不折"那几支都不作数，见 <see cref="ISchemaReader.ColumnShapeMatches"/>）。</param>
/// <param name="MaxChars">字符上限；读不出的家给 null。</param>
/// <param name="MaxBytes">字节上限；只有一家用另一把尺量它（MySQL/Oracle），其余留 null。</param>
/// <param name="IsNullable">库里这一列能不能为空。</param>
/// <param name="OrdinalPosition">列序。</param>
public sealed record SchemaColumn(
    string Name,
    string StoreType,
    int? MaxChars,
    int? MaxBytes,
    bool IsNullable,
    int? OrdinalPosition)
{
    /// <summary>列备注。<b>null = 这一家没有列注释这个概念</b>（SQLite、GBase 8s），空串 = 有这概念而库里没写。
    /// 两者分不开的话，"备注同步"就永远只有单向发射：写进去的备注下一趟读不回来，判不出差别。</summary>
    public string? Comment { get; init; }

    /// <summary>这一列是不是库给值的那几列（自增 / <c>rowversion</c>）。null = 读不出。</summary>
    public bool? IsIdentity { get; init; }

    /// <summary>这一列是不是主键的一员。null = 读不出。</summary>
    public bool? IsPrimaryKey { get; init; }

    /// <summary>标度（小数的位数，也是 <c>datetime2</c> 那一族的时间精度）。null = 本家没有这一格或没读。</summary>
    public int? Scale { get; init; }
}

/// <summary>库里读回的一张表。扩出来的两格同样以 <b>null = 这一家读不出</b> 说话。</summary>
/// <param name="Table">表名。</param>
/// <param name="Columns">列清单，按 <see cref="SchemaColumn.OrdinalPosition"/> 的顺序。</param>
public sealed record SchemaTable(TableInfo Table, IReadOnlyList<SchemaColumn> Columns)
{
    /// <summary>表备注。null = 读不出；空串 = 这一家有这概念而库里没写。</summary>
    public string? Comment { get; init; }

    /// <summary>这张表上现有的索引（含唯一性）。<b>null = 读不出 ⇒ 补建索引那一档在这一家判不了</b>；
    /// 空清单 = 读得出而这张表确实一个索引都没有。主键不在这一格里（它不是"补建"的对象）。</summary>
    public IReadOnlyList<SchemaIndex>? Indexes { get; init; }
}

/// <summary>库里读回的一个非主键索引。<b>列的顺序是内容的一部分</b>：<c>(a,b)</c> 与 <c>(b,a)</c>
/// 是两个索引，名字一样也不能当成同一个。</summary>
/// <param name="Name">索引名（不是契约里那颗名字，是库里那一颗）。</param>
/// <param name="IsUnique">是不是唯一索引。</param>
/// <param name="Columns">列序，内容的一部分。</param>
public sealed record SchemaIndex(string Name, bool IsUnique, IReadOnlyList<string> Columns)
{
    /// <summary>这一颗其实是<em>约束</em>（唯一约束那一形，不是 <c>CREATE UNIQUE INDEX</c> 的产物）。
    /// <b>它和主键同一个处境：不是"补建"的对象，也不是 <c>DROP INDEX</c> 能删的对象</b>——那一句在
    /// SQL Server 上直接报错，要拆它得走 <c>ALTER TABLE … DROP CONSTRAINT</c>。所以开了删索引那一档时
    /// 这一条不进删除清单，只进"要人工"清单并指名它是约束。</summary>
    public bool IsConstraint { get; init; }
}

public sealed record LengthSemanticsReading(string CollationName, int DeclaredLength, int DefinitionByteLength, bool CharSemanticsProven);

public interface IKeyStrategy
{
    /// <summary>能否在批量插入后一次取回全部主键（<c>OUTPUT</c> / <c>RETURNING</c>）。
    /// DM/Oracle 系为 false，这是 §16.9 那个决定的代价来源。</summary>
    bool SupportsBulkKeyRetrieval { get; }

    bool SupportsExplicitIdentityInsert { get; }

    /// <summary>把 <c>OUTPUT inserted.[a],[b]</c> / <c>RETURNING a,b</c> 写进 writer。</summary>
    void EmitReturning(SqlWriter writer, IReadOnlyList<ColumnContract> returning);

    /// <summary>打开/关闭 <c>SET IDENTITY_INSERT</c> 这类会话开关。</summary>
    string? IdentityInsertToggle(SqlWriter writer, TableInfo table, bool on);

    /// <summary>「语句之后回读」那一档在不在（v0.34 立的口子，§14 卡了两家的缺口从此有形状）。
    /// 为 true 的含义窄而硬：当 <see cref="SupportsBulkKeyRetrieval"/> 为 false、且回填集<b>恰好是一颗自增列</b>时，
    /// Core 把插入逐条到每语句一行，并调 <see cref="EmitPostStatementKeyRead"/> 把回读句<em>接在同一句命令文本的
    /// 尾巴上</em>（同一条命令 = 同一个会话，<c>LAST_INSERT_ID()</c> 这类会话级函数才问得着刚插的那一颗）。
    /// 并发列不在这一档的覆盖里（回读句给不了它们的值），多颗自增列同理 —— 两种都落回
    /// <see cref="EmitReturning"/> 的拒绝路径。建模时<b>不</b>碰"多行 id 连续性"：那条路要连自增锁模式一起
    /// 建模（§14 的 MySQL 那格），而逐条+每句一带回读对单颗键不依赖任何锁模式。</summary>
    bool SupportsPostStatementKeyRead { get; }

    /// <summary>把"取回刚才那条单行 INSERT 由库生成的那一颗键"的回读句（如 <c>SELECT LAST_INSERT_ID()</c>）
    /// 写进 writer；调用方（Core）负责前面的 <c>;</c> 分隔，与 <see cref="EmitReturning"/> 的空格纪律同一族。
    /// 只在 <see cref="SupportsPostStatementKeyRead"/> 为 true 时才会被调用。</summary>
    void EmitPostStatementKeyRead(SqlWriter writer, ColumnContract key);
}

public enum ConcurrencyMode { None, RowVersion, FullColumnCompare, TimestampColumn }

public interface IConcurrencyStrategy
{
    ConcurrencyMode Mode { get; }

    /// <summary>给一列生成"库里这一列还是我读到的那一版"的比较式。<b>调用点在发射之前</b>：内核把它合取进
    /// AST 的 <c>WHERE</c>，随后 AST 成文，才轮到 <c>CudBuilding</c>（§10.1、§8.2 的落地记录）。
    /// 早先那句"追加到文本尾巴、所以在 CudBuilding 之后"不成立：两家的收尾位置不同
    /// （T-SQL 的 <c>OUTPUT</c> 在 <c>FROM</c> 之前，PG 系的 <c>RETURNING</c> 在整句之后），文本级追加必在一家写出语法错。</summary>
    SqlExpression BuildPredicate(SqlExpression target, ColumnContract column, object? originalValue);
}

/// <summary>「库里这一格与行集里那一格是不是不同」的比较形状（§12.4 第 2 项的变更守卫）。
/// 归 provider 是因为只有它知道两件事：哪一家有 <c>IS DISTINCT FROM</c>，以及哪一型在它那儿连等号都不接受。</summary>
public interface IChangeGuard
{
    /// <summary>这一列能不能进守卫。<b>假是一等公民而不是异常</b>：返回 false 时调用方必须把整批退回逐条，
    /// 而不是把这一列从守卫里悄悄省掉 —— 省掉一列的守卫会把「只改了那一列」的行也挡掉，那是丢写。</summary>
    bool Compares(ColumnContract column);

    /// <summary>「不同则真」的比较式。<paramref name="stored"/> 是表侧、<paramref name="incoming"/> 是行集侧：
    /// 两侧的 NULL 语义不对称（表侧可能是 NULL，行集侧是这次要写的值），所以形状不能只看一个类型。</summary>
    SqlExpression BuildDifference(ColumnContract column, SqlExpression stored, SqlExpression incoming);
}

/// <summary>批量高速层（§12.1 的 L1/L2/L4）。P0 的 provider 一律返回 null，退回链自然从 L3 起。</summary>
/// <remarks><b>§12.1 那张决策表里今天在场的只有两颗</b>：<see cref="ParameterBudget"/> 有四个真读数（2100 / 32767 /
/// 65535 / 没有 provider 报的那些家走共用层的 100），<see cref="ResolveCopyChannel()"/> 从本版起有三家落地
/// （SQL Server 的 <c>SqlBulkCopy</c>、MySQL 血统的 <c>MySqlBulkCopy</c>、PG 血统的二进制 COPY 导入器），
/// 三家同一手法：反射绑定、第一次取用时才找类型、缺成员整家退 L3 —— 装载类都不在
/// <c>DbProviderFactory</c> 的抽象面上，而本仓库的编译期驱动引用纪律不许直接 <c>new</c> 它们（§1.2 原则 3）。
/// <b>"缺成员整家退 L3"这一条是三家共同的<em>看不见</em>的失败</b>：那个形状与"这一家根本没有装载类"逐字同形，
/// 离线册只能证形状不能证命中，所以每一家都要在真库上跑一次才算接上【实测·MySQL 血统那一家第一版就是这么
/// 静默退了一整版 —— 驱动在 C# 调用面与反射面之间有四格形状差（可选参数、<c>ValueTask</c> 返回值、
/// 构造子重载、没有 <c>Dispose</c> 面），逐颗见 <c>MySqlBulkCopyChannel</c> 的改判段】。
/// 三家的差别只记在两格里：闸门（只有 MySQL 家欠连接串一颗参数，见 <see cref="IBulkCopyChannel.UnavailableBecauseOf"/>）
/// 与载体（两家交 <c>DataTable</c>、一家逐行推）。原稿那颗 <c>CanResolveKeys</c> 没有进来，因为它要答的问题（"批量 + 要主键能不能成立"）
/// 今天由 <c>InsertEngine</c> 自己判得更准：那一档不是"通道能不能给键"，是"这一条出口要不要键"，
/// 而后者是出口的形状不是库的能力，放在这里会让 provider 替调用方决定它自己没问过的东西。
/// <para><b>【本笔补的一句 —— 上面那句"今天在场的只有两颗"从今天起要读成四颗】</b>：写侧扩展这两版又挂上两颗
/// 带默认值的表态槽（<see cref="SupportsMergedStatements"/> 与 <see cref="UpsertClause"/>），它们不在 §12.1
/// 那张<em>插入</em>阶梯的决策表里，而在 <c>UpdateRange</c> 那条链的选层判据里，所以那句"决策表里在场的只有两颗"
/// 说的是那张表，不是这颗接口。四颗共同的形状：<b>默认值就是"不接"</b>，接一家不必改其余十四家。</para></remarks>
public interface IBulkStrategy
{
    int ParameterBudget { get; }

    string Describe { get; }

    /// <summary>L1 装载通道（§12.1 第三行）。交回 <see langword="null"/> = 这一家不接（或这一支驱动里没有装载类），
    /// 阶梯自然从 L3 起 —— 默认实现让不接的每一家一个字节都不必改，这与 <see cref="IDbProvider.CanCreateDatabase"/> 那颗默认位同一个手法：
    /// <b>新增的 SPI 槽位一律带默认值，否则接一家就要改六家</b>。
    /// <para><b>它是方法而不是属性</b>：<c>RequiresUnreferencedCode</c> 的特性目标里没有属性
    /// （CS0592），而这一颗<em>必须</em>带那一句标注 —— 取它的过程是一次按类型名的反射查找，
    /// <b>被裁剪的驱动会让它交回 null</b>。于是差异是行为级的：同一份代码在裁剪后的产物上报 L3、
    /// 在完整运行时上报 L1。税因此要往上冒到 <c>ExplainBulk</c> 与 <c>ExecuteBulkAsync</c> 两条公开出口上，
    /// 而不是藏在 provider 里当没人负责的实现细节。名字里的 <c>Resolve</c> 说的就是这件事：它不是"取一个已有的值"。</para>
    /// <para>与 <c>DriverRegistry.Factory</c> 同一手法：反射只在第一次取用时走一次，结果（连<em>没有</em>这一种）缓存。</para></summary>
    [RequiresUnreferencedCode("装载通道按类型名在驱动程序集里找（SqlBulkCopy 那一族没有 ADO.NET 抽象面），被裁剪的驱动会交回 null。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：反射解析装载类需要运行时生成代码。")]
    IBulkCopyChannel? ResolveCopyChannel() => null;

    /// <summary>这一家能不能把<b>多条写语句塞进一条命令文本、一次往返发出去</b>（§12.1 批量写链的 Merged 档，
    /// <c>UpdateRange</c> 的整行更新走的那一档）。默认 false = 没有读数证明这一家收得下多语句批，
    /// <c>UpdateRange</c> 的 Auto 因此落逐条 —— 与 <see cref="ResolveCopyChannel"/> 同一手法：
    /// <b>新增 SPI 槽位一律带默认值，表态是覆写者的事，不表态就是 false</b>。
    /// <para>今天覆写为 true 的两家各有各的读数：<b>SQL Server</b>（<c>DbCapabilities.MultiStatement</c>
    /// 从第一天就声明，SqlClient 的分号批是公开契约），<b>MySQL 血统</b>（本仓库已经在一条命令文本里发过
    /// <c>INSERT … ; SELECT LAST_INSERT_ID()</c> 并在授权实例上跑通 —— <c>InsertEngine.Build</c> 的 postRead
    /// 分支与 <c>AdoExecutor.WriteWithResultAsync</c> 那句"批的<em>第一个</em>结果"的注释就是它的复点）。
    /// PG 血统的 Npgsql 同样收批，但本仓库今天没有任何一条产出的批在它那儿跑过，按 §4「代码今天真的走到」
    /// 不声明；Oracle 血统连一颗有出处的驱动读数都没有，更不声明。</para>
    /// <para><b>本笔把这两句声称换成了一条链自己的真库读数</b>：<c>UpdateRange</c> 的合并档（四句 UPDATE 并成
    /// 一条命令）在 SQL Server 与 MySQL 两支上都落库成功、逐列读回一致，而在没表过态的三家（PG 血统、虚谷、
    /// SQLite）Auto 走的确实是逐条、点名 Merged 确实降级、<c>FallbackPolicy.Never</c> 确实拒在连接之前
    /// 【实测·集成册 <c>BulkWritePathTests</c>，五家各一趟：<c>obj/logs/v40-{mysql,sqlserver,postgresql,xugu,sqlite}-bulkwrite-2.log</c>】。
    /// 反面仍然没人补：PG 血统那一家今天有活库，缺的是<em>在它身上跑一次并好的批</em>那一格——
    /// 那一次绿了就把它的 false 翻成 true，不是在这一句里先猜。</para></summary>
    bool SupportsMergedStatements => false;

    /// <summary>这一家"<b>一句</b>把整批改完"的那一段句尾（§12.1 写侧扩展的 Upsert 档，
    /// <c>UpdateRange</c> 点名才走，2026-10-07 的第二条裁决）。交回 <see langword="null"/> = 这一家不接 ——
    /// 与 <see cref="SupportsMergedStatements"/> 同一手法：<b>新增 SPI 槽位一律带默认值，接两家不必改其余十四家</b>。
    /// <para><b>交回的是句尾而不是整句</b>：内核已经按 <see cref="Sql.ISqlDialect"/> 的发出口拼好了
    /// <c>INSERT INTO 表 (列) VALUES (…),(…)</c>，这一颗只补它后面那一段（MySQL 血统的行别名加
    /// <c>ON DUPLICATE KEY UPDATE</c>、PG 血统的 <c>ON CONFLICT … DO UPDATE SET … = EXCLUDED.…</c>）。
    /// 拆成句尾而不是一整句，为的是让列清单、VALUES 的形状与参数表只有<em>一处</em>真相 —— 那一处是内核，
    /// provider 不许自己再拼一遍 VALUES（它一旦拼了，分块与取号就跟着有第二个出处）。</para>
    /// <para><b>两种形状都是量出来的，不是从文档抄的</b>【实测·探针 <c>obj/probe-upsert</c>，
    /// <c>obj/logs/v40-upsert-shapes2.log</c>】：MySQL 8.4.8 上老的 <c>VALUES(col)</c> 写法仍然收下而每句报两条
    /// Warning 1287（厂商写明后续版本移除），所以本槽接的是 8.0.19+ 的行别名写法——同一条三值批在它那儿收下而
    /// <c>SHOW WARNINGS</c> 为空；PG 那台上 <c>ON CONFLICT (键) DO UPDATE SET 列 = EXCLUDED.列</c> 的三值批同样收下。
    /// 两颗共有的那一件事是<em>缺键就插</em>：那是这一档与 Merged 档的<em>语义</em>差而不是速度差，
    /// 所以 <c>BulkWriteMethod.Auto</c> 永远不取它（判据在 <c>UpdateBulkEngine.Decide</c>）。</para>
    /// <para><b>两道拒收不在这一颗里</b>：全局过滤器在场、以及"主键由库给值而调用方没给键"那两种现场由内核拒，
    /// 因为那一半是这条链的形状而不是这一家的能力。这里也与 <c>DbCapabilities.Upsert_*</c> 那三位不重叠：
    /// 位是公开的能力清单而槽是写侧选层的判据（<c>MySqlProviderBase</c> 类注释里 <c>MultiStatement</c> 那一条
    /// 立的"位与槽是两面"在此原样适用），那三位今天仍然没有读者。</para></summary>
    string? UpsertClause(UpsertColumns columns) => null;
}
