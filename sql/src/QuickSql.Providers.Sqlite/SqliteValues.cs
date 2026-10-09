using System.Data;
using System.Globalization;
using System.Text;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;
using QuickSql.Values;

namespace QuickSql.Providers.Sqlite;

/// <summary>CLR 值 → 参数与字面量（§4 的 IValueBinder，第七家）。<b>这一份是七家里唯一一处"绑定侧
/// 自己就是数据的形状"</b>的实现：另六族的归一活最多改一颗枚举的宽度，本家的 <see cref="Normalize"/>
/// 要决定四型走文本还是整数，而<em>库不会替我们纠正任何一种选错</em>。全部判据来自二十轮探针
/// 【实测】，其中三条是本文件存在的理由：
/// <list type="bullet">
/// <item><b>驱动<em>完全不看</em> <c>DbType</c> 与 <c>Size</c></b>【实测·500/500b/501】：
/// 把 <c>DbType</c> 报成 <c>Int16</c>、<c>Size</c> 报成 1，交进去一颗 <c>long</c> 仍原样落库读回
/// <c>Int64</c>。也就是说 ADO.NET 那两颗"提示"在本家是<em>纯装饰</em>，值往库里落成什么形状由
/// <b>CLR 值型本身</b>决定。于是本家所有真正的定型动作只能在 <see cref="Normalize"/> 里做完，
/// 而 <see cref="DbTypeOf"/> 与 <see cref="SizeOf"/> 是七家里唯一一对"写出来只为让读代码的人看见
/// 我们的意图，而库里没人听"的实现。两颗仍然填，理由在各自方法旁边。</item>
/// <item><b>定点与 64 位无符号必须绑成文本</b>：驱动把 <c>decimal</c> 交出去会<em>自己重写那一串</em>
/// 【实测·63g：<c>SELECT @v</c> 里 <c>12.3400m</c> 得到 <c>12.34</c>】，第二十五轮把同一件事在一颗无
/// affinity 的列上逐颗复点【实测·2500：<c>12.3400</c> → <c>'12.34'</c>、<c>100</c> → <c>'100.0'</c>、
/// 22 位整数那颗<em>逐位保真</em>只多一颗 <c>'.0'</c>】；把 <c>ulong.MaxValue</c> 走 <c>long</c>
/// 【实测·505，2400 的 <c>id 19</c> 又点了一次】出来是 <c>-1</c>。两型改绑不变形文本后逐字可回
/// 【实测·326/333/701/702】。这是"库里没有无损的落处"那一格的绑定侧（<c>SqliteTypes</c> 第三、四条），
/// 两文件读法必须一致：型名 <c>TEXT</c> ∶ 值也 <c>TEXT</c>。
/// <b>本条原来的归因在这一版改口</b>：它写的是"驱动把 <c>decimal</c> 走双精度"，而 2500 那批读数不允许
/// 这句话 —— 真走双精度会得到 <c>1.23E+21</c> 那一形，而这里得到的是逐位的 22 颗数字。所以本文件只说
/// 量到的那一件事：<b>它会重排标度</b>；至于它内部经过哪一环，<em>本次没有读数</em>（那一格连同
/// 早先那颗 505c 的"小数整段没了"一起留给 §14：505 那一轮的脚本没有留底，读数在而输入字形不在，
/// 所以它只能算"还有一条没对上的旧读数"，不能拿来定原因）。结论整颗不动，动的是归因：
/// 让绑定侧<em>不</em>把形状交给驱动的真正理由是 2503 那一颗 —— 同一列上 <c>"v" = 12.34</c> 数到
/// <c>0</c> 而 <c>"v" = '12.3400'</c> 数到 <c>1</c>，两串在本家<em>永不相等</em>，所以
/// "谁去格式化定点"在别家是风格问题、在本家是那一行命中与否。</item>
/// <item><b>时刻与跨度分两条路</b>：<c>DateTime</c> 交给驱动（它写的是空格分隔的 ISO 文本【实测·G2/G3b】
/// 而 100 纳秒那一档是 <c>.4567</c> 那两颗量住的【实测·318/319】，
/// 而 <c>Kind</c> 它丢【实测·320/321】），<c>TimeSpan</c> 与 <c>TimeOnly</c> 绑 <c>Ticks</c>
/// 【实测·605/700 TS】—— 后两型<em>直接交会被写成文本</em> <c>01:30:00</c>【实测·507/508】，
/// 与列声明的 <c>INTEGER</c> 撞在同一格里。</item>
/// </list>
/// <b>而这三条里最贵的一条不在上面，在下面那颗 <see cref="InlineDateTime"/> 里</b>：文本列上的等号比的是
/// <em>字面串</em>，所以内联字面量必须与驱动写入的那一形<em>逐字同形</em>，否则一句 <c>WHERE</c>
/// 静默返回 0 行【实测·329 那颗 <c>COUNT(*) = 0</c> 是"文本与数不等"的最简形状】，
/// 而不是报一个错。六族里"内联什么形状"从来只是缓存问题，本家是第一次它成为正确性问题。
///
/// <para>参数名这一档本家与 SQL Server 同侧：<b>带前缀与不带前缀都收</b>【实测·E1/E2b/O/414】，
/// 所以 <see cref="ISqlDialect.DriverAcceptsPrefixedParameterNames"/> 取 true，而 v0.32 那颗摘前缀的口子
/// 在本家用不上（不是"我们做对了"，是"驱动两侧都认"）。<b>匿名占位符不能用</b>：驱动侧直接拒
/// <c>?</c>【实测·225】，尽管引擎自己认 <c>?1</c>【实测·N】—— 于是"引擎支持而驱动不支持"这一格
/// 在本仓库是第一次有实测（另六族的 <c>ParameterPrefix</c> 全是具名形，没人问过这颗）。
/// <c>null</c> 值必须显式折成 <see cref="DBNull"/>：留 <c>null</c> 的话驱动报
/// "Value must be set"【实测·517】，那是一条与 SQL 无关的驱动内异常，而 <see cref="Normalize"/>
/// 第一支就是为它写的。</para></summary>
public sealed class SqliteValues : IValueBinder
{
    public static SqliteValues Instance { get; } = new();

