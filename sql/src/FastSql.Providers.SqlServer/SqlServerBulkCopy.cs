using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using FastSql.Metadata;

namespace FastSql.Providers.SqlServer;

/// <summary>§12.1 的 L1「批量装载通道」在 T-SQL 那一家的落地：<c>SqlBulkCopy</c>。
///
/// <para><b>为什么是反射而不是直接 <c>new</c></b>：装载类不在 <see cref="DbProviderFactory"/> 的抽象面上，
/// 而本仓库的编译期驱动引用纪律（§1.2 原则 3）不许 provider 程序集认识 <c>Microsoft.Data.SqlClient</c>。
/// 类型从<em>注入的那只工厂所在的程序集</em>里按名字取 —— 与 <c>DriverRegistry</c> 解析工厂同一手法，
/// 于是通道永远来自连接所属的那一家（现场同时装了 M.D.SqlClient 与 S.D.SqlClient 时，
/// 跟着工厂走是<em>唯一</em>可能对的那个答案）。</para>
///
/// <para><b>数据载体是 <see cref="DataTable"/>，不是自己写的 <see cref="IDataReader"/> 包装</b>：
/// <c>WriteToServer(IDataReader)</c> 那条重载在文档上是并列的，但驱动内部读 reader 的方式
/// （走 <c>GetSchemaTable</c> 还是逐列 <c>GetFieldType</c>）不是公开契约，而 <c>DataTable</c> 那一条是
/// 这一族 API 二十年来的稳定面。选可证的那条：本文件里所有跨反射边界的东西都能靠一次真库跑验掉，
/// 而自写 reader 要验的是驱动的私有行为。</para>
///
/// <para><b>递交的两份代价</b>，都记在 <c>Describe</c> 与 <see cref="BulkReport.Reason"/> 里而不是藏起来：
/// ① 每格多一次 <see cref="AdoParameter"/> 装箱（<c>InsertEngine.CopyRequest</c> 过值门那一扇，
/// 为的是"类型归一只长在一个门上"）；② 整批一份 <see cref="DataTable"/>，行 × 列的 <c>object[]</c> 再抄一遍。
/// ②是这一档的入场券而不是疏漏：<c>SqlBulkCopy</c> 的<em>快</em>在往返与 TDS 流式写入上，不在客户端形状上。</para></summary>
sealed class SqlServerBulkCopy : IBulkCopyChannel
{
    readonly object _options;
    readonly ConstructorInfo _ctor;
    readonly PropertyInfo _destination;
    readonly PropertyInfo _timeout;
    readonly PropertyInfo _mappings;
    readonly PropertyInfo _rowsCopied;
    readonly MethodInfo _addMapping;
    readonly MethodInfo _write;

    SqlServerBulkCopy(object options, ConstructorInfo ctor, PropertyInfo destination, PropertyInfo timeout,
        PropertyInfo mappings, MethodInfo addMapping, PropertyInfo rowsCopied, MethodInfo write)
    {
        _options = options;
        _ctor = ctor;
        _destination = destination;
        _timeout = timeout;
        _mappings = mappings;
        _addMapping = addMapping;
        _rowsCopied = rowsCopied;
        _write = write;
    }

    public bool SupportsColumn(ColumnContract column) => NetType(column) is not null;

