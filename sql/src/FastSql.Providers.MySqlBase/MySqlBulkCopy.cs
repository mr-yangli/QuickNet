using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using FastSql.Metadata;
using FastSql.Providers;

namespace FastSql.Providers.MySqlBase;

/// <summary>§12.1 的 L1「批量装载通道」在 MySQL 血统那一家的落地：<c>MySqlBulkCopy</c>
/// （驱动把它包成 <c>LOAD DATA LOCAL INFILE</c> 的流式上传）。
///
/// <para><b>【2026-10-07 改判 —— 原文照抄在前，改判在后】</b>本类上一版抬头写的是"<b>成员形状不是猜的</b>：
/// 构造子 <c>(MySqlConnection)</c>、<c>ColumnMappings.Add(MySqlBulkCopyColumnMapping)</c>、
/// <c>new MySqlBulkCopyColumnMapping(int, string)</c>、<c>WriteToServerAsync(DataTable)</c> 那几颗，全部在
/// 对照量具（m1∶m2 两趟，MySqlConnector 2.6.2，MySQL 8.4.8）里以强类型写法跑通过"。那句话把<em>强类型
/// 调用面</em>当成了<em>反射面</em>，而两处不是一式。四颗读数（同一支驱动的运行时反射面）：
/// ① <c>MySqlBulkCopy</c> 只有<em>一颗</em>公开构造子 <c>(MySqlConnection, MySqlTransaction? = null)</c> ——
/// 量具里 <c>new MySqlBulkCopy(conn)</c> 能编译是因为第二位可省，而 <c>GetConstructors()</c> 交回的是两位的
/// 完整签名，于是按"一位参数"点形状的 <c>ConnectionScopedCtor</c> 在这一家永远点空，整家静默退 L3；
/// ② <c>MySqlBulkCopyColumnMapping</c> 只有 <c>(int, string, string? expression = null)</c>，而
/// <c>Activator.CreateInstance(type, 两个实参)</c> 的绑定器<em>不认默认值</em>，当场 <c>MissingMethodException</c>；
/// ③ <c>WriteToServerAsync(DataTable, CancellationToken)</c> 交回的是 <c>ValueTask&lt;MySqlBulkCopyResult&gt;</c>
/// 而不是 <c>Task</c>，那句 <c>(Task)</c> 硬转会红在强转上；④ 这一族的装载类<em>没有任何 Dispose 面</em>
/// （零接口、无 <c>Dispose</c>/<c>DisposeAsync</c>），而旧写法收尾时硬 cast 成 <c>IDisposable</c>。
/// <b>旧句里"量具跑通过"那部分仍然成立</b>——它证的是强类型那条路；这一档要的是反射那条路，
/// 而两处形状的差只有把通道真接上去才看得见（本笔的活库跑就是那一次接上）。</para>
///
/// <para><b>按序号映射源列、按名字映射目标列</b>：那颗映射构造子是 <c>(int sourceOrdinal, string destinationColumn)</c>
/// 加一位本仓库永远交 <see langword="null"/> 的表达式位，与 T-SQL 那一家「源名 ↔ 目标名」的写法不同——
/// 这不是疏漏而是那一族的形状：源是我们自己按 <see cref="BulkCopyRequest.Columns"/> 顺序建的
/// <see cref="DataTable"/>（序号对齐在这里由构造保证），而目标列名才是跨表序漂移真正要防的那一格。
/// <c>DestinationTableName</c> 的语义与 T-SQL 那条同：填不带库名的表名，限定交给连接所在的库——
/// 分表时 <c>BulkCopyRequest.DestinationTable</c> 已由 <c>ISqlDialect.EmitTableName</c> 换算到片，
/// 两边仍是同一张表（§9.2 那句在这里的第四个读者）。</para>
///
/// <para><b>连接串那道闸门在这里点名，而不是让装载在半路炸</b>：<c>LOAD DATA LOCAL</c> 要
/// <c>AllowLoadLocalInfile=true</c> 放行，缺它时驱动在执行当场抛 NotSupportedException【实测·对照量具
/// 补这颗参数前的第一趟，两条批量路径同红】。<see cref="UnavailableBecauseOf"/> 把那句话提到选层之前：
/// Auto 因此落在 L3，而不是替调用方付一次半途而废的往返。<b>落在 L3 不等于留降级痕</b>：Auto 没承诺过
/// 任何一层，缺这颗参数时它交回 Method=MultiValue、Degraded=false，而这句话原样进 <c>BulkReport.Reason</c>
/// 【实测·真库 8.4.8 那一台原始串，集成册 <c>BulkPathTests.The_local_infile_gate_…</c>】—— 那句话因此是
/// Auto 那条路上唯一的可见面，写成"没有通道"会把人支到 provider 那一侧，而该查的是那一串配置。
/// 服务端那颗 <c>local_infile</c>
/// 开关（8.4.8 实测为 ON）不在客户端可见面里，判不了就不装判——那条红线由写当场抛驱动自己的话接住。</para>
///
/// <para><b>事务那一格今天有了入参面</b>：旧句写的是"装载类的构造子只收连接，不像 T-SQL 那一家给一颗外部
/// 事务参数，所以 <c>BulkConnection.Transaction</c> 在 L1 上交不出去"——那一读同样来自强类型面
/// （<c>new MySqlBulkCopy(conn)</c> 那一形看不见第二位）。改判段第①颗读数里那颗两位构造子的第二位
/// 正是 <c>MySqlTransaction</c>，所以这一格今天<em>交得出去</em>；交进去之后也带得动——活事务里写进去的四行
/// 随回滚一并没有【实测·真库 8.4.8，集成册 <c>BulkPathTests.The_copy_channel_rides_the_transaction_and_rolls_back_with_it</c>：
/// 提交那一半读得到、回滚那一半读不到，两半都是同一颗通道】。这一格因此不再是未实测的边界。
sealed class MySqlBulkCopyChannel : IBulkCopyChannel
{
    readonly ConstructorInfo _bulkCtor;
    readonly ConstructorInfo _mappingCtor;
    readonly PropertyInfo _destinationTable;
    readonly PropertyInfo _timeout;
    readonly PropertyInfo _mappings;
    readonly PropertyInfo _rowsCopied;
    readonly MethodInfo _addMapping;
    readonly MethodInfo _write;

