using QuickSql.Metadata;
using QuickSql.Sql.Ast;

namespace QuickSql.Sql;

/// <summary>方言层的出口。Emit 不返回 string（§12.7）：返回 string 的形状会让每一层先分配再拼接。</summary>
public sealed record DialectSql(string Text, IReadOnlyList<AdoParameter> Parameters)
{
    public int ParameterCount => Parameters.Count;
}

/// <summary>语法分岔点：标识符、分页、类型名、UPSERT、RETURNING（§4）。
/// 一个方言实例必须能被多个线程同时使用，因此这里不允许有可变状态。</summary>
public interface ISqlDialect
{
    /// <summary>参数前缀：<c>@</c> / <c>:</c> / <c>?</c>。<see cref="SqlWriter"/> 靠它生成参数名。</summary>
    char ParameterPrefix { get; }

    /// <summary>这一家的驱动收不收<em>带方言前缀</em>的参数名（v0.34 第二笔，§14 三条省法里的第 ② 条落地）。
    /// <c>AdoExecutor.Bind</c> 默认把 <see cref="ParameterPrefix"/> 摘掉再交给驱动 —— 那是虚谷（交 <c>":p0"</c>
    /// 得 E10036「未绑定变量 P0」）逼出来的摘法，代价是每绑一颗参数多一次 <c>Substring</c> 分配。
    /// 答 <see langword="true"/> 表示本家驱动两种写法都收、且<em>带前缀那一路有过活库见证</em>，于是执行器原样递过去、
    /// 不吃那份价格。今天只有 SQL Server 为 true：带前缀在两台实例上绿过 v0.19–v0.31，摘前缀后也绿（v0.32 起），
    /// 所以它声明的是"这一家不必替第五家付账"。其余四家保持 false 各有所因：虚谷<em>必须</em>摘；
    /// 另三家"两种都认"只是 v0.32 那句观察、没有各自的活库复点（§9.1 三句的第 ② 句），按"没有实测就不改默认"留在摘的那边。
    /// <b>它不是 <see cref="Providers.IValueBinder"/> 的许可证</b>：provider 若要给驱动另一颗名字（比如整族不带前缀），
    /// 走 <see cref="AdoParameter.DriverName"/> 那颗自带字段，这里只管"前缀摘不摘"这一格。</summary>
    bool DriverAcceptsPrefixedParameterNames { get; }

    /// <summary>是否把字符串当 Unicode 传参。SQL Server 必须为 true，见 §10.4 的 cp1252 实测。</summary>
    bool PrefixUnicodeLiterals { get; }

    /// <summary>恒假谓词。空集合 <c>IN ()</c> 特化到这里，各家写法不同（T-SQL <c>1 = 0</c>、PG <c>false</c>）。</summary>
    string EmptySetPredicate { get; }

    /// <summary>恒真谓词，<c>IN</c> 全集与 <c>Any()</c> 空集合用。<b>这两处用途今天没有接线</b>：
    /// 这一颗在 Core 里的消费点是 0 处（v0.31 复核：全仓库只有接口声明、<c>DialectBase</c> 的默认值与
    /// 金仓那一族的覆写三处命中），与上面那颗恒假不同 —— 恒假有 1 处真实消费者（<c>ExpressionTranslator</c>
    /// 的空集合 <c>IN ()</c> 特化）。所以这颗是"给将来留的口"，五家里四家吃默认、一家覆写，
    /// 而那一次覆写今天不产出任何文本。缺口记 §14。</summary>
    string AlwaysTruePredicate { get; }

    /// <summary>这个库的分页语法本身是否要求 <c>ORDER BY</c>（T-SQL 的 <c>OFFSET..FETCH</c>、DM 的 <c>ROWNUM</c> 要求；
    /// PG 的 <c>LIMIT</c> 不要求）。§9.4 的 <c>Throw</c>/<c>AutoOrder(PK)</c> 在上游就把无序列出拦掉了，
    /// 走到这里说明策略是 <c>Allow</c>：要求 ORDER BY 的方言必须补一个占位序，否则生成的 SQL 直接语法报错。</summary>
    bool PagingRequiresOrdering { get; }

