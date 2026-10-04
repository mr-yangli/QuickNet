namespace FastSql.Tracking;

/// <summary>跟踪器里一个实体的状态（§8.2 的状态机）。
/// <b>与规格那句列举的顺序差了一格，而且是有意的</b>：§8.2 写的是
/// <c>Added / Unmodified / Modified / Deleted / Detached</c>，这里把 <see cref="Detached"/> 挪到 <c>0</c> 位 ——
/// 一个没被赋过值的 <c>EntityState</c> 字段（结构体默认、忘了设置的局部变量、反序列化缺字段）
/// 落在 <c>Added</c> 上的后果是「<em>一次保存把一只谁都没打算写的实体插进库</em>」，
/// 而落在 <c>Detached</c> 上的后果是「这一行不进任何语句」。宁可少做，不要多做。
/// 名字与个数一个都没改，所以按名字 switch 的调用方不受影响。</summary>
public enum EntityState
{
    /// <summary>会话不认识这个实例：不在身份映射里、不进 <see cref="SaveChangesReport"/>，
    /// 也是 <c>default(EntityState)</c> 那一档。</summary>
    Detached = 0,

    /// <summary>本会话新加入、库里还没有这一行。<see cref="EntityState"/> 的写入顺序里它在最前。</summary>
    Added,

    /// <summary>从库里读出来（或被 <c>Attach</c>）且<em>相对快照没变</em>：不产生任何语句。</summary>
    Unmodified,

    /// <summary>相对快照有变化：写入时只发变化的那些列（§8.2 的「脏列合成」）。
    /// 变化被撤销后回到 <see cref="Unmodified"/>，所以「曾经碰过」不会变成一次多余的 <c>UPDATE</c>。</summary>
    Modified,

    /// <summary>本会话要删掉这一行；写完之后实例转为 <see cref="Detached"/>。</summary>
    Deleted,
}