    /// <summary>三颗（值、<c>DbType</c>、<c>Size</c>）<b>同源于一次归一</b>，这与六家那一句
    /// <c>new(name, Normalize(value), DbTypeOf(value, hint), SizeOf(value, hint), hint)</c> 不同形
    /// —— 那六家把<em>原始</em>值喂给后两颗，本家喂的是归一<em>之后</em>的那一颗。理由是类注释第一条：
    /// 这里 <c>DbType</c> 不参与，所以"值说是文本而 <c>DbType</c> 说是定点"在两处读起来是矛盾的，
    /// 而矛盾会误导下一个改这个文件的人（§13.5 立过的那条"注释与代码不许各说一套"在代码里也要成立）。
    /// 归一之后的形状恰好就是驱动会自己推断出来的那个形状，所以两侧一致是<em>唯一诚实</em>的写法。</summary>
    public AdoParameter CreateParameter(string name, object? value, ColumnContract? hint)
    {
        var bound = Normalize(value);

        return new(name, bound, DbTypeOf(bound, hint), SizeOf(bound, hint), hint);
    }

    /// <summary>见类注释第一、二、三条。逐支：
    /// <list type="bullet">
    /// <item><c>null</c> → <see cref="DBNull"/>：驱动那一侧的硬要求（实测 517）。</item>
    /// <item>枚举 → 底层整数：与六家同规，本家多一层可说的：不折的话驱动按 <c>ToString()</c> 落成文本，
    /// 而列声明是 <c>INTEGER</c>（<c>SqliteTypes</c> 那条"枚举最实的一档"）。</item>
    /// <item><c>decimal</c> → 不变形文本，<b>不抹尾零</b>：<c>decimal</c> 自己保标度
    /// （<c>12.3400m.ToString(Invariant)</c> 就是 <c>12.3400</c>，实测 701 读回 <c>scale=4</c>）。
    /// 这一支是本仓库第一次"由绑定侧决定小数位数在库里活不活得下来"。
    /// <b>第二十五轮把"那一条归一"与"我们这一条"逐字对过</b>：直接把 <c>decimal</c> 交给驱动，它写出的
    /// 也是文本、也是精确值，只是<em>重排了标度</em>【实测·2500：<c>12.3400</c> → <c>'12.34'</c>、
    /// <c>100</c> → <c>'100.0'</c>、<c>1234567890123456789012</c> → 同串加 <c>'.0'</c>】—— 也就是说
    /// "绑文本"这一支<em>不是无损的唯一出口</em>，而是"保真并且保我们自己那一份形状"的出口。
    /// 判据因此落在 2503 那一颗：文本与数在这一族<em>永不相等</em>（<c>"v" = 12.34</c> 得 0 而
    /// <c>"v" = '12.3400'</c> 得 1），所以内联侧与绑定侧必须<em>同一条归一</em>；让驱动去重排标度，
    /// 等于让一句 <c>WHERE</c> 的形状取决于对方版本怎么格式化 <c>decimal</c>。两支都量了才敢说这一支。</item>
    /// <item><c>ulong</c> → 文本（实测 505/702；2400 的 <c>id 19</c> 又独立复点了一次原形那一路：
    /// <c>ulong.MaxValue</c> 直接交出得到的是 <c>'-1'</c> 那颗 2 字符串）。</item>
    /// <item><c>TimeSpan</c> 与 <c>TimeOnly</c> → <c>Ticks</c>（实测 507/508 是原形那一路的读数，
    /// 605/700 TS 是这一路的读数）。<b>原形那一路现在有了逐字的形状</b>：驱动把两型都写成 .NET 往返文本
    /// <c>'10:20:30.4560000'</c> 与 <c>'1.02:03:04.0050000'</c>【实测·2400 <c>id 11/12/13</c>，
    /// 负跨度带 <c>'-'</c> 号】，而列声明是 <c>INTEGER</c>（<c>SqliteTypes</c> 第六条）——
    /// 两形落在同一格里就是"这一列既有整数又有文本"，所以这里不是取更好的一档，是取与列一致的那一档。</item>
    /// <item><c>char</c> → <c>string</c>：这一支<em>已有点位实测</em>【实测·2400 <c>id 20</c>】，
    /// 而读到的结论是"两支同形"：驱动自己会把 <c>char</c> 写成 1 字符串 <c>'A'</c>，与我们折成
    /// <c>string</c> 得到的那一颗逐字相同。所以这一支从"防一颗没问过的推断"改成了"把意图写在代码里"
    /// —— 折与不折在库里没有差别，而<em>读这份文件的人</em>需要看到 <c>char</c> 落在文本那一臂。
    /// （初稿在这里写的是【推断】并给了另一句理由，第二十四轮把它量掉了；改判整颗记 §13.5。）</item>
    /// </list>
    /// 其余一律原样交出：<c>Guid</c>、<c>DateTime</c>、<c>DateTimeOffset</c>、<c>DateOnly</c>、
    /// <c>bool</c>、<c>byte[]</c> 与三颗整数宽都由驱动自己写成我们已经量过的形状
    /// （608 / 2400 那一族 / 604 / 602 / 607 / 511）。其中两颗要在交出<em>之前</em>知道：
    /// <c>DateTime</c> 的 <c>Kind</c> 会整颗丢掉【实测·2400 <c>id 1/2/3</c>：<c>Unspecified</c>、
    /// <c>Utc</c>、<c>Local</c> 三颗同一时刻交进去，读回的 23 字符串逐字相同】，
    /// 所以本家<b>没有</b>把"这是哪一种钟"送进库的路；<c>DateOnly</c> 交出去得到的是裸日期
    /// <c>'2026-03-09'</c>（10 字符串）而<em>不是</em>零点那一形【实测·2400 <c>id 10</c>】，
    /// 那一条正是 <c>SqliteExpressions</c> 里 <c>Fn.ToDate</c> 两臂之差的另一半。</summary>
    static object? Normalize(object? value) => value switch
    {
        null => DBNull.Value,
        { } typed when typed.GetType().IsEnum => EnumAsIntegral(typed),
        decimal money => money.ToString(CultureInfo.InvariantCulture),
        ulong wide => Invariant(wide),
        TimeSpan span => span.Ticks,
        TimeOnly clock => clock.Ticks,
        char letter => letter.ToString(),
        var typed => typed,
    };

