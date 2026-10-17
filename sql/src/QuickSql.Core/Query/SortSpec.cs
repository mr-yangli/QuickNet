namespace QuickSql.Query;

/// <summary>把前端那一串排序写法（<c>"Id,Name desc"</c>）拆成「名字 + 方向」的清单。
///
/// <para><b>这一颗只拆，不认列</b>：认列全仓库只有 <c>QueryEngine.Column</c> 那一份三档（属性名 ∶ 列名 ∶
/// 大小写不敏感的唯一命中），拆出来的每一颗名字都从那里过，认不出当场抛。所以串里的文本
/// 依旧一个字都不进 SQL，多出来的只是"一串"而不是"一颗"。</para>
///
/// <para>方向词只有挂在名字后面那一形，取值只有 <c>asc</c> / <c>desc</c>（<c>ascending</c> /
/// <c>descending</c> 同义）四颗、大小写不敏感。<b>认不出方向词的那一段整颗按列名交出去</b>，
/// 而不是猜一个方向 —— 于是 <c>"desc"</c> 指的可以是那一颗名叫 desc 的列，
/// <c>"a; drop table t--"</c> 指的是一颗认不出而当场响的名字。</para></summary>
static class SortSpec
{
    /// <summary>逗号分颗，空颗（含 <c>"Id,,Name,"</c> 那种前端 <c>join(',')</c> 留下的尾巴）跳过。
    /// <paramref name="defaultDescending"/> 是<em>没写方向的颗</em>的方向，写了的以串为准 ——
    /// 那颗形参因此从"这一颗的方向"变成"这一串里没点名的那些颗的方向"，单颗写法逐字不变。</summary>
    /// <remarks>收 <c>null</c> 而交回空清单：那颗串是 HTTP 查询参数直过来的，而"没给排序"要响的是
    /// 认列那一句（消息里带着表名），不是 <c>NullReferenceException</c>。</remarks>
    public static IReadOnlyList<(string Name, bool Descending)> Parse(string? sortSpec, bool defaultDescending)
    {
        List<(string Name, bool Descending)> keys = [];

        foreach (var raw in (sortSpec ?? string.Empty).Split(',', StringSplitOptions.TrimEntries))
        {
            if (raw.Length > 0)
            {
                keys.Add(SplitOne(raw, defaultDescending));
            }
        }

        return keys;
    }

    static (string Name, bool Descending) SplitOne(string token, bool defaultDescending)
    {
        var at = token.Length;

        while (at > 0 && !char.IsWhiteSpace(token[at - 1]))
        {
            at--;
        }

        if (at == 0)
        {
            return (token, defaultDescending);
        }

        var head = token[..(at - 1)].TrimEnd();
        var tail = token[at..];

        if (tail.Equals("desc", StringComparison.OrdinalIgnoreCase) || tail.Equals("descending", StringComparison.OrdinalIgnoreCase))
        {
            return (head, true);
        }

        if (tail.Equals("asc", StringComparison.OrdinalIgnoreCase) || tail.Equals("ascending", StringComparison.OrdinalIgnoreCase))
        {
            return (head, false);
        }

        return (token, defaultDescending);
    }
}
