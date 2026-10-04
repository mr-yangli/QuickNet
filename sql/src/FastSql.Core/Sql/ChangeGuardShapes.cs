using FastSql.Sql.Ast;

namespace FastSql.Sql;

/// <summary>变更守卫的<b>共用形状</b>（§12.4 第 2 项）。"库里这一格与行集里那一格是不是不同"在多数家里
/// 只能是同一个三段式，所以它从 <c>SqlServerChangeGuard</c> 提到这里 —— 提出来这个动作本身就是那一版的结论：
/// 一个方言族接出来之后，<em>多数派</em>换边了（v0.30 把"两家"改成"三家"：第四家也落在这一边，见下面那段；
/// v0.31 把它改成"四家"，第五家同边而<em>第三条来路</em>）。
///
/// <para>三段是 <c>a &lt;&gt; b</c>、<c>a IS NULL AND b IS NOT NULL</c>、<c>a IS NOT NULL AND b IS NULL</c>。
/// 需要它的条件是<b>结构性的</b>：那个库的布尔表达式不是一等值，所以 §12.4 原稿那个近似式
/// <c>(a IS NULL) &lt;&gt; (b IS NULL)</c> 过不去（T-SQL 那一侧是 2026-09-21 真库跑出"布尔不能出现在比较符一侧"
/// 这条报错才定下来的，见 <c>SqlServerChangeGuard</c> 的类注释；Oracle 血统同一条限制，理由同源）。</para>
///
/// <para>有 <c>IS DISTINCT FROM</c> 的那一族（PG 系与金仓）用不着这里 —— 它一句就够，
/// 而那一句在别家<em>不存在</em>，所以那颗算子仍留在 <see cref="BinaryOperator"/> 里由 provider 自选。
/// 今天五家已经变成"一句 vs 三段"的 <b>1∶4</b>，§9.1 那份矩阵里 <c>Oracle</c>+ 那一行往后每接一家都先落进
/// 三段这一边 —— v0.30 的第四家兑了这句预测，而它落进来的理由<em>不是</em>上面那条结构性的理由
/// （这一家有 <c>IS DISTINCT FROM</c>，也有 <c>&lt;=&gt;</c>）：是"共用层发不出那一颗算子、而 <c>IS DISTINCT FROM</c>
/// 要按版本发"。同一个形状、两条来路，第二条记在 <c>MySqlChangeGuard</c> 的类注释里。
/// <b>v0.31 的第五家是第三条来路，而它是三条里唯一一条"按缺席读"的</b>：既没有那条结构性限制的证据、
/// 也没有"<em>有</em>那颗算子"的证据（文档与两份产物里都没出现过 <c>IS DISTINCT FROM</c>），
/// 所以这里的三段式不是选择而是<em>唯一发得出的形状</em>——那一格写在 <c>XuguChangeGuard</c> 第一段后半，
/// 而它记进 §14 的方式与上面两条不同：那两条是"知道为什么"，这一条是"不知道为什么也必须这么发"。</para></summary>
public static class ChangeGuardShapes
{
    /// <summary>「不同则真」的三段展开。<b>右倾</b>：同种连接词在 <see cref="DialectBase"/> 里不加括号，
    /// 所以五家出来的都是人写的那种平铺 <c>OR</c>，而黄金样本比的是版式。</summary>
    public static SqlExpression ThreeWay(SqlExpression stored, SqlExpression incoming)
    {
        var different = new SqlBinaryExpression(BinaryOperator.NotEqual, stored, incoming);

        var storedNull = new SqlUnaryExpression(UnaryOperator.IsNull, stored);
        var incomingNull = new SqlUnaryExpression(UnaryOperator.IsNull, incoming);
        var storedPresent = new SqlUnaryExpression(UnaryOperator.IsNotNull, stored);
        var incomingPresent = new SqlUnaryExpression(UnaryOperator.IsNotNull, incoming);

        var onlyIncoming = And(storedNull, incomingPresent);
        var onlyStored = And(storedPresent, incomingNull);

        return Or(different, Or(onlyIncoming, onlyStored));
    }

    static SqlExpression And(SqlExpression left, SqlExpression right)
        => new SqlBinaryExpression(BinaryOperator.And, left, right);

    static SqlExpression Or(SqlExpression left, SqlExpression right)
        => new SqlBinaryExpression(BinaryOperator.Or, left, right);
}
