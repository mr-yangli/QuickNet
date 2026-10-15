using System.Data.Common;
using QuickSql.Transactions;

namespace QuickSql.Execution;

/// <summary>一条真连接 + 一个真 <see cref="DbTransaction"/> + 环境槽位（§6 (1)(3)）。
/// 构造与释放都归 <see cref="AdoExecutor.BeginTransaction"/> 那条同步路径：连接在这里被<b>独占</b>到事务结束，
/// 不再回池 —— 这是事务的正确语义，也是"事务期间少一条可用连接"的代价来源（池上限要按并发事务数算，§15）。
///
/// <para><b>嵌套 = SAVEPOINT</b>（v0.38 落地）：在已有环境事务上再调 <c>BeginTransaction</c> 不再抛，
/// 而是在外层事务上创建一个 SAVEPOINT，返回一只<em>保存点事务</em>。保存点事务共享外层的连接与
/// <see cref="DbTransaction"/>，Commit 释放保存点（改动并入外层），Rollback 回退到保存点（只撤销内层）。
/// 只有根事务才真正 Commit/Rollback 那条 <see cref="DbTransaction"/>。</para>
///
/// <para><b>保存点这句话由谁发，有两种</b>（v0.38 第一笔）：驱动自己发（另四家）与这一家不发、
/// 由我们发文本（虚谷）。分岔与"走哪条路"的记账都在 <see cref="_textSavepoints"/> 那一段，
/// 三口文本的出处与取法在 <c>XuguDialect</c> 的那三个覆写里。本类因此<em>不</em>直接
/// 依赖"嵌套 = 保存点"这句话怎么落地 —— 它只保证创建与拆除走同一条。</para></summary>
/// <remarks>环境槽位用 <see cref="AsyncLocal{T}"/>，且<b>只有同步写入才回到调用方的帧</b>（v0.12 探针，见
/// <see cref="IQuickSqlTransaction"/> 的 remarks）。所以本类不自己 begin，也不提供任何 async 成员。</remarks>
public sealed class QuickSqlTransaction : IQuickSqlTransaction
{
    static readonly AsyncLocal<QuickSqlTransaction?> _ambient = new();

    readonly DbConnection _connection;
    readonly DbTransaction _transaction;
    readonly bool _ownsConnection;

    // ── 嵌套（SAVEPOINT）──────────────────────────────────────────────
    // 保存点事务共享外层的连接与 DbTransaction，不独占地占有它们。
    // _parent 非 null = 这是一只保存点事务，Commit/Rollback 走保存点路径而不是真提交/回滚。

    readonly QuickSqlTransaction? _parent;
    readonly string? _savepointName;

    /// <summary>这只保存点事务走的是<em>文本</em>那一条路（而不是驱动 <c>DbTransaction.Save</c>）。
    /// 由 <see cref="AdoExecutor.BeginTransaction"/> 在<b>创建它的那一刻</b>定下来传进来，
    /// 于是"创建它的那条路"就是"拆除它的那条路"：不存在驱动发的保存点被一句文本去 RELEASE
    /// （或反过来）这种跨路拆卸，而那两种跨路在虚谷与另四家上都得到同一颗 <c>NotSupportedException</c>
    /// 或者一句语法错 —— 两种跨路各红在哪一家不是这里要记的账，取 false 的判据见
    /// <see cref="QuickSql.Sql.ISqlDialect.SupportsDriverSavepoints"/>；根事务永远用不到这颗
    /// （它走 <c>Commit</c>/<c>Rollback</c> 无参那两颗），留默认值即可，不额外分支。</summary>
    readonly bool _textSavepoints;

    // ── 传播（RequiresNew）──────────────────────────────────────────────
    // RequiresNew 挂起当前环境事务，新开一条独立事务。_suspended 持有被挂起的那条，
    // Finish 时恢复到环境槽位。与 _parent 互斥：保存点事务不挂起任何东西。

    readonly QuickSqlTransaction? _suspended;

    /// <summary>这场事务除了在当前帧里，还钉在自己的 executor 上（形态 (4)，由
    /// <see cref="AdoExecutor.BindSessionTransaction"/> 置位）。根事务结束时据此摘钩。
    /// 只有根事务会拿到 <c>true</c>：保存点事务共用外层的连接与归属，不重复钉。</summary>
    internal bool SessionScoped;

    /// <summary>保存点发号器：每在这条事务上创建一个嵌套保存点就 +1。只根事务持有它，
    /// 保存点事务共享外层的计数器（通过 <c>parent._nextSavepointId++</c>）。
    /// 单调递增不回退：即使保存点被回滚了，那个号码也不再使用，避免名称碰撞。</summary>
    internal int _nextSavepointId;

    /// <summary>根事务构造：持有连接与 DbTransaction 的所有权。
    /// <paramref name="cluster"/> 是这场事务<em>钉住的那座集群</em>（§16.4）：之后这条流上任何要落别座集群的
    /// 语句都在取连接那一支被拒，判据只有 <see cref="AdoExecutor"/> 一处。
    /// <paramref name="suspended"/> 是 <c>RequiresNew</c> 挂起的上一条环境事务，Finish 时恢复它。</summary>
    internal QuickSqlTransaction(AdoExecutor executor, string cluster, DbConnection connection, DbTransaction transaction, bool ownsConnection = true, QuickSqlTransaction? suspended = null)
    {
        Executor = executor;
        Cluster = cluster;
        _connection = connection;
        _transaction = transaction;
        _ownsConnection = ownsConnection;
        _suspended = suspended;
    }

