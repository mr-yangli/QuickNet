using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text;
using QuickSql.Metadata;
using QuickSql.Providers;

namespace QuickSql.Providers.PostgreSqlBase;

/// <summary>§12.1 的 L1「批量装载通道」在 PG 血统那一家的落地：<c>BeginBinaryImportAsync</c> →
/// <c>NpgsqlBinaryImporter</c>（<c>COPY … FROM STDIN (FORMAT BINARY)</c> 的流式写面）。
///
/// <para><b>这一族的形状与 T-SQL / MySQL 两家都不同，而且差别不在"叫什么名"上</b>：那两家的装载类
/// 都吃<em>整批载体</em>（<c>DataTable</c>），这一家给的是<em>逐行推</em>的导入器 ——
/// <c>WriteRowAsync(ct, values)</c> 一行进、<c>CompleteAsync(ct)</c> 收尾报数。所以本通道不建
/// <see cref="System.Data.DataTable"/>：那份载体在两家里的用处是"让驱动的列类型推断看见形状"，
/// 而这一族的列形状是 <c>BEGIN</c> 那一刻由 <c>COPY</c> 语句自己向库要的（解析目标列的 OID），
/// 载体再抄一遍只是白花一次行 × 列的 <c>object[]</c>。递交的数据面因此是 <see cref="BulkCopyRequest.Rows"/>
/// 原样逐行进 <c>WriteRowAsync</c> —— 值已经是驱动侧形态（<c>null</c> 交回的是 <see cref="DBNull"/>），
/// 与那两家过同一扇值门（§9.3），白名单也复用同一把尺（见 <see cref="SupportsColumn"/>）。</para>
///
/// <para><b>成员形状是按形状找的，构造期与解析期各守同一条纪律</b>：<c>TryResolve</c> 先看
/// <c>Npgsql.NpgsqlBinaryImporter</c> 在不在，再<em>扫导出类型</em>找那一颗「收 (string, CancellationToken)
/// 且交回 <c>Task&lt;导入器&gt;」的连接方法 —— 不写死 <c>NpgsqlConnection</c> 的名字，与
/// <c>SqlServerBulkCopy.ConnectionScopedCtor</c> 按形状找构造子同一手法：现场同时装两家时，
/// 通道永远来自工厂所在的那一家。这套成员缺任何一件交回 <see langword="null"/>，阶梯自然从 L3 起 ——
/// <b>这一条对借邻家臂的叶子同样成立</b>：内建表上金仓那颗 primary（Kdbndp）与 openGauss/GaussDB/瀚高/GBase 8c
/// 各家若装的是真 Npgsql，通道到场；装的是没有导入器的 fork，通道整家缺席、照旧走文本层，
/// 而 provider 代码一个字不改。</para>
///
/// <para><b>两个已知边界照实写，不装成已验证</b>：① <see cref="BulkConnection.Transaction"/> 这一格
/// 交不出去 —— 导入器挂在连接上、没有外部事务入参面，量具与集成册跑到这一档的路径都是无事务的，
/// 带活事务的 L1 是未实测边界（与 MySQL 那一家同格，两边的措辞互相指认）；② 服务端限制一格：
/// <c>COPY FROM STDIN</c> 在 PG 里进的是当前事务语义，而二进制格式对<em>列类型不匹配</em>的报错落在
/// 流的中段 —— 一批里第 n 行不合格时这一档给不出"坏在哪一行"（§12.1 那句"失败不能定位到行"在本家的原文），
/// 这也是白名单不放宽到 <c>char</c>/<c>DateOnly</c>/<c>TimeOnly</c> 的原因之一：不是它们进不了
/// 二进制格式，是它们过不了与文本层同一把类型尺。</para></summary>
sealed class PostgreSqlBulkCopyChannel : IBulkCopyChannel
{
    readonly MethodInfo _begin;
    readonly PropertyInfo _timeout;
    readonly MethodInfo _writeRow;
    readonly MethodInfo _complete;
    readonly MethodInfo _asTask;
    readonly MethodInfo? _disposeAsync;
    readonly MethodInfo? _dispose;

    PostgreSqlBulkCopyChannel(MethodInfo begin, PropertyInfo timeout, MethodInfo writeRow, MethodInfo complete,
        MethodInfo asTask, MethodInfo? disposeAsync, MethodInfo? dispose)
    {
        _begin = begin;
        _timeout = timeout;
        _writeRow = writeRow;
        _complete = complete;
        _asTask = asTask;
        _disposeAsync = disposeAsync;
        _dispose = dispose;
    }

    /// <summary>白名单与另两家<em>同一把尺</em>：那五类缺席的理由（<c>char</c>/<c>DateOnly</c>/
    /// <c>TimeOnly</c>/无符号/<c>object</c>）在本家同样成立，而且本家还多一条 —— 抬头末段那句
    /// "坏行定位不了"。三通道判据一致，才有 §12.4 那句"批量与逐条在库里等价"可数。</summary>
    public bool SupportsColumn(ColumnContract column) => NetType(column.ClrType) is not null;

