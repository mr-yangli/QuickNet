using System.Data.Common;
using QuickSql.Providers.ClickHouse;

namespace QuickSql.TestInfra;

/// <summary>ClickHouse 这一系的组装根，与 <see cref="SqlServer"/>、<see cref="Xugu"/>、<see cref="Gbase8s"/>、
/// <see cref="Sqlite"/>、<see cref="DuckDb"/> 同形：<b>provider 由调用方直接构造并交进来</b>（§12.7 F），
/// 内核不做 <c>Type.GetType(string)</c>。
///
/// <para>它是单层 provider 的第二颗【文档口径行】（继 DuckDb 而来），存在的理由与 <see cref="DuckDb"/>
/// 那一份同一条：本机没有任何一台引擎实例，<c>ClickHouse.Client</c> 驱动也从未被解析成功过
/// （§13.5 第十七本台账整行「未实测」），所以这里<em>没有</em> <see cref="SqlServer.Driver"/>、
/// <see cref="Sqlite.Driver"/> 的对应物 —— 有了它就会被误读成"这条解析路径验过"，而本家连
/// "驱动装上能开一条 TCP"都还没有第一枚读数。它与 <see cref="DuckDb"/> 那一档同案：
/// 只有离线册，离线册拿 <see cref="SqlServer.NoDriverFactory"/>，碰驱动就是 §3 那条约束的红。</para>
///
/// <para>连接串占位符复用 <see cref="SqlServer.OfflineConnectionString"/>：这条串永远不被任何驱动解析
///（<see cref="SqlServer.NoDriverFactory"/> 让碰它的第一行代码就变红）。本家的"建库"整格不可走
///（provider 侧 <c>CanCreateDatabase=false</c> —— 引擎型库没有"锚点库"那一档，与 DuckDb 同案不同因），
/// 连那颗占位串要不要带库名这一问，本家还欠个实现方给答案的重开条件（§14）。</para></summary>
public sealed class ClickHouse
{
    public static ClickHouseProvider Provider(DbProviderFactory factory) => new(factory);

    /// <summary>离线组装根：<paramref name="factory"/> 为 null 时用 <see cref="SqlServer.NoDriverFactory"/>，
    /// 于是"只生成 SQL"这一条约束在第十家上与前面九家享有同一个探针。</summary>
    public static QuickSqlOrm Orm(DbProviderFactory? factory = null, string? connectionString = null)
        => new(new QuickSqlOrmOptions
        {
            Provider = Provider(factory ?? SqlServer.NoDriverFactory.Instance),
            ConnectionString = connectionString ?? SqlServer.OfflineConnectionString,
        });
}