    /// <summary>这一家能不能把<em>别的表</em>带进一条写语句（<c>UPDATE … FROM 行集</c> / <c>DELETE … USING …</c>）。
    /// 它是 §12.5 那个合并档的唯一闸门：<see cref="Providers.IDbProvider"/> 一侧的跟踪器在建 AST 之前问这一颗，
    /// 答 <see langword="false"/> 就整桶退回一句一实体（v0.18 的形状，语义等价、代价是 N 次往返）。
    /// <b>为什么这不是 <c>DbCapabilities</c> 上的一位</b>：那一份位掩码今天<em>零读者</em>（§14 已把它记成缺口），
    /// 而同一档的"分页要不要 ORDER BY"早就落在这颗接口上（<see cref="PagingRequiresOrdering"/>）——
    /// 判据是"代码走到没有"，不是"库有没有"（§4）。Oracle 血统取 false：那一家没有 <c>UPDATE … FROM</c>，
    /// 它的等价物是 <c>MERGE INTO</c>，而 §12.5 的合并形状今天只在两种文本上落地过。
    /// <b>不许有人为了让合并档跑起来把它改成 true</b>：那只会让发射层抛，而抛点离写它的人隔了一次构建。</summary>
    bool SupportsJoinedWrites { get; }

    /// <summary>这一家的<em>驱动事务对象</em>收不收 ADO.NET 的保存点三件套 ——
    /// <c>DbTransaction.Save(name)</c> ∶ <c>DbTransaction.Rollback(name)</c> ∶ <c>DbTransaction.Release(name)</c>。
    /// 基类对这三颗的默认实现是<b>抛 <c>NotSupportedException</c></b>，所以"驱动没覆写"与"驱动明确不支持"
    /// 在调用点上是同一颗异常、量不出分别是哪一件；这一颗把这件事从异常接回方言层：
    /// 答 <see langword="false"/> 的方言必须同时给出下面三口文本发射口，执行层据此<em>在外层事务那条连接上</em>
    /// 发一句文本保存点（<c>SAVEPOINT</c> / <c>ROLLBACK TO SAVEPOINT</c> / <c>RELEASE SAVEPOINT</c>）。
    /// <b>语义一格都不动</b>：内层提交并入外层、内层回滚只撤销内层、外层照常提交或回滚 ——
    /// 两条路给的是同一件事，变的只有"这句话由驱动发还是由我们发"（§6 (1) 的嵌套形状因此不必改判）。
    ///
    /// <para><b>为什么这又是一颗 <see cref="ISqlDialect"/> 上的位而不是 <c>DbCapabilities</c> 的一位</b>：
    /// 与 <see cref="SupportsJoinedWrites"/> 逐字同一条判据 —— 那一份位掩码在 <c>src/</c> 里仍然<em>零读者</em>，
    /// 而这一颗的读者是三处真分派（<c>AdoExecutor.BeginTransaction</c> 创建那一句，
    /// <c>QuickSqlTransaction.Commit</c> 与 <c>Rollback</c> 各释放/回退那一句）。</para>
    ///
    /// <para><b>今天取 false 的只有一家</b>：虚谷。【实测·<c>obj/logs/v37-xg-recon-savepoint-1.log</c>】
    /// 那一家的 <c>XuguClient.XGTransaction</c> 只覆写了<em>无参</em> <c>Rollback()</c>，
    /// 带名字的三颗全部停在 <c>DbTransaction</c> 基类声明上（Q-02x 三行），调用即
    /// <c>NotSupportedException: Specified method is not supported.</c>（Q-03 与 Q-04 各一处），
    /// 而同一台库把文本那三句收下了、并且给出的就是保存点语义：保存点之后插的那一行被
    /// <c>ROLLBACK TO SAVEPOINT</c> 撤掉，保存点之前那一行在<em>同一个事务里</em>仍然数得到
    /// （Q-08 ∶ Q-10 ∶ Q-11 ∶ Q-12 <c>CNT=1</c> ∶ Q-13 <c>ID=1 ∶ TAG=before</c>），
    /// <c>RELEASE SAVEPOINT</c> 之后提交，库里剩 1 行（Q-14 ∶ Q-15）。
    /// 同一次探针顺手证伪了"照 SQL Server 抄"那一形：<c>SAVE TRANSACTION</c> 在这一家是<em>语法错</em>
    /// （Q-09：<c>E19260 L1 C5 syntax error, unexpected TRANSACTION, expecting end of file</c>）。
    /// 另外它一开始还证伪了自己的读法：第一趟用 reader 跑非结果集的保存点语句会把进程撞在驱动的
    /// <c>get_nextrow</c> 原生 0xC0000005 上（该探针文件抬头那句），所以这三句执行层的口径是
    /// <b>非查询</b>，与下面三口只产文本、由 <c>AdoExecutor.ExecuteSavepoint</c> 决定怎么发，恰好对上。</para>
    ///
    /// <para><b>取 true 的四家各有活库见证</b>：§13.4 那五场里
    /// <c>WritePathTests.A_nested_transaction_uses_savepoint_and_only_rolls_back_inner_changes</c>
    /// 在 SQL Server ∶ PostgreSQL ∶ MySQL ∶ SQLite 四场都<em>跑过并且绿</em>（那四份只列 FAIL/SKIP，
    /// 里面没有这一颗的名字），只有虚谷那一场列了它并给出上面那颗异常。
    /// <b>不许有人为了让某一场变绿把它改成 false</b>：下面三口默认抛，那样只会把红点从驱动层挪到发射层，
    /// 离写它的人更远一格 —— 与 <see cref="SupportsJoinedWrites"/> 末尾同一句纪律。
    /// 另一家 ORM 绕开这一格的做法（复用外层事务、根本不做保存点）连它的语义代价一起记 §14，
    /// 不在这里复述成"业界都这么做"。</para></summary>
    bool SupportsDriverSavepoints { get; }

