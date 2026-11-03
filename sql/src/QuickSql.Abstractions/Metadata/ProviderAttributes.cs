namespace QuickSql.Metadata;

/// <summary>各家 provider 的方言专属声明位。名称约定为 Provider 加 Feature 加 Attribute，一格一个语义；
/// 新的一格只有两个门槛：自研通用注解与 DataAnnotations 表达不了，且该语义有活库或文档的实证
/// （最好有发射形判据）。没有这两条的家不在这里立空壳。
/// 解析纪律与 IndexAttribute 同形：两侧管线（源生成与反射）都只抄声明进 SkipIndexFacts，
/// 成员到列换算、约定名、声明间冲突的判定全部收在 EntityContractFactory 一份实现里；
/// 对着发射形有 provider 语义的校验（参数档位、型号拼写）落在印发那一家的 CodeFirst 上，不进中性契约。</summary>
public static class ProviderAttributes
{
    /// <summary>SkipIndexKind 渲染成命名约定里的短词（EntityContractFactory 生成约定名用）。
    /// 这个表是纯名字，不发任何判据：档位合不合法是发射侧（那一家 CodeFirst）的事。</summary>
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

/// <summary>ClickHouse 两侧的数据跳过索引（data skipping index）型号。
/// 型的拼写属于发射侧（ClickHouse CodeFirst）：契约里只存这一格档名，
/// 拼不拼得出合法的 TYPE 文本由发射时判，参数校验同样落那一侧——中性契约不背方言拼写。</summary>
public enum SkipIndexKind
{
    /// <summary>本家的默认档：列的最值两颗。</summary>
    MinMax = 0,

    /// <summary>列值的去重集合，参数 max_rows 由 SkipIndexContract.SetMaxRows 带。</summary>
    Set = 1,

    /// <summary>布隆滤波，无参数档。</summary>
    BloomFilter = 2,

    /// <summary>按整词切的布隆滤波（tokenbf_v1），参数三颗：桶、哈希函数数、种子。</summary>
    TokenBloomFilter = 3,

    /// <summary>按 n-gram 切的布隆滤波（ngrambf_v1），参数四颗：n 元宽、桶、哈希函数数、种子。</summary>
    NgramBloomFilter = 4,
}

/// <summary>ClickHouse 的数据跳过索引声明位（这一族没有 CREATE INDEX 语句级格）。
/// 打在属性上的是"这一列一颗索引"（表达式缺省就是那个列名；要挂着别的表达式就把 Expression 写上）；
/// 打在类上的是表级表达式索引，必须显式给 Expression（类挂没有"缺省成员"可言）。
/// 一颗声明一颗索引，类与属性两处都允许多颗。索引名缺省按约定给（QSI_表_列_档，类挂 QSI_表_序号），
/// 与 IndexAttribute 同一条纪律：两侧管线抄声明，约定名在运行期收口。
/// 声明位的形状是"建表语句骨架里的 INDEX 名 表达式 TYPE 档 GRANULARITY n"，
/// 发射那一半归 ClickHouse 的 CodeFirst；契约侧只有 SkipIndexContract。</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class, AllowMultiple = true)]
public sealed class ClickHouseSkipIndexAttribute(SkipIndexKind kind) : Attribute
{
    /// <summary>跳过索引型号（档名本身不拼进任何文本；发射那一侧按本家语法定 TYPE）。</summary>
    public SkipIndexKind Kind { get; } = kind;

    /// <summary>GRANULARITY 颗位。0 = 没写，发射侧按本家缺省（1）发。</summary>
    public int Granularity { get; set; }

    /// <summary>索引表达式。属性挂时为 null = 就是那颗列（发射侧加方言引号）；类挂时必填，
    /// 表达式里的标识符由声明者自己负责（原样抄，不做成员到列换算）。</summary>
    public string? Expression { get; set; }

    /// <summary>显式索引名。缺省走约定，两条声明撞同一个名字在 EntityContractFactory 当场红。</summary>
    public string? Name { get; set; }

    /// <summary>Set 档的 max_rows 参数；那档没有它就是发射时红。</summary>
    public int SetMaxRows { get; set; }

    /// <summary>NgramBloomFilter 档的 n 元宽。</summary>
    public int NgramSize { get; set; }

    /// <summary>tokenbf_v1/ngrambf_v1 那两档的滤波桶大小。</summary>
    public int BucketSize { get; set; }

    /// <summary>tokenbf_v1/ngrambf_v1 那两档的哈希函数数；0 = 没写，发射侧按 1 发。</summary>
    public int HashFunctions { get; set; }

    /// <summary>tokenbf_v1/ngrambf_v1 那两档的种子。</summary>
    public int Seed { get; set; }
}

/// <summary>一侧管线给另一侧（含 EntityContractFactory）交出的跳过索引声明：
/// 反射侧由 ClickHouseSkipIndexAttribute 抄，源生成侧由生成器把同一形状写成代码。
/// 列名换算、约定名、冲突判定都不在这里做——EntityContractFactory 只有一份实现。</summary>
public sealed record SkipIndexFacts
{
    /// <summary>属性挂时的成员名；类挂 = null（那一路必须给 Expression）。</summary>
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

/// <summary>已解析的跳过索引契约。Expression 属性挂时是换算过的列名（plain、没加引号），
/// 发射侧要自己加方言引号；类挂的原样表达式不加引号整段抄。
/// MemberName 非 null 的另一份含义就是给发射侧的"要加引号"信号。</summary>
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
