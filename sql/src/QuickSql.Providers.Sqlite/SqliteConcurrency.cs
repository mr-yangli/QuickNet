using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.Sqlite;

/// <summary>SQLite 的乐观并发（§4，第七家）。比较式与另六家<em>逐字同形</em>（<c>"Version" = @p</c>，
/// 只差那颗前缀字形），分岔仍然只在<b>谁负责让那一格变</b>，而本家落的是"应用给值"那一侧：
/// 七家的分布因此是 <c>1 家库给值（T-SQL）∶ 6 家应用给值</c>。
///
/// <para><b>【第一条：本家没有 <c>rowversion</c> 那一档，而"没有"在这里的证法与另六家不同】</b>
/// 虚谷那一家这一格是被<em>正面推翻</em>的：库里建得成 <c>ROWVERSION</c>、真的会自增、而驱动读不回值
/// （<c>XuguConcurrency</c> 第一条那批 RV-/A- 编号，v0.32 第三笔【实测否定】）。本家问不出这一层，
/// 原因是更前面的一颗：<b>这一族的型名压根不参与存储</b>——七颗随手写的型名一次建表，目录原样回读它们
/// （<c>DATETIME ∶ BOOLEAN ∶ DECIMAL(18,4) ∶ VARCHAR(10) ∶ NVARCHAR(10) ∶ BLOB ∶ UUID</c>【实测·B1/C2】），
/// 而值往库里落成什么形状由 CLR 值型决定、驱动不看 <c>DbType</c> 与 <c>Size</c>【实测·500/500b/501】。
/// 于是在本家写 <c>[Column(TypeName = "rowversion")]</c> 得到的是一列普通列：它不会自增，
/// 因为"由库给值"在这一族只有两个入口——列上的 <c>AUTOINCREMENT</c>（只作用于 rowid 那一格
/// 【实测·2802 ∶ 2810】）与触发器（本仓库没有那种形状，而本次四十二轮一条都没量过）。
/// <b>这一条是【推断】，但它是由两颗实测撑着的推断</b>，与"文档没列它所以没有"那种按缺席读不同档：
/// 那六家问的是"库里有没有那一型"，本家这一问在库里<em>没有对应的对象</em>。
/// 所以本家<em>不</em>像 <c>XuguCodeFirst</c> 那样为那颗型名立一道拒收闸门：这里没有"DDL 绿而读即红"的
/// 形状可挡，写 <c>rowversion</c> 的结果是一列普通文本/整数列，它会<em>诚实地</em>不参与递增，
/// 而"看起来在用并发列其实没有"那一格归 §14 的"人写的型名 provider 不校验"那条通例。</para>
///
/// <para><b>【第二条：比较式的两态在本家是数出来的，含"版本列为空"那一档】</b>
/// <c>BuildPredicate</c> 发的那一句在活库上按命中与不命中各跑一次：<c>affrows = 1</c> ∶ <c>affrows = 0</c>
/// 【实测·3302 ∶ 3303】，整型版本列同样两颗【实测·3308 ∶ 3309】。<b>更要紧的是空值那一档的两颗</b>：
/// 版本列本身为 <c>NULL</c> 时，<c>"T" = @p</c> 恒不命中（<c>affrows = 0</c>【实测·3306】）而
/// <c>"T" IS NULL</c> 命中（<c>affrows = 1</c>【实测·3307】）—— 这正是内核必须在发射之前把空值折成
/// <c>IS NULL</c> 的那一颗理由（§5.8），本家它是数出来的而不是推出来的：<b>不折的话，那一行每次保存都成一次"冲突"，
/// 而冲突的处置路径是回滚与重读，不是丢写</b>（这一格的后果方向记在下面第四段，与 MySQL 血统那颗反一反）。
/// 并发谓词与本家的 <c>RETURNING</c> 叠在同一句上也走得通（<c>UPDATE … WHERE 版本 = @p RETURNING "V"</c>
/// 交回新值那一行【实测·3310】），而"回填不改变计数"那一格另有读数（<c>SqliteKeys</c> 类注释第二段，
/// 【实测·340/341】）。本家是有活库的第三家（§9.5），这两对是它在并发这一格上的第一对编号。</para>
///
/// <para><b>【第三条：时间戳在本家是文本，于是"同一条版本"要文本相同才成立】</b>
/// <c>DateTime</c> 交给驱动写它自己的 ISO 文本（<c>SqliteValues</c> 第三条，100 纳秒保得住【实测·318/319】），
/// 而两颗对照把这一档的边界钉住了：<b>库里那一格是我们自己写的话，参数打得中</b>
/// （<c>'2026-09-26 10:00:00'</c> 与一颗零尾数的 <c>DateTime</c> 参数 ⇒ <c>affrows = 1</c>【实测·3304】——
/// 这一颗顺带量出驱动在无尾数时<em>不写</em>那颗小数点），
/// <b>库里那一格是别的写入方留的话，打不中</b>（同一颗参数打在 <c>'…10:00:00.000'</c> 上 ⇒
/// <c>affrows = 0</c>【实测·3305】）。所以本家的并发列有一个另六家没有的形状：
/// <b>它比较的不是那个时刻，而是那个时刻的写法</b>，而写法在这一族没有规范化的落处
/// （<c>Stored()</c> 那个规整器只作用于我们自己发出去的文本，管不到库里已有的行）。
/// 后果方向要说清：它是<em>假冲突</em>（每次保存都撞、永不成功写入那一行），不是丢写，
/// 与 <c>MySqlChangeGuard</c> 那颗 CI 排序规则的方向相反、与 <c>XuguChangeGuard</c> 那颗去尾空格同边。
/// <b>整格记 §14，重开触发写具体：任何"与非本仓库写入方共享一张表"的场景第一天就会撞它。</b></para>
///
/// <para><b>【第四条：本家的"冲突"发生在没有锁的库上，这一句要说得很窄】</b>六家服务器型的库里，
/// 乐观并发的对位是悲观锁（<c>SELECT … FOR UPDATE</c> 那一族），而本家那颗语句是语法错
/// 【实测·第 2 轮 103 ∶ 2164 同一句 <c>near "UPDATE": syntax error</c>】—— 这一族压根没有行级锁那一层。
/// 现场读到的两颗相关设置是 <c>journal_mode = delete</c> 与 <c>busy_timeout = 0</c>【实测·第 2 轮 118/118b】，
/// 也就是"冲突"在这里的真实形状是<em>两个写者撞在同一颗文件上时立刻抛错</em>而不是排队。
/// <b>但这一句只到"两颗设置读到了"为止</b>：本仓库的探针是单连接的，两个连接互相撞出的那颗
/// <c>SQLITE_BUSY</c> 本次<em>没有量过</em>，所以"退避策略该配在哪一层"（连接串那颗
/// <c>Default Timeout</c>、还是 <c>PRAGMA busy_timeout</c>、还是 §9.5 那条"生产走脚本"的路子）
/// 整格记 §14，并明确写在本笔的实测清单里（任务 #21：两连接对撞）。</para>
///
/// <para>今天这一档的实际后果与另六家同形：只写 DDL 而不给那一列赋值的话，保存永远成功而冲突永远不发生。</para>
public sealed class SqliteConcurrency : IConcurrencyStrategy
{
    public ConcurrencyMode Mode => ConcurrencyMode.TimestampColumn;

    /// <summary>与另六家同一个调用点（发射之前合取进 AST 的 <c>WHERE</c>，§10.1）。
    /// 空值那一档由调用方先折成 <c>IS NULL</c> 再下来：<c>"col" = :p</c> 在 NULL 上恒为 UNKNOWN，
    /// 本家这一句的两侧都有编号（3306 ∶ 3307 那一对，见类注释第二段）。
    /// <b>本家在这一槽上比另六家多一处要小心的地方</b>：版本列是文本时，比较的是写法而不是时刻
    /// （类注释第三段，3304 ∶ 3305 那一对），所以"这一列能不能当版本列"在本家取决于<b>它是否只由本仓库写过</b>。</summary>
    public SqlExpression BuildPredicate(SqlExpression target, ColumnContract column, object? originalValue)
        => new SqlBinaryExpression(BinaryOperator.Equal, target, new ParameterValue(originalValue, column));
}
