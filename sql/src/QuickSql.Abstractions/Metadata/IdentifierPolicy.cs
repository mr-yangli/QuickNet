using System.Text;

namespace QuickSql.Metadata;

/// <summary>标识符策略（§9.2）。大小写与引号是国产化联调第一天就要定的事，
/// 因为它同时决定 DDL、查询、以及 <c>doctor</c> 的比对基准。</summary>
/// <param name="QuoteAlways">每个标识符都加引号。SQL Server 取 true：方括号零成本，还能顺带避开
/// <c>[User]</c>、<c>[Order]</c> 这类保留字——它们恰恰是业务里最常见的表名。</param>
/// <param name="FoldToUpperCase">发射之前先把名字折成大写。<b>v0.23 起只有 Oracle 血统取 true</b>：那一家不带引号
/// 的 <c>QuickSqlOrder</c> 在库里存的是 <c>QUICKSQLORDER</c>，而我们发出去的是带引号的 <c>"QuickSqlOrder"</c> ——
/// 两者指的不是同一张表（§9.2 点名的"查询 100% 报表不存在"）。折叠发生在
/// <see cref="AppendQuoted"/> 与 <see cref="AppendPlain"/> 这两支唯一的出口上，所以建表与查询不可能分岔。
/// 它的对称件"折向小写"没有库种需要：PG 那一系折小写，而那一系的现实做法是全部加引号、不折。
/// <b>v0.31 起这一格有了第二家</b>（<see cref="Xugu"/>），而上一条"没有库种需要"仍然是对的 —— 新增的是折向
/// <em>大写</em>的第二家，不是折向小写的第一家。两家的来路不同一条：Oracle 血统是靠厂商文档，虚谷是靠
/// 已发布产物里那句 <c>INDEX_NAME=UPPER('{0}')</c>（不加引号的标识符进目录时已经被折成大写，所以要按大写查）。
/// 判据与"取值形状"逐字同形，因而 §9.2 那条"一条折向大写的独立血统"今天读作两条。
/// <b>v0.37 第一笔给那条对称件添了<em>库种</em>而不添<em>取值</em></b>：<see cref="Gbase8s"/> 那一族的库
/// 确实折小写，而这一颗字段<em>仍然</em>没有取那一支——它的假值说的是"发射层不折"，而本家靠恒加引号对齐
/// （<c>FoldToUpperCase</c> 因此是那颗字段第一次"为假而库其实在折"的）。于是上面那句"没有库种需要"
/// 从今天起要读成两半：<b>库种有需要，字段没有那一支</b>，缺口记 §14（它要三值才表达得下）。
/// 统计口径同一批改：六颗预设里这颗字段取真的是 2 家（Oracle 血统、虚谷）、取假的是 4 家
/// （T-SQL、PG 系、MySQL 血统、本家），而本家那一票与 PG 系那两家同值不同解。</param>
public sealed record IdentifierPolicy(
    char OpenQuote,
    char CloseQuote,
    bool CaseSensitive,
    bool FoldToUpperCase,
    bool QuoteWhenMixedCase,
    bool QuoteAlways = false)
{
    public static IdentifierPolicy SqlServer { get; } = new('[', ']', CaseSensitive: false, FoldToUpperCase: false, QuoteWhenMixedCase: false, QuoteAlways: true);

    public static IdentifierPolicy PostgreSQL { get; } = new('"', '"', CaseSensitive: true, FoldToUpperCase: false, QuoteWhenMixedCase: true);

    public static IdentifierPolicy OracleFamily { get; } = new('"', '"', CaseSensitive: true, FoldToUpperCase: true, QuoteWhenMixedCase: true);

    /// <summary>MySQL 血统的预设：<b>反引号、一律加引号、不折大小写</b>。取 <see cref="QuoteAlways"/>
    /// 的理由与 <see cref="SqlServer"/> 同一格而更强：这一家的保留字表是<em>落地前四家</em>里最长的（<c>ORDER</c>、<c>GROUP</c>、
    /// <c>RANK</c>、<c>ROWS</c>、<c>STATUS</c> 都在线上），而业务表名恰恰就叫前两个。
    /// <b>那句"前四家"是 v0.30 的口径，而"最长"这一判据在五家里没有重数过</b>：第五家的完整保留字清单
    /// 本次两档出处都没读到【无出处】，所以这句"最长"没有替它签字（同一句式在 <c>MySqlExpressions</c>
    /// 的舍入模式那一格也用过一次，两处一起留给 <c>doctor</c>）。
    /// <b>不折大小写</b>是因为它的那一颗敏感开关不在库里而在<em>文件系统</em>上（<c>lower_case_table_names</c>
    /// 建库时定死，Linux 默认区分、Windows 默认不区分），所以这里既不能赌"区分"也不能赌"不区分"，
    /// 只能把契约里写的那个字形原样带引号发出去 —— 与 <see cref="PostgreSQL"/> 同一个结论，来路完全不同。
    /// 另有一颗模式要一并记下：那一家还有 <c>ANSI_QUOTES</c>，它会把<b>双引号</b>变成字符串的字面量引号，
    /// 而反引号在两种模式下都是标识符引号。这是"为什么这一家与 PG 系在同一个 SQL 标准档位上仍然分道"的全部理由。</summary>
    public static IdentifierPolicy MySql { get; } = new('`', '`', CaseSensitive: false, FoldToUpperCase: false, QuoteWhenMixedCase: false, QuoteAlways: true);

    /// <summary>虚谷的预设：<b>双引号、区分大小写、折向大写、混合大小写才加引号</b> —— 与 <see cref="OracleFamily"/>
    /// <em>逐字同形</em>，而四颗取值没有一颗的来路与它相同一条，所以这一颗不复用而另立（§9.2 v0.31 那一格）。
    /// 【v0.31 复核】那句"没有一颗"要按<em>前三颗</em>读：双引号与 <c>PostgreSQL</c> 同字、区分大小写与它同结论、
    /// 折大写与 <c>OracleFamily</c> 同形状，三颗各自的<em>依据</em>确实没有一颗复用得过来（逐颗见下面三条）；
    /// 而第四颗 <see cref="QuoteWhenMixedCase"/> 恰恰是<b>反例</b> —— 它没有自己的依据，写的就是"跟着
    /// <c>OracleFamily</c> 的形状抄"（下面末段），所以它同一条来路复用自那一族。四颗里三颗自立、一颗照抄。
    /// <list type="bullet">
    /// <item><b>折向大写</b>有两档出处，而<em>两档说的都是条件句</em>：产物那一侧读目录时问的是
    /// <c>... WHERE INDEX_NAME=UPPER('...')</c>，那句 <c>UPPER</c> 只有在"不加引号建出来的名字在目录里
    /// 已经是大写"时才有意义【产物】；文档那一侧把这颗折叠交给了 <c>COMPATIBLE_MODE</c> ——
    /// 它取 NONE 或 ORACLE 时折大写、取 MYSQL 时不处理、取 POSTGRESQL 时折小写【文档】。
    /// 于是这一格的真状态是<b>"默认那一档折大写，而它是建库时可以改掉的参数"</b>：这里取的是
    /// 默认档，理由是它与产物实证一致、也与"折向大写的另一族"逐字同形，代价（换成 POSTGRESQL 那一档
    /// 时整套折叠判断反号）记进 §14 那一行"会话/兼容参数改变方言假设"——这一家是<b>第四个实例</b>
    /// （前三颗：金仓的兼容模式、MySQL 的 <c>lower_case_table_names</c>、虚谷自己的 <c>CHAR_SET</c>，
    /// 后者记在 <c>XuguTypes</c>）。<b>读侧要同一颗折叠</b>：<see cref="XuguSchemaReader"/> 因此绑的
    /// 是折过大写的名字，与 <c>OracleSchemaReader</c> 同一条纪律，漏掉它的后果也同 §9.2 那一格，
    /// 只是发生在 <c>doctor</c> 侧。</item>
    /// <item><b>双引号而不是反引号</b>是<em>取向</em>而不是"只有这一支合法"：文档那一页把反引号也列为
    /// 合法的标识符引号【文档】，而同一批产物里虚谷一侧的反引号只出现在建表语句<em>整段</em>的外壳上，
    /// 列名与索引名的引用形状用的是双引号【产物】。两档合起来读作：双引号是那两份实现<em>实际写出来</em>
    /// 的形状，反引号是"合法但今天没有任何一份产物这样用"的另一支 —— 取实际形状，而这一格是五家里
    /// 第一次"两档出处各支持一支、按产物的用法而不是按语法的许可选型"。</item>
    /// <item><b>区分大小写</b>取 true 与 <see cref="PostgreSQL"/> 同一个结论：既然库会把不带引号的名字折叠，
    /// 带引号的那一支就必须被当作"照字面"处理，否则 <c>"QuickSqlOrder"</c> 与 <c>"QUICKSQLORDER"</c> 在发射层就分不出谁是谁。
    /// 这颗为真还顺带让 <see cref="QuoteWhenMixedCase"/> 变成<em>惰性位</em>（<see cref="NeedsQuote"/> 在那儿短路），
    /// 于是下面那句"唯一没有独立出处的取值"今天不承担任何行为。</item>
    /// </list>
    /// 三颗里唯一<em>没有</em>独立出处的是 <see cref="QuoteWhenMixedCase"/>：它跟着 <see cref="OracleFamily"/> 的形状
    /// 抄，理由是"折向大写的库对大小写的处理必然成对出现"，而这一格今天标为推断（§13.5 台账）。
    /// 注意 <c>QuoteAlways: false</c> 并不等于"会走到 <see cref="AppendPlain"/>"：这颗为假而 <c>CaseSensitive</c>
    /// 为真，<see cref="NeedsQuote"/> 仍然对每个标识符都返回真，所以两支出口的分布与 <see cref="OracleFamily"/> 完全一致。</summary>
    public static IdentifierPolicy Xugu { get; } = new('"', '"', CaseSensitive: true, FoldToUpperCase: true, QuoteWhenMixedCase: true);

    /// <summary>Informix 血统（GBase 8s）的预设：双引号、区分大小写、<b>库折向小写而发射层不折</b>
    /// （"不折"由恒加引号保证，见下面倒数第二句）。
    /// 它是本仓库第一颗<em>整条形状与 <see cref="PostgreSQL"/> 逐字相同而出处完全不同</em>的策略
    /// （那一颗的折法是厂商文档写明，这一颗是 <c>SELECT</c> 语法页上"未定界的标识符折成小写"那一句），
    /// 也是 <see cref="FoldToUpperCase"/> 这一格第一次有了"字段为假而库其实在折"的一户
    /// （六颗预设的取值分布从此是 2 真 ∶ 4 假，而本家那一票与 PG 系那两家同值而异解：
    /// 那两家的库里折小写而我们不折，靠加引号对齐；T-SQL 与 MySQL 血统那两家的假说的是"库里压根不折"）。
    /// <b>取向与 PG 系同一条纪律，而这一家的代价更大一格</b>：不带引号建 <c>QuickSqlOrder</c> 在库里存的是
    /// <c>quicksqlorder</c>，而我们发出去的是带引号的 <c>"QuickSqlOrder"</c> —— 两者指的不是同一张表，
    /// 于是"忘加引号"在这一家与在 PG 系同样是 §9.2 那句"联调第一天就要定"的那一类故障。
    /// <see cref="CaseSensitive"/> 为真让 <see cref="NeedsQuote"/> 对每个标识符都返回真，
    /// 所以本条与 <see cref="Xugu"/> 那一条末尾同一句：两支出口（<see cref="AppendQuoted"/> ∶
    /// <see cref="AppendPlain"/>）的分布在这里是"只有前一支"，而 <c>FoldToUpperCase</c> 因此是
    /// <b>一颗永远不会被走到的取值</b> —— 它写成 false 不是"选了不折"，而是"这一家折的方向本来就是小写，
    /// 而这颗字段表达不了那个方向"。这一格的可读性缺口记 §14（那颗布尔要变成三值枚举才表达得下：
    /// "折小写"那一支今天有两位住户，PG 血统与这一族，而前一位一直靠加引号躲着它）。</summary>
    public static IdentifierPolicy Gbase8s { get; } = new('"', '"', CaseSensitive: true, FoldToUpperCase: false, QuoteWhenMixedCase: true);

    /// <summary>SQLite 的预设：<b>双引号、一律加引号、<em>不</em>区分大小写</b>。它是这一份表里第一颗
    /// 「双引号 ∶ 恒加引号 ∶ 不敏感」三者同堂的组合（<see cref="PostgreSQL"/> 那一系双引号而敏感、
    /// <see cref="SqlServer"/> 与 <see cref="MySql"/> 恒加引号而不敏感却各用一副自己的引号字形），
    /// 也因此是本仓库第一颗<b>每一颗取值的来路都是一台活库</b>的策略：七家里另外六家的这一格全部
    /// 出自文档或已发布产物，只有这一家是把候选形状打进去问过（探针 <c>probe10</c> 的 10/11/12/18/19/20/21，
    /// 见 §13.5 那一本新台账）。逐颗读法：
    /// <list type="bullet">
    /// <item><b>双引号</b>【实测】：SQL 标准那一副，与 <see cref="PostgreSQL"/> 同字。<b>方括号也认</b>
    /// （10：{@code [Name]} 与 {@code "Name"} 命中同一列），那是它兼容 TCL/SQLite 历史包袱的一颗礼物，
    /// 而本家不取 —— 取标准字形与取"这一家唯一没有第二候选的字形"在这里恰好同向。</item>
    /// <item><b>不区分大小写</b>【实测】：带引号的 {@code "MIXEDcase"} 能命中不带引号建的 {@code mixedcase}
    /// （12/19），连表身份本身也是不区分大小写的（18：再建一次 {@code mixedcase} 报
    /// "mixedcase already exists"）。于是 <see cref="CaseSensitive"/> 取 false 在这一家说的是<b>库里的事实</b>
    /// 而不是一句"我们不在乎"。<b>更要紧的是这颗在本家第一次不承担行为</b>：
    /// <see cref="NeedsQuote"/> 被 <see cref="QuoteAlways"/> 短路在前面，false 与 true 在这一家
    /// 走的是同一支出口 —— 它是本仓库第一颗"取值有实测出处、而这一格里没有任何一条路径读它"的字段
    /// （<see cref="Xugu"/> 那一条末尾那句"惰性位"到本家才真的成为一句可复述的事实）。</item>
    /// <item><b>恒加引号</b>【实测】，而这一条的理由与另两家反着来：<b>在这一家加引号是<em>降低</em>错误响度</b>。
    /// 不带引号而库里没有的那个标识符会被解析器静默改判：<c>SELECT nosuchcol</c> 报错（J/P 响亮），
    /// 而 {@code SELECT "nosuchcol"} 在投影位置落成一列常量字符串（13）、在 WHERE 里恒假（14）、
    /// 在算术里恒零（15）、在 ORDER BY 里既不报错也不改序（L）。四格都是<b>能绿着错</b>的一类。
    /// 于是这一格与 §9.2 那句"引号是保留字保险"（<see cref="SqlServer"/> 那一条）不冲突而多出一半：
    /// 本家加引号不只为避开 {@code group}／{@code order} 那一类保留字（H/I 过而 J 拒），
    /// 更为把"名字写错了"这件事留在报错而不是留在空结果集。</item>
    /// <item><b>不折叠</b>【实测】：这一家压根不折叠 —— 建表时写的字形就是目录里读回的字形
    /// （22/81/616 那一串读数），既不像 PG 血统折小写、也不像 Oracle 血统折大写。所以
    /// <see cref="FoldToUpperCase"/> 这一颗在七家的分布第一次是 <b>2 真 ∶ 5 假，而假的那五颗分三种说法</b>：
    /// "库里不折"（本家、T-SQL）、"库里折而字段表达不了那个方向"（<see cref="Gbase8s"/>、
    /// <see cref="PostgreSQL"/>）、"另有 MySQL 那一档文件系统参数"（<see cref="MySql"/>）。</item>
    /// </list>
    /// <see cref="QuoteWhenMixedCase"/> 取 false 与 <see cref="SqlServer"/> 同一格（恒加引号短路在它前面），
    /// 所以两支出口（<see cref="AppendQuoted"/> ∶ <see cref="AppendPlain"/>）的分布是"只有前一支"，
    /// 与 <see cref="Xugu"/>、<see cref="Gbase8s"/> 那两条末尾同一句。</summary>
    public static IdentifierPolicy Sqlite { get; } = new('"', '"', CaseSensitive: false, FoldToUpperCase: false, QuoteWhenMixedCase: false, QuoteAlways: true);

    /// <summary>加引号后的标识符。<b>发射层不走这一条</b>：它用 <c>SqlWriter.AppendIdentifier</c> 直接把
    /// 带引号的文本写进缓冲区，因为这条拼接路径每列都要过一次，而一次调用要 new 出两三个中间串
    /// （§12.8 第 1 行那笔"每列约 304 B"的大头就是它们）。两条路径共用 <see cref="NeedsQuote"/> 与
    /// <see cref="AppendQuoted"/>，所以 DDL 与查询文本不可能写出两个不同的标识符 —— <b>折叠也在这一条上</b>，
    /// 于是 §9.2 那句"建出来的表与查的表必须是同一张"在 <see cref="FoldToUpperCase"/> 这一家同样成立。</summary>
    public string Quote(string identifier)
    {
        identifier = Normalize(identifier);

        if (!NeedsQuote(identifier))
            return identifier;

        var builder = new StringBuilder(identifier.Length + 2);
        AppendQuoted(builder, identifier);

        return builder.ToString();
    }

    /// <summary>要不要加引号 —— 判据只有这一份。<c>QuoteAlways</c> 短路在最前，所以 SQL Server
    /// 永远不进 <see cref="IsPlainIdentifier"/> 那一趟扫描。<b>这一条不看 <see cref="FoldToUpperCase"/></b>：
    /// 折叠改的是字形而不是"是不是一个朴素标识符"，两边同判会让每一次取号都多跑一趟大写映射。</summary>
    public bool NeedsQuote(string identifier)
        => identifier.Length > 0 && (QuoteAlways || CaseSensitive || QuoteWhenMixedCase || !IsPlainIdentifier(identifier));

    /// <summary>把带引号的标识符直接落进 <paramref name="builder"/>：除了缓冲区自身的容量，一个字节都不多分配 ——
    /// <b>除了</b> <see cref="FoldToUpperCase"/> 为真的那一家，它必然要先得到一个折叠过的新串。引号内出现右引号
    /// 要翻倍，这是 SQL 标准做法，各家一致。</summary>
    internal void AppendQuoted(StringBuilder builder, string identifier)
    {
        identifier = Normalize(identifier);

        builder.Append(OpenQuote);

        var at = identifier.IndexOf(CloseQuote);

        if (at < 0)
        {
            builder.Append(identifier);
        }
        else
        {
            var start = 0;

            do
            {
                builder.Append(identifier, start, at - start + 1);
                builder.Append(CloseQuote);
                start = at + 1;
            }
            while ((at = identifier.IndexOf(CloseQuote, start)) >= 0);

            builder.Append(identifier, start, identifier.Length - start);
        }

        builder.Append(CloseQuote);
    }

    /// <summary>不加引号的那一支也要折叠：两支共用一个归一口，才不会出现"带引号的查得到、不带引号的查不到"。
    /// 今天没有任何一家走到这一支（五个预设里 <c>QuoteAlways</c> 或 <c>CaseSensitive</c> 至少一颗为真，
    /// v0.31 新增的那一颗是后一种），它是 <see cref="AppendQuoted"/> 的对称件而不是现状。</summary>
    internal void AppendPlain(StringBuilder builder, string identifier) => builder.Append(Normalize(identifier));

    /// <summary>大小写折叠（§9.2）：Oracle 血统不带引号的名字会被库折成<b>大写</b>，而别的库折成小写或不折，
    /// 所以"折叠"这件事的方向也是方言知识。它只有 <c>FoldToUpperCase</c> 这一个取值 —— 折向小写的那一家的
    /// 现实形状是"全部加引号、不折"（<see cref="PostgreSQL"/> 就是那一支），今天没有需要折向小写的库种。</summary>
    string Normalize(string identifier) => FoldToUpperCase ? identifier.ToUpperInvariant() : identifier;

    static bool IsPlainIdentifier(string value)
    {
        if (!char.IsLetter(value[0]) && value[0] != '_')
            return false;

        foreach (var c in value)
        {
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '$')
                return false;
        }

        return true;
    }
}

public enum IndexKind { Unique, NonClustered, Clustered, FullText }

/// <summary>已解析的索引契约。<see cref="ColumnNames"/> 是库里的列名，不是 CLR 成员名——
/// 由解析链从 <c>MemberNames</c> 映射而来，所以它是 <c>required</c>：漏映射就是建出一个不存在的列。</summary>
public sealed record IndexContract(string Name, IndexKind Kind, IReadOnlyList<string> MemberNames, bool Descending = false)
{
    public required string[] ColumnNames { get; init; }
}
