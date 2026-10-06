using System.Collections.Generic;

namespace FastSql.Generators;

/// <summary>一个实体的 G1 产出计划。<b>存的是语义值，不是代码文本</b>：转义与排版全在
/// <see cref="EntityFactsWriter"/>，判定全部在 <see cref="EntityFactsBuilder"/>，
/// 于是「§10.2 的规则写在哪一处」这个问题只有一个答案。</summary>
sealed class EntityModel
{
    public string Namespace = "";

    /// <summary>实体类型表达式，<c>global::FastSql.TestInfra.Order</c>。</summary>
    public string Entity = "";

    /// <summary>生成类名前缀：嵌套链折成一个标识符，保证同编译期内不撞名。</summary>
    public string Name = "";

    public string TableName = "";

    public string? TableSchema;

    public List<ColumnModel> Columns = [];

    public List<IndexModel> Indexes = [];

    public List<RelationshipModel> Relationships = [];

    /// <summary>分表声明，null = 这张表不分表。与 <see cref="TableName"/> 分开存：
    /// 名字是模板还是实名，两侧都由运行期的同一份校验判（<c>EntityContractFactory</c>），
    /// 生成期不重复那一遍。</summary>
    public SplitModel? Split;

    /// <summary>集群归属，存的是注解里那个<em>原样字符串</em>；null 只表示"没打 <c>[DataSource]</c>"。
    /// 空串与空白不在这里判、也不 <c>Trim()</c>：那一格由运行期的 <c>EntityContractFactory</c> 收口
    /// （两侧同一句 <c>DataSourceRouting.ValidateName</c>），与 <see cref="Split"/> 同一分工。</summary>
    public string? Source;
}

/// <summary><c>[SplitTable]</c> 的编译期抄本：档名（<see cref="Kind"/> 是
/// <c>FastSql.Metadata.SplitKind</c> 的成员名）、分片键成员名、桶数。</summary>
sealed class SplitModel
{
    public string Kind = "";

    public string Member = "";

    public int Buckets;
}

sealed class ColumnModel
{
    public string Member = "";

    /// <summary>属性声明类型（含 <c>Nullable&lt;T&gt;</c>），与反射侧的 <c>PropertyType</c> 同一个。</summary>
    public string ClrType = "";

    public string ColumnName = "";

    /// <summary><see cref="FastSql.Metadata.MetadataLayer"/> 的成员名。整条 origin 取这一层（§10.2）。</summary>
    public string ColumnNameLayer = "Convention";

    public string ColumnNameNote = "";

    public int? MaxChars;

    public string LengthNote = "";

    public bool IsNullable;

    public string NullableNote = "";

    public bool IsPrimary;

    /// <summary>null ⇒ 交给「整型主键 ⇒ 自增」约定，由运行期的 <c>EntityContractFactory</c> 判定。</summary>
    public bool? DeclaredIdentity;

    public int? Precision;

    public int? Scale;

    public string? StoreType;

    public string? Comment;

    /// <summary>并发列（<c>[ConcurrencyToken]</c>）。与其余各格同一口径：生成期只看得见注解，
    /// Fluent 那一层 G1 整体上就吃不到（<c>GeneratedEntitySchemaRegistry</c> 的类注释里写着这条）。</summary>
    public bool IsConcurrencyToken;

    public string? ConcurrencyNote;

    /// <summary>CLR 侧是不是 <c>string</c>：只有字符串列有长度尺子（§10.4）。</summary>
    public bool IsString;

    /// <summary>这一列的<em>直读臂</em>方法名（<c>DirectRead.ReadInt32</c> 那一批），null = 没有臂、进装箱口。
    /// 判据与编号都在运行期那一处（<c>ReadOps</c>/<c>DirectRead</c> 同一个文件），这里只挑名字：
    /// 名字写错是编译错误，挑错臂（比如给 <c>short</c> 列挑了 <c>ReadInt32</c>）则两侧编号对不上、
    /// 静默退回装箱口 —— <b>能错的方向是"没变快"，不是"读错值"</b>（§16.21）。</summary>
    public string? DirectArm;

    /// <summary>非空值类型装箱后不可能是 null ⇒ 必填检查是死代码，生成器不写。</summary>
    public bool CanBeNull;
}

sealed class IndexModel
{
    /// <summary>可空：留 null 时运行期按 <c>IX_实体_成员</c> 生成，与反射侧同一个条件。
    /// 自研 <c>[Index]</c> 的构造器要求给名字，所以实际上只有这一条路能到 null。</summary>
    public string? Name;

    public List<string> Members = [];

    public bool Unique;

    public bool Clustered;

    public bool Descending;
}

/// <summary>一条导航边的声明（to-one 或集合，按 <see cref="IsCollection"/> 分）。列名换算不在生成期做——
/// 那是运行期 <c>EntityContractFactory</c> 的活，与索引同一份实现（§10.3）。</summary>
sealed class RelationshipModel
{
    /// <summary>导航属性名，它自己不是列。</summary>
    public string Member = "";

    /// <summary>对面那一端的实体类型表达式。<c>IsCollection</c> 为真时那是<em>子端</em>。</summary>
    public string TargetType = "";

    /// <summary>外键成员名，复合（<c>[ForeignKey("A,B")]</c>）有多条。to-one 那一档是<em>本端</em>的成员名，
    /// 生成期就能对着列清单验；集合那一档是<em>子端</em>的，本端清单里没有，留空即"没声明、运行期再解"。</summary>
    public List<string> Members = [];

    /// <summary><see cref="FastSql.Metadata.MetadataLayer"/> 的成员名：这一格只有两档，
    /// DataAnnotations 的 <c>[ForeignKey]</c> 与「导航名 + Id」的约定。</summary>
    public string Layer = "Convention";

    public string Note = "";

    /// <summary>true = 这是一根集合导航（父端 → 子端），折进 <c>RelationshipFacts.IsCollection</c>。</summary>
    public bool IsCollection;

    /// <summary>级联开关，缺省开；<c>[NoCascade]</c>（打在实体或这根导航上）折成 <c>false</c>。</summary>
    public bool Cascade = true;
}