    /// <summary>发射"创建保存点"那一句文本。<b>只在 <see cref="SupportsDriverSavepoints"/> 为 false 时被调用</b>，
    /// 且必须与下面两口收<em>同一个</em> <paramref name="savepointName"/>：名字由 <c>AdoExecutor</c> 按外层事务上的
    /// 计数器生成（形状 <c>quicksql_sp_&lt;n&gt;</c>，字母 ∶ 数字 ∶ 下划线，全部小写，<b>不是用户输入</b>），
    /// 三口一旦在字形上分岔，得到的是"回退到一个不存在也不报错的名字"——那比抛更贵。
    /// 收 <see cref="SqlWriter"/> 而不返回 <c>string</c>，与 §12.7 那条一致。</summary>
    void EmitSavepoint(SqlWriter writer, string savepointName);

    /// <summary>发射"回退到保存点"那一句（<see cref="Commit"/> 之外的那一半：内层只撤销自己）。</summary>
    void EmitRollbackToSavepoint(SqlWriter writer, string savepointName);

    /// <summary>发射"释放保存点"那一句（内层提交：改动并入外层，外层事务继续）。</summary>
    void EmitReleaseSavepoint(SqlWriter writer, string savepointName);

    void Emit(in SelectStatement select, SqlWriter writer);

    void Emit(in InsertStatement insert, SqlWriter writer);

    /// <summary>发一整句 upsert——<b>只给"没有 <see cref="Providers.IBulkStrategy.UpsertClause"/> 那颗句尾槽"的一家</b>
    /// （内核先问那份句尾，交得出就走 <see cref="Emit(InsertStatement)"/> 再接尾巴，走不到这一颗）。到这里的一家
    /// 发的是两种写法之一：SQLite 的 <c>INSERT … ON CONFLICT DO UPDATE SET 列 = excluded.列</c>，或
    /// <c>MERGE INTO</c> 那一族（SqlServer、Oracle 血统、虚谷、Gbase8s）。AST 形态中立，写法由这一家自己定。
    /// <b>默认实现抛</b>——只有覆写了它的家发得出这一句，没覆写又没有句尾的家，<c>InsertOrUpdate</c> 在内核那一层
    /// 就当场拒（口径见 <see cref="Providers.IBulkStrategy"/> 末尾那句"位与槽是两面"）。收 <see cref="SqlWriter"/>
    /// 而不返回 <c>string</c>，与 §12.7 那条一致。</summary>
    void Emit(in UpsertStatement upsert, SqlWriter writer);

    void Emit(in UpdateStatement update, SqlWriter writer);

    void Emit(in DeleteStatement delete, SqlWriter writer);

    void EmitIdentifier(SqlWriter writer, string identifier);

    void EmitTableName(SqlWriter writer, TableInfo table);

    /// <summary>写语句（<c>UPDATE</c>/<c>DELETE</c>）的目标表在 <c>SET</c> 与 <c>WHERE</c> 里<em>用什么名字引用自己</em>。
    /// T-SQL 与 PG 系给目标起一个独立别名（<c>t0</c>：T-SQL 靠 <c>UPDATE [t0] … FROM [表] AS [t0]</c>，
    /// PG 系靠 <c>UPDATE 表 AS t0</c>），而 Oracle 血统两样都不接受 —— 目标不能起别名，引用只能用表名本身。
    /// <b>为什么这是一口而不是发射时的一个判断</b>：这个名字长在 AST 的 <see cref="TableRef"/> 里，
    /// <c>SET</c> 与 <c>WHERE</c> 的列引用都从它取前缀，而方言实例是跨线程共享的（本接口的那句"不允许有可变状态"），
    /// 所以"现在是写语句"这件事必须在建 AST 之前就定下来，不能等到 <c>EmitColumn</c> 现场猜。</summary>
    string WriteTargetAlias(TableInfo table);

    /// <summary>CAST / DDL 用的存储类型名。DDL 归 <see cref="Providers.ICodeFirstDialect"/>，
    /// 但类型名只有一份真相，所以 CodeFirst 从这里取。</summary>
    string GetStoreType(ColumnContract column);
}
