using QuickSql.Metadata;
using QuickSql.Sql;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.TDengine;

/// <summary>TDengine 的 SQL 文本层。分页落在句尾且两子句独立可缺省（LIMIT n OFFSET m，
/// VendorDoc·TDengine：SELECT 语法页）；恒假/恒真保持基类的 1 = 0/1 = 1——本家布尔字面量在
/// SELECT 骨架里认不认没有读数，算术谓词是哪一家都收的那一形；布尔列的判定等式落 = true/= false
///（BOOL 是本家原生型，谓词与列型同边）；UPDATE/DELETE 整格拒发——3.x 没有传统 UPDATE 语句
///（VendorDoc·TDengine：变更只有 INSERT 覆盖与 ALTER 那两族），DELETE 的官方形状只认
/// "首列时间窗收束成一个确定闭区间"的条件（VendorDoc·TDengine：DELETE 页），时间窗分析这一格
/// 本层没有，宁可当场红也不发一条猜出来的条件；SupportsJoinedWrites 跟着写格落 false。
/// EmitReturning 转交 TDengineKeys.EmitReturning（那里抛）。</summary>
public sealed class TDengineDialect(TDengineTypes types, TDengineKeys keys, TDengineValues values, TDengineExpressions expressions)
    : DialectBase(IdentifierPolicy.TDengine, values, expressions)
{
    public override DbKind Kind { get; } = DbKind.TDengine;

    /// <summary>问号，且是唯一的一支。这是本文件里最贵的一格：前缀错了在别家红在"名字对不上"，
    /// 在这一家红在值绑定到隔壁列（位置形没有第二道校验）。驱动侧的另一半在 binder：stmt2 按
    /// ParameterName 前缀给值分类，binder 用 DriverName 把 '?p0' 翻成 '@p0'——文本、编号、日志
    /// 都还是位置形那一套，只有跨进驱动对象那一步换名。</summary>
    public override char ParameterPrefix => '?';

    public override string GetStoreType(ColumnContract column) => types.GetStoreType(column);

    /// <summary>合并档开关跟着写格落 false：UPDATE/DELETE 两整格都抛，读侧 EntityTracker 走"一句一实体"那一支；
    /// 合并那一档在这颗开关 false 时走不到默认出口，所以 EmitJoinedSources 不覆写。
    /// 恒假/恒真也不覆写：基类默认 1 = 0/1 = 1——布尔字面量 true/false 在本家 SELECT 骨架里
    /// 收不收没有读数，算术谓词是哪一家都收的那一形，"没有"要说成"没有"。</summary>
    public override bool SupportsJoinedWrites => false;

    /// <summary>布尔是本家的原生型，与列那一侧 BOOL 同边：判定等式落 = true/= false。
    /// VendorDoc·TDengine：BOOL 型页与 SELECT 示例里布尔列与 true/false 直接比等。</summary>
    protected override void EmitBooleanEquality(SqlWriter writer, bool value)
        => writer.Append(value ? " = true" : " = false");

    /// <summary>本家没有句首限定词那一档：LIMIT/OFFSET 都在句尾（VendorDoc·TDengine：
    /// SELECT 语法页 LIMIT [n OFFSET m]）。两子句独立可缺省。</summary>
    protected override void EmitPaging(SqlWriter writer, SelectStatement select)
    {
        if (select.Limit is { } limit)
        {
            writer.Append(" LIMIT ");

            EmitExpression(limit, writer);
        }

        if (select.Offset is { } offset)
        {
            writer.Append(" OFFSET ");

            EmitExpression(offset, writer);
        }
    }

    /// <summary>登记参数而不落地它的名字，只落一颗 ?——位置形与 GBase 8s 同一形状。顺序纪律：
    /// 先登记、后落文本——文本序 = 编号序 = 绑定序，这条耦合在本家更硬：stmt2 按
    /// ParameterCollection 的顺序 BindRow，没有第二道名字校验可兜。</summary>
    protected override void EmitParameter(ParameterValue parameter, SqlWriter writer)
    {
        writer.RegisterParameter(Values.CreateParameter(writer.NextParameterName(), parameter.Value, parameter.Hint));

        writer.Append('?');
    }

    /// <summary>转交 TDengineKeys.EmitReturning（那里抛）。与 GBase 8s 同一立场：宁可当场红，
    /// 也不发一条"看着像在回填"的语句。</summary>
    protected override void EmitReturning(SqlWriter writer, IReadOnlyList<ColumnContract> returning) => keys.EmitReturning(writer, returning);

    /// <summary>变异档：3.x 没有传统 UPDATE 语句（VendorDoc·TDengine：数据变更只有
    /// INSERT 的 upsert 覆盖与 ALTER 那两族）。同 ts 重写覆盖旧行确实像天然 upsert，
    /// 但"重复时间戳的确切语义（整行覆盖还是部分列）"要活库钉死才敢接进 InsertOrUpdate，
    /// 重开面记 notes——这一格今天整格抛。</summary>
    public override void Emit(in UpdateStatement update, SqlWriter writer)
        => throw new QuickSqlException(
            "TDengine 没有 UPDATE 语句：3.x 的数据变更只有 INSERT 的 upsert 覆盖与 ALTER 那两族"
            + "【文档·TDengine】，本仓库今天不发变异语句，所以这里整格抛。"
            + "InsertOrUpdate 那一格也没有接：INSERT 同 ts 覆盖的语义要活库钉死才敢翻。");

    /// <summary>同上一格：DELETE 的官方形状只认"首列时间窗收束成一个确定闭区间"的条件
    ///（VendorDoc·TDengine：DELETE 页——时间窗条件是硬性的，普通列条件只能跟着时间窗走）。
    /// 时间窗分析这一格本层没有：任意 WHERE 的 DELETE 大概率被服务端拒回，
    /// 宁可当场红也不发一条猜出来的条件。</summary>
    public override void Emit(in DeleteStatement delete, SqlWriter writer)
        => throw new QuickSqlException(
            "TDengine 的 DELETE 只认首列时间窗条件（WHERE 必须把主键时间戳收束成一个确定闭区间，"
            + "https://docs.tdengine.com/tdengine-sql/08-delete/），本层不建模时间窗分析，"
            + "任意条件的 DELETE 发出去大概率被服务端拒回——这里整格抛。");
}
