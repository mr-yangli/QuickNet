namespace QuickSql.Metadata;

/// <summary>
/// 一个列的全部已解析事实。DDL 生成、参数绑定、写前校验三处只能读这里，
/// 不允许各自再去读一遍 attribute（§10.3，由架构测试卡住）。
/// </summary>
public sealed record ColumnContract
{
    public required string MemberName { get; init; }

    public required Type ClrType { get; init; }

    public required string ColumnName { get; init; }

    /// <summary>目标库里这个列的 n 是字符还是字节。由 provider 决定，不由注解决定。</summary>
    public required LengthSemantics LengthSemantics { get; init; }

    /// <summary>true = 定长/变长的 Unicode 列（nvarchar/nchar）。SQL Server 默认必须为 true，
    /// 否则 cp1252 排序规则下的 varchar 会把中文静默写成 <c>?</c>。</summary>
    public required bool Unicode { get; init; }

    public required bool IsNullable { get; init; }

    /// <summary>这一列的"非空"是不是<b>有人说过</b>的一句话。<c>false</c> 只有一种来源：可空上下文之外
    /// （<c>#nullable disable</c>、没带可空元数据的引用程序集）读回来的"没表过态"。
    /// 它只摘掉 §10.6 的必填尺子（<see cref="ContractRules"/>），<b>不改列的形状</b>：
    /// <see cref="IsNullable"/> 与 DDL 那一侧照旧，因为改那一格要的是现库的列，而不是一句推断。</summary>
    public bool NullabilityDeclared { get; init; } = true;

    public required bool IsPrimary { get; init; }

    public required bool IsIdentity { get; init; }

    /// <summary>并发列（§8.2 的乐观锁）：<b>只比较不书写</b> —— 不进 <c>SET</c>、不进列清单、不进脏判定，
    /// 但进 <c>WHERE</c> 与回填集。判据只有这一格，跟踪器与写入引擎都不再去读 attribute（§10.3）。</summary>
    public bool IsConcurrencyToken { get; init; }

    /// <summary>provider 说"这一型由库自己给值、且永不为空"（<b>输入</b>，不是结论）。今天只有 SQL Server 的
    /// <c>rowversion</c> 一型，由本家的 <c>ITypeMap.ApplyStorageDefaults</c> 落 —— 哪一型算"库自动递增"是
    /// 存储类型的事实，共用层不去猜，而猜得到那一半的（自增列）已经另有 <see cref="IsIdentity"/>。</summary>
    /// <remarks>刻意与 <see cref="ValueFromStore"/> 分成两格：后者是"自增 ∪ 这一格"的结论，而结论只有一个读者
    /// （<see cref="NullableInStore"/>），两格合并就会让 provider 侧的输入去替约定层下判断。</remarks>
    public bool StoreFillsValue { get; init; }

    /// <summary>「值归库给」那一档 —— 与自增列同族的第三档：这一侧不给写，<b>而库里那一格永远有值</b>。
    /// 两个来源：约定层判出的 <see cref="IsIdentity"/>，与 provider 落下的 <see cref="StoreFillsValue"/>。</summary>
    /// <remarks><b>它与 <see cref="IsConcurrencyToken"/> 不是一回事</b>：后者说"这一侧不写它"，这一格说
    /// "库里它必不为空"。两型在"可空的并发列而库里没有任何东西给它值"（<c>CheckedAt</c> 那一型）上分家：
    /// 那一列不进列清单，可 DDL 写 <c>NOT NULL</c> 就是让每一句不带它的 INSERT 撞库。
    /// 唯一的读者是 <see cref="NullableInStore"/>（DDL 那一侧），写侧的"进不进列清单"照旧读
    /// <see cref="IsConcurrencyToken"/>，两者今天覆盖同一批列而判据不同，由
    /// <c>ConcurrencyTokenTests</c> 那条对拍钉住不许悄悄分岔到第三处。</remarks>
    public bool ValueFromStore => IsIdentity || StoreFillsValue;

    /// <summary>字符上限。故意用 <c>required</c>：漏算一尺就是事故，编译器比约定可靠。</summary>
    public required int? MaxChars { get; init; }

    /// <summary>字节上限。非 Unicode 列的 UTF-16→UTF-8 最坏情况按 3 倍算，实测值优先。</summary>
    public required int? MaxBytes { get; init; }

    public required int? Precision { get; init; }

    public required int? Scale { get; init; }

    /// <summary>provider 覆写后的存储类型名（<c>VARCHAR2(50 CHAR)</c> / <c>nvarchar(50)</c>）。</summary>
    public string? StoreTypeName { get; init; }

    /// <summary>列备注，来自 <c>[Column(Comment=…)]</c>、Fluent 的 <c>HasComment</c> 或 <c>[Display]</c> 那一条链。
    /// <b>null 与空串不是一回事</b>（同 <see cref="TableInfo.Comment"/>）：结构同步那一头按这一格决定要不要重发
    /// <c>COMMENT ON COLUMN</c>。G1 开着 <c>QuickSqlXmlDocComments</c> 时链尾多一颗源码注释兜底。</summary>
    public string? Comment { get; init; }

    public required OriginTrace Origin { get; init; }

    /// <summary>写前校验用的字符数尺子（§10.6 的 ContractOnly 集合之一）。</summary>
    public bool FitsChars(string value) => MaxChars is null || value.Length <= MaxChars;

    /// <summary>UTF-8 字节数尺子。代理对按 4 字节、汉字按 3 字节，与列的排序规则一致时才可用。</summary>
    public bool FitsBytes(string value) => MaxBytes is null || System.Text.Encoding.UTF8.GetByteCount(value) <= MaxBytes;

    /// <summary>DDL 里这一列写 <c>NULL</c> 还是 <c>NOT NULL</c>。<b>不等于 <see cref="IsNullable"/></b>：
    /// 值归库给的那一列在 CLR 侧只能声明成可空（§10.6 的必填规则量的是整只实体，而新增时那一格还没有值），
    /// 库里它却永不为空 —— 把 CLR 那份可空性原样抄进 DDL，得到一个"这一列可以由人写空值"的假承诺（§14 那一格）。
    /// 五族 <c>EmitColumnDefinition</c> 都只读这一格，所以"改一处注解就让两条 DDL 对上"那种修法无处遁形。</summary>
    public bool NullableInStore => IsNullable && !ValueFromStore;
}
