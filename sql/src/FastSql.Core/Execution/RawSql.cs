using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using FastSql.Sql;

namespace FastSql.Execution;

/// <summary>原生 SQL 侧的「文本 + 参数对象」→ <see cref="DialectSql"/>。<b>文本一颗不动</b>：
/// §11 这一口的全部前提就是"这句话是你自己写的"，所以这里只做参数命名，不做占位符改写、
/// 不做语法校验、不补引号 —— 那三件事任何一件做了，用户写的 SQL 就不再是他写的那一句。</summary>
public static class RawSql
{
    /// <summary>原生 SQL 在 AOP 与日志里的操作名。表清单恒空：这一口的文本可以碰任何表，
    /// <see cref="ExecutionTag"/> 那个槽位说的只有"链式模型知道自己在动哪几张表"，这里没有模型。</summary>
    public static ExecutionTag Tag { get; } = new("raw", []);

    /// <summary>参数这一侧的 trim 代价，<c>AdoExecutor</c> 那几个转发口的标注复用的是同一句话。</summary>
    internal const string ParameterMessage = "参数对象按运行期类型取公共属性与属性值。";

    /// <summary>参数名 = <paramref name="parameterPrefix"/> + 属性名，与生成 SQL 那条路的口径一致
    /// （<see cref="SqlWriter.NextParameterName"/> 造的也是一颗"文本里长什么样就怎么填"的两用名，
    /// 摘前缀留给 <c>AdoExecutor.Bind</c> 按 <c>DriverAcceptsPrefixedParameterNames</c> 决定）。
    /// 于是这里的分工只有一句：<b>文本里的占位符由 SQL 作者写，名字由属性名给</b>，两边必须同形。</summary>
    /// <exception cref="FastSqlException">这一家是位置式占位符（GBase 8s 的 <c>?</c>）时：
    /// 那颗形里根本没有名字可挂，按属性名编号会造出一颗驱动收不到的名字，而症状是"参数没绑上"那种看不出原因的错。</exception>
    [RequiresUnreferencedCode(ParameterMessage)]
    public static DialectSql ToDialectSql(string sql, object? args, char parameterPrefix)
    {
        ArgumentNullException.ThrowIfNull(sql);

        if (parameterPrefix is '?')
            throw new FastSqlException(
                "这一家的占位符是位置式的 ?，参数没有名字可挂：原生 SQL 口在这里不接参数对象。" +
                "要带参数请用低层口自己造 DialectSql（AdoExecutor.QueryAsync<T>(DialectSql, …)），按 ? 的顺序排参数。");

        if (args is null)
            return new DialectSql(sql, []);

        if (args is CancellationToken token)
            throw CancellationTokenAsArgument(token);

        var properties = args.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);

        if (properties.Length == 0)
            throw new FastSqlException(
                $"原生 SQL 的参数对象 {args.GetType().Name} 上没有公共实例属性：参数是从属性名取名字的，" +
                "所以这里要么给一个匿名对象（new { id = 1 }），要么干脆不传参数。");

        var parameters = new List<AdoParameter>(properties.Length);

        foreach (var property in properties)
        {
            if (property.GetIndexParameters().Length > 0)
                continue;

            var value = property.GetValue(args);

            // 一颗 CancellationToken 躺在参数对象里，唯一可信的解释是"ct 传错了位置"：
            // 把它当参数绑出去，得到的是两条属性（IsCancellationRequested/None）的盒子，而库里收到的是一句缺参数的语句。
            if (value is CancellationToken inner)
                throw CancellationTokenAsArgument(inner);

            parameters.Add(new AdoParameter(parameterPrefix + property.Name, value, System.Data.DbType.Object, 0));
        }

        return new DialectSql(sql, parameters);
    }

    static FastSqlException CancellationTokenAsArgument(CancellationToken token) => new(
        $"参数对象里出现了一颗 CancellationToken（{token.GetType().Name}）：那多半是把 ct 传到了 args 的位置上。" +
        "原生 SQL 口的签名是 (sql, args, ct)，取消令牌请用 ct: 命名传。");
}
