namespace FastSql.Metadata;

/// <summary>自研强属性层（§10.2 优先级 2）。只用于注解本身表达不了的语义；
/// 能由 DataAnnotations 表达的绝不新增属性，否则实体会同时存在两套写法。</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class TableAttribute(string name) : Attribute
{
    public string Name { get; } = name;

    public string? Schema { get; set; }

    public string? Comment { get; set; }
}

/// <summary>注解必须能区分「没写」和「写了 false」，否则优先级链无法判定（§10.2）。
/// <c>bool</c> 表达不了这件事，所以自研属性一律用三态。</summary>
public enum TriState
{
    Unset = 0,
    Yes = 1,
    No = 2,
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class ColumnAttribute(string? name = null) : Attribute
{
    public string? Name { get; } = name;

    /// <summary>provider 原生类型名，绕过类型映射表（<c>VARCHAR2(50 CHAR)</c>、<c>jsonb</c>）。</summary>
    public string? DbTypes { get; set; }

    public int Length { get; set; }

    public int Precision { get; set; }

    public int Scale { get; set; }

    public TriState IsNullable { get; set; } = TriState.Unset;

    public TriState IsPrimary { get; set; } = TriState.Unset;

    /// <summary>No = 有列但不是数据库自增（客户端主键、序列由 <c>IKeyStrategy</c> 决定）。</summary>
    public TriState IsIdentity { get; set; } = TriState.Unset;

    public bool IsIgnore { get; set; }

    public string? Comment { get; set; }
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class IndexAttribute(string name, params string[] memberNames) : Attribute
{
    public string Name { get; } = name;

    public string[] MemberNames { get; } = memberNames;

    public bool Unique { get; set; }

    public bool Clustered { get; set; }

    public bool Descending { get; set; }
}

/// <summary>乐观并发的比较列（§8.2、§10.1 的乐观锁那一档）。DataAnnotations 里没有等价的注解
/// （<c>KeyAttribute</c> 是身份、<c>TimestampAttribute</c> 在 EF Core 的程序集里），所以这一格落在自研属性层。</summary>
/// <remarks><b>它的语义是「只比较、不书写」，四件事同时成立</b>：这一列不进 <c>UPDATE</c> 的 <c>SET</c>、
/// 不进 INSERT 的列清单（值由库给：SQL Server 的 <c>rowversion</c>、别家的触发器或默认值），
/// 但进 <c>UPDATE</c>/<c>DELETE</c> 的 <c>WHERE</c>（比较的是<em>物化时拿到的那个值</em>）
/// 与 INSERT/UPDATE 之后的<b>回填集</b>（不回读就是「手里的值永远是旧的」，下一次保存必然假冲突）。
/// <para><b>「不书写」不等于「不参与脏判定」</b>：往这一列里写值是有人绕开了"值归库给"这条，必须现形，
/// 所以脏判定照比，比出差异就在发射前<em>整句拒掉</em>（而不是剔掉那一列后照发）。
/// 剔掉才是错的：那等于把调用方写进去的值静默丢弃，而静默丢弃查不出来。</para>
/// <para>由<em>调用方</em>自己递增的版本号（<c>int Version</c> 那一派写法）不属于这一格：那一列要进 <c>SET</c>，
/// 而「谁负责 +1」还没定，缺口与判据记在 §14 的 P1 余项。</para></remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ConcurrencyTokenAttribute : Attribute;