    [RequiresUnreferencedCode("本对象的 MemberInfo 全部来自按类型名的反射查找（TryResolve 那一句），裁剪后的驱动走不到这里。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：反射调用装载类需要运行时生成代码。")]
    public async Task<int> WriteAsync(BulkConnection target, BulkCopyRequest request, CancellationToken ct)
    {
        // 构造子只认这三件：连接、开关、外部事务。事务交 null 也是对的形状（那一批归连接自己的隐式事务）。
        var bulk = _ctor.Invoke([target.Connection, _options, target.Transaction]);

        _destination.SetValue(bulk, request.DestinationTable);
        _timeout.SetValue(bulk, target.TimeoutSeconds);

        // 按名字映射，一次都不许省：不给 ColumnMappings 时 SqlBulkCopy 按【序号】对齐，
        // 而我们递交的是列清单那一个子集（自增列与并发列不在里面），序号对齐会把第 3 个值写进第 3 列。
        // 那不是"慢一点"，是把整批写歪 —— 而且歪得很绿。
        var mappings = _mappings.GetValue(bulk)!;

        for (var column = 0; column < request.Columns.Count; column++)
        {
            var name = request.Columns[column].ColumnName;

            // 源名与目标名同一个串：源那一侧就是 DataTable 的列名，而它取自 ColumnContract.ColumnName，
            // 与文本层用的是同一颗名字（§9.2「建出来的表与查的表必须是同一张」在这里的第四个读者）。
            _addMapping.Invoke(mappings, [name, name]);
        }

        try
        {
            await (Task)_write.Invoke(bulk, [Table(request), ct])!;

            // RowsCopied 是这一族唯一的"进了几行"。它不存在时 TryResolve 已经交回 null（整家没有通道），
            // 所以走到这里就一定拿得出数 —— 而拿不出数的那一种宁可没有通道，也不报一个看着对的数。
            return checked((int)Convert.ToInt64(_rowsCopied.GetValue(bulk), CultureInfo.InvariantCulture));
        }
        finally
        {
            ((IDisposable)bulk).Dispose();
        }
    }

    /// <summary>这一列在装载通道里的 CLR 形状 —— <b>白名单与取型是同一次判断</b>：点得到名的才交回类型，
    /// 点不到的交回 <see langword="null"/>（= 整批退回 L3，见 <see cref="SupportsColumn"/>）。
    ///
    /// <para><b>缺席的三类各自有理由，都不是"忘了"</b>：<c>char</c>（值门给它 <c>DbType.String</c> 加
    /// <c>Size=1</c>，而 <c>DataTable</c> 那一侧是 <c>System.Char</c>，两边不是同一个说法）；
    /// <c>DateOnly</c> / <c>TimeOnly</c>（<c>DataTable</c> 的类型面里根本没有这两位，要进通道得先文本化成
    /// <c>DateTime</c>，那是替库改类型）；无符号那一族与 <c>object</c>（T-SQL 侧没有对应存储类型，
    /// 或压根不知道那一格要存什么）。</para>
    ///
    /// <para><b>枚举按底层整数判</b>：值门已经把它折成整数了（§9.3 那句"enum 存 INT"），
    /// 拿 <c>ClrType</c> 原样判会把每一条带枚举的批量都白退一次。</para>
    ///
    /// <para><b>这份名单为什么写成一颗颗 <c>typeof</c> 而不是一个数组</b>：<c>DataTable</c> 建列要按公共
    /// 字段/属性访问那个类型，而 <c>DataColumnCollection.Add</c> 的那一颗参数带
    /// <see cref="DynamicallyAccessedMembersAttribute"/> —— 分析器只认"来源是编译期看得见的 <c>typeof</c>"这一种证据，
    /// 数组里的元素在它眼里是无源的（IL2073 当场红，而这条红是<em>对的</em>：数组来源确实证不了裁剪安全）。
    /// 换成 switch 之后每个出口都是一颗 <c>typeof</c>，白名单因此同时是"裁剪器要保住的那几张类型"的清单。</para></summary>
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

    /// <summary>整批的载体。列类型取 <see cref="NetType"/>（值门产出的那个形状），
    /// 值已经是 <see cref="DBNull"/> 或实值 —— <c>DataTable</c> 允许 <see cref="DBNull"/> 进任何列，
    /// 而 NOT NULL 那一列上库里会自己红，这一侧不替它兜。</summary>
    static DataTable Table(BulkCopyRequest request)
    {
        var table = new DataTable("FastSqlBulk");

        foreach (var column in request.Columns)
            table.Columns.Add(column.ColumnName, NetType(column)!);

        foreach (var row in request.Rows)
            table.Rows.Add(row);

        return table;
    }

