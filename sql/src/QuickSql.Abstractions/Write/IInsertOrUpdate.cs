namespace QuickSql.Write;

/// <summary><c>orm.InsertOrUpdate&lt;T&gt;().SetSource(…)</c> 的链（§5.4 的写侧）：<b>按主键一句决定"没有就插、有就改"</b>
/// —— 每一行把除主键、库里给值的列与并发列以外的全部列写进去，主键既是插入清单的一部分、又是冲突判定的键，
/// 命中就改那一行、没命中就插这一行。它是 <see cref="IUpdateBulk{T}"/> 的 <see cref="BulkWriteMethod.Upsert"/> 档
/// 那一份能力的<b>一等公民入口</b>（同一句文本、同一把列尺子），拆成独立一条链是为了让"我要 upsert"这件事
/// 在方法名上就写出来，而不是藏在 <c>UpdateRange(...).WithBulk(Upsert)</c> 的一格参数里。
/// <para><b>两种发射形态都走这一条链，调用方不选</b>：句尾那一族（MySQL 血统的 <c>ON DUPLICATE KEY UPDATE</c>、
/// PG 血统的 <c>ON CONFLICT … DO UPDATE SET … = EXCLUDED.…</c>）发的是内核那一条 <c>INSERT … VALUES</c>
/// 再接一段本家句尾；整语句那一族（<c>MERGE INTO</c>：SqlServer、Oracle 血统、虚谷、Gbase8s）发的是一整句
/// <c>MERGE</c>。选哪一形由 provider 与方言自己定（<see cref="Providers.IBulkStrategy.UpsertClause"/> 交得出句尾
/// 就走句尾，否则问方言那颗收 <c>UpsertStatement</c> 的整语句 emit），两家都拿不到时这一条链在这一家当场拒。
/// 逐家的形态与"哪几家只到文本层、没有活库见证"记 §9.1 与使用册那一节。</para>
/// <para><b>入口零反射税</b>，与 <see cref="QuickSqlOrm.InsertRange{T}"/>、<see cref="QuickSqlOrm.UpdateRange{T}"/> 同档甚至
/// 更干净一档：列清单来自契约、值来自编译好的读写委托，一个表达式树都不建，连全局过滤器都不叠（在场即拒，见
/// <see cref="BulkWriteMethod.Upsert"/> 那两道门），所以 <see cref="Explain"/> 与执行两条出口都不欠 RUC/RDC 那一句标注。</para>
/// <para><b>主键由库给值（自增 ∶ 序列）而这一行没带键时当场拒</b>：那种键的冲突判定出不来，写进去的是一个新行而不是
/// 改了那一行——这一句与 <see cref="BulkWriteMethod.Upsert"/> 那一道门逐字同源，不是这一条链新立的规矩。</para></summary>
public interface IInsertOrUpdate<T> where T : class
{
    /// <summary>交一只实体。链是可复用的（<c>SetSource</c> 换一批再执行一次），但一条链只算一次取值段的
    /// <see cref="Aop.QuickSqlAop.AuditValue"/>：与 <see cref="IInsert{T}"/> 抬头那句"一个插入对象只算一次列值"
    /// 同一条纪律，所以 <see cref="ToSql"/> 与紧随的 <see cref="ExecuteAffrowsAsync"/> 是同一条语句。</summary>
    IInsertOrUpdate<T> SetSource(T entity);

    /// <summary>交一批实体（立刻物化，理由与 <see cref="QuickSqlOrm.InsertRange{T}"/> 逐字相同：看到的那份数据
    /// 与发出去的那份必须是同一份）。多行按 §12.3 的参数预算分块，跨块不是原子的，见 <see cref="WithBatchTransaction"/>。</summary>
    IInsertOrUpdate<T> SetSource(IEnumerable<T> entities);

    /// <summary>这一批要不要<em>一次提交</em>（默认 <see langword="true"/>，与 <see cref="IUpdateBulk{T}.WithBatchTransaction"/>
    /// 同一条 2026-10-07 的裁决）。判据是<em>语句数</em>而不是块数：一句 upsert 本身就原子，包它只多付两次往返，
    /// 只有这批被切成两句以上时才自开一条事务。传 <see langword="false"/> 回到"每句自己提交、半批可以在库里落地"。</summary>
    IInsertOrUpdate<T> WithBatchTransaction(bool atomic = true);

    /// <summary>不连库看这批会发出的命令文本（发射形态已按这一家的句尾 ∶ MERGE 落定）。
    /// <b>这一颗不交全局过滤器那笔 RUC/RDC 税</b>，比 <see cref="IUpdateBulk{T}.Explain"/> 更干净一档：那条链要把
    /// 过滤器叠进 <c>WHERE</c>（走闭包常量求值），而这一条链的句子里没有 WHERE 可叠，过滤器在场是<em>当场拒</em>而不是
    /// 叠加（见 <see cref="BulkWriteMethod.Upsert"/> 那两道拒收）——它一次表达式翻译都不做，与
    /// <see cref="QuickSqlOrm.InsertRange{T}"/> 同为"入口零反射税"那一族。</summary>
    BulkWritePlan Explain();

    /// <summary>不连库；<b>只有单条命令时可取</b>，口径与 <see cref="IInsert{T}.ToSql"/> 逐字相同。</summary>
    string ToSql();

    /// <summary>逐块内联字面量，多块时带 <c>-- 命令 n</c> 分节：交付给人审阅的文本必须能看出有几条语句。</summary>
    string ToLogSql();

    /// <summary>受影响行数（多块时各块之和，按驱动口径）。这一颗不回填键：一句 upsert 的回填那一档
    /// 各家形态不一（<c>MERGE … OUTPUT</c>、<c>ON CONFLICT … RETURNING</c>），今天都不在这一条链上，
    /// 要键走 <see cref="IInsert{T}.ExecuteReturnKeysAsync"/> 或先插后查。</summary>
    Task<int> ExecuteAffrowsAsync(CancellationToken ct = default);
}