    /// <summary>把 <c>ValueTask</c> 交回成 <c>Task</c> 的那一颗；驱动直接返 <c>Task</c> 时为 null（改判段第③颗）。</summary>
    readonly MethodInfo? _asTask;

    MySqlBulkCopyChannel(ConstructorInfo bulkCtor, ConstructorInfo mappingCtor, PropertyInfo destinationTable,
        PropertyInfo timeout, PropertyInfo mappings, PropertyInfo rowsCopied, MethodInfo addMapping,
        MethodInfo write, MethodInfo? asTask)
    {
        _bulkCtor = bulkCtor;
        _mappingCtor = mappingCtor;
        _destinationTable = destinationTable;
        _timeout = timeout;
        _mappings = mappings;
        _rowsCopied = rowsCopied;
        _addMapping = addMapping;
        _write = write;
        _asTask = asTask;
    }

    public bool SupportsColumn(ColumnContract column) => NetType(column) is not null;

    /// <summary>这条串过不去的那句话（形状与 T-SQL 那一家交回 <see langword="null"/> 相反——这一家真实欠一颗参数）。
    /// 键名认两颗：<c>AllowLoadLocalInfile</c>（MySqlConnector 正主）与 <c>UseLocalInfile</c>（Oracle 官方驱动的旧名），
    /// 值认驱动文档的那四种布尔写法（true/1/yes/on，不区分大小写）。句子只报缺哪颗参数，不回显串本身。</summary>
    public string? UnavailableBecauseOf(string connectionString)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };

        if (Read(builder, "AllowLoadLocalInfile") || Read(builder, "UseLocalInfile"))
            return null;

        return "这条连接串没有放行本地装载（AllowLoadLocalInfile=true）：MySqlBulkCopy 走的是 LOAD DATA LOCAL，缺这颗参数驱动会在执行当场拒";

        static bool Read(DbConnectionStringBuilder builder, string key)
        {
            if (!builder.TryGetValue(key, out var value))
                return false;

            if (value is not string text)
                return value is true;

            return bool.TryParse(text, out var parsed)
                ? parsed
                : text.Equals("1", StringComparison.OrdinalIgnoreCase)
                  || text.Equals("yes", StringComparison.OrdinalIgnoreCase)
                  || text.Equals("on", StringComparison.OrdinalIgnoreCase);
        }
    }

    [RequiresUnreferencedCode("本对象的 MemberInfo 全部来自按类型名的反射查找（TryResolve 那一句），裁剪后的驱动走不到这里。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：反射调用装载类需要运行时生成代码。")]
    public async Task<int> WriteAsync(BulkConnection target, BulkCopyRequest request, CancellationToken ct)
    {
        // 反射调用不认 C# 可省参数的默认值：实参数组要铺满签名。多出来那一位是外部事务，
        // BulkConnection.Transaction 为 null 时交 null 恰好就是"没有外部事务"那一格。
        var arguments = new object?[_bulkCtor.GetParameters().Length];

        arguments[0] = target.Connection;

        if (arguments.Length > 1) arguments[1] = target.Transaction;

        var bulk = _bulkCtor.Invoke(arguments);

        _destinationTable.SetValue(bulk, request.DestinationTable);
        _timeout.SetValue(bulk, target.TimeoutSeconds);

        var mappings = _mappings.GetValue(bulk)!;

        for (var column = 0; column < request.Columns.Count; column++)
        {
            // 映射类的第三位是驱动给自己那档"由表达式算列值"留的；本仓库永远交值而不是交表达式 ⇒ null。
            var cells = new object?[_mappingCtor.GetParameters().Length];

            cells[0] = column;
            cells[1] = request.Columns[column].ColumnName;

            _addMapping.Invoke(mappings, [_mappingCtor.Invoke(cells)!]);
        }

        var outcome = _write.Invoke(bulk, [Table(request), ct])!;

        // ValueTask 只能在生成它的那一帧上等，而这里恰好就是那一帧；AsTask() 交出的那条 Task 与
        // T-SQL 那一家的直返 Task 走同一个 await 形状（驱动哪天改回返 Task，这一句自己走 null 分支）。
        await (outcome as Task ?? (Task) _asTask!.Invoke(outcome, null)!);

        // RowsCopied 是这一族唯一的"进了几行"，给不出数的形状在 TryResolve 那里已经整家退掉了。
        return checked((int) Convert.ToInt64(_rowsCopied.GetValue(bulk), CultureInfo.InvariantCulture));

        // 这里没有收尾的 finally：这一族的装载类没有 Dispose 面（改判段第④颗读数），
        // 而连接与事务的生命周期在内核那一条 using 上（AdoExecutor），不在通道手里。
    }

    /// <summary>这一列在装载通道里的 CLR 形状——白名单与取型是同一次判断，判法与理由同
    /// <c>SqlServerBulkCopy.NetType</c> 的抬头（缺席的 <c>char</c>/<c>DateOnly</c>/<c>TimeOnly</c>/无符号/
    /// <c>object</c> 五类一颗都不新增：那五颗的缺席与驱动无关，是 <see cref="DataTable"/> 类型面的形状）。
    /// 枚举按底层整数判、可空剥壳，同一把尺。</summary>
    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
    static Type? NetType(ColumnContract column) => Flatten(column.ClrType) switch
    {
        var t when t == typeof(bool) => typeof(bool),
        var t when t == typeof(byte) => typeof(byte),
        var t when t == typeof(short) => typeof(short),
        var t when t == typeof(int) => typeof(int),
        var t when t == typeof(long) => typeof(long),
        var t when t == typeof(float) => typeof(float),
        var t when t == typeof(double) => typeof(double),
        var t when t == typeof(decimal) => typeof(decimal),
        var t when t == typeof(string) => typeof(string),
        var t when t == typeof(byte[]) => typeof(byte[]),
        var t when t == typeof(Guid) => typeof(Guid),
        var t when t == typeof(DateTime) => typeof(DateTime),
        var t when t == typeof(DateTimeOffset) => typeof(DateTimeOffset),
        var t when t == typeof(TimeSpan) => typeof(TimeSpan),
        _ => null,
    };

    static Type Flatten(Type clrType)
    {
        var member = Nullable.GetUnderlyingType(clrType) ?? clrType;

        return member.IsEnum ? Enum.GetUnderlyingType(member) : member;
    }

    static DataTable Table(BulkCopyRequest request)
    {
        var table = new DataTable("FastSqlBulk");

        foreach (var column in request.Columns)
            table.Columns.Add(column.ColumnName, NetType(column)!);

        foreach (var row in request.Rows)
            table.Rows.Add(row);

        return table;
    }

    /// <summary>把这一家的装载类<em>整套</em>验一遍，缺任何一件交回 <see langword="null"/>（阶梯自然从 L3 起，
    /// 与"这一家没有通道"同形）。<b>两颗构造子按反射面点死，而不是按 C# 的调用面</b>：必填那几位的类型
    /// 要对，多出来的一位必须是可省参数（<c>IsOptional</c>）——写死"只收连接的一位"在这一家永远点空，
    /// 而那个失败形状与"这一家根本没有装载类"完全同形（抬头改判段第①颗）。按形状而不是写死类型名，
    /// 与 T-SQL 那家 <c>ConnectionScopedCtor</c> 同一手法：通道永远来自工厂所在的那一家。</summary>
    [RequiresUnreferencedCode("按类型名在驱动程序集里找装载类与它的成员（TryResolve 本体），被裁剪的驱动会交回 null。")]
    [RequiresDynamicCode("反射调用装载类需要运行时生成代码。")]
    internal static MySqlBulkCopyChannel? TryResolve(Assembly driver)
    {
        var type = driver.GetType("MySqlConnector.MySqlBulkCopy", throwOnError: false);
        var mapping = driver.GetType("MySqlConnector.MySqlBulkCopyColumnMapping", throwOnError: false);

        if (type is null || mapping is null)
            return null;

        var ctor = ConnectionScopedCtor(type);
        var mappingCtor = OrdinalColumnCtor(mapping);
        var destinationTable = type.GetProperty("DestinationTableName");
        var timeout = type.GetProperty("BulkCopyTimeout");
        var mappings = type.GetProperty("ColumnMappings");
        var rowsCopied = type.GetProperty("RowsCopied");
        var write = type.GetMethod("WriteToServerAsync", [typeof(DataTable), typeof(CancellationToken)]);

        if (ctor is null || mappingCtor is null || destinationTable is null || timeout is null
            || mappings is null || rowsCopied is null || write is null)
            return null;

        // 那颗带取消的出口在这一族交回的是 ValueTask 而不是 Task：要么直接可等，要么给得出 AsTask()。
        // 两者都给不出就整家退 L3——"等不到回包"比"少一次收尾"严重得多，因为进了几行从此没有证人。
        MethodInfo? asTask = null;

        if (write.ReturnType != typeof(Task))
        {
            asTask = write.ReturnType.GetMethod("AsTask", Type.EmptyTypes);

            if (asTask is null || !typeof(Task).IsAssignableFrom(asTask.ReturnType))
                return null;
        }

        // 那颗 Add 收的是映射对象本身（ColumnMappings 是 List<映射类>），不是 T-SQL 那一家收两个字符串的写法。
        var add = mappings.PropertyType.GetMethod("Add", [mapping]);

        return add is null ? null : new MySqlBulkCopyChannel(ctor, mappingCtor, destinationTable, timeout, mappings, rowsCopied, add, write, asTask);

        // 「自家连接 + 可选的外部事务」。第一位收具体类型对我们只会更严：只有本家的真连接进得去，
        // 而那正是"通道跟着工厂走"要保证的那件事。第二位存在时必须可省（调用面交 null），且类型为事务的子类。
        static ConstructorInfo? ConnectionScopedCtor(Type bulkType)
        {
            foreach (var candidate in bulkType.GetConstructors())
            {
                var parameters = candidate.GetParameters();

                if (parameters.Length is not (1 or 2)) continue;

                if (parameters[0].ParameterType.IsSubclassOf(typeof(DbConnection))
                    && (parameters.Length == 1
                        || parameters[1].IsOptional && parameters[1].ParameterType.IsSubclassOf(typeof(DbTransaction))))
                    return candidate;
            }

            return null;
        }

        // 「序号 + 目标列名（+ 可选的表达式位）」；表达式位存在时交 null，判据与上面那颗同一把尺。
        static ConstructorInfo? OrdinalColumnCtor(Type mappingType)
        {
            foreach (var candidate in mappingType.GetConstructors())
            {
                var parameters = candidate.GetParameters();

                if (parameters.Length is not (2 or 3)) continue;

                if (parameters[0].ParameterType == typeof(int) && parameters[1].ParameterType == typeof(string)
                    && (parameters.Length == 2
                        || parameters[2].IsOptional && parameters[2].ParameterType == typeof(string)))
                    return candidate;
            }

            return null;
        }
    }
}
