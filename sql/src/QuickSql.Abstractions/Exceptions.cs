using System.Text;

namespace QuickSql;

public class QuickSqlException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>元数据冲突（§10.2）。同层冲突必须启动期抛出——一旦建表按一个值、校验按另一个值，
/// 事故会在三个月后才现形。</summary>
public sealed class MetadataConflictException(string memberName, IReadOnlyList<string> sources)
    : QuickSqlException($"属性 '{memberName}' 的同一层元数据来源互斥：{string.Join(" vs ", sources)}。QuickSql 不自动取更严的那条。")
{
    public string MemberName { get; } = memberName;

    public IReadOnlyList<string> Sources { get; } = sources;
}

/// <summary>翻译不了就抛（§5.8），并带一句说明或改写。绝不静默降级成客户端求值。
/// <b>v0.36 第一笔（§16.8 毛病 1，取零签名修法）</b>：旧模板在拼接处硬嵌「，可改写为 」前缀，
/// 而那颗槽位里塞的九成是说明文字不是改写，整句读不通 —— 改为句号后直拼，槽位内容与属性形状都不动。</summary>
public sealed class UntranslatableExpressionException(string nodeText, string? suggestedRewrite, string? offendingNode = null)
    : QuickSqlException($"无法翻译表达式：{nodeText}" + (suggestedRewrite is null ? "。" : $"。{suggestedRewrite}"))
{
    public string? OffendingNode { get; } = offendingNode ?? nodeText;

    public string? SuggestedRewrite { get; } = suggestedRewrite;
}

/// <summary>驱动未就位（§1.2 原则 3 的反射加载路径）。错误消息里必须带两条修法：把驱动 DLL 丢进
/// <c>drivers/</c>，或用 <c>DriverRegistry.Register</c> 注入已在进程里的工厂。</summary>
public sealed class DriverNotAvailableException(string driverKey, string? detail, Exception? inner = null)
    : QuickSqlException($"未加载到驱动 '{driverKey}'。把该库的 ADO.NET 驱动程序集丢进 <输出目录>/drivers/（文件名按内建表 BuiltinDrivers 的程序集名），或用 DriverRegistry.Register 注入已在进程里的工厂；{detail ?? "详见 §9.5 交付形态"}", inner)
{
    public string DriverKey { get; } = driverKey;
}

/// <summary>§10.4：目标列是非 Unicode 的 varchar 且排序规则不认非 ASCII，而契约允许非 ASCII —— 启动期拒绝。</summary>
public sealed class UnsafeLengthSemanticsException(string tableName, string columnName, string collation)
    : QuickSqlException($"列 {tableName}.{columnName} 的排序规则 {collation} 会把非 ASCII 静默写成 '?'。请改列为 UTF-8 排序规则或 NVARCHAR，或把实体映射为 ASCII-only。");

public sealed class ContextAffinityException(string message) : QuickSqlException(message);

