using System.Text;
using FastSql.Providers;

namespace FastSql.Sql;

/// <summary>参数内联（§3 的 <c>ToLogSql()</c>）：把占位符换成可读字面量，交付给 DBA 审阅。
/// 能不能内联由 <see cref="Values.IValueBinder"/> 单点决定（§4）；决定不了的<b>保留占位符</b>，
/// 不猜一个字面量 —— 猜出来的类型语义（日期格式、布尔写法、字符集）比缺一个值更危险。</summary>
public static class SqlLogging
{
    /// <summary>按参数名替换，而不是按出现顺序：模板允许实参顺序与文本顺序不一致
    /// （<c>DATEADD(day, {1}, {0})</c>），此时注册顺序与文本顺序就是两套，位置匹配会张冠李戴。
    /// 已知代价：SQL 文本里若出现与参数同名的一段字符串字面量（<c>N'a@p0'</c>），会被一起替换 ——
    /// 这是给人看的诊断输出，不做词法解析。</summary>
    public static string ToLogSql(this DialectSql sql, IDbProvider provider)
    {
        if (sql.Parameters.Count == 0)
            return sql.Text;

        var byName = new Dictionary<string, AdoParameter>(sql.Parameters.Count, StringComparer.Ordinal);

        foreach (var parameter in sql.Parameters)
            byName[parameter.Name] = parameter;

        var prefix = provider.Dialect.ParameterPrefix;
        var text = sql.Text;
        var builder = new StringBuilder(text.Length + 64);

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != prefix || i + 1 >= text.Length || !char.IsLetter(text, i + 1))
            {
                builder.Append(text[i]);
                continue;
            }

            var end = i + 1;

            while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
                end++;

            if (byName.TryGetValue(text.Substring(i, end - i), out var matched))
            {
                // 交回字面量或原占位符本体：两者都不含前缀之外的多余字符，也不会把 "@p0" 写成 "p0"。
                if (Literal(provider, matched) is { } literal)
                    builder.Append(literal);
                else
                    builder.Append(text, i, end - i);

                i = end - 1;
                continue;
            }

            builder.Append(text[i]);
        }

        return builder.ToString();
    }

    /// <summary>能内联就给字面量，不能就给 null —— 调用方保留占位符。
    /// binder 里的 null 已被 <c>Normalize</c> 成 <see cref="DBNull"/>，所以 null 分支实际是"参数没给值"。</summary>
    static string? Literal(IDbProvider provider, AdoParameter parameter)
    {
        if (parameter.Value is null or DBNull)
            return "NULL";

        return provider.Values.TryInlineLiteral(parameter.Value, hint: null, out var literal) ? literal : null;
    }
}
