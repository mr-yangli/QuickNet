using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using FastSql.Metadata;
using FastSql.Sql;
using FastSql.Sql.Ast;
using FastSql.Values;

namespace FastSql.Providers;

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

    /// <summary>返回 false 表示该库不支持列注释，Core 会改走表级注释或跳过并 WARN。</summary>
    bool TryEmitColumnComment(SqlWriter writer, TableInfo table, ColumnContract column);

    void EmitCreateIndex(SqlWriter writer, TableInfo table, IndexContract index);
}

/// <summary>系统表读结构（§4 里差异最大的部分）。</summary>
public interface ISchemaReader
{
    Task<SchemaTable?> ReadTableAsync(DbConnection connection, TableInfo table, CancellationToken ct = default);

    /// <summary>§10.4 的量纲探测：只读，不建临时表、不写数据（生产库常禁 DDL 权限）。</summary>
    Task<LengthSemanticsReading?> ProbeLengthSemanticsAsync(DbConnection connection, TableInfo table, string columnName, CancellationToken ct = default);
}

public sealed record SchemaColumn(string Name, string StoreType, int? MaxChars, int? MaxBytes, bool IsNullable, int? OrdinalPosition);

public sealed record SchemaTable(TableInfo Table, IReadOnlyList<SchemaColumn> Columns);

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
/// 65535 / 没有 provider 报的那些家走共用层的 100），<see cref="ResolveCopyChannel()"/> 从本版起有唯一一家落地（SQL Server 的
/// <c>SqlBulkCopy</c>，反射绑定，因为装载类不在 <c>DbProviderFactory</c> 的抽象面上，而本仓库的编译期驱动引用纪律
/// 不许直接 <c>new</c> 它）。原稿那颗 <c>CanResolveKeys</c> 没有进来，因为它要答的问题（"批量 + 要主键能不能成立"）
/// 今天由 <c>InsertEngine</c> 自己判得更准：那一档不是"通道能不能给键"，是"这一条出口要不要键"，
/// 而后者是出口的形状不是库的能力，放在这里会让 provider 替调用方决定它自己没问过的东西。</remarks>
public interface IBulkStrategy
{
    int ParameterBudget { get; }

    string Describe { get; }

    /// <summary>L1 装载通道（§12.1 第三行）。交回 <see langword="null"/> = 这一家不接（或这一支驱动里没有装载类），
    /// 阶梯自然从 L3 起 —— 默认实现让其余六家一个字节都不必改，这与 <see cref="IDbProvider.CanCreateDatabase"/> 那颗默认位同一个手法：
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
}