    /// <summary>枚举的底层类型由声明决定，所以按它取对应宽度：<c>byte</c> 底不许被 widen 成 <c>int</c>，
    /// 否则 <c>DbType</c> 与实参类型对不上。这一份是七家第七次同形拷贝（§2 那条禁令造成的重复，
    /// 与连接器那一格同一判据）；本家的拷贝<em>比另六家更必要</em>：这里 <c>DbType</c> 不参与
    /// （类注释第一条），所以"落成什么"完全由这一颗返回的 CLR 型决定，宽窄选错会静默改变存储文本
    /// （实测 500 那颗 <c>Int64</c> 读数说的就是"驱动信值型不信声明"）。</summary>
    static object EnumAsIntegral(object value)
    {
        var convertible = (IConvertible) value;

        return Type.GetTypeCode(Enum.GetUnderlyingType(value.GetType())) switch
        {
            TypeCode.Byte => convertible.ToByte(null),
            TypeCode.SByte => convertible.ToSByte(null),
            TypeCode.Int16 => convertible.ToInt16(null),
            TypeCode.UInt16 => convertible.ToUInt16(null),
            TypeCode.UInt32 => convertible.ToUInt32(null),
            TypeCode.Int64 => convertible.ToInt64(null),
            TypeCode.UInt64 => convertible.ToUInt64(null),
            _ => convertible.ToInt32(null),
        };
    }

