using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using FastSql.Providers;

namespace FastSql.Providers.SqlServer;

/// <summary>批量高速层（§12.1）在这一家的三档：<b>L1 <c>SqlBulkCopy</c> + L3 多值语句 + L5 逐条</b>，
/// 而 L2 数组绑定与 L4 临时表在这一家没有对应实现，所以 <see cref="BulkMethod"/> 里也没有那两个成员。
/// <b>分块按参数个数算而不是按行数</b>（§12.3）：500 行 × 30 列在 2100 上限下是 5 块而不是 1 块。</summary>
/// <remarks><c>ResolveCopyChannel</c> 那一颗是本版补的接线：能力位 <see cref="DbCapabilities.Bulk_Copy"/> 从第一天就报 true，
/// 而 SPI 上根本没有通道可取 —— 那是「规格里写了设计、今天没接线」那一格的本家样本。今天两边对齐了：
/// 报这颗位的地方与交得出通道的是同一份实现。</remarks>
public sealed class SqlServerBulk : IBulkStrategy
{
    readonly Assembly _driver;

    IBulkCopyChannel? _channel;

    public SqlServerBulk(DbProviderFactory driverFactory)
    {
        // 构造期一次反射都没有：这里只记下"工厂住在哪个程序集"，第一次真取通道时才去找装载类
        // （§12.7「加载可以反射，调用不行」的那一半）。惰性是有对象的 —— 只产 SQL 的离线测试也构造 provider，
        // 而那条路上不该有任何一次类型查找。取用点因此只能落在 ResolveCopyChannel 自己身上：
        // 诊断是跟着"调用出现在哪个方法里"走的，写在构造函数里就得给构造函数交税，而构造者是所有人都在用的。
        _driver = driverFactory.GetType().Assembly;
    }

    public int ParameterBudget => 2100;

    public string Describe => "L1 SqlBulkCopy（按名字映射，CheckConstraints|FireTriggers 两颗开关从驱动的枚举上读，" +
                              "白名单之外的列整批退 L3）+ L3 多值 VALUES + L5 逐条；参数预算 2100（§12.3）";

    /// <remarks><b>没有锁，命中与落空都只查一次</b>：装载类存在时结果进 <c>_channel</c>，之后全是字段读。
    /// 两个线程同时第一次取最多各查一次 —— 那是一次幂等的纯查找，产出的两个对象等价，所以这里不值得为它上锁。
    /// 落空的那一种（驱动太老或已被裁剪）<em>每次取都重查一遍</em>，而这条分支的下一步是退到文本层、
    /// 为同一批数据付 N 次往返：为它省一次类型查找没有意义。</remarks>
    [RequiresUnreferencedCode("取通道是一次按类型名的反射查找（SqlServerBulkCopy.TryResolve），裁剪后的驱动交回 null，阶梯因此从 L3 起。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：反射解析装载类需要运行时生成代码。")]
    public IBulkCopyChannel? ResolveCopyChannel() => _channel ??= SqlServerBulkCopy.TryResolve(_driver);
}
