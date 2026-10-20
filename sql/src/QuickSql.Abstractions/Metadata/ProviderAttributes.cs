namespace QuickSql.Metadata;

/// <summary>各家 provider 的方言专属声明位。名称约定 <c>{Provider}{Feature}Attribute</c>，
/// 一格一个语义；新的一格只有两个门槛：<b>自研通用注解（<see cref="QuickColumnAttribute"/>、
/// <see cref="IndexAttribute"/> 等）与 DataAnnotations 表达不了</b>，且<b>该语义有活库或文档的实证
/// （最好有发射形判据）</b>。没有这两条的家不在这里立空壳 —— 声明一颗没有消费者的"支持"
/// 正是 §13.5 那本台账要避免的东西。</summary>
/// <remarks>这一层的解析纪律与 <see cref="IndexAttribute"/> 同形：两侧管线（G1 源生成 ∶ G2 反射）
/// 都只<b>抄声明</b>进 <see cref="SkipIndexFacts"/>，成员 → 列换算、约定名、声明间冲突的判定全部
/// 收在 <see cref="EntityContractFactory"/> 一份实现里（§10.3）；对着发射形有 provider 语义的校验
/// （参数档位、型号拼写）则落在印发那一家的 CodeFirst 上，不进中性契约。</remarks>
public static class ProviderAttributes
{
    /// <summary><see cref="SkipIndexKind"/> 渲染成命名约定里的短词（<see cref="EntityContractFactory"/>
    /// 生成约定名用）。这个表是<b>纯名字</b>，不发任何判据：档位合不合法是发射侧（那一家 CodeFirst）的事。</summary>
    public static string KindToken(SkipIndexKind kind) => kind switch
    {
        SkipIndexKind.MinMax => "minmax",
        SkipIndexKind.Set => "set",
        SkipIndexKind.BloomFilter => "bloom",
        SkipIndexKind.TokenBloomFilter => "tokenbf",
        SkipIndexKind.NgramBloomFilter => "ngrambf",
        _ => kind.ToString().ToLowerInvariant(),
    };
}

/// <summary>ClickHouse 两侧的数据跳过索引（data skipping index）型号。</summary>
/// <remarks>型的拼写属于发射侧（ClickHouse CodeFirst）：契约里只存这一格档名，
/// 拼不拼得出合法的 <c>TYPE</c> 文本由发射时判，参数校验同样落那一侧 —— 中性契约不背方言拼写。</remarks>
public enum SkipIndexKind
{
    /// <summary>本家的默认档：列的最值两颗。</summary>
    MinMax = 0,

    /// <summary>列值的去重集合，参数 <c>max_rows</c> 由 <see cref="SkipIndexContract.SetMaxRows"/> 带。</summary>
    Set = 1,

    /// <summary>布隆滤波，无参数档。</summary>
    BloomFilter = 2,

    /// <summary>按整词切的布隆滤波（tokenbf_v1），参数三颗：桶、哈希函数数、种子。</summary>
    TokenBloomFilter = 3,

    /// <summary>按 n-gram 切的布隆滤波（ngrambf_v1），参数四颗：n 元宽、桶、哈希函数数、种子。</summary>
    NgramBloomFilter = 4,
}

/// <summary>ClickHouse 的数据跳过索引声明位（§9.1 第 22 颗库种，这一族没有 CREATE INDEX 语句级格）。
/// 打在<b>属性</b>上的是"这一列一颗索引"（表达式缺省就是那个列名；要挂着别的表达式就把
/// <see cref="Expression"/> 写上）；打在<b>类</b>上的是表级表达式索引，<b>必须显式给
/// <see cref="Expression"/></b>（类挂没有"缺省成员"可言）。一颗声明一颗索引，
/// <see cref="AttributeTargets.Class"/> 与属性两处都允许多颗（<c>AllowMultiple</c>）。
/// 索引名缺省按约定给（<c>QSI_{表}_{列}_{档}</c> / 类挂 <c>QSI_{表}_{序号}</c>），
/// 与 <see cref="IndexAttribute"/> 同一条纪律：<b>两侧管线抄声明，约定名在运行期收口</b>，
/// 所以 §13.11 的两侧对拍连这一格的名字一起管。</summary>
/// <remarks>声明位的形状是"建表语句骨架里的 <c>INDEX 名 表达式 TYPE 档 GRANULARITY n</c>"，
/// 发射那一半归 ClickHouse 的 CodeFirst；契约侧只有 <see cref="SkipIndexContract"/>。</remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class, AllowMultiple = true)]
public sealed class ClickHouseSkipIndexAttribute(SkipIndexKind kind) : Attribute
{
    /// <summary>跳过索引型号（档名本身不拼进任何文本；发射那一侧按本家语法定 <c>TYPE</c>）。</summary>
    public SkipIndexKind Kind { get; } = kind;

