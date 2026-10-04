using System.Data.Common;
using FastSql.Metadata;
using FastSql.Providers;
using FastSql.Sql;
using FastSql.Values;

namespace FastSql.Tests.Integration;

/// <summary>把 <see cref="IBulkStrategy.ParameterBudget"/> 掰小的<b>转发</b>包装（§12.3 的分块式在合并档上的见证）。
///
/// <para>为什么要有这一份：合并语句的每行参数只有<em>键 + 改动列</em>两颗，按 SQL Server 的真预算
/// （2100）算出来是 945 行一块 —— 要在真库上跨到第二块就得灌 946 行实体、再把它们全部读进会话。
/// 那一格的<em>判据</em>与行数无关（要判的是"块边界处列没串、每行落回自己那一格"），
/// 所以把预算掰小到 6（= 2 行一块）、灌 3 行，跑的是同一段循环。</para>
///
/// <para>转发而不是重写是关键：内层还是<em>真的</em>那一家的 provider（v0.32 第二格起那颗由
/// <c>Database.Provider</c> 给，缺省那一家仍是 <c>SqlServerProvider</c>），于是这里发出的语句与真库上
/// 任何其他用例发出的逐字相同，被掰的只有那一个数字。与 <c>NoBulkKeysProvider</c> 同一个手法。</para></summary>
sealed class LowBudgetProvider(IDbProvider inner, int parameterBudget) : IDbProvider
{
    public static LowBudgetProvider Around(IDbProvider inner, int parameterBudget) => new(inner, parameterBudget);

    public DbKind Kind => inner.Kind;

    public DbCapabilities Capabilities => inner.Capabilities;

    public IConnectorFactory Connectors => inner.Connectors;

    public ISqlDialect Dialect => inner.Dialect;

    public ITypeMap Types => inner.Types;

    public IValueBinder Values => inner.Values;

    public IExpressionRules Expressions => inner.Expressions;

    public ICodeFirstDialect CodeFirst => inner.CodeFirst;

    public ISchemaReader Schema => inner.Schema;

    public IKeyStrategy Keys => inner.Keys;

    public IConcurrencyStrategy Concurrency => inner.Concurrency;

    public IChangeGuard ChangeGuard => inner.ChangeGuard;

    public IBulkStrategy? Bulk => inner.Bulk is null ? null : new TightBudget(inner.Bulk, parameterBudget);

    sealed class TightBudget(IBulkStrategy inner, int budget) : IBulkStrategy
    {
        public int ParameterBudget => budget;

        public string Describe => inner.Describe + $"（本用例把预算掰成 {budget}）";
    }
}
