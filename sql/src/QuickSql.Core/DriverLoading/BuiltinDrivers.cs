namespace QuickSql.DriverLoading;

/// <summary>内建的驱动默认表：<b>清单里没有那一行时的退路</b>，形状与内容照抄仓库根那份
/// <c>QuickSqlDrivers.json</c>（每颗 key 与 <see cref="DbKind"/> 的 <c>ToString().ToLowerInvariant()</c> 逐字相同，
/// 所以这里不需要映射表）。有它才有 FreeSql 那一档现场姿势 —— <c>Type.GetType("FreeSql.MySql.MySqlProvider`1,FreeSql.Provider.MySql")</c>
/// 是它核心里写死的一张按名字的表，而不是配置文件；本库的 provider 侧照 §1.7 走编译期登记，
/// 驱动侧本来就在运行期按程序集名装载（<see cref="DriverRegistry"/> 那条 <c>Assembly.Load</c>），
/// 所以这张表只是把同一件事的<em>名字来源</em>从磁盘挪进代码，原则 3（provider 程序集编译期不引用任何驱动）一格没动。
///
/// <para>清单仍然是第一优先：信创现场要改程序集名、要写 <c>path</c> 指向 <c>drivers/</c> 里那颗 DLL，
/// 改的还是 <c>QuickSqlDrivers.json</c>，不用重编译。这张表只负责"什么都没配"那一档。</para></summary>
public static class BuiltinDrivers
{
    static readonly DriverRef[] _refs =
    [
        new("sqlserver", "Microsoft.Data.SqlClient", null, "Microsoft.Data.SqlClient.SqlClientFactory"),
        new("postgresql", "Npgsql", null, null),
        new("kingbase", "Kdbndp", null, "Kdbndp.KdbndpFactory"),
        new("opengauss", "Npgsql", null, null),
        new("gaussdb", "Npgsql", null, null),
        new("highgo", "Npgsql", null, null),
        new("gbase8c", "Npgsql", null, null),
        new("oracle", "Oracle.ManagedDataAccess", null, null),
        new("dameng", "DmProvider", null, null),
        new("shentong", "Oscar", null, null),
        new("mysql", "MySqlConnector", null, null),
        new("tidb", "MySqlConnector", null, null),
        new("oceanbase", "MySqlConnector", null, null),
        new("xugu", "XuguClient", null, null),
        new("gbase8s", "GBase.DataDB", null, null),
        new("sqlite", "Microsoft.Data.Sqlite", null, "Microsoft.Data.Sqlite.SqliteFactory"),
    ];

    public static IReadOnlyList<DriverRef> Drivers => _refs;

    public static DriverRef? Find(string key)
    {
        foreach (var reference in _refs)
            if (string.Equals(reference.Key, key, StringComparison.OrdinalIgnoreCase))
                return reference;

        return null;
    }
}
