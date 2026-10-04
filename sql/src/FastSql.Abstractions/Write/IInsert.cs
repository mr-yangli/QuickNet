using FastSql.Aop;
using FastSql.Providers;
using FastSql.Sql;

namespace FastSql.Write;

/// <summary>写侧链式契约（§5.4、§6 的示例入口）。与读侧同一套入口风格但<b>不合并</b>：
/// 读侧的出口是行，写侧的出口是受影响行数与回填的键，两者的钩子集合也不同（§10.1 的改写类只在写侧有意义）。
/// <see cref="Query.IQuery{T}"/> 那条"不可变值、可复用、不需要 Clone"（§5.11）在这里<b>不成立</b>：
/// <see cref="FastSqlAop.AuditValue"/> 会写实体，所以一个插入对象只算一次列值，
/// 之后的出口共用同一份结果 —— 于是 <c>ToSql()</c> 与紧随其后的 <c>ExecuteAffrowsAsync()</c> 必然是同一条语句，
/// 不会出现"看着 SQL 执行了另一条"。</summary>
public interface IInsert<T> where T : class
{
    /// <summary>不回填键那一档的计划（对应 <see cref="ExecuteAffrowsAsync"/>）。</summary>
    InsertPlan Explain();

    /// <summary>回填键那一档的计划（对应 <see cref="ExecuteReturnKeysAsync"/>/<see cref="ExecuteIdentityAsync"/>）。
    /// <b>单独一个口，而不是让 <see cref="Explain"/> 看情况给两种文本</b>：要不要 <c>OUTPUT</c> 由出口决定，
    /// 而"要审阅的那条 SQL 到底是哪一条"不能靠调用方猜。两档共用同一份取值结果，差别只在文本。</summary>
    InsertPlan ExplainKeys();

    /// <summary>不连库（§3）。<b>只有单块时可取</b>：分块后每块是一份独立的参数表，
    /// 拼在一起会得到两段 <c>@p0</c> 起始的文本 —— 那不是任何一条能执行的语句。</summary>
    string ToSql();

    string ToSql(out IReadOnlyList<AdoParameter> parameters);

    /// <summary>逐块内联字面量，多块时带 <c>-- 块 n</c> 分节：交付给 DBA 的文本必须能看出有几条语句。</summary>
    string ToLogSql();

    /// <summary>受影响行数（多块时是各块之和）。不回填主键：要键用 <see cref="ExecuteReturnKeysAsync"/>。</summary>
    Task<int> ExecuteAffrowsAsync(CancellationToken ct = default);

    /// <summary>单行自增主键。多行或自增列不是一份整数时抛明确异常，而不是静默返回首行的键。</summary>
    Task<long> ExecuteIdentityAsync(CancellationToken ct = default);

    /// <summary>回填自增主键并返回同一批实体（顺序即入参顺序）。provider 无批量 <c>RETURNING</c> 时
    /// 自动退到 L5 逐条，语义不变（§12.4 第 1 项、§16.9）。</summary>
    Task<List<T>> ExecuteReturnKeysAsync(CancellationToken ct = default);
}

/// <summary>一个块：一次 round-trip 的语句 + 它覆盖的行区间。
/// <see cref="FirstRow"/> 是回填时的实体下标，<b>顺序映射就是这里唯一的真相</b>
/// （<c>OUTPUT</c>/<c>RETURNING</c> 的回包按源行序，§12.4 第 1 项的代价面），集成测试按 payload 证明这条映射。</summary>
public sealed record InsertChunk(int FirstRow, int RowCount, Sql.Ast.InsertStatement Ast, DialectSql Sql);

/// <summary>一次插入的完整决策记录。<see cref="Reason"/> 必须是给人看的一句话（§12.3 对 <c>BulkPlan</c> 的同一要求）：
/// 现场排障时先看到"为什么是 63 行一块"，而不是先去读源码。</summary>
public sealed record InsertPlan(
    IReadOnlyList<InsertChunk> Chunks,
    int RowCount,
    int ColumnsPerRow,
    int RowsPerStatement,
    int ParameterBudget,
    string Reason,
    IReadOnlyList<HookTrace> HookReplay)
{
    /// <summary>与 <see cref="Query.QueryPlan"/> 同理：AST 与文本本来就是某一家方言的产物，内联字面量要问它的 binder。</summary>
    public required IDbProvider Provider { get; init; }

    public bool IsSingleStatement => Chunks.Count == 1;

    /// <summary>0 行时没有语句（不连库、返回 0 行），所以这里抛的是"没有文本"，不是"没有执行"。</summary>
    public string ToSql() => Chunks.Count switch
    {
        1 => Chunks[0].Sql.Text,
        0 => throw new FastSqlException("0 行插入没有对应的 SQL 文本：不产生语句，执行会直接返回 0。"),
        _ => throw new FastSqlException($"这次插入被分成 {Chunks.Count} 块（{Reason}），没有单一 SQL 文本。取 Explain().Chunks[i].Sql，或用 ToLogSql() 看全部分块。"),
    };

    public string ToLogSql()
    {
        if (Chunks.Count == 0)
            return "-- 0 行：不产生任何语句";

        var lines = new string[Chunks.Count];

        for (var i = 0; i < Chunks.Count; i++)
        {
            var chunk = Chunks[i];
            var log = chunk.Sql.ToLogSql(Provider);

            lines[i] = Chunks.Count == 1 ? log : $"-- 块 {i + 1}：第 {chunk.FirstRow}~{chunk.FirstRow + chunk.RowCount - 1} 行{Environment.NewLine}{log}";
        }

        return string.Join(Environment.NewLine, lines);
    }

    public override string ToString() => Reason + Environment.NewLine + ToLogSql();
}
