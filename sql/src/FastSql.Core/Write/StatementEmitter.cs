using System.Globalization;
using System.Text;
using FastSql.Aop;
using FastSql.Sql;
using FastSql.Sql.Ast;

namespace FastSql.Write;

/// <summary>写侧三条链共用的发射段。<b>为什么只抽这两步</b>：分块、列合成、取值是各链自己的事
/// （Insert 有多值语句与回填两档，Update/Delete 各一条语句），而「AST → 文本 → 参数改写」这一步
/// 三家逐字相同。留成两份的代价不是行数而是 §10.1 硬规则 1 —— 回放这件事会在某一条链上悄悄缺失。</summary>
static class StatementEmitter
{
    /// <summary>provider 没给参数预算时的保守值（§12.3）。这里没有任何库种事实可依，所以取一个
    /// 低于所有已知上限的数：宁可多几次 round-trip，也不要撞上一次就整批失败的硬上限。
    /// <b>SQL Server 走不到这条</b>（<c>SqlServerBulk.ParameterBudget = 2100</c>）。
    /// 【v0.31】这一句的对照面重数一次：五家里今天<em>走到</em>这一份兜底的是<b>两家</b>
    /// （<c>OracleProviderBase.Bulk</c> 与 <c>XuguProvider.Bulk</c> 各交回 <see langword="null"/>），
    /// 报得出数字的是<b>三家</b>：2100 / 32767 / 65535。位数在 v0.30 是 1 ∶ 3，本版变成 2 ∶ 3，
    /// 因为新进来那一家落在"没有真预算"那一边；逐家的块数由 <c>DialectParityTests</c> 钉死（3 / 1 / 50 / 1 / 50）。</summary>
    public const int ConservativeParameterBudget = 100;

    /// <summary>§12.3 的分块公式，两处调用共用一份：批量 INSERT 的多值语句与 §12.5 的合并 UPDATE。
    /// 留十分之一给文本本身（表名/列名/<c>OUTPUT</c>/<c>ON</c>），再除以每行参数个数。
    /// <b>分母是"每行几颗参数"而不是"每行几列"</b>——两者在 INSERT 上恰好相等，在合并 UPDATE 上不等（键列不进 <c>SET</c> 却进 <c>VALUES</c>）。</summary>
    public static int RowsPerStatement(int budget, int parametersPerRow)
        => Math.Max(1, budget * 9 / 10 / parametersPerRow);

    /// <summary>只用于 <see cref="StringBuilder"/> 的预容量（§12.7 E）：每颗参数按 <c>@p123, </c> 估 7 个字符。</summary>
    public static int EstimateLength(int parameters, int identifierChars) => 96 + parameters * 7 + identifierChars;

    public static SqlWriter Writer(ISqlDialect dialect, int capacity)
        => new(new StringBuilder(capacity), [], dialect.ParameterPrefix);

    /// <summary><c>CudBuilding</c> 的发射点（§10.1 那一格里欠到 v0.18 的那个假槽位就此结清）：
    /// 写侧三条链与跟踪器的两个发射口都走这里，位置在<b>AST 已经变成文本之后、<see cref="ApplyBinding"/> 之前</b> ——
    /// 这一个钩子改的是整句文本（软删改写、影子表、全局注释），要看到完整句子才有得改；
    /// 而参数级改写必须在它之后，否则前一个钩子新加的参数没人过钩子。</summary>
    /// <remarks><b>"按注册顺序串联，后一个能看到前一个的结果"是靠逐个派发实现的</b>：
    /// 一次 <c>Action</c> 多播调用会把<em>同一个</em>事件对象递给每一个订阅者，各自写 <c>Rewritten</c> 就成了
    /// 最后一个人赢，而前一个人改出来的文本第二个人根本没看见。所以这里展开调用列表，每人一份事件、
    /// 拿到的 <c>Sql</c> 是已经串到当前的那一份。代价只在订阅了的时候付（硬规则 2：未订阅连事件对象都不构造）。
    /// <para>改写类钩子抛异常直接失败，不吞（硬规则 3）—— 吞了就把改写坏的句子送进库里了。</para></remarks>
    public static DialectSql ApplyCud(FastSqlOrm orm, DialectSql sql, string operationKind, List<HookTrace> traces, ref int step)
    {
        var cud = orm.Aop.CudBuilding;

        if (cud is null)
            return sql;

        var current = sql;
        var hooks = cud.GetInvocationList();

        for (var i = 0; i < hooks.Length; i++)
        {
            var @event = new CudBuildingEvent { OperationKind = operationKind, Sql = current };

            ((Action<CudBuildingEvent>) hooks[i])(@event);

            var next = @event.Rewritten ?? current;

            if (next.Equals(current))
                continue;

            traces.Add(new HookTrace(step++, "CudBuilding", HookCategory.Rewrite, current.Text, next.Text));
            current = next;
        }

        return current;
    }

    /// <summary><c>ParameterBinding</c> 的发射点。<b>在这里而不是执行层 bind 之前</b>是 §10.1 硬规则 1 逼出来的：
    /// 回放要求 <c>Explain()</c> 能看到每一颗参数被改成了什么，而执行层改的参数在 <c>Explain()</c> 里根本不存在。
    /// 代价是读侧的参数不过这道钩子 —— 已记进 §14 的缺口。</summary>
    public static DialectSql ApplyBinding(FastSqlOrm orm, DialectSql sql, List<HookTrace> traces, ref int step)
    {
        var bind = orm.Aop.ParameterBinding;

        if (bind is null || sql.Parameters.Count == 0)
            return sql;

        AdoParameter[]? rewritten = null;

        for (var i = 0; i < sql.Parameters.Count; i++)
        {
            var parameter = sql.Parameters[i];
            var @event = new ParameterBindingEvent(parameter, parameter.Hint);

            bind(@event);

            var next = @event.Parameter;

            if (next.Equals(parameter))
                continue;

            traces.Add(new HookTrace(step++, "ParameterBinding", HookCategory.Rewrite, $"{parameter.Name} = {Text(parameter.Value)}", $"{next.Name} = {Text(next.Value)}"));

            // 整表拷一次而不是逐格补：改写多半不止一颗，而 <c>??=</c> 的回填循环在宽表上是 n²。
            rewritten ??= [.. sql.Parameters];
            rewritten[i] = next;
        }

        return rewritten is null ? sql : new DialectSql(sql.Text, rewritten);
    }

    /// <summary>值的可读文本 —— 钩子回放与冲突消息里那几颗值都从这里落地。<b>二进制单独一档</b>：
    /// <c>byte[].ToString()</c> 交的是类型名，而 <c>rowversion</c> 恰恰是并发列最常见的那一型，
    /// 「会话以为是 System.Byte[]，库里是 System.Byte[]」会把这条消息的全部用处抹掉
    /// （§8.2 的"逐个给出"要的正是看出<em>哪一列</em>与<em>差在哪</em>）。形状对齐库里的写法：<c>0x</c> + 大写十六进制。</summary>
    public static string Text(object? value) => value switch
    {
        null or DBNull => "NULL",
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? value.GetType().Name,
    };
}