    /// <summary>这一家的装载类不欠连接串任何开关（<c>UnavailableBecauseOf</c> 走接口的默认 null）——
    /// 与 MySQL 那一家恰好是这道闸门的两个样本：那一家的 <c>LOAD DATA LOCAL</c> 真实欠一颗参数，
    /// 这一家的 <c>COPY</c> 什么都不欠，服务端能连上就能装。</summary>

    [RequiresUnreferencedCode("本对象的 MemberInfo 全部来自按类型名与形状的反射查找（TryResolve），裁剪后的驱动走不到这里。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：反射调用导入器需要运行时生成代码。")]
    public async Task<int> WriteAsync(BulkConnection target, BulkCopyRequest request, CancellationToken ct)
    {
        var importer = await Await(_begin.Invoke(target.Connection, [Statement(request), ct])!).ConfigureAwait(false);

        // Timeout 那颗的可写性在 TryResolve 里已点死（SetMethod 为空整家不进场），这里直接落值。
        _timeout.SetValue(importer, TimeSpan.FromSeconds(target.TimeoutSeconds));

        try
        {
            foreach (var row in request.Rows)
                await (Task)_writeRow.Invoke(importer!, [ct, row])!;

            // CompleteAsync 交回的就是"进了几行"（导入器给不出这一数的形状在 TryResolve 那里已经整家退掉了）。
            var copied = await Await(_complete.Invoke(importer!, [ct])!).ConfigureAwait(false);

            return checked((int)Convert.ToInt64(copied!, CultureInfo.InvariantCulture));
        }
        finally
        {
            // 两条 disposal 至少有一条在 TryResolve 被点死；优先异步那一条，因为同步 Dispose 在这一族
            // 会把"没 Complete 就掐断"的清理拉进当前线程的同步上下文里。
            if (_disposeAsync is not null)
                await Await(_disposeAsync.Invoke(importer!, null)!).ConfigureAwait(false);
            else
                _dispose!.Invoke(importer!, null);
        }
    }

    /// <summary>把 <see cref="Task"/> 与装箱的 <see cref="ValueTask{TResult}"/> 折成同一个 await 面，
    /// 并读出它的 Result。<b>反射边界上没有静态泛型</b>（BeginBinaryImportAsync 交回 <c>Task&lt;导入器&gt;</c>、
    /// CompleteAsync 交回 <c>ValueTask&lt;行数&gt;</c>，两家的形状又随版本漂），所以这里只能"先 await、再按名取 Result"。
    /// <c>ValueTask</c> 转 <c>Task</c> 走它自己的 <c>AsTask()</c> —— 那颗方法在 TryResolve 里已被点存在，
    /// 到这里不存在即不可能。</summary>
    /// <summary><see cref="object.GetType"/> 交回的类型在分析器眼里是无源的（IL2075/IL2073），而这里两次点它
    /// 都是<em>确定有源</em>的：装箱的 <c>ValueTask</c> 与 <c>Task&lt;T&gt;</c> 的公共成员由运行时与驱动自己保住，
    /// 本通道整颗又在 <see cref="RequiresUnreferencedCodeAttribute"/> 的税下。标注落在这颗 await helper 上：
    /// 它把整段"按名点成员"的反射边界圈在自己体内，调用方（<c>WriteAsync</c> 那颗）本来就带着同一条税。</summary>
    [RequiresUnreferencedCode("装箱 awaitable 的 AsTask 与 Result 按名点取：裁剪产物上这条通道不进场（TryResolve 已把泛型形状点死，走到这里即有源）。")]
    [RequiresDynamicCode("反射调用装箱的 ValueTask/Task 成员需要运行时生成代码。")]
    static async Task<object?> Await(object awaitable)
    {
        var task = awaitable as Task
            ?? (Task)awaitable.GetType().GetMethod("AsTask", Type.EmptyTypes)!.Invoke(awaitable, null)!;

        await task.ConfigureAwait(false);

        return task.GetType().GetProperty("Result")?.GetValue(task);
    }

    /// <summary>装载语句：列清单必须逐列点名 —— 不给列清单时 <c>COPY</c> 装的是<em>全表列序</em>，
    /// 而我们递交的行只含可写列（自增列与并发列不在里面），列序错位会把第 3 个值写进第 3 列，
    /// 与 <c>SqlServerBulkCopy</c> 里"按名字映射，一次都不许省"说的是同一件事（本家连名字都只在这条语句里出现一次，
    /// 导入器后面不再有映射面）。目标表名由 <c>EmitTableName</c> 取号限定过，与文本层同一张表（§9.2 第四读者·第五家）。</summary>
    static string Statement(BulkCopyRequest request)
    {
        var columns = new StringBuilder();

        foreach (var column in request.Columns)
        {
            if (columns.Length > 0)
                columns.Append(", ");

            columns.Append('"').Append(column.ColumnName.Replace("\"", "\"\"")).Append('"');
        }

        return "COPY " + request.DestinationTable + " (" + columns + ") FROM STDIN (FORMAT BINARY)";
    }

