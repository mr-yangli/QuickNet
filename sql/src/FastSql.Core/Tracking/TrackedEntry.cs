using FastSql.Metadata;

namespace FastSql.Tracking;

/// <summary><see cref="EntityEntry"/> 的落地。<b>它只是一个视图，不持有任何状态</b>：全部读写都落在
/// <see cref="Node"/> 与 <see cref="EntityTracker{T}"/> 上，所以 <c>Entry(o)</c> 拿两次、
/// 或者拿完之后又 <c>DetectChanges</c> 一次，看到的都是当时的事实，不会有第二份要同步的副本。
/// 这也是 <see cref="EntityEntry"/> 的类注释里那句"写下去改的就是跟踪器的状态"的实现形态。</summary>
sealed class TrackedEntry<T>(EntityTracker<T> tracker, Node node) : EntityEntry<T> where T : class
{
    /// <summary>基类那一个抽象成员（公开的 <c>Entity</c> 两档都由它转交，理由写在
    /// <see cref="EntityEntry.EntityInstance"/> 那一段）。</summary>
    protected override object EntityInstance => node.Entity;

    public override Type EntityType => typeof(T);

    /// <summary>赋值走 <see cref="EntityTracker{T}.ApplyState"/>，因此连带效果一件不少：
    /// <see cref="EntityState.Unmodified"/> 会重拍快照，<see cref="EntityState.Modified"/> 会把可写列全标脏，
    /// <see cref="EntityState.Detached"/> 会真的把它从身份映射里摘出去。
    /// <b>在这里直接写 <c>node.State = value</c> 才是那个"看起来能用其实只做了一半"的版本</b>。</summary>
    public override EntityState State
    {
        get => node.State;
        set => tracker.ApplyState(node, value);
    }

    public override bool IsModified => tracker.WillWrite(node);

    public override IReadOnlyList<string> ModifiedMembers => tracker.ModifiedMembers(node);

    public override PropertyEntry Property(string memberName) => tracker.Property(node, memberName);
}

/// <summary>一列的读写入口。<b>三样东西各有各的出处，这正是这一格容易写错的地方</b>：
/// <see cref="CurrentValue"/> 在实体上、<see cref="OriginalValue"/> 在节点的快照里、
/// <see cref="IsModified"/> 在"覆盖 + 侦测结论"的合成结果里。前两者是数据，最后一个是意图。</summary>
sealed class TrackedProperty<T>(EntityTracker<T> tracker, Node node, int ordinal, ColumnContract column) : PropertyEntry
    where T : class
{
    public override string MemberName => column.MemberName;

    /// <summary>读写的是<em>实体本身</em>那一格，不是快照。写它之后 <c>DetectChanges</c> 才看得见这次改动 ——
    /// 「改 <c>CurrentValue</c> 就等于改实体」是这一档存在的理由，否则它就成了一个改了没用的 setter。</summary>
    public override object? CurrentValue
    {
        get => tracker.Value((T) node.Entity, ordinal);
        set => tracker.Write((T) node.Entity, ordinal, value);
    }

    public override object? OriginalValue => node.Snapshot[ordinal];

    /// <summary>读的是合成结论（覆盖优先，否则比较），写的是覆盖 —— 两者不是一回事，
    /// 所以 <c>IsModified = IsModified</c> 这句看起来无害的写法会把"没表过态"变成"表过态"，
    /// 从此这一列不再参与比较。<b>要退回比较只有一个办法</b>：把整行的状态重设
    /// （<c>Entry(o).State = Unmodified</c> 会清掉所有覆盖）。</summary>
    public override bool IsModified
    {
        get => node.Dirty[ordinal];
        set
        {
            node.Overrides[ordinal] = value;
            node.Dirty[ordinal] = value;

            if (value)
                tracker.MarkWrites();
        }
    }
}
