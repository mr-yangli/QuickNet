namespace QuickPdf.Internal;

/// <summary>
/// Finds a CJK-capable TrueType font on the current machine and loads it.
/// Order: explicit options.FontFile &gt; options.FontDirectory scan &gt; Windows \Fonts &gt; common Linux font dirs.
/// Candidate files are try-parsed: CFF-based .otf or otherwise unusable fonts are skipped, because only
/// glyf/TrueType outlines can be embedded through a PDF /FontFile2 stream.
/// </summary>
internal static class FontResolver
{
    // Preferred file names, most faithful CJK rendering first.
    private static readonly string[] Candidates =
    [
        "msyh.ttc", "msyh.ttf",            // Microsoft YaHei (UI 黑体, Win8+)
        "simhei.ttf",                      // SimHei
        "simsun.ttc", "simsun.ttf",        // SimSun (宋体)
        "NotoSansCJK-Regular.ttc",
        "wqy-microhei.ttc", "wqy-zenhei.ttc",
    ];

    private static readonly string[] LinuxDirs =
    [
        "/usr/share/fonts", "/usr/local/share/fonts",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/fonts")
    ];

    public static Ttf Resolve(PdfOptions options)
    {
        if (options.FontFile is not null)
            return LoadOrThrow(options.FontFile);

        foreach (var path in EnumeratePaths(options))
        {
            var ttf = TryLoad(path);
            if (ttf is not null) return ttf;
        }

        throw new QuickPdfException(
            "No embeddable CJK TrueType font found. Install Microsoft YaHei / SimHei (Windows), " +
            "or Noto Sans CJK / WenQuanYi (Linux), or set PdfOptions.FontFile to a .ttf/.ttc path.");
    }

    private static IEnumerable<string> EnumeratePaths(PdfOptions options)
    {
        if (options.FontDirectory is { } optDir && Directory.Exists(optDir))
            foreach (var p in Probe(optDir)) yield return p;
        if (Environment.GetEnvironmentVariable("WINDIR") is string windir && Directory.Exists(Path.Combine(windir, "Fonts")))
            foreach (var p in Probe(Path.Combine(windir, "Fonts"))) yield return p;
        foreach (var dir in LinuxDirs)
            if (Directory.Exists(dir))
                foreach (var p in Probe(dir)) yield return p;
    }

    /// <summary>Candidate names in the directory, then any matching name recursively, then any *.ttf/.ttc.</summary>
    private static IEnumerable<string> Probe(string dir)
    {
        foreach (var cand in Candidates)
        {
            var direct = Path.Combine(dir, cand);
            if (File.Exists(direct)) yield return direct;
        }
        List<string> files;
        // 空 catch 收窄（v0.2 第二笔）：这里豁免的只有"目录走不通"那一族——IO 与权限。
        // OOM 与逻辑 bug 不再被伪装成"目录里没有字体"（那正是畸形字体长期没被告发的原因之一）。
        try { files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var f in files)
        {
            var name = Path.GetFileName(f);
            foreach (var cand in Candidates)
                if (string.Equals(name, cand, StringComparison.OrdinalIgnoreCase)) { yield return f; break; }
        }
        foreach (var f in files)
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is ".ttf" or ".ttc") yield return f;
        }
    }

    private static Ttf? TryLoad(string path)
    {
        // 空 catch 收窄（v0.2 第二笔）：候选探测豁免的仍是"这一份读不下去"的形状——读失败/权限/坏 sfnt。
        // 其它异常（含 OOM）照抛：装不进 InvalidDataException 的错不是"换下一个候选"能吸收的。
        try { return Ttf.Load(File.ReadAllBytes(path)); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null; // unreadable / collection index / no glyf / no cmap -> next candidate
        }
    }

    private static Ttf LoadOrThrow(string path)
    {
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (Exception ex) { throw new QuickPdfException($"Cannot read font file '{path}': {ex.Message}", ex); }
        try { return Ttf.Load(data); }
        catch (Exception ex) { throw new QuickPdfException($"Font '{path}' is not an embeddable TrueType font: {ex.Message}", ex); }
    }
}
