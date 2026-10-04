using FastSql.Metadata;
using FastSql.Sql;
using FastSql.Sql.Ast;

namespace FastSql.Providers.Xugu;

/// <summary>虚谷的 SQL 文本层（§9.4，第五家）。<b>它没有新增第四套分页实现</b>——§9.4 那句"套数没变、
/// 每套几家变了"到这一家仍然成立，三套里的第一套从两家变三家；新增的是那一套<em>内部</em>的第二颗哨兵
/// （见 <see cref="NoLimitSentinel"/>）。与已落地四族的分岔逐条对照，每一条都是一个覆写点或一处<em>刻意不覆写</em>，出处按
/// 【文档】/【产物】/【推断】/【实测】四档标（前三档的判据见 <see cref="XuguExpressions"/> 类注释第二段，
/// 第四档是 v0.32 第二格新立的那一档：同一台活库上问出来的那一句读数，覆盖面逐颗记 §13.5）：
/// <list type="bullet">
/// <item><b>分页是句尾 <c>LIMIT n OFFSET m</c>，不要求 <c>ORDER BY</c></b>：<see cref="PagingRequiresOrdering"/>
/// 保持 false（与金仓、MySQL 血统同档）。真正属于这一家的是<b>另一半</b>：<c>OFFSET</c> 单独出现<em>不合法</em>
/// 【文档】，所以这里也补"无上限"哨兵 —— 而这一家能补的那一颗数与 MySQL 血统那颗不是一回事，
/// 见 <see cref="NoLimitSentinel"/>。顺带记两枚产物指纹：同一批产物里这一家<em>也</em>认 <c>SELECT TOP n</c>，
/// 而它的逗号形是 <c>LIMIT 偏移, 条数</c>（顺序与阅读方向相反）—— 两支都没取，取的是标准两子句那一形
/// <b>【实测，v0.32 第二格改判；原判【推断】】</b>：<c>obj/logs/v32-recon-survey-2.log</c> 的 S-10 交出
/// <c>SELECT 1 LIMIT 1 OFFSET 0</c> 一行（<em>不带 <c>ORDER BY</c> 的两子句形在真库上收</em>，所以
/// <see cref="PagingRequiresOrdering"/> 保持 false 也有了一格真数），S-11 交出 <c>SELECT TOP 1</c> 同样一行
/// （两支都能跑，取哪一支仍是取向而不是能力），而 <c>obj/logs/v32-recon-funcs-14-plain.log</c> 的 F-46/F-46b
/// 把<em>另一支</em>问穿了：T-SQL 那句 <c>OFFSET 0 ROWS FETCH FIRST 3 ROWS ONLY</c> 在这一家交
/// <c>[E19260 L1 C85] syntax error, unexpected OFFSET</c>，同一张表换成 <c>LIMIT 3 OFFSET 0</c> 交出三行
/// —— 本仓库读侧发的正是后一形（集成层 <c>MaterializationAllocationTests</c> 那句裸 SQL 已在 pass 13 里绿）。</item>
/// <item><b>表别名没有 <c>AS</c></b>（<see cref="SourceAliasKeyword"/> 落成一颗空格）【产物】：
/// 两份产物里带别名的表源<em>都没有</em>写过 <c>AS</c>（它们的 <c>MERGE INTO t1 USING (…) t2 ON (…)</c> 就是形状本身）。
/// 这一格与 Oracle 血统逐字同形，而它是"这一家像 Oracle"的第三枚指纹（前两枚是 <c>DUAL</c> 与 <c>SYSDATE</c>）。</item>
/// <item><b>写语句的目标不起别名</b>（<see cref="WriteTargetAlias"/> 与 <see cref="EmitUpdateTarget"/> 成对覆写）
/// 【产物】：那份适配器的更新文本是 <c>UPDATE 表 SET … WHERE …</c>，目标位只有一张表、别名那颗参数无处安放。
/// <b>这一档的风险是不对称的，要写清</b>：Oracle 血统那一家"目标不能起别名"是有厂商文档背书的确定事实，
/// 而这里看到的是"两份产物都没写别名"——<em>没写</em>不等于<em>不许</em>。所以这一格取的是"跟产物形状"而不是
/// "跟最保守形状"，理由是：万一这一家其实<em>允许</em>别名，不发别名的代价是零（语句照样跑），
/// 而万一反过来（它不许而我发了别名），代价是每一条更新都红。两个方向不对称，取错不起的那一边。
/// 这一格与 <see cref="EmitJoinedSources"/> 里那一抛同属"未知时的取向"，判据写在 §13.5 同一列。</item>
/// <item><b>没有 <c>UPDATE … FROM</c>，所以 <see cref="SupportsJoinedWrites"/> 取 false</b>：
/// §12.5 的合并档在这一族整桶退回一句一实体。这一档从"两家"变成 <b>三家</b>（3∶2），而三家的理由<em>三条各不同</em>：
/// Oracle 血统是"目标不能起别名 ⇒ 带不进别的表"，MySQL 血统是"目标能起别名但没有那个子句"，
/// 这一家是两样都观察到而没有任何一样被证。第三道闸门的加入把 §12.5 那句"不该写成 <c>if (是达梦)</c>"
/// 从论证变成了事实分布。</item>
/// <item><b>驱动不发保存点，所以 <see cref="SupportsDriverSavepoints"/> 取 false，本家改用文本三句</b>
/// （<see cref="EmitSavepoint"/> ∶ <see cref="EmitRollbackToSavepoint"/> ∶ <see cref="EmitReleaseSavepoint"/>）
/// 【实测·<c>obj/logs/v37-xg-recon-savepoint-1.log</c>】：那一家的 <c>XuguClient.XGTransaction</c> 只覆写了
/// <em>无参</em> <c>Rollback()</c>，带名字的三颗全停在 <c>DbTransaction</c> 基类上，调即
/// <c>NotSupportedException</c>（Q-02x ∶ Q-03 ∶ Q-04），而文本那三句在同一台库上收且语义正确
/// （Q-08 ∶ Q-11 ∶ Q-12 <c>CNT=1</c> ∶ Q-13 <c>ID=1 ∶ TAG=before</c> ∶ Q-14 ∶ Q-15 剩 1 行）。
/// <b>这是本文件里第一颗"驱动层缺能力、由方言层补文本"的开关</b>：前面几颗分岔量的是<em>库</em>认不认，
/// 这一颗量的是<em>那一家的 .NET 驱动</em>覆没覆写 —— 同一个库如果有第二支驱动覆写了这三颗，
/// 翻这一颗就够，三句文本不必改。Q-09 顺手把 T-SQL 那一形问死了（<c>SAVE TRANSACTION</c> 是语法错），
/// 所以这里没有"照 SQL Server 抄"的余地；名字的取法（裸名而不是带引号的大写名）写在
/// <see cref="EmitSavepoint"/> 那一段。</item>
/// <item><b>有 <c>RETURNING</c>，落在句尾</b>（<see cref="DialectBase.ReturningPrecedesValues"/> 保持 false）：
/// 转交给 <see cref="XuguKeys.EmitReturning"/>，与金仓那一族同一个转交形状。这一格是"两家抛 ∶ 三家发"的那一票，
/// 而两派的证据都在同一批产物里 —— 矛盾本身记在 <see cref="XuguKeys"/> 类注释。</item>
/// <item><b>布尔谓词不覆写</b>：取基类的 <c> = 1</c>/<c> = 0</c>，与 T-SQL、Oracle 血统、MySQL 血统三家同形
/// （金仓那一族 <c>IS TRUE</c> 一家）。判据与 <c>MySqlDialect</c> 第五条同一格：这一家的布尔列落成
/// <c>TINYINT</c>（<see cref="XuguTypes"/>），它装得下 <c>2</c>，所以 <c>IS TRUE</c> 与 <c> = 1</c> 不是同一判据，
/// 而 §10.5 那类"可空布尔列上 <c> = 0</c> 匹配不到 NULL"的事故换写法换不掉语义。
/// 恒假/恒真谓词同样不覆写：这一家有布尔字面量吗<b>【实测，v0.32 第二格改判；原判【无出处】】</b> ——
/// <b>有</b>：<c>obj/logs/v32-recon-survey-2.log</c> 的 S-13 <c>SELECT TRUE</c> 交出 <c>True &lt;Boolean&gt;</c>，
/// S-14 <c>CASE WHEN TRUE THEN 'YES' ELSE 'NO' END</c> 交出 <c>"YES"</c>（当值与当谓词两问都绿）。
/// <b>而这一格改判完仍然不覆写，理由跟着换一条</b>：原来是"没出处所以不敢用"，今天是有出处而<em>不用</em> ——
/// 基类那两颗（<c>1 = 0</c> / <c>1 = 1</c>）由<em>四家</em>共用、只有金仓那一家整对改写成 <c>false</c>/<c>true</c>，
/// 所以这一家是那四家里的第四家；上面那颗 <c>TINYINT</c> 的理由管的是同一件事的另一半：换成
/// <c>false</c>/<c>true</c> 会让这一家在这一格上从"跟四家"变成"跟金仓"，而那颗字面量的实测对
/// <em>谓词</em>没有给出任何收益（恒假那一颗在两形下同样不匹配任何行），所以取向不动、出处先补上。
/// <b>而这两颗今天跑得起来的只有一颗</b>（本版复核时才数出来的，不是这一家的分岔而是共用层的一格空白，
/// 五家同岗）：<c>EmptySetPredicate</c> 在 Core 里有 1 处消费者（<c>ExpressionTranslator.cs:456</c>，
/// 空集合 <c>IN ()</c> 特化），<c>AlwaysTruePredicate</c> 的<em>调用点</em>是 0 处
/// （<c>grep -rn AlwaysTruePredicate src/ --include=*.cs | grep -v "///"</c> 三行：接口声明、基类默认、
/// 金仓那一家覆写，没有第四行）—— 那句"<c>IN</c> 全集与 <c>Any()</c> 空集合用"是接口注释里写的<em>用途</em>，
/// 不是已接线的调用。所以"两处同形"里今天只有一处在生产路径上被量过，缺口记 §14。</item>
/// </list>
/// 类是 <c>sealed</c>：§9.1 那份清单里没有第二种挂在这家底下的库种（不像 PG 系有三家、MySQL 系有两家叶子），
/// 所以这一族今天只有这一层，与 <c>FastSql.Providers.SqlServer</c> 同档而不是与 <c>MySqlBase</c> 同档。
/// <b>这一句在本版翻掉了，原文照抄在这里</b>：上一版这里写的是"<em>本类的产出文本没有在一台真库上跑过</em>
/// —— 授权的实例里没有一家是虚谷，见 §13.5 的出处台账"。前半句当时是真的，后半句在 v0.31 就不真了
/// （§13.17 添了第三台授权实例，正是一台虚谷），而它一直没人翻，是因为那一版只有解析与连接两级证人。
/// v0.32 第二格把它翻成了能判的样子：集成层开了一条虚谷通路（<c>tests/FastSql.Tests.Integration/Database.cs</c>
/// 的家族缝），95 条用例带着<em>这一层产出的文本</em>在那台实例上跑，黄金样本 <c>golden/xugu/</c> 那六份
/// 也由探针逐句执行过 —— 逐颗账（几条绿、几条因何跳、六份配了几次对）记 §13.5 与 §14 的本版那一段。
/// <b>而"跑过"的边界要说死，否则这一句会被读成"已适配"</b>：它成立的是"本层产出的文本在真库上执行过"，
/// <em>不</em>成立的是"本族每一条文本都执行过"（键回填那一格今天还红着，只是被按名字跳掉了），
/// 也不覆盖"现场那颗驱动是否交付入库的那一颗"—— 三句一起才是这一家的现状。
/// 与另四家不同的是：这一族的"不认 X"第一次可以从<em>已发布代码</em>里读，而不是只能从手册里读。</summary>
public sealed class XuguDialect(XuguTypes types, XuguKeys keys, XuguValues values, XuguExpressions expressions)
    : DialectBase(IdentifierPolicy.Xugu, values, expressions)
{
    /// <summary><c>OFFSET</c> 单独出现时补的"无上限"哨兵：<b>这一家的 <c>LIMIT</c> 有上界</b>
    /// （当前版本 <c>LIMIT</c> 最大支持到 2147483647，超过就报错【文档】），所以这里补的是<em>那一颗上界本身</em>
    /// 而不是一个够大的数。它与 <c>MySqlDialect</c> 那颗 <c>18446744073709551615</c>（2^64−1，无符号整型的顶）
    /// <b>同形不同义</b>：那颗是"这一型的最大值，所以它表示没有上限"，这一颗是"厂商写死的天花板"，
    /// 于是四家里的第五家在这一槽上第一次有了 <c>2 补哨兵 ∶ 3 不补</c> 的分布而两颗哨兵的出处<em>不是一类</em>。
    /// <b>这一格是交账前改判的一格</b>：上一版这里<em>抛</em>，理由是"没有任何一份产物发出过不带 <c>LIMIT</c> 的
    /// <c>OFFSET</c>，所以也没有可抄的哨兵"——那句在"没有可抄的"上是对的，在"没有哨兵"上是错的，
    /// 因为厂商文档直接给了 <c>LIMIT</c> 的取值范围，而"语法要求 <c>LIMIT</c> 必须在场"与"<c>LIMIT</c> 能吃多大"
    /// 是两问，前一问产物已经答过（两支都两数齐全）。改判的依据、被推翻的原话和这次改判本身都记 §13.5。
    /// 与 <c>MySqlDialect</c> 同一档纪律：<b>写成文本而不是参数</b>，它不是一格数据而参数预算量的是真值个数。</summary>
    const string NoLimitSentinel = "2147483647";

    public override DbKind Kind { get; } = DbKind.Xugu;

    /// <summary>冒号。这一格让五家的分布变成 <b>2 家 <c>:</c> ∶ 3 家 <c>@</c></b>【推断·取 Oracle 血统那一支】：
    /// 产物里那枚 <c>:XuguParam</c>【产物】是这颗前缀唯一的一处直接证据，而它是<em>参数名</em>而不是
    /// <c>CommandText</c> 里的占位符 —— 两件事在 ADO.NET 里通常一致，但"通常"不是"证据"。
    /// 这一格是 <see cref="XuguConnectorFactory"/> 那句"驱动从未解析成功过"<em>盖得最薄</em>的地方
    /// （本条初稿写作"唯一盖不到的地方"，交账时按同一批【产物】推翻：同一份产物还为 <c>RETURNING</c>、
    /// <c>IDENTITY(1,1)</c>、<c>CHAR(36)</c> 与目录视图那几颗名字各签过一次，见 <c>XuguKeys</c>
    /// 与 <c>XuguTypes</c> 的类注释）。前缀错了会红在<em>每一条</em>语句上，
    /// 是这份文件里最便宜就能证伪的一格。</summary>
    public override char ParameterPrefix => ':';

    public override string GetStoreType(ColumnContract column) => types.GetStoreType(column);

    /// <summary>取 false 的判据见类注释第四条。<b>这颗开关不是错误豁免</b>：共用层据此整批退回一句一实体，
    /// 而 <see cref="EmitJoinedSources"/> 仍然抛 —— 走到那里说明有人绕过了开关，那种时候宁可红。</summary>
    public override bool SupportsJoinedWrites => false;

    /// <summary>取 false 的实测出处（三颗方法停在基类 ∶ 一句文本都能收）写在类注释第四条与
    /// <see cref="ISqlDialect.SupportsDriverSavepoints"/>。执行层据此改走文本那条路，
    /// 嵌套事务的<em>语义</em>与另四家逐字相同（§6 (1) 不改判），动的只有"这句话由谁发"。</summary>
    public override bool SupportsDriverSavepoints => false;

    /// <summary><c>SAVEPOINT 名字</c>。名字<b>按探针证过的那一形原样、不带引号</b>发出（Q-08 发的是裸名
    /// <c>probe_sp</c>），这里<em>刻意不走</em> <see cref="DialectBase.EmitIdentifier"/>：
    /// 本家的 <see cref="IdentifierPolicy.Xugu"/> 是"区分大小写 ⇒ 每个标识符都加引号 ⇒ 引号内折成大写"，
    /// 一走它就得 <c>"FASTSQL_SP_0"</c> —— 那一形<em>没有</em>活库见证，而"没有实测就不改默认"是本仓库的判据。
    /// 裸名为什么在这一家也是安全的：<see cref="IdentifierPolicy.FoldToUpperCase"/> 说的是"不带引号的名字
    /// 进目录时已经被折成大写"，而三口出口读的是<em>同一个</em>生成名，于是 <c>SAVEPOINT fastsql_sp_0</c> 与
    /// <c>ROLLBACK TO SAVEPOINT fastsql_sp_0</c> 折到同一个键上 —— 折叠这件事只要三口一致就不是风险。
    /// <b>这个一致性靠的是"三口共用一个名字来源"，不是靠引号兜着</b>：名字来自
    /// <c>AdoExecutor.BeginTransaction</c> 里那一颗计数器，形状 <c>fastsql_sp_&lt;n&gt;</c>（字母 ∶ 数字 ∶ 下划线，
    /// 全小写，<em>不是用户输入</em>），所以这里既没有保留字风险也没有注入面 —— 一旦有人把用户可控的字符串
    /// 接成保存点名，这两句"不走 EmitIdentifier"立刻变成两处洞。</summary>
    public override void EmitSavepoint(SqlWriter writer, string savepointName)
        => writer.Append("SAVEPOINT ").Append(savepointName);

    /// <summary><c>ROLLBACK TO SAVEPOINT 名字</c>：内层回滚只撤销内层（Q-11 ∶ Q-12 ∶ Q-13 三句量的就是这一条）。
    /// 名字取法同上一口。本家没有 T-SQL 那一形（Q-09 已证伪），也没有"回滚整条事务"的捷径 ——
    /// 那句在这里不是语法问题而是语义问题，写在 <c>ISqlDialect.SupportsDriverSavepoints</c> 末段。</summary>
    public override void EmitRollbackToSavepoint(SqlWriter writer, string savepointName)
        => writer.Append("ROLLBACK TO SAVEPOINT ").Append(savepointName);

    /// <summary><c>RELEASE SAVEPOINT 名字</c>：内层提交把改动并入外层，外层事务继续（Q-14，其后 Q-15 数出 1 行）。
    /// <b>这一口不许省</b>：某些库的释放是纯装饰（PG 系那句 <c>RELEASE</c> 什么都不做），而这里它是被实测点过的一句；
    /// 就算它在这一家同样是装饰，缺了它红点会落在<em>内层提交</em>而不是创建那一句上，难查一档。</summary>
    public override void EmitReleaseSavepoint(SqlWriter writer, string savepointName)
        => writer.Append("RELEASE SAVEPOINT ").Append(savepointName);

    /// <summary>目标不起别名，所以它在 <c>SET</c> 与 <c>WHERE</c> 里<em>用自己的表名</em>引用自己
    /// （与 <c>OracleDialect</c> 同一形状、同一句"刻意不带模式前缀"：目标一旦限定到模式，
    /// 引用侧就只认那个不带模式的名字）。产物那一形 <c>UPDATE 表 SET … WHERE 列 = …</c> 里连引用前缀都没有，
    /// 而共用层发的是带前缀的列引用 —— 前缀取表名是这一档唯一与那一份产物不冲突的填法。</summary>
    public override string WriteTargetAlias(TableInfo table) => table.Name;

    protected override string SourceAliasKeyword => " ";

    /// <summary>与上面那颗 <see cref="WriteTargetAlias"/> <em>成对</em>（§4 那句话在两族上各落地一次，
    /// 这里是第二次）。基类的默认出口是 <c>UPDATE 表 AS 别名</c>，而这一族的形状按产物是
    /// <c>UPDATE 表 SET …</c>：目标位只有一张表，别名那颗参数无处安放，写出来会得到
    /// <c>UPDATE "FASTSQLORDER" AS "FASTSQLORDER"</c> 那种"表给自己起一个与自己同名的别名"的形状 ——
    /// 它在两家产物里都没出现过，而在 Oracle 血统那一家 <c>AS</c> 直接是语法错。
    /// <b>这一格是测试切片抓出来的，不是设计阶段想到的</b>：类注释第三条早就写着"成对覆写"，
    /// 而 provider 层只覆写了一半，直到 <c>XuguDialectTests</c> 把这一族的 UPDATE 与达梦那一份逐字对读才显形。
    /// <see cref="DialectBase.EmitDeleteTarget"/> 默认转给这一口，所以 <c>DELETE</c> 那半边跟着一起改对
    /// （两半同时被断言的纪律与 <c>OracleDialect</c> 那一份同一条）。</summary>
    protected override void EmitUpdateTarget(SqlWriter writer, TableRef table) => EmitTableName(writer, table.Table);

    /// <summary>句尾两子句，各自可缺省，但<b>只有 <c>OFFSET</c> 时缺省 <c>LIMIT</c> 不合法</b>【文档】
    /// （金仓那一族恰好相反：<c>OFFSET</c> 可以单独出现），所以那一支走 <see cref="NoLimitSentinel"/>。
    /// 逗号那一形（<c>LIMIT 偏移, 条数</c>）把偏移放在前面，与三家的阅读顺序都反，所以取标准形 ——
    /// 它同时让这一格与金仓、MySQL 血统两家的<em>文本</em>逐字同形（少一处假分岔），
    /// 而"同形"在上一段那颗哨兵上不再成立。</summary>
    protected override void EmitPaging(SqlWriter writer, SelectStatement select)
    {
        if (select.Limit is null && select.Offset is null)
            return;

        writer.Append(" LIMIT ");

        if (select.Limit is { } limit)
            EmitExpression(limit, writer);
        else
            writer.Append(NoLimitSentinel);

        if (select.Offset is { } offset)
        {
            writer.Append(" OFFSET ");
            EmitExpression(offset, writer);
        }
    }

    /// <summary>转交给 <see cref="XuguKeys.EmitReturning"/>：与金仓那一族同一个转交形状（那一家的理由写在
    /// <c>PostgreSqlDialect.EmitReturning</c>）—— 两家共用"INSERT 与 UPDATE 两档共用一份文案"这一条纪律，
    /// 而这一家的两处都能真的发出文本（那两家到不了正常返回，因为那里的口径是抛）。</summary>
    protected override void EmitReturning(SqlWriter writer, IReadOnlyList<ColumnContract> returning) => keys.EmitReturning(writer, returning);

    /// <summary>"任何表源都不接受"的<em>第三份</em>（前两份在 <c>OracleDialect</c> 与 <c>MySqlDialect</c>），
    /// 三家的<em>后果</em>同形而<b>理由各不同一条</b>（类注释第四条）。这一份的措辞刻意与那两份都不同：
    /// 那两家说的是"这一家没有那个子句"，这一家<em>不能</em>那么说 —— 产物里出现过
    /// <c>MERGE INTO … USING (…) …</c>，也就是说"把行集带进写语句"这件事在这一家有语法，
    /// 缺的是共用层那一个形状（<c>(VALUES …) AS t(cols)</c> 的带列名清单行集）在它上面成不成立
    /// <b>【实测否定，v0.32 第二格改判；原判【无出处】】</b>：<c>obj/logs/v32-recon-funcs-14-plain.log</c>
    /// 的 F-20 把 <c>SELECT * FROM (VALUES (1,'a'),(2,'b')) AS t (id, nm)</c> 发出去，交
    /// <c>[E19260 L1 C16] syntax error, unexpected VALUES</c> —— 那颗行集形状在这一家<em>没有</em>语法，
    /// 而同一份日志的 F-19 交出反方向的半格：<c>MERGE INTO … USING (SELECT 1 AS "ID") S ON (…)
    /// WHEN MATCHED THEN UPDATE SET …</c> 执行通过（<c>AFFECTED=0</c>，那条 ON 没配上任何行，
    /// 但语法与<em>执行</em>两级都过了）。两合起来读：<b>这一家能带行集进写语句，只是只能从
    /// <c>USING (SELECT …)</c> 那一格带</b>，而 §12.5 合并档发的是 <c>(VALUES …)</c> 那一形 ——
    /// 所以"没接"这一条今天有了实测背书的理由，而它将来接的是 <c>MERGE INTO</c> 而不是
    /// <c>UPDATE … FROM</c>（把那颗行集翻成 <c>USING (SELECT … UNION ALL …)</c> 是另一版的活，记 §14）。
    /// 所以这条消息说的是"没接"而不是"没有"。</summary>
    protected override void EmitJoinedSources(SqlWriter writer, string keyword, IReadOnlyList<JoinClause> joins, out SqlExpression? folded)
    {
        folded = null;

        if (joins.Count == 0)
            return;

        throw new FastSqlException(
            $"{Kind} 的写语句不接受目标表之外的表源（{keyword.Trim()} 那一段有 {joins.Count} 个）：这一家有 MERGE INTO … USING (…) ON (…) 的语法"
            + "（已发布产物里出现过），但 §12.5 的合并档发的是 (VALUES …) AS t(cols) 那种带列名清单的行集，"
            + "而那一形在这一家没有语法（v0.32 在活库上正问过：SELECT * FROM (VALUES …) AS t(cols) 交 syntax error, unexpected VALUES），所以今天不接。"
            + "整批退回一句一实体由 SupportsJoinedWrites 决定（语义与合并档等价，§12.4 第 2 项），走到这里说明那颗开关被绕过了 —— "
            + "宁可报错也不能悄悄丢掉表源，那等于丢掉连接条件（§1.2 原则 4）。");
    }
}
