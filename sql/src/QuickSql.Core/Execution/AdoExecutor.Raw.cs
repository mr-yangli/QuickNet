using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using QuickSql.Materialization;
using QuickSql.Sql;

namespace QuickSql.Execution;

/// <summary>§11 逃生舱的执行侧落点：<b>字符串 SQL + 参数对象</b>那一族口。
///
/// <para>它们全部只是把「文本 + 匿名对象」折成 <see cref="DialectSql"/>，然后交给已有的低层口 ——
/// 所以 AOP 的 <c>Executed</c>/<c>Error</c>/<c>SlowQuery</c>、会话级连接复用、事务环境槽位、
/// <c>CommandTimeout</c>、摘前缀那一条纪律，<b>一行都没有第二份实现</b>。
/// 这一条是刻意的：逃生舱如果自带一套执行路径，它就会在自己的日志格式、自己的连接策略上漂移，
/// 而报表 SQL 恰恰是最需要和链式 SQL 出现在同一份日志里的东西。</para>
///
/// <para><b>行出口的两种形状</b>：标量（含可空与枚举）直读第 0 列；对象按<em>列名对属性名</em>装配
/// （<see cref="RawRowReader"/>，规则与它的报错都写在那一节的注释里）。
/// 构造器 DTO 与 record 走这一族口装不出来，那是 <see cref="QueryAsync{T}(DialectSql,Func{DbDataReader,T},ExecutionTag,int?,CancellationToken)"/>
/// 的落点 —— 自己给委托，形状就不归库管了。</para></summary>
public sealed partial class AdoExecutor
{
    /// <summary>把参数对象折成 <see cref="DialectSql"/>：文本原样不动，参数名取
    /// 本家 <see cref="ISqlDialect.ParameterPrefix"/> 加属性名（口径与 <see cref="RawSql"/> 那一段相同）。
    /// 要把一句自定义的行委托接到原生 SQL 上时，入口就是这里 —— 下面那三个口只是它的三个常用特例。</summary>
    [RequiresUnreferencedCode(RawSql.ParameterMessage)]
    public DialectSql ToRawSql(string sql, object? args)
        => RawSql.ToDialectSql(sql, args, Provider.Dialect.ParameterPrefix);

    [RequiresUnreferencedCode(RawExitMessage)]
    [RequiresDynamicCode(RawExitMessage)]
    public Task<List<T>> QueryAsync<T>(string sql, object? args = null, CancellationToken ct = default)
        => QueryAsync(ToRawSql(sql, args), RawRowReader.Create<T>(), RawSql.Tag, null, ct);

    [RequiresUnreferencedCode(RawExitMessage)]
    [RequiresDynamicCode(RawExitMessage)]
    public Task<T?> FirstAsync<T>(string sql, object? args = null, CancellationToken ct = default)
        => FirstAsync(ToRawSql(sql, args), RawRowReader.Create<T>(), RawSql.Tag, ct);

    /// <summary>逐行流式（§12.7 那一族 <c>IAsyncEnumerable</c> 出口在原生 SQL 侧的对应物）。</summary>
    [RequiresUnreferencedCode(RawExitMessage)]
    [RequiresDynamicCode(RawExitMessage)]
    public IAsyncEnumerable<T> StreamAsync<T>(string sql, object? args = null, CancellationToken ct = default)
        => StreamAsync(ToRawSql(sql, args), RawRowReader.Create<T>(), RawSql.Tag, ct);

    /// <summary>第一行第一列的值，<see cref="DBNull"/> 折成 <c>null</c>。零行交出 <c>null</c>
    /// （这里没有"聚合恒定一行"那条判据：那句 SQL 是你写的，它选出零行是可能的）。</summary>
    [RequiresUnreferencedCode(RawSql.ParameterMessage)]
    public async Task<object?> ExecuteScalarAsync(string sql, object? args = null, CancellationToken ct = default)
    {
        Func<DbDataReader, object?> map = static reader => reader.IsDBNull(0) ? null : reader.GetValue(0);

        return await FirstAsync(ToRawSql(sql, args), map, RawSql.Tag, ct).ConfigureAwait(false);
    }

    /// <summary>非查询语句（DDL、批量脚本片段、存储过程式的调用）。返回受影响行数，
    /// 与 <see cref="NonQueryAsync(DialectSql,ExecutionTag,CancellationToken)"/> 同一个出口，因此同一个 AOP 链。</summary>
    [RequiresUnreferencedCode(RawSql.ParameterMessage)]
    public Task<int> ExecuteNonQueryAsync(string sql, object? args = null, CancellationToken ct = default)
        => NonQueryAsync(ToRawSql(sql, args), RawSql.Tag, ct);

    /// <summary>一次调用发多句文本，共用一条连接与一次事务环境：语句之间不各自开连接。
    /// <b>它不开事务</b> —— 要把这一串包进事务请显式 <c>orm.BeginTransaction()</c> 或
    /// <c>orm.Ado.BeginTransaction(...)</c>，因为"这一串是不是一个原子单位"只有 SQL 作者知道。</summary>
    public async Task ExecuteBatchAsync(IReadOnlyList<string> statements, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(statements);

        for (var i = 0; i < statements.Count; i++)
            await NonQueryAsync(new DialectSql(statements[i], []), RawSql.Tag, ct).ConfigureAwait(false);
    }

    const string RawExitMessage = "原生 SQL 的行出口按运行期类型找属性并编译读写委托。";
}
