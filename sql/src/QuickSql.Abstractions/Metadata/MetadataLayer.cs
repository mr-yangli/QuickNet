namespace QuickSql.Metadata;

/// <summary>元数据来源层，优先级从高到低（§10.2）。</summary>
public enum MetadataLayer
{
    ProviderFallback = 0,
    Convention = 1,
    DataAccessAnnotation = 2,
    QuickSqlAttribute = 3,
    Fluent = 4,
}

/// <summary>长度的量纲。混用这两把尺子是国产化联调第一天炸中文列的根因（§10.4）。</summary>
public enum LengthSemantics
{
    /// <summary>n 表示字符数：PG 系的 <c>varchar(n)</c>、SQL Server 的 <c>nvarchar(n)</c>。</summary>
    Char = 0,

    /// <summary>n 表示字节数：Oracle/DM 的 <c>VARCHAR2(n)</c>、SQL Server 非 UTF-8 排序规则下的 <c>varchar(n)</c>。</summary>
    Byte = 1,
}

public sealed record OriginTrace(MetadataLayer Layer, string Note)
{
    public override string ToString() => $"{Layer}: {Note}";
}
