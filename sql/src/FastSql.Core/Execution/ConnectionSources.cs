using FastSql.Metadata;

namespace FastSql.Execution;

/// <summary>集群名 → 连接串的在册表。<b>它是"一 ORM 实例 = 一条固定连接串"那颗硬堵点的解法</b>
/// （规格 §16.4①）：连接串从<em>一个字段</em>折成<em>一张按集群名查的表</em>，于是执行层问的不再是
/// "那条串"而是"这一座集群、这一个角色的串"。
///
/// <para><b>连接串仍然只活在这里，不进元数据、不进契约、不进版本库</b>：实体侧的
/// <see cref="DataSourceFacts"/> 只有名字。一张表属于哪座库是声明，那条库怎么连是配置，
/// 两者今天第一次被分到两个类型里。</para>
///
/// <para><b>一个 ORM 实例一家 provider</b>：多集群说的是同一家厂商的几座库（分库、多租户、报表库），
/// 不是异构引擎。跨厂商要两颗 <see cref="FastSqlOrm"/>，而那颗判据落在组装根而不是这里 ——
/// 这一层连 provider 都不持有，只管名字与串。</para></summary>
public sealed class ConnectionSources
{
    sealed class ClusterSlot(string name, string primary, IReadOnlyList<string> replicas)
    {
        /// <summary>从库轮询位。<see cref="Interlocked"/> 递增：一次 <c>ExecuteNonQuery</c> 与它的
        /// 下一句读可能在两条线程上，而这一格的代价只是一颗整数交换，不是锁。</summary>
        int counter;

        public string Name { get; } = name;

        public string Primary { get; } = primary;

        public IReadOnlyList<string> Replicas { get; } = replicas;

        /// <summary>取模的是<em>已经用过的次数</em>，所以第一句落在清单的第一颗：<c>Interlocked.Increment</c>
        /// 给的是递增<em>之后</em>的值，不减那一颗就悄悄变成"第一句打第二颗"。</summary>
        public int NextReplicaOrdinal() => DataSourceRouting.ReplicaOrdinal(Interlocked.Increment(ref counter) - 1, Replicas.Count);
    }

    readonly Dictionary<string, ClusterSlot> _clusters;

    /// <param name="defaultPrimary">缺省集群（<see cref="DataSourceRouting.DefaultCluster"/>）的主库串。</param>
    /// <param name="defaultReplicas">缺省集群的从库，可以为空 —— 为空时读与写落在同一条串上，
    /// 也就是今天的行为。</param>
    /// <param name="clusters">其余具名集群。</param>
    public ConnectionSources(string defaultPrimary, IReadOnlyList<string> defaultReplicas, IReadOnlyList<ClusterConfig> clusters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultPrimary);

        _clusters = new Dictionary<string, ClusterSlot>(StringComparer.Ordinal)
        {
            [DataSourceRouting.DefaultCluster] = new ClusterSlot(DataSourceRouting.DefaultCluster, defaultPrimary, Sanitize(defaultReplicas, DataSourceRouting.DefaultCluster)),
        };

        foreach (var cluster in clusters)
        {
            var name = DataSourceRouting.ValidateName(cluster.Name, "FastSqlOrmOptions.Clusters");

            if (string.Equals(name, DataSourceRouting.DefaultCluster, StringComparison.Ordinal))
                throw new FastSqlException("具名集群里出现了 main：这一座由 ConnectionString 那一条串占着，要用 ClusterConfig 加库请另起一个名字");

            if (string.IsNullOrWhiteSpace(cluster.Primary))
                throw new FastSqlException($"具名集群 {name} 没有主库串：ClusterConfig 里那颗 Primary 是空的。这一座要么给一条真的连接串，要么整条从 Clusters 清单里删掉");

            _clusters[name] = new ClusterSlot(name, cluster.Primary, Sanitize(cluster.Replicas, name));
        }

        ClusterNames = [.. _clusters.Keys.Order(StringComparer.Ordinal)];
    }

    /// <summary>在册的集群名，按序。<b>报错消息与 DDL 分组都读这一颗</b>，所以"写了个不存在的集群名"
    /// 那一句话里能看见有哪些可选。</summary>
    public IReadOnlyList<string> ClusterNames { get; }

    public bool Knows(string cluster) => _clusters.ContainsKey(cluster);

    public int ReplicaCount(string cluster) => Slot(cluster).Replicas.Count;

    /// <summary>这一座集群的主库串。事务、写语句与"三档强制主库"那三档都读它。</summary>
    public string PrimaryConnectionString(string cluster) => Slot(cluster).Primary;

    /// <summary>该用哪一条串：主库直接给，从库按配上的顺序轮询取一颗。<b>没有权重、没有延迟探测</b>，
    /// 理由写在 <see cref="DataSourceRouting.ReplicaOrdinal"/> 那一段。</summary>
    public string ConnectionStringFor(string cluster, ConnectionRole role)
    {
        var slot = Slot(cluster);

        return role == ConnectionRole.Primary || slot.Replicas.Count == 0
            ? slot.Primary
            : slot.Replicas[slot.NextReplicaOrdinal()];
    }

    ClusterSlot Slot(string cluster)
        => _clusters.TryGetValue(cluster, out var slot)
            ? slot
            : throw new FastSqlException($"集群 {cluster} 不在册（在册的是 {string.Join("、", ClusterNames)}）。实体声明的集群名要与 FastSqlOrmOptions.Clusters 里那一条对上，或按缺省的 {DataSourceRouting.DefaultCluster} 不写声明。");

    /// <summary>从库清单的收口：去掉 null 与空白串。<b>不许静默少一颗</b>——给了 <c>["", "…"]</c>
    /// 而库数变成一，症状是"轮询少一台"，那种账要到容量出问题时才看得见。</summary>
    static IReadOnlyList<string> Sanitize(IReadOnlyList<string>? replicas, string cluster)
    {
        if (replicas is null or { Count: 0 })
            return [];

        var kept = new List<string>(replicas.Count);

        foreach (var replica in replicas)
        {
            if (string.IsNullOrWhiteSpace(replica))
                throw new FastSqlException($"集群 {cluster} 的从库清单里有一颗空的：要么删掉那一个位置，要么给它一条真的连接串");

            kept.Add(replica);
        }

        return kept;
    }
}