    /// <summary>尺寸。<b>本家它是七家里唯一一颗"实测证明完全不被读"的</b>（500/500b/501），
    /// 所以这里填的数<em>不改变任何行为</em>，填的意义只剩两格：<see cref="ColumnContract.FitsBytes"/>
    /// 那道闸在共用层，与本数无关；<c>SqlLogging</c> 打印参数时它会露一次脸。
    /// 之所以仍然按 hint 报而不是恒 0：将来若换一颗认 <c>Size</c> 的驱动（本家驱动是可替换的，
    /// §1.2 原则 3 只禁编译期引用），这里不需要重学一遍"该报哪颗数"。</summary>
    static int SizeOf(object? value, ColumnContract? hint) => value switch
    {
        null => 0,
        string text => hint?.MaxChars ?? text.Length,
        byte[] bytes => bytes.Length,
        _ => 0,
    };

    /// <summary>与 <see cref="Normalize"/> 同源（<see cref="CreateParameter"/> 那一段），所以本方法看到的
    /// <c>decimal</c>/<c>ulong</c>/<c>TimeOnly</c>/<c>TimeSpan</c> 已经是字符串或整数了 —— 下面那几臂
    /// 与 <c>SqliteTypes.GetStoreType</c> 逐颗对得上，两处不许分岔（同一颗纪律在
    /// <c>Gbase8sTypes.ApplyStorageDefaults</c> 第一条旁边）。
    /// <b>本表里最没用的九行与最有用的第一行是同一件事</b>：驱动不看它（类注释第一条），
    /// 而"没用的那颗填错也不会红"正是它要写对的理由 —— 它是本家绑定意图唯一可读的地方。</summary>
    static DbType DbTypeOf(object? value, ColumnContract? hint)
    {
        var clrType = value?.GetType() ?? hint?.ClrType;

        if (clrType is null)
            return DbType.Object;

        clrType = Nullable.GetUnderlyingType(clrType) ?? clrType;

        if (clrType == typeof(string))
            return DbType.String;

        if (clrType == typeof(byte[]))
            return DbType.Binary;

        if (clrType.IsEnum)
            return DbType.Int32;

        return clrType switch
        {
            var t when t == typeof(bool) => DbType.Boolean,
            var t when t == typeof(byte) || t == typeof(sbyte) => DbType.Byte,
            var t when t == typeof(short) || t == typeof(ushort) => DbType.Int16,
            var t when t == typeof(int) || t == typeof(uint) => DbType.Int32,
            var t when t == typeof(long) => DbType.Int64,

            // 定点与无符号归一后已经是文本，所以本家<b>永远</b>不会带着 DbType.Decimal 进来 ——
            // 那两臂留在这里是"如果谁把 Normalize 那一支删了，这里会先露出矛盾"的形状。
            var t when t == typeof(decimal) => DbType.Decimal,
            var t when t == typeof(ulong) => DbType.Int64,

            var t when t == typeof(float) => DbType.Single,
            var t when t == typeof(double) => DbType.Double,

            // DateTime2 与另六家同数而<em>本家的理由最弱</em>：这一家的时刻是 TEXT affinity 加驱动
            // 自己写的 ISO 串（907/908），<c>DbType</c> 那位不参与。取 <c>DateTime2</c> 而不是
            // <c>DateTime</c>，是为了不把"7 位小数会被截到 3 位"那一族语义写进本仓库的意图里
            // —— 那一条在 T-SQL 是实测（<c>SqlServerValues</c>），在本家它只是一句自述。
            var t when t == typeof(DateTime) => DbType.DateTime2,
            var t when t == typeof(DateOnly) => DbType.Date,
            var t when t == typeof(DateTimeOffset) => DbType.DateTimeOffset,

            // TimeOnly/TimeSpan 归一后是 long，所以这两臂今天到不了；写出来说的是"列是整数、
            // 跨度按 Ticks"这一格契约（<c>SqliteTypes</c> 第六条）。
            var t when t == typeof(TimeOnly) || t == typeof(TimeSpan) => DbType.Int64,
            var t when t == typeof(Guid) => DbType.Guid,
            var t when t == typeof(char) => DbType.String,
            _ => DbType.Object,
        };
    }