    static Type? NetType(Type clrType) => Flatten(clrType) switch
    {
        var t when t == typeof(bool) => t,
        var t when t == typeof(byte) => t,
        var t when t == typeof(short) => t,
        var t when t == typeof(int) => t,
        var t when t == typeof(long) => t,
        var t when t == typeof(float) => t,
        var t when t == typeof(double) => t,
        var t when t == typeof(decimal) => t,
        var t when t == typeof(string) => t,
        var t when t == typeof(byte[]) => t,
        var t when t == typeof(Guid) => t,
        var t when t == typeof(DateTime) => t,
        var t when t == typeof(DateTimeOffset) => t,
        var t when t == typeof(TimeSpan) => t,
        _ => null,
    };

    static Type Flatten(Type clrType)
    {
        var member = Nullable.GetUnderlyingType(clrType) ?? clrType;

        return member.IsEnum ? Enum.GetUnderlyingType(member) : member;
    }

    /// <summary>整套验一遍，缺任何一件交回 <see langword="null"/>（阶梯自然从 L3 起，与"这一家没有通道"同形）。
    /// 五颗成员各有不退的理由：<c>WriteRowAsync</c> 缺了就只能逐格 <c>Write</c>（行边界由导入器数，逐格写会把
    /// "缺一颗"错报成"多一列"）；<c>CompleteAsync</c> 交回的行数是 §12.1"报不出行数就不算通道"的那一颗；
    /// 行数那颗泛型只认 <c>long</c>/<c>ulong</c> 两种（<c>Complete()</c> 同步那一颗在 10.0.3 上是 <c>ulong</c>，
    /// 泛型参数随版本漂过，认死一种会把本来能成的整家退掉）；<c>Timeout</c> 缺了批量会静默继承连接的
    /// 默认超时，那是与文本层不同的一道默认值，不接；disposal 两条至少要有一条。</summary>
    [RequiresUnreferencedCode("按类型名与形状在驱动程序集里找导入器与它的成员（TryResolve 本体），被裁剪的驱动会交回 null。")]
    [RequiresDynamicCode("反射调用导入器需要运行时生成代码。")]
    internal static PostgreSqlBulkCopyChannel? TryResolve(Assembly driver)
    {
        var importer = driver.GetType("Npgsql.NpgsqlBinaryImporter", throwOnError: false);

        if (importer is null)
            return null;

        // 那颗「收 (COPY 语句, 取消) 且交回 Task<导入器>」的方法只从连接类上按形状找：
        // 写死 NpgsqlConnection 会把 fork 与将来改名一并赌进去（与 SqlServer 那家按形状找构造子同一条理由）。
        MethodInfo? begin = null;

        foreach (var candidate in driver.GetExportedTypes())
        {
            if (!candidate.IsSubclassOf(typeof(DbConnection)))
                continue;

            var method = candidate.GetMethod("BeginBinaryImportAsync", [typeof(string), typeof(CancellationToken)]);

            if (method is not { ReturnType.IsGenericType: true }
                || method.ReturnType.GetGenericTypeDefinition() != typeof(Task<>)
                || method.ReturnType.GetGenericArguments()[0] != importer)
                continue;

            begin = method;
            break;
        }

        var timeout = importer.GetProperty("Timeout");
        var writeRow = importer.GetMethod("WriteRowAsync", [typeof(CancellationToken), typeof(object[])]);
        var complete = importer.GetMethod("CompleteAsync", [typeof(CancellationToken)]);

        if (begin is null || timeout is null || writeRow is null || complete is null || timeout.SetMethod is null)
            return null;

        // 行数只认 long/ulong 两种泛型参数；AsTask 是把 ValueTask 折进同一个 await 面的那颗，缺了不接。
        if (!complete.ReturnType.IsGenericType || complete.ReturnType.GetGenericTypeDefinition() != typeof(ValueTask<>))
            return null;

        var count = complete.ReturnType.GetGenericArguments()[0];

        if (count != typeof(long) && count != typeof(ulong))
            return null;

        var asTask = complete.ReturnType.GetMethod("AsTask", Type.EmptyTypes);

        if (asTask is null || asTask.ReturnType != typeof(Task<>).MakeGenericType(count))
            return null;

        var disposeAsync = importer.GetMethod("DisposeAsync", Type.EmptyTypes);
        var dispose = typeof(IDisposable).IsAssignableFrom(importer) ? importer.GetMethod("Dispose", Type.EmptyTypes) : null;

        if (disposeAsync is null && dispose is null)
            return null;

        return new PostgreSqlBulkCopyChannel(begin, timeout, writeRow, complete, asTask, disposeAsync, dispose);
    }
}
