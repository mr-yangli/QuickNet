using System.Collections.Frozen;

namespace FastSql.Schema;

/// <summary>按 <see cref="Type"/> 惰性填充的<b>无锁读</b>表：§12.7 B 那句「读多写极少 → FrozenDictionary」
/// 在 type→实体 那几格上的落地（各家方言的类型表早就用了冻结表，唯独这两个注册表还停在
/// <c>ConcurrentDictionary</c>，于是每次取 schema 都过一次分桶锁的读路径，§12.8 第 5 行量的就是这一格）。</summary>
/// <remarks><b>为什么"惰性填充"与"一次冻结"能共存</b>：写入不是运行期的事，是<b>每个实体第一次被问到</b>的那一次，
/// 一生每型一次；所以这里用 copy-on-write —— 读走冻结表快照（一次易失读 + 一次查找，无锁、无 CAS、无分桶），
/// 写时先在锁内更新种子表，再把整张新表一次性换掉指针。每写一次重建整表，这笔钱按实体总数付而不是按行数付，
/// 而实体总数是几十到几百的量级，写入次数与它同阶，所以总代价是 O(实体数²) 的一次性开销，
/// 换到的是此后每一次读都没有锁。
/// <b>快照的发布只有一个方向</b>：写侧永远在锁内基于当前种子表重建，因此后写的那一份一定包含先写的那一份，
/// 读侧只会看到"少一个键"或"全都有"，不会看到半张表。</remarks>
sealed class TypeTable<TValue>
{
    readonly Dictionary<Type, TValue> _seed = [];
    readonly object _gate = new();

    FrozenDictionary<Type, TValue> _table = new Dictionary<Type, TValue>().ToFrozenDictionary();

    /// <summary>无锁读路径：命中就直接给。</summary>
    public bool TryGet(Type key, out TValue? value) => Volatile.Read(ref _table).TryGetValue(key, out value);

    /// <summary>取，未见过这一型就当场建。工厂的额外参数照 <c>ConcurrentDictionary</c> 那一份形状留着：
    /// 两个注册表都靠它避免"每次调用.alloc 一个闭包"。</summary>
    public TValue GetOrAdd<TArg>(Type key, Func<Type, TArg, TValue> factory, TArg arg)
    {
        if (TryGet(key, out var hit))
            return hit!;

        lock (_gate)
        {
            if (_seed.TryGetValue(key, out var existing))
                return existing;

            var value = factory(key, arg);

            _seed[key] = value;
            Publish();

            return value;
        }
    }

    /// <summary>记账用的插入（已存在则不动）：注册表用它维护"解析过哪些实体"。</summary>
    public void Add(Type key, TValue value)
    {
        if (TryGet(key, out _))
            return;

        lock (_gate)
        {
            if (_seed.ContainsKey(key))
                return;

            _seed[key] = value;
            Publish();
        }
    }

    /// <summary>已登记的键。读的是快照，所以调用方拿到的是当时那一刻的集合，不会边读边变。</summary>
    public IReadOnlyCollection<Type> Keys => Volatile.Read(ref _table).Keys.ToArray();

    public int Count => Volatile.Read(ref _table).Count;

    void Publish() => Volatile.Write(ref _table, _seed.ToFrozenDictionary());
}