    /// <summary>保存点事务构造：共享外层的连接、DbTransaction <b>与那座集群</b>，不占有它们。
    /// <paramref name="textSavepoints"/> 是创建它时走过的那条路（true = 文本，false = 驱动），
    /// 由 <see cref="AdoExecutor.BeginTransaction"/> 传进来，理由写在 <see cref="_textSavepoints"/>。</summary>
    internal QuickSqlTransaction(QuickSqlTransaction parent, string savepointName, bool textSavepoints)
    {
        Executor = parent.Executor;
        Cluster = parent.Cluster;
        _connection = parent._connection;
        _transaction = parent._transaction;
        _ownsConnection = false;
        _parent = parent;
        _savepointName = savepointName;
        _textSavepoints = textSavepoints;
    }

    /// <summary>当前帧可见的环境事务，<b>且必须属于这个 executor</b>。两个 orm 实例（两套库）共享同一个
    /// async 帧时，把语句塞进另一家库的事务里是"查不出来"级别的错：它不报错，只是数据少了一半。
    /// <para><b>帧里没有、但这只实例钉着一场时，回退读那一场</b>（形态 (4)，见
    /// <see cref="AdoExecutor.SessionTransaction"/>）：归属从「开它的那一刻所在的执行流」扩成
    /// 「流 <em>或</em> 实例」，为的是装配层（ABP 的工作单元）那一类写法——它在自己的 async 帧里开事务，
    /// 回到调用方那一帧时槽位已经空了（<see cref="IQuickSqlTransaction"/> 抬头那条实测纪律），
    /// 于是后面的语句全落在事务外。判据仍只有一条：句柄归这只 executor，或者归它自己钉的那一场。</para></summary>
    internal static QuickSqlTransaction? For(AdoExecutor executor)
        => _ambient.Value is { } ambient && ReferenceEquals(ambient.Executor, executor)
            ? ambient
            : executor.SessionTransaction;

    /// <summary>不管属不属于谁 —— 只要帧里有环境事务。用于判是否需要创建保存点而不是新开事务。</summary>
    internal static bool HasAmbient => _ambient.Value is not null;

    /// <summary>当前帧可见的环境事务（不限 executor）。保存点创建时需要拿到外层的 DbTransaction。</summary>
    internal static QuickSqlTransaction? Ambient => _ambient.Value;

    /// <summary>清空环境槽位。<c>RequiresNew</c> 挂起当前事务时调用。</summary>
    internal static void ClearAmbient() => _ambient.Value = null;

    internal AdoExecutor Executor { get; }

    /// <summary>这场事务钉住的那座集群。取连接那一支按它判"这条语句能不能进这场事务"，
    /// 所以公开它不是为了读数，是为了让会话侧能在开事务之前给出同一句话（<c>DbContext</c> 与测试都读它）。</summary>
    internal string Cluster { get; }

    internal DbConnection Connection => _connection;

    internal DbTransaction DbTransaction => _transaction;

    /// <summary>这是一只保存点事务而不是根事务。保存点事务 Commit/Rollback 走保存点路径，
    /// 不真正提交/回滚 DbTransaction，也不释放连接。</summary>
    public bool IsSavepoint => _parent is not null;

    public bool Completed { get; private set; }

    /// <summary>由 <see cref="AdoExecutor.BeginTransaction"/> 在<b>调用方的同步帧</b>里写入。</summary>
    internal void Install() => _ambient.Value = this;

    public void Commit()
    {
        if (Completed)
            throw new QuickSqlException("事务已经结束（提交或回滚），不能再提交一次。重复 Commit 通常是 finally 里又调了一遍——把它删掉，兜底是 Dispose。");

        if (_parent is not null)
        {
            // 保存点事务：释放保存点，改动并入外层事务。两条路做的是同一件事，见 _textSavepoints。
            if (_textSavepoints)
                Executor.ExecuteSavepoint(this, SavepointStep.Release, _savepointName!);
            else
                _transaction.Release(_savepointName!);

            Finish();
        }
        else
        {
            _transaction.Commit();
            Finish();
        }
    }

    /// <summary>不抛：<see cref="Dispose"/> 在未提交时就是靠调它来兜底，而 Dispose 可以发生在已结束的事务上。</summary>
    public void Rollback()
    {
        if (Completed)
            return;

        try
        {
            if (_parent is not null)
            {
                // 保存点事务：回退到保存点，只撤销内层改动，外层事务继续。两条路见 _textSavepoints。
                if (_textSavepoints)
                    Executor.ExecuteSavepoint(this, SavepointStep.Rollback, _savepointName!);
                else
                    _transaction.Rollback(_savepointName!);
            }
            else
            {
                _transaction.Rollback();
            }
        }
        finally
        {
            Finish();
        }
    }

    /// <summary>不调 <see cref="Commit"/> 即回滚（§6 (1) 的"兜底"）。</summary>
    public void Dispose()
    {
        if (!Completed)
            Rollback();
    }

    void Finish()
    {
        Completed = true;

        // 保存点事务：还原外层到环境槽位，不 dispose 共享的 DbTransaction 与连接。
        if (_parent is not null)
        {
            _ambient.Value = _parent;
            return;
        }

        // 根事务：如果有被挂起的环境事务（RequiresNew），恢复它；否则清空槽位。
        _ambient.Value = _suspended;

        // 钉在实例上的那一场（形态 (4)）：结束即摘钩，否则这只 orm 之后的语句会走进一条已结束的
        // 事务里。谁钉的谁来摘——只有根事务持有实例归属，保存点事务共用外层那一条。
        if (SessionScoped)
            Executor.UnbindSessionTransaction(this);

        _transaction.Dispose();

        // 会话级连接复用（§8.4）：事务可能复用了 DbContext 绑的连接，
        // 那种情况下连接不归事务管——它还在 AsyncLocal 里，后续语句继续复用。
        if (_ownsConnection)
            ConnectionRelease.Release(_connection);
    }
}
