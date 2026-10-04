namespace FastSql.Tracking;

/// <summary>身份映射的键：主键各列的值组成的序列（§8.2 的「键为 <c>(EntityType, PK)</c>」里 PK 那一半，
/// 类型那一半在 <c>EntityTracker&lt;T&gt;</c> 上，所以这里不需要再带 <see cref="Type"/>）。
///
/// <b>为什么手写相等而不是包一个 <c>Record</c></b>：<c>object?[]</c> 作为记录成员时，合成的
/// <c>Equals</c>/<c>GetHashCode</c> 走的是 <c>StructuralEquality</c>/<c>StructuralHashCode</c>，
/// 那两套 helper 每比较一次就把序列拷进一个 <c>object[]</c> 再逐项处理 —— 每行两次装箱加一次数组分配。
/// 物化一万行时这笔钱是每行都付的，而 <see cref="Materialization.Materializer"/> 的 <c>ReaderKey</c>
/// 已经因为同一条理由手写过一遍（§12.7 C）。这里抄它的做法：<see cref="GetHashCode"/> 一次算完存字段，
/// <see cref="Equals"/> 逐项 <c>Equals</c>，不分配。
///
/// <b>值本身就是装箱的</b>：<c>IEntityTypeSchema&lt;T&gt;.GetValue</c> 返回 <see cref="object"/>，
/// 所以键这一路的装箱发生在取值那一步，不是这里加的。消除它要给 schema 加一份非装箱的主键读取口，
/// 记在 §14 的 P1 余项里。</summary>
readonly struct IdentityKey(object?[] values) : IEquatable<IdentityKey>
{
    readonly object?[] _values = values;

    public readonly int HashCode = ComputeHash(values);

    public bool Equals(IdentityKey other)
    {
        var values = _values;

        if (values.Length != other._values.Length)
            return false;

        for (var i = 0; i < values.Length; i++)
            if (!Equals(values[i], other._values[i]))
                return false;

        return true;
    }

    public override bool Equals(object? obj) => obj is IdentityKey key && Equals(key);

    public override int GetHashCode() => HashCode;

    /// <summary>逐项混合。<b>顺序参与哈希</b>：复合主键 <c>(A, B)</c> 与 <c>(B, A)</c> 是两个不同的键，
    /// 而 <c>xor</c> 折叠会把它们算成同一个（相等判据仍然对，只是碰撞变多）。</summary>
    static int ComputeHash(object?[] values)
    {
        var hash = new HashCode();

        foreach (var value in values)
            hash.Add(value);

        return hash.ToHashCode();
    }
}
