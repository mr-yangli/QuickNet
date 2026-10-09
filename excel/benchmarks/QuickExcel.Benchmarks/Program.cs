using System.Diagnostics;
using QuickExcel;
using MiniExcelLibs;

// ---------------------------------------------------------------------------
// QuickExcel vs MiniExcel benchmark: 100k rows export + import.
// Run: dotnet run -c Release --project benchmarks/QuickExcel.Benchmarks
// ---------------------------------------------------------------------------

public sealed class Order
{
    public int Id { get; set; }
    public string? Customer { get; set; }
    public string? Product { get; set; }
    public decimal Amount { get; set; }
    public DateTime Created { get; set; }
    public bool Paid { get; set; }
    public string? Note { get; set; }
}

public static class Program
{
    private const int Rows = 100_000;

    public static void Main()
    {
        var data = new List<Order>(Rows);
        var baseDate = new DateTime(2020, 1, 1);
        for (int i = 0; i < Rows; i++)
        {
            data.Add(new Order
            {
                Id = i,
                Customer = $"客户{i % 997}",
                Product = $"产品-{i % 50}",
                Amount = i * 3.17m,
                Created = baseDate.AddDays(i % 3000),
                Paid = i % 2 == 0,
                Note = i % 7 == 0 ? "备注 & <特殊> 字符" : null,
            });
        }

        string dir = Path.Combine(Path.GetTempPath(), "quickexcel_bench");
        Directory.CreateDirectory(dir);
        string fastFile = Path.Combine(dir, "fast.xlsx");
        string miniFile = Path.Combine(dir, "mini.xlsx");
        string tpl = Path.Combine(dir, "tpl.xlsx");
        string tplOut = Path.Combine(dir, "tpl_out.xlsx");
        // MiniExcel.SaveAs uses CreateNew by default -> drop stale outputs from previous runs
        foreach (var f in new[] { fastFile, miniFile, tpl, tplOut })
            if (File.Exists(f)) File.Delete(f);

        Console.WriteLine($"Rows: {Rows:N0}");
        Console.WriteLine();
        Console.WriteLine($"{"op",-28} {"lib",-12} {"time",10} {"alloc",10} {"file",10}");

        // ---- export ----
        var t = Run(() => { MiniExcel.SaveAs(miniFile, data); return 0L; });
        Report("export", "MiniExcel", t, s_alloc, new FileInfo(miniFile));

        t = Run(() => { QuickExcel.Excel.SaveAs(fastFile, data); return 0L; });
        Report("export", "QuickExcel", t, s_alloc, new FileInfo(fastFile));

        // ---- import (typed) ----
        t = Run(() =>
        {
            long n = 0;
            foreach (var _ in MiniExcel.Query<Order>(miniFile)) n++;
            return n;
        });
        Report("import typed", "MiniExcel", t, s_alloc, null);

        t = Run(() =>
        {
            long n = 0;
            foreach (var _ in QuickExcel.Excel.Query<Order>(fastFile)) n++;
            return n;
        });
        Report("import typed", "QuickExcel", t, s_alloc, null);

        // ---- import (validated) ----
        var r = QuickExcel.Excel.Import<Order>(fastFile);
        Console.WriteLine($"\nQuickExcel Import<Order>: valid={r.Data.Count:N0} errors={r.Errors.Count}");

        // ---- template render (100k rows) ----
        QuickExcel.Excel.SaveAs(tpl, new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["A"] = "标题 {{Title}}", ["B"] = "", ["C"] = "" },
            new Dictionary<string, object?> { ["A"] = "Id", ["B"] = "Customer", ["C"] = "Amount" },
            new Dictionary<string, object?> { ["A"] = "{{items.Id}}", ["B"] = "{{items.Customer}}", ["C"] = "{{items.Amount}}" },
        }, new ExportOptions { PrintHeader = false });

        t = Run(() =>
        {
            QuickExcel.Excel.SaveAsByTemplate(tplOut, tpl,
                new Dictionary<string, object?> { ["Title"] = "百万订单", ["items"] = data });
            return 0L;
        });
        Report("template 100k rows", "QuickExcel", t, s_alloc, new FileInfo(tplOut));

        // ---- peak process memory ----
        var p = Process.GetCurrentProcess();
        Console.WriteLine($"\npeak working set: {p.PeakWorkingSet64 / 1024 / 1024:N0} MiB");
    }

    private static long s_alloc;

    private static TimeSpan Run(Func<long> action)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = GC.GetTotalAllocatedBytes(true);
        var sw = Stopwatch.GetTimestamp();
        action();
        var elapsed = Stopwatch.GetElapsedTime(sw);
        s_alloc = GC.GetTotalAllocatedBytes(true) - before;
        return elapsed;
    }

    private static void Report(string op, string lib, TimeSpan t, long alloc, FileInfo? file)
    {
        string f = file is null ? "-" : $"{file.Length / 1024:N0} KiB";
        Console.WriteLine($"{op,-28} {lib,-12} {t.TotalMilliseconds,9:N0} ms {alloc / 1024 / 1024,7:N1} MB {f,10}");
    }
}