    /// <summary>GRANULARITY 颗位。0 = 没写，发射侧按本家缺省（1）发。</summary>
    public int Granularity { get; set; }

    /// <summary>索引表达式。属性挂时为 null = 就是那颗列（发射侧加方言引号）；类挂时<b>必填</b>，
    /// 表达式里的标识符由声明者自己负责（原样抄，不做成员 → 列换算）。</summary>
    public string? Expression { get; set; }

    /// <summary>显式索引名。缺省走约定（<c>QSI_{表}_…</c>），两条声明撞同一个名字在
    /// <see cref="EntityContractFactory"/> 当场红。</summary>
    public string? Name { get; set; }

    /// <summary><see cref="SkipIndexKind.Set"/> 的 <c>max_rows</c> 参数；那档没有它就是发射时红。</summary>
    public int SetMaxRows { get; set; }

    /// <summary><see cref="SkipIndexKind.NgramBloomFilter"/> 的 n 元宽。</summary>
    public int NgramSize { get; set; }

    /// <summary>tokenbf_v1/ngrambf_v1 那两档的滤波桶大小。</summary>
    public int BucketSize { get; set; }

    /// <summary>tokenbf_v1/ngrambf_v1 那两档的哈希函数数；0 = 没写，发射侧按 1 发。</summary>
    public int HashFunctions { get; set; }

    /// <summary>tokenbf_v1/ngrambf_v1 那两档的种子。</summary>
    public int Seed { get; set; }
}

/// <summary>一侧管线给另一侧（含 <see cref="EntityContractFactory"/>）交出的跳过索引声明：
/// G2 由 <see cref="ClickHouseSkipIndexAttribute"/> 反射抄，G1 由源生成器把同一形状写成代码。
/// 列名换算、约定名、冲突判定都不在这里做 —— <see cref="EntityContractFactory"/> 只有一份实现（§10.3）。</summary>
public sealed record SkipIndexFacts
{
    /// <summary>属性挂时的成员名；类挂 = null（那一路必须给 <see cref="Expression"/>）。</summary>
    public string? MemberName { get; init; }

    /// <summary>显式表达式。属性挂时为 null = 就是那颗列；类挂时必须非 null（两侧都在声明抄本那一层判）。</summary>
    public string? Expression { get; init; }

    /// <summary>显式索引名；null = 约定名，运行期收口。</summary>
    public string? Name { get; init; }

    public required SkipIndexKind Kind { get; init; }

    /// <summary>0 = 声明里没写，发射侧按本家缺省发。</summary>
    public int Granularity { get; init; }

    public int SetMaxRows { get; init; }

    public int NgramSize { get; init; }

    public int BucketSize { get; init; }

    public int HashFunctions { get; init; }

    public int Seed { get; init; }
}

/// <summary>已解析的跳过索引契约。<see cref="Expression"/> 属性挂时是<b>换算过的列名</b>（plain、
/// 没加引号），发射侧要自己加方言引号；类挂的原样表达式<em>不加引号整段抄</em>。
/// <see cref="MemberName"/> 非 null 的另一份含义就是给发射侧的"要加引号"信号。</summary>
public sealed record SkipIndexContract(
    string Name,
    SkipIndexKind Kind,
    int Granularity,
    string Expression,
    string? MemberName = null,
    int SetMaxRows = 0,
    int NgramSize = 0,
    int BucketSize = 0,
    int HashFunctions = 0,
    int Seed = 0);
