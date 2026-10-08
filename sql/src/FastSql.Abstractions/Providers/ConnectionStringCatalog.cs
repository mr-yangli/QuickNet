using System.Data.Common;

namespace FastSql.Providers;

/// <summary>连接串里那颗「目标库名」的读取与摘除，供支持建库的 provider 共用
/// （MySQL 血统、PG 血统、T-SQL 三家；<see cref="IDbProvider.EnsureDatabaseExistsAsync"/>）。
///
/// <para>为什么要收成一层：<c>EnsureDatabaseExistsAsync</c> 读不到库名时走的是「这条串没指名字，本来就不该建库」
/// 那一岔，<b>静默返回</b>。这个判断本身没错，错的是它可以被一种读法伪装成另一种 —— 只读 <c>Database</c> 的实现
/// 面对一条写 <c>Initial Catalog=…</c> 的合法串（MySqlConnector 认这颗同义词）就落到静默那一岔，
/// 本项目实测过的形状正是这一种：<c>CanCreateDatabase</c> 一路报 <see langword="true"/>、建库那一步却静默没跑，
/// 最后连不上库的是调用方，而 MySQL 报出来的 <c>Unknown database</c> 里已经没有「本应建库」这件事了。</para>
///
/// <para>第二格要记的是：<em>裸</em> <see cref="DbConnectionStringBuilder"/> <b>不做同义词归一</b>。
/// .NET 10 实测（合成串 <c>…; Initial Catalog=MedicineNext;…</c>）：<c>TryGetValue("Database")</c> 返回 false，
/// <c>"InitialCatalog"</c>、<c>"initial_catalog"</c>、双空格的写法也一律 false —— 它只按字面键比，仅忽略大小写。
/// 所以同义词必须由调用方一颗颗列出来，不能指望 builder 替归一；列了哪几颗，就只认哪几颗。</para>
/// </summary>
/// <param name="keys">这一家驱动认的库名键的字面写法，按常见程度排。至少一颗。</param>
public sealed class ConnectionStringCatalog(params string[] keys)
{
    /// <summary>候选键。构造期定死，之后不变更。</summary>
    readonly string[] _keys = keys.Length > 0
        ? [.. keys]
        : throw new ArgumentException("同义词表不能是空的：一颗键都不列就等于这座库名永远读不出来。", nameof(keys));

    /// <summary>那颗目标库名；<see langword="null"/> 即「这条串没指名字」，调用方按原样跳过建库。
    /// <b>两颗键同时在场且值不同时抛而不是挑一颗</b>：这一格的产出是「建哪座库」，
    /// 挑错的那一种结果是建了一座没人要的库、而连接还是连不上原来那座 —— 静默挑一颗在这里比抛更贵。</summary>
    public string? Read(DbConnectionStringBuilder builder)
    {
        string? found = null;
        string? foundKey = null;

        foreach (string key in _keys)
        {
            if (!builder.TryGetValue(key, out object? value) || value is not string text || text.Length == 0)
                continue;

            if (found is null)
            {
                found = text;
                foundKey = key;
                continue;
            }

            if (string.Equals(found, text, StringComparison.OrdinalIgnoreCase))
                continue;

            throw new FastSqlException(
                $"这条连接串里有两颗互相矛盾的库名：{foundKey}={found} 与 {key}={text}。" +
                "本方法要按那个名字建库，所以不能替你挑一颗 —— 请把它们改成同一个名字（或删掉其中一颗）。");
        }

        return found;
    }

    /// <summary>把所有候选键摘干净。<b>必须整颗摘而不是只摘读到的那一颗</b>：留两颗互相矛盾的键
    /// 等于把「哪一颗说了算」交给驱动的解析顺序，而驱动自己会同义词归一，比这一层宽容。
    /// 摘完之后调用方再写上自己要连的那座锚点库（<c>master</c>、<c>postgres</c>，或干脆不写）。</summary>
    public void Clear(DbConnectionStringBuilder builder)
    {
        foreach (string key in _keys)
            builder.Remove(key);
    }
}