    /// <summary>把这一家的装载类<em>整套</em>验一遍，缺任何一件交回 <see langword="null"/>：
    /// 阶梯自然从 L3 起，与"这一家没有通道"同形。<b>宁缺毋滥的那一半是开关</b>：
    /// 少了 <c>CheckConstraints</c> 检查约束不验、少了 <c>FireTriggers</c> 插入触发器不跑，
    /// 两者都会让"批量"与"逐条"在库里不等价（§12.4 的等价性），而这一档的默认必须是等价的那一个。
    /// 数值一个都没写死：两个开关的位是从驱动的枚举上按名字读的。</summary>
    [RequiresUnreferencedCode("按类型名在驱动程序集里找装载类与它的成员（TryResolve 本体），被裁剪的驱动会交回 null。")]
    [RequiresDynamicCode("反射调用装载类需要运行时生成代码。")]
    internal static SqlServerBulkCopy? TryResolve(Assembly driver)
    {
        var type = driver.GetType("Microsoft.Data.SqlClient.SqlBulkCopy", throwOnError: false)
                ?? driver.GetType("System.Data.SqlClient.SqlBulkCopy", throwOnError: false);

        if (type is null || type.Namespace is not { Length: > 0 } ns)
            return null;

        var options = driver.GetType(ns + ".SqlBulkCopyOptions", throwOnError: false);

        if (options is null || Flags(options) is not { } flags)
            return null;

        var ctor = ConnectionScopedCtor(type, options);
        var destination = type.GetProperty("DestinationTableName");
        var timeout = type.GetProperty("BulkCopyTimeout");
        var mappings = type.GetProperty("ColumnMappings");
        var rowsCopied = type.GetProperty("RowsCopied");
        var write = type.GetMethod("WriteToServerAsync", [typeof(DataTable), typeof(CancellationToken)]);

        if (ctor is null || destination is null || timeout is null || mappings is null || rowsCopied is null || write is null)
            return null;

        var add = mappings.PropertyType.GetMethod("Add", [typeof(string), typeof(string)]);

        return add is null ? null : new SqlServerBulkCopy(flags, ctor, destination, timeout, mappings, add, rowsCopied, write);

        // 那一颗「收连接、开关、外部事务」的构造子：按形状找，两家都能找到 ——
        // System.Data.SqlClient 收 (DbConnection, options, DbTransaction)，
        // 而 Microsoft.Data.SqlClient 收的是同族的具体类型 (SqlConnection, options, SqlTransaction)
        // （真库 7.x 上量到：四条公开构造子里只有这一颗三位）。写死 typeof(DbConnection) 去点后面那一家会
        // 当场点空、整家退到 L3，而那个失败形状与"这一家根本没有装载类"完全同形 —— 看不见也修不了。
        // 收具体类型对我们只会更严：只有本家的真连接进得去，而那正是"通道跟着工厂走"要保证的那件事。
        static ConstructorInfo? ConnectionScopedCtor(Type type, Type options)
        {
            foreach (var candidate in type.GetConstructors())
            {
                var parameters = candidate.GetParameters();

                if (parameters.Length == 3
                    && parameters[0].ParameterType.IsSubclassOf(typeof(DbConnection))
                    && parameters[1].ParameterType == options
                    && parameters[2].ParameterType.IsSubclassOf(typeof(DbTransaction)))
                    return candidate;
            }

            return null;
        }

        static object? Flags(Type enumeration)
        {
            var check = Value(enumeration, "CheckConstraints");
            var fire = Value(enumeration, "FireTriggers");

            // 位是从驱动自己的枚举读的，不是这里写死的数：那两颗位在不同版本里没有对换过名字，
            // 但把 20 这种数字抄进来就等于赌"两家、所有版本的位序相同"，而这个赌注没有复点可付。
            return check is null || fire is null
                ? null
                : Enum.ToObject(enumeration, Convert.ToInt64(check, CultureInfo.InvariantCulture) | Convert.ToInt64(fire, CultureInfo.InvariantCulture));

            static object? Value(Type enumeration, string name) => enumeration.GetField(name)?.GetValue(null);
        }
    }
}