/// <summary>校验闸门（§10.6）在语句生成之前整批拒绝。<b>消息自带一份按「成员 + 规则」归并的清单</b>
/// （见 <see cref="BuildMessage"/>），因为这一格抛的时候库里什么都还没发生，能定位的只有契约的那几条尺。</summary>
public sealed class ValidationFailedException(string message, IReadOnlyList<ValidationFailure> failures)
    : QuickSqlException(BuildMessage(message, failures))
{
    /// <summary>逐行逐列的明细，顺序 = 检查顺序。<b>消息里那份归并清单不替代它</b>：组数与项数是全部，
    /// 逐列的计数也是全部，只有"每一组的第一条原文"截了 <see cref="GroupLimit"/> 份。</summary>
    public IReadOnlyList<ValidationFailure> Failures { get; } = failures;

    /// <summary>归并清单逐组列出的上限。超出只少列、不少数：抬头那句报全部项数，首行报全部组数。</summary>
    public const int GroupLimit = 5;

    /// <summary>按「成员 + 规则」归并，项数多的排前面。<b>不是按行取前几项</b>：真实的违例形状是一列 ×
    /// 几十行（一次 134 行的种子报出过 656 项），按行取会把槽位全花在同一列的连续几行上，读的人既不知道
    /// 一共坏了几类、也不知道其它类是谁 —— 而摘要要答的正是"契约哪几条太严"。</summary>
    static string BuildMessage(string message, IReadOnlyList<ValidationFailure> failures)
    {
        // 只有一项时不出清单：抬头已经点到成员与列，再来一份一行的清单只是重说一遍。
        if (failures.Count <= 1) return message;

        var groups = new List<Group>();

        foreach (var failure in failures)
        {
            var index = groups.FindIndex(g => g.Member == failure.MemberName && g.Rule == failure.Rule);

            if (index < 0)
                groups.Add(new Group(failure.MemberName, failure.Rule, 1, failure.Message));
            else
                groups[index] = groups[index] with { Count = groups[index].Count + 1 };
        }

        var ordered = groups.OrderByDescending(g => g.Count).ToList();
        var listed = Math.Min(ordered.Count, GroupLimit);

        var sb = new StringBuilder();

        sb.Append(message).Append(Environment.NewLine)
          .Append("按「成员 + 规则」归并（共 ").Append(ordered.Count).Append(" 组");

        if (ordered.Count > listed)
            sb.Append("，列出前 ").Append(listed).Append(" 组，其余见 ").Append(nameof(Failures));

        sb.Append("）：");

        for (var i = 0; i < listed; i++)
            sb.Append(Environment.NewLine).Append("  ").Append(i + 1).Append(". ")
              .Append(ordered[i].Member).Append(" / ").Append(ordered[i].Rule)
              .Append("：").Append(ordered[i].Count).Append(" 项 —— 例：").Append(ordered[i].Example);

        return sb.ToString();
    }

    sealed record Group(string Member, string Rule, int Count, string Example);
}

public sealed record ValidationFailure(string MemberName, string Rule, string Message);