    /// <summary>字面量内联。<b>本家取"发"，而它的判据与另六家没有一条相同</b>：那六家在这格上争的是
    /// 计划缓存（§5.8）与 <c>N'</c> 前缀，本家争的是<b>正确性</b> —— 库里的值是文本，等号比的也是文本，
    /// 所以内联的形状只要与绑定侧差一齿，一句 <c>WHERE</c> 就静默空集而不是报错（实测 329）。
    /// 于是下面每一支都<em>必须</em>与 <see cref="Normalize"/> 逐字同形，而这一点是七家里只有本家
    /// 需要检查的：另六家这两条路各自都能被库纠正。
    /// <b>不发的两档</b>：<c>byte[]</c>（<c>X'…'</c> 那一形本次没有点位实测，§5.8 的纪律是不发没问过的
    /// 写法，与另六家同判，但本家是<em>第一次"有能力发而不发"</em>——那六家是真的没有可用的十六进制形）、
    /// 非有限浮点与未知对象。<c>DateTimeOffset</c> 这一档本家<em>发</em>，而它是七家里唯一一颗
    /// "内联了带偏移文本"的实现，理由与残余风险逐字写在 <see cref="InlineOffset"/> 旁边。</summary>
    public bool TryInlineLiteral(object? value, ColumnContract? hint, out string sql)
    {
        switch (value)
        {
            case null:
                sql = "NULL";
                return true;

            case string text:
                sql = Literal(text);
                return true;

            // 库里那一格是 1/0（实测 607/335），所以内联也只能是 1/0。
            case bool flag:
                sql = flag ? "1" : "0";
                return true;

            case byte or sbyte or short or ushort or int or uint or long:
                sql = Invariant(value);
                return true;

            // ulong 与定点同一支：加引号（实测 702 与 326/333 那四条读数都是带形状入库的文本）。
            case ulong or decimal:
                sql = Literal(Invariant(value));
                return true;

            // 跨度落整数（列声明与绑定两侧都是 INTEGER，实测 605）。
            case TimeSpan span:
                sql = Invariant(span.Ticks);
                return true;

            case TimeOnly clock:
                sql = Invariant(clock.Ticks);
                return true;

            case float or double:
                sql = InlineNumber(Invariant(value));

                // 空串 = 这一格拒了（非有限值）。本家有 'Inf'/'NaN' 那一族 CAST 写法【文档】，
                // 而它落在 REAL 那一臂、与我们的文本档不同一条 —— 没量过就不发。
                return sql.Length > 0;

            case Guid id:
                // 'D' 形小写，与驱动写入的那一形连大小写都一样（实测 608）。这一句在本家不是洁癖：
                // 文本等号区分大小写而 LIKE 不区分（实测 97 那颗 'Abc' = 'abc' 得 0 ∶ 实测 96 那颗
                // 'A' LIKE 'a' 得 1），所以大写形在 = 上是一场静默空集。
                sql = Literal(id.ToString("D", CultureInfo.InvariantCulture));
                return true;

            case DateTime moment:
                sql = Literal(InlineDateTime(moment));
                return true;

            case DateOnly day:
                sql = Literal(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                return true;

            case DateTimeOffset withOffset:
                sql = Literal(InlineOffset(withOffset));
                return true;

            case char letter:
                sql = Literal(letter.ToString());
                return true;

            default:
                sql = string.Empty;
                return false;
        }
    }

    /// <summary>时刻内联形：<c>yyyy-MM-dd HH:mm:ss</c> 加一段<em>去掉尾零</em>的小数，全零时连小数点
    /// 一起不留。<b>这一颗方法是本文件唯一一处"抄驱动的私有形状"</b>，支撑它的读数分两批：
    /// <c>.4560000</c> → <c>.456</c>、<c>.4561000</c> → <c>.4561</c>【实测·907】，整秒 → 不带小数点
    /// 【实测·908】，7 位全有效时逐位保真【实测·921 ∶ 922 那颗 27 字符串】；<b>第二十四轮把剩下两种宽度补齐</b>
    /// 【实测·2400】：<c>40</c> 毫秒写成 <c>'.04'</c>（<c>id 5</c>，两位而不是一位也不是三位）、
    /// <c>1</c> tick 写成 <c>'.0000001'</c>（<c>id 6</c>，七位逐位保真、没有被就近舍到 6 位），
    /// 加上 <c>id 1…4</c> 那四颗，本规则与驱动<em>在四种小数宽度上逐字对过</em>。
    /// 之所以<em>不</em>写成定宽 7 位：那样内联出的串与库里存的串不相等（文本等号是逐字符的，
    /// 实测 97 那颗 <c>'Abc' = 'abc'</c> 得 <c>0</c>），而那正是要防的那一场静默空集。之所以<em>不</em>写成 3 位（另六家的常见做法）：
    /// 本家存的是 100 纳秒，截到毫秒会让一句真命中变空集。
    /// <b>残余风险比初稿小了一档但没有清零</b>：初稿写的是"实测到三个点、外推一条规则"，
    /// 现在那一族的已知宽度（0/2/3/4/7 位）都各有一颗读数；仍然没量的是<em>跨零的那一侧</em>
    /// （比如 <c>X 年 12 月 31 日 23:59:59.9999999</c> 过驱动之后是不是还是那一串），
    /// 而它属于 <c>SqliteTypes</c> 第五条那一族"文本序 vs 时刻序"的口子，不另立一颗。
    /// 记 §14。</summary>
    static string InlineDateTime(DateTime value)
        => Stamp(value.ToString(SecondFormat, CultureInfo.InvariantCulture), value.Ticks);

    /// <summary>带偏移那一形：时刻与小数同一条归一，后面直接接 <c>zzz</c>（实测 604 那条形如
    /// <c>2026-03-09 10:20:30-05:00</c> 的读数：偏移前不带空格、带冒号）。
    /// <b>第二十四轮把这一形在三颗偏移上逐字对过</b>【实测·2400 <c>id 7/8/9/25</c>】：
    /// <c>+08:00</c> 与 <c>-05:30</c> 都<em>原样</em>入库（半小时偏移没有被抹成整点），而
    /// <b>零偏移写的是 <c>+00:00</c>、不是 <c>Z</c> 也不是不留</b> —— 那一句决定了本方法只能取
    /// <c>zzz</c>：<c>z</c> 在零偏移上得到 <c>+0</c>，而 <c>K</c> 那一形得到 <c>Z</c>，两形与库里都不等。
    /// 同一批读数里 <c>id 9</c> 还独立复点了 <see cref="Stamp"/> 那一颗：<em>整秒 + 带偏移</em>得到
    /// <c>10:20:30-05:30</c>（小数点与小数一起不留，而偏移那颗 <c>30</c> 留着）——
    /// 也就是"先拼小数、再拼偏移"这一条<em>顺序</em>在这里不是风格，是 <c>TrimEnd('0')</c> 会咬到偏移的那一颗。
    /// <b>本家发这一支，而另六家都不发</b>，来路是"两侧同源"：绑定侧交出的就是驱动自己格式化的那一颗
    /// <see cref="DateTimeOffset"/>，内联侧<em>复用同一条格式化规则</em>，所以等号两侧可比；另六家不发
    /// 是因为它们的库里那一格由服务端归一，客户端猜不出形。
    /// 真正的疑点是另一颗，写在 <c>SqliteExpressions</c> 旁边：<b>带偏移文本的词法序不等于时刻序</b>
    /// —— 这一句现在有直接的<b>读数</b>而不只是一条推理【实测·2413：<c>id 7</c> 与 <c>id 8</c> 是
    /// <em>同一串钟面</em>（<c>10:20:30.456</c>）配两个偏移，按真实瞬间算 <c>+08:00</c> 那一颗<em>早</em>八小时，
    /// 而 <c>"7" &lt; "8"</c> 数到 <c>0</c> —— 因为词法上 <c>'+0'</c> 排在 <c>'+8'</c> 前面；
    /// 同一颗问里 <c>id 9</c>（<c>-05:30</c>）与 <c>id 4</c>（无偏移）相比也得 <c>0</c>，那是<em>前缀</em>
    /// 那一档造成的：无偏移的串是带偏移那一串的开头】。所以本家的 <c>DateTimeOffset</c> 列
    /// <em>能等值而不能排序</em>。那是排序语义的缺口而不是等号的缺口，记 §14 与 <c>SqliteTypes</c> 第五条并排。</summary>
    static string InlineOffset(DateTimeOffset value)
        => Stamp(value.ToString(SecondFormat, CultureInfo.InvariantCulture), value.Ticks)
           + value.ToString("zzz", CultureInfo.InvariantCulture);

    /// <summary>秒那一段后面接什么：<em>非零</em>小数就接（去掉尾零），整秒什么都不接。
    /// <see cref="InlineDateTime"/> 与 <see cref="InlineOffset"/> 共用。
    /// 之所以按 <c>Ticks</c> 算而不是一条 <c>fffffff</c> 格式串再 <c>TrimEnd</c>：偏移那一支的串以
    /// 数字结尾（<c>+08:00</c>），整串 <c>TrimEnd('0')</c> 会把偏移也削掉 —— 那是这一族独有的形状
    /// 造成的错（另六家没有带偏移的文本），所以两颗分开拼、小数只属于自己的那一段。</summary>
    static string Stamp(string seconds, long ticks)
    {
        var fraction = (ticks % TimeSpan.TicksPerSecond).ToString("D7", CultureInfo.InvariantCulture).TrimEnd('0');

        return fraction.Length == 0 ? seconds : seconds + "." + fraction;
    }

    const string SecondFormat = "yyyy-MM-dd HH:mm:ss";

    static string Invariant(object? value)
        => value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : value?.ToString() ?? string.Empty;

    /// <summary>浮点内联的那一条判据<em>六</em>家同源，这里是第七份：<b>纯整数形式要补小数点，指数式不许补</b>。
    /// 抄的仍然是 <c>SqlServerValues.InlineNumber</c> 那一份 2026-09-22 真库实测<em>之后</em>的形状
    /// （<c>1E-08.0</c> 是语法错；那一次修复前后的两版在另两族各撞出来一次，见 §13.5 那一格）。
    /// 本家这一格<em>第一次有了可问的对象</em>：REAL 列上 <c>3</c> 与 <c>3.0</c> 是同一档隐式转换吗？
    /// 答案是<b>"是，而且库自己会折"</b>——一颗整数字面量打进声明 <c>REAL</c> 的列，<c>typeof</c> 交回
    /// <c>real</c>【实测·4204】，<c>UPDATE … SET r = 5</c> 与绑定参数那两条路同样【实测·4226 ∶ 4228 ∶ 4230】，
    /// 所以补不补那颗小数点在浮点那一臂<em>不落进存储</em>；<b>它落进的是文本比较那一侧</b>，而同一条字面量
    /// 打进声明 <c>TEXT</c> 的列交回的是 <c>'3'</c>【实测·4204 的 <c>typeof("t")</c> 那一格】——本家的定点列正是 <c>TEXT</c>。
    /// 六家写这一句时都只能"取一致"，本家是第一家能把它分成两臂说清的：<b>无关的是浮点那一臂，
    /// 有关的恰好是我们把 <c>decimal</c> 搬过去的那一臂</b>（同一判据另见 <see cref="SqliteTypes"/> 第三条）。
    /// <b>本家初稿在这一格写过一句反的话</b>（"整数字面量进 REAL affinity 列会存成 <c>3</c> 而不是 <c>3.0</c>"，
    /// 出处当时挂的是第 2 轮那颗 TEXT 列的 affinity 读数），第 42 轮当场否掉，改在此处——
    /// 这一格与 §13.5 "本版被实测改掉的设计判断"那一列并排。</summary>
    static string InlineNumber(string text)
    {
        // 判整串字符集而不是判首字符：`-∞` 打头那颗 `-` 是合法的浮点首字符，只判首字符会放它过来。
        foreach (var c in text)
            if (!char.IsAsciiDigit(c) && c is not ('-' or '+' or '.' or 'E' or 'e'))
                return string.Empty;

        if (!text.Contains('.') && !text.Contains('E', StringComparison.OrdinalIgnoreCase))
            text += ".0";

        return text;
    }

    /// <summary>单引号成对转义，<c>unicode: false</c>。<b>这一家取假不是"没决定"而是被正面量过</b>：
    /// <c>N'…'</c> 在本族是<em>语法错</em>【实测·P】，而文本一律 UTF-8（<c>SqliteTypes</c> 第一条），
    /// 所以"宽窄两型"这句话在这里没有对象 —— 与 <c>Gbase8sValues.Literal</c> 那颗同值而同判据
    /// （那一家取假的理由是"没有带前缀的宽字符形可读"，本家取假的理由是"写了会报错"，
    /// 一档在文档一档在实测）。
    /// 另一颗只在本家成立的：<see cref="SqlWriter.AppendStringLiteral"/> 走的是本方言的引号纪律，
    /// 而本家的标识符引号是 <c>"</c>（<c>IdentifierPolicy.Sqlite</c>）——
    /// <b>双引号在这一族不是标识符引号而是"字符串字面量的第二形"</b>，那一格正是本家
    /// "标识符写错成静默字符串"那颗陷阱的成因【实测·13/14/15/L】。这里只发单引号，
    /// 而那颗陷阱的防线在 <c>IdentifierPolicy</c> 与 <c>SqliteDialect</c> 两处。</summary>
    static string Literal(string value)
    {
        var writer = new SqlWriter(new StringBuilder(), [], '@');

        writer.AppendStringLiteral(value, unicode: false);

        return writer.ToString();
    }
}
