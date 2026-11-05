using System.Text;
using QuickJob.Benchmarks;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

Console.OutputEncoding = Encoding.UTF8;

if (args.Contains("--gate", StringComparer.Ordinal))
{
    // 判据只能在 Release 下出：Debug 里装箱更多、内联更少，红会假红、绿会假绿。
    // 退出码 2 与「门禁判出 N 红」的 N 区分开，免得把"跑错了配置"读成"有两条不达标"。
    // Release 下的读法：红行数（这一半会随负载翻，逐次值记 docs 的门禁账），红行比基线差时再 +32 —— 见 Gates.RegressionFlag。
    if (IsDebug())
    {
        Console.Error.WriteLine("--gate 要在 Release 下跑，否则每一个数字都不作数：");
        Console.Error.WriteLine("    dotnet run -c Release --project benchmarks/QuickJob.Benchmarks -- --gate");

        return 2;
    }

    return Gates.Run();
}

// 两种跑法共用同一批调用形状：判据与报告不能各长一套，否则半年后比的不是同一件事。
//
// 工具链换成 InProcessEmit —— 本机 SDK 是 net11 preview（11.0.100-rc.3），BDN 0.15.8（NuGet 上的最新版，
// 没有更高可升）的 DotNetSdkValidator 解不出 preview 名片，NotRecognized → NotImplemented 在
// "// Validating benchmarks:" 一行之后当场炸。InProcess 不落子项目，CsProjCoreToolchain.Validate 整段不跑，那条路不存在。
//
// InProcess 的代价（写在这里，别当它不存在）：
//   ① 失去子进程隔离 —— 28 个方法在同一进程里轮，前一个的 JIT 台路与堆形态会渗进后一个的读数；
//   ② GC 状态共享 —— [MemoryDiagnoser] 的 Allocated 列在 InProcess 下仍可用（BDN 0.13 起支持），
//      但准头不如子进程档，读数时打这份折扣。
//
// 收益：preview 期跑得动 —— 等 BDN 或 SDK 出 stable 名片，把 AddJob 那一行删掉就回到默认子进程
// 工具链，基准代码一行不用动。
// （API 注：0.15.8 的 ManualConfig 没有 AddToolchain 这个 fluent —— toolchain 挂在 Job 上，
//  Job.Default.WithToolchain(...) 再 AddJob 进 config。timeout 显式给 30 分钟：InProcess 的预算
//  是<b>整个进程一份</b>，默认 5 分钟 —— 前几个方法各 30~60 秒就把钱花完了，轮到收件箱基准时报
//  "takes too long to run"。）
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
    .Run(args, ManualConfig.CreateMinimumViable()
        .AddJob(Job.Default.WithToolchain(new InProcessEmitToolchain(TimeSpan.FromMinutes(30), false))));

return 0;

static bool IsDebug()
{
#if DEBUG
    return true;
#else
    return false;
#endif
}