/// <summary>结构同步（§9.5）里那一句 DDL 被库拒绝了，而且<b>这一颗说的是"哪一句"</b>。
///
/// <para><b>为什么这一格要专门有一种异常</b>：驱动原本抛回来的那一句只说库的回答
/// （"Unknown column 'x' in 'field list'"），不说的是哪一句、也不说它前面已经落库了几句。而这一趟有两个
/// 读者拿不到的东西：DDL 不走 <c>Aop.Executing</c>（那条钩子只管带参数的语句），而
/// <c>SchemaSyncResult.DdlStatements</c> 只在<em>整趟成功</em>时才交得出来 —— 中途抛出就没有结果，
/// 于是"发了什么"这两半信息一起消失。这一颗把它们捞回来：失败的那一句原文、它改的是哪一格、
/// 以及在此之前真发出去过的每一句。</para>
///
/// <para><b>"已经生效的那几句"不是客套，是这一颗存在的另一半理由</b>：这一趟是一句一句各自落库的
/// （同步不把整批包进事务，而 MySQL 那一族本来也回滚不了 DDL），所以失败之后库的形状是
/// "前 N 句成了、第 N+1 句没成"。不知道 N 就没法修：下一次启动是从这个半新半旧的形状上继续 diff 的，
/// 而人工收拾也要先知道收拾到哪一张表。</para></summary>
/// <param name="sql">失败的那一句原文。</param>
/// <param name="target">它改的是哪一格，与 <c>SchemaSyncResult</c> 各清单里同一个词
/// （建表给表名、补列给 <c>表.列</c>）。</param>
/// <param name="changeKind">那一档动作的名字（<c>CreateTable</c> / <c>AddColumn</c> / <c>AlterColumn</c> /
/// <c>ColumnComment</c> / <c>TableComment</c> / <c>CreateIndex</c> / <c>DropIndex</c>）。</param>
/// <param name="applied">失败之前真发出去过且已生效的每一句，顺序 = 发送顺序。可能是空的
/// （第一句就失败）。</param>
/// <param name="database">连接打开后报出的目标库名；驱动没给就为 null。</param>
/// <param name="inner">驱动那一句原话，照 <see cref="QuickSqlException"/> 的规矩留在
/// <see cref="Exception.InnerException"/> 里，不在文本里重说一遍。</param>
public sealed class SchemaSyncFailedException(
    string sql,
    string target,
    string changeKind,
    IReadOnlyList<string> applied,
    string? database,
    Exception inner)
    : QuickSqlException(BuildMessage(sql, target, changeKind, applied, database, inner), inner)
{
    /// <summary>失败的那一句 DDL 原文。</summary>
    public string Sql { get; } = sql;

    /// <summary>这一句要改的那一格的名字。</summary>
    public string Target { get; } = target;

    /// <summary>那一档动作。</summary>
    public string ChangeKind { get; } = changeKind;

    /// <summary>失败之前已生效的每一句。<b>是抛出那一刻的快照</b>，不是那份还在被人加的活清单。</summary>
    public IReadOnlyList<string> AppliedStatements { get; } = [.. applied];

    /// <summary>目标库名（取自打开后的连接），可能为 null。</summary>
    public string? Database { get; } = database;

    /// <summary>清单里逐句列出的上限。<b>超出只少列，不少数</b>：首行那句"已生效 N 句"说的是全部，
    /// 而全量始终在 <see cref="AppliedStatements"/> 里。设这一档是因为一趟建 40 张表的清单
    /// 塞进一条消息会让人读不出重点是那一句失败的。</summary>
    public const int ListedAppliedLimit = 5;

    static string BuildMessage(
        string sql,
        string target,
        string changeKind,
        IReadOnlyList<string> applied,
        string? database,
        Exception inner)
    {
        var sb = new StringBuilder();

        sb.Append("结构同步失败：").Append(changeKind).Append(' ').Append(target).Append(" 那一句被库拒绝。");

        if (database is { Length: > 0 })
            sb.Append("目标库：").Append(database).Append('。');

        sb.Append(Environment.NewLine).Append("失败的句子：").Append(Environment.NewLine).Append(sql);

        if (applied.Count > 0)
        {
            sb.Append(Environment.NewLine)
              .Append("已经生效的 ").Append(applied.Count)
              .Append(" 句（DDL 不回滚，失败前发出去的都留下了）：");

            var listed = Math.Min(applied.Count, ListedAppliedLimit);

            for (var i = 0; i < listed; i++)
                sb.Append(Environment.NewLine).Append("  ").Append(i + 1).Append(". ").Append(SingleLine(applied[i]));

            if (applied.Count > listed)
                sb.Append(Environment.NewLine)
                  .Append("  …另有 ").Append(applied.Count - listed).Append(" 句，见 ").Append(nameof(AppliedStatements));
        }

        // 只摘库那一句原话（完整原文含堆栈仍在 InnerException 里）：凭它分清是方言层的拒绝还是连不上。
        sb.Append(Environment.NewLine).Append("库的回答：").Append(inner.GetType().Name).Append(": ").Append(inner.Message);

        return sb.ToString();
    }

    static string SingleLine(string sql) => sql.Replace("\r", " ").Replace("\n", " ").Trim();
}

/// <summary>完整性违例的四档（§8.4）。分成四档而不是留给消费者读一个枚举字段，是因为
/// <c>catch</c> 的写法要跟着"这一档的处置"走：唯一键通常是重复提交（回一句"已经提交过了"），
/// 外键与检查是数据本身不合法（该点名的字段要点名），非空常常是漏填——三件事的文案与状态码都不一样，
/// 用一颗类型加一个字段表达就等于把分流推给了调用方。</summary>
public enum IntegrityKind
{
    Unique,
    NotNull,
    ForeignKey,
    Check,
}

/// <summary>方言判出来的那一格"这是哪一种完整性违例"（§8.4）。判不出就返回 <c>null</c>，
/// 收口点于是原样抛驱动那颗——<b>误分类比不分类坏得多</b>：把外键冲突报成"重复提交"，
/// 用户会照着提示再点一次，而库里根本还没有那一行。</summary>
/// <param name="Kind">那一档。</param>
/// <param name="ConstraintName">库给回的约束/索引名。方言给不出就是 <c>null</c>——
/// PG 实测只有非空那一档不给（那一档本来就没有命名约束），其余三档都给。</param>
public sealed record IntegrityViolation(IntegrityKind Kind, string? ConstraintName = null);

