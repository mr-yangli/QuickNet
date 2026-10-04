namespace FastSql;

public class FastSqlException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>元数据冲突（§10.2）。同层冲突必须启动期抛出——一旦建表按一个值、校验按另一个值，
/// 事故会在三个月后才现形。</summary>
public sealed class MetadataConflictException(string memberName, IReadOnlyList<string> sources)
    : FastSqlException($"属性 '{memberName}' 的同一层元数据来源互斥：{string.Join(" vs ", sources)}。FastSql 不自动取更严的那条。")
{
    public string MemberName { get; } = memberName;

    public IReadOnlyList<string> Sources { get; } = sources;
}

/// <summary>翻译不了就抛（§5.8），并带一句说明或改写。绝不静默降级成客户端求值。
/// <b>v0.36 第一笔（§16.8 毛病 1，取零签名修法）</b>：旧模板在拼接处硬嵌「，可改写为 」前缀，
/// 而那颗槽位里塞的九成是说明文字不是改写，整句读不通 —— 改为句号后直拼，槽位内容与属性形状都不动。</summary>
public sealed class UntranslatableExpressionException(string nodeText, string? suggestedRewrite, string? offendingNode = null)
    : FastSqlException($"无法翻译表达式：{nodeText}" + (suggestedRewrite is null ? "。" : $"。{suggestedRewrite}"))
{
    public string? OffendingNode { get; } = offendingNode ?? nodeText;

    public string? SuggestedRewrite { get; } = suggestedRewrite;
}

/// <summary>驱动未就位（§1.2 原则 3 的反射加载路径）。错误消息里必须带 <c>FastSqlDrivers.json</c> 的修法。</summary>
public sealed class DriverNotAvailableException(string driverKey, string? detail, Exception? inner = null)
    : FastSqlException($"未加载到驱动 '{driverKey}'。请在 FastSqlDrivers.json 的 drivers 数组里配置 key/assembly/path/factoryType；{detail ?? "详见 §9.5 交付形态"}", inner)
{
    public string DriverKey { get; } = driverKey;
}

/// <summary>§10.4：目标列是非 Unicode 的 varchar 且排序规则不认非 ASCII，而契约允许非 ASCII —— 启动期拒绝。</summary>
public sealed class UnsafeLengthSemanticsException(string tableName, string columnName, string collation)
    : FastSqlException($"列 {tableName}.{columnName} 的排序规则 {collation} 会把非 ASCII 静默写成 '?'。请改列为 UTF-8 排序规则或 NVARCHAR，或把实体映射为 ASCII-only。");

public sealed class ContextAffinityException(string message) : FastSqlException(message);

public sealed class ValidationFailedException(string message, IReadOnlyList<ValidationFailure> failures)
    : FastSqlException(message)
{
    public IReadOnlyList<ValidationFailure> Failures { get; } = failures;
}

public sealed record ValidationFailure(string MemberName, string Rule, string Message);

/// <summary>乐观并发冲突（§8.2 那句「冲突时逐个给出 <c>DbUpdateConcurrencyException</c>」）。
/// 触发条件只有一个：<b>带着并发谓词的那一句 <c>UPDATE</c>/<c>DELETE</c> 影响了 0 行</b>。
///
/// <para><b>0 行有两种成因，这一格把它们分开</b>：那句 <c>WHERE</c> 是「主键 ∧ 并发列」，
/// 主键匹配而并发列不匹配 = 别人改过这一行；主键都不匹配 = 这一行已经不在了。
/// 两者在行数上一模一样，所以抛出前会按主键补查一次现值（<see cref="DatabaseValues"/>），
/// 补查回来空 ⇒ <see cref="RowIsGone"/>。<b>代价只在冲突那一条上付</b>，正常保存一条语句都不多。</para>
///
/// <para>两半字典都<b>只覆盖并发列</b>，不是整行：整行刷新是 <c>Refresh</c> 那一格的活（§14 的 P1 余项，
/// 没接线），把它写进这里就等于承诺了一个没有实现的东西。而「会话以为库里是哪一版」（= <c>WHERE</c> 里那几颗
/// 参数的值）没有单独一格，它在消息文本里按列与现值并排写着 —— §8.2 那一句点名的只有
/// <c>DatabaseValues</c>/<c>CurrentValues</c> 两半，第三半要等 <c>Refresh</c> 一起接。</para>
///
/// <para><see cref="Sql"/> 是<em>最终执行的那一份文本</em>，也就是 <c>CudBuilding</c> 改写之后的。
/// 并发谓词由内核拼进 <c>WHERE</c> 的最后一档、随后才轮到改写钩子（§10.1 的落地记录写着这两者的先后与理由），
/// 所以如果这一句文本里看不到并发列，那是改写钩子把它挤掉了 —— 此时 0 行不该算并发冲突，
/// 那句"别人改过"是假的。消息里带着文本就是为了让人一眼分出这两种。</para></summary>
public sealed class DbUpdateConcurrencyException(
    string message,
    object entity,
    string operation,
    string sql,
    IReadOnlyDictionary<string, object?> currentValues,
    IReadOnlyDictionary<string, object?>? databaseValues) : FastSqlException(message)
{
    /// <summary>冲突的那一只实体实例。它<em>仍然</em>留在会话里、状态仍然欠着那一句 ——
    /// 抛出时整批已经回滚，会话没有结算（§8.2 落地记录第一条）。</summary>
    public object Entity { get; } = entity;

    /// <summary>引发冲突的那一档：<c>update</c> 或 <c>delete</c>，与 <c>ExecutionTag</c> 同一个词。</summary>
    public string Operation { get; } = operation;

    public string Sql { get; } = sql;

    /// <summary>实体此刻手里的那些值（<b>不是</b>物化时的快照 —— 那一版是 <c>WHERE</c> 里的那几颗参数，
    /// 消息文本按列把两者并排写出来了）。用户手里本来就拿着这一只实例，这半字典是给 catch 里那句
    /// <c>foreach</c> 用的，而不是唯一有用的那一半。</summary>
    public IReadOnlyDictionary<string, object?> CurrentValues { get; } = currentValues;

    /// <summary>补查回来的库里现值；<b>null 表示按主键也没查到行</b>（见 <see cref="RowIsGone"/>）。</summary>
    public IReadOnlyDictionary<string, object?>? DatabaseValues { get; } = databaseValues;

    /// <summary>true = 这一行被删掉了，不是被人改了。两者的处置不同：前者通常丢弃意图，后者要合并。</summary>
    public bool RowIsGone { get; } = databaseValues is null;
}