/// <summary>写路径被库拒绝的共同底座（§8.4）。并发冲突与完整性违例是它的两个分支，
/// 之所以要在这一格合到一个基类下，是因为消费者普遍要写的那句是"这一整族都记一笔再往上抛"——
/// 没有这个底座就得列五颗类型名，而漏一颗的后果是那条路径悄悄绕过日志。</summary>
/// <param name="message">说清楚"哪一格被拒"的中文文本。</param>
/// <param name="sql">最终执行的那一份文本，口径与 <see cref="DbUpdateConcurrencyException"/> 相同：
/// <c>CudBuilding</c> 改写之后的，不是改写之前的。</param>
/// <param name="constraintName">库给回的约束名，拿不到就 <c>null</c>。</param>
/// <param name="inner">驱动那一颗原话，留在 <see cref="Exception.InnerException"/> 里，不在文本中重说。</param>
public abstract class DbUpdateException(
    string message,
    string sql,
    string? constraintName,
    Exception? inner)
    : QuickSqlException(message, inner)
{
    public string Sql { get; } = sql;

    public string? ConstraintName { get; } = constraintName;

    // 键列不在任何驱动的结构化字段里（§8.4：PG 的 DETAIL 在默认连接串上被驱动整段涂掉，连"能不能拿到"
    // 都不是方言说了算的事），正解是拿 ConstraintName 回到契约查——那一步要 ExecutionTag 进到这一格，
    // 还没有接，所以这里没有 Columns 这一颗：没有实现的东西不进契约（§16.22 ②）。
}

/// <summary>唯一键 / 唯一索引撞了（§8.4）。今天唯一的真实消费者是"同一笔被提交了两次"，
/// 所以这一颗单独存在而不是并进 <see cref="DbUpdateException"/> 的一个字段。</summary>
public sealed class UniqueConstraintException(
    string message,
    string sql,
    string? constraintName,
    Exception inner)
    : DbUpdateException(message, sql, constraintName, inner);

/// <summary>非空违例（§8.4）。库在这一档不给约束名，给的是列名与表名——而列名要走契约回填，
/// 所以今天这一颗手上比其余三颗少一格。</summary>
public sealed class NotNullConstraintException(
    string message,
    string sql,
    string? constraintName,
    Exception inner)
    : DbUpdateException(message, sql, constraintName, inner);

/// <summary>外键违例（§8.4）。<b>插入侧与删除侧同码</b>（PG 实测两侧都是 23503，只有文案分岔），
/// 所以这一颗不区分"引用不存在"与"被引用不许删"——那是文案的事，不是类型的事。</summary>
public sealed class ForeignKeyConstraintException(
    string message,
    string sql,
    string? constraintName,
    Exception inner)
    : DbUpdateException(message, sql, constraintName, inner);

/// <summary>检查约束违例（§8.4）。</summary>
public sealed class CheckConstraintException(
    string message,
    string sql,
    string? constraintName,
    Exception inner)
    : DbUpdateException(message, sql, constraintName, inner);

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
    IReadOnlyDictionary<string, object?>? databaseValues) : DbUpdateException(message, sql, null, null)
{
    /// <summary>冲突的那一只实体实例。它<em>仍然</em>留在会话里、状态仍然欠着那一句 ——
    /// 抛出时整批已经回滚，会话没有结算（§8.2 落地记录第一条）。</summary>
    public object Entity { get; } = entity;

    /// <summary>引发冲突的那一档：<c>update</c> 或 <c>delete</c>，与 <c>ExecutionTag</c> 同一个词。</summary>
    public string Operation { get; } = operation;

    /// <summary>实体此刻手里的那些值（<b>不是</b>物化时的快照 —— 那一版是 <c>WHERE</c> 里的那几颗参数，
    /// 消息文本按列把两者并排写出来了）。用户手里本来就拿着这一只实例，这半字典是给 catch 里那句
    /// <c>foreach</c> 用的，而不是唯一有用的那一半。</summary>
    public IReadOnlyDictionary<string, object?> CurrentValues { get; } = currentValues;

    /// <summary>补查回来的库里现值；<b>null 表示按主键也没查到行</b>（见 <see cref="RowIsGone"/>）。</summary>
    public IReadOnlyDictionary<string, object?>? DatabaseValues { get; } = databaseValues;

    /// <summary>true = 这一行被删掉了，不是被人改了。两者的处置不同：前者通常丢弃意图，后者要合并。</summary>
    public bool RowIsGone { get; } = databaseValues is null;
}
