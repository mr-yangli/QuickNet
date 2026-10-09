using System.Data.Common;
using System.Runtime.CompilerServices;
using QuickSql.Metadata;
using QuickSql.Schema;

namespace QuickSql.Materialization;

/// <summary>「这一列能不能不装箱」的唯一判定处，与它同处的还有那批直读档的编号。
/// <para>这一格是 §16.21 记下的那条提升：<b>探测从每值挪到每场次</b>。上一版那道强类型门写的是
/// 每读一颗值问一次 <c>GetFieldType</c>，于是买 24 B 的省、赔 41.5 ns 的探（§12.7 C 末尾那张表）；
/// 把同一问搬到每场次一次之后，每值只剩 <c>IsDBNull</c> + 原始访问器 —— 实测那格 89.0 ns / 0 B
/// 对装箱口 95.2 ns / 24 B，两头都赢，那一刀才成立。那一档留下的 <c>IsDBNull</c> 后来在第七笔被量出
/// 它自己的价（见 <see cref="DirectRead"/> 的「非空值类型那一族臂不再问 IsDBNull」），非空列现已不再付。</para>
/// <para><b>判据只有一条，而且它不猜</b>：驱动交回的类型（<see cref="DbDataReader.GetFieldType"/>）
/// 与成员声明的类型<em>是同一个</em>才直读。所以金仓把 boolean 回成 <c>t/f</c> 时那一列自动落回装箱口，
/// DM 把 uniqueidentifier 回成 <c>byte[]</c> 时同理 —— 不需要在这处再登记一份"哪家怎么回"的表，
/// 也不会出现"探测说是 int、驱动实际给字符串"那种错（<c>GetFieldType</c> 是驱动的承诺，
/// 它与 <c>GetValue</c> 交回的对象同型是 <c>DbDataReader</c> 的契约）。</para></summary>
public static class ReadOps
{
    /// <summary>不直读：进 <see cref="ValueConverter.Read{T}"/> 那扇装箱口。</summary>
    public const byte Boxed = 0;

    public const byte Boolean = 1;
    public const byte Byte = 2;
    public const byte Int16 = 3;
    public const byte Int32 = 4;
    public const byte Int64 = 5;
    public const byte Single = 6;
    public const byte Double = 7;
    public const byte Decimal = 8;
    public const byte Char = 9;
    public const byte DateTime = 10;
    public const byte String = 11;
    public const byte Guid = 12;

    static readonly Dictionary<Type, byte> ByType = new()
    {
        [typeof(bool)] = Boolean,
        [typeof(byte)] = Byte,
        [typeof(short)] = Int16,
        [typeof(int)] = Int32,
        [typeof(long)] = Int64,
        [typeof(float)] = Single,
        [typeof(double)] = Double,
        [typeof(decimal)] = Decimal,
        [typeof(char)] = Char,
        [typeof(DateTime)] = DateTime,
        [typeof(string)] = String,
        [typeof(Guid)] = Guid,
    };

    /// <summary>每列每场次一次：探驱动，落这一列的读法编号。</summary>
    public static byte Probe(DbDataReader reader, int ordinal, Type declared)
    {
        var underlying = Nullable.GetUnderlyingType(declared) ?? declared;

        // 枚举不在 ByType 里（成员型是 State 而驱动交回的是底层整数，两边不同型），
        // 而 byte[] / DateOnly / TimeOnly / DateTimeOffset / TimeSpan 也一律留在这里之外：
        // 前四者 <c>DbDataReader</c> 没有"一次调用一个值"的原始访问器（这一族是构建期实测撞出来的：
        // 那里根本没有 <c>GetTimeSpan</c> 这一首），写下去是编译错误而不是优化。
        if (underlying.IsEnum || !ByType.TryGetValue(underlying, out var op))
            return Boxed;

        return reader.GetFieldType(ordinal) == underlying ? op : Boxed;
    }

    /// <summary>建一张票：<c>startOffset</c> 起连续 <c>contract.Columns.Length</c> 列。
    /// 实体投影的选列就是契约列序（<c>QueryCompiler.EntityColumns</c>），Include 那一档则是节点自带的偏移，
    /// 所以两处都是"连续一段"，票上因此只带一颗起点、不带序号数组（<see cref="ReadPlan.Offset"/>）——
    /// 探测用的 <c>startOffset + i</c> 与读取委托用的那一式是同一个，错位空间只剩一个加法。
    /// <b>每场次落堆的只有那一颗 <c>byte[]</c></b>（30 列 = 48 B），票本身是值。</summary>
    public static ReadPlan Build(DbDataReader reader, EntityContract contract, int startOffset)
    {
        var columns = contract.Columns;
        var ops = new byte[columns.Length];

        for (var i = 0; i < columns.Length; i++)
            ops[i] = Probe(reader, startOffset + i, columns[i].ClrType);

        return new ReadPlan(startOffset, ops);
    }
}

/// <summary>直读臂：<b>一条臂 = 一型的读法，两段都在同一行里</b>。
/// <para>§12.7 C 第二条要的是"某一列的读法只有一处可改"，这一版没有把它换成两处：
/// 每条臂的非直读分支就是 <see cref="ValueConverter.Read{T}"/> 本身（不是复制来的等价实现），
/// 所以"这一列怎么读"仍然只有那一扇门在回答，臂只多回答了一个新问题——"它能不能少一次装箱"。
/// 这件事由 <c>TypedReadTests</c> 与 <c>ZeroBoxingTests</c> 那两条架构用例钉住（每个臂都必须出现一次
/// <c>ValueConverter.Read</c>）。</para>
/// <para><b>非空值类型那一族臂不再问 <c>IsDBNull</c></b>（第七笔用真驱动读数改的判）：上一版每条臂都是
/// 「先 <c>IsDBNull</c>、再取」，一次取值付两趟驱动调用。真驱动上这一截按行加总（八列，只有有臂的列付）
/// 是 mysql 268.9 ns、sqlite 135.7、虚谷 70.5、sqlserver 50.4、postgresql 3.7，出自 <c>obj/probe-real</c>
/// 的「臂 ∶ 裸臂」两档。跳掉之后非空列一次取值一趟调用；代价是一句语义改判：<b>库里往非空值类型的列
/// 塞 NULL，现在抛，不再静默给 <c>default</c></b> —— 旧版那个 <c>0</c> 是把模型的谎话咽下来。
/// <c>string</c> 臂与可空那一族 11 首臂照旧问 <c>IsDBNull</c>：NULL 在它们是成员本身要表达的合法值。</para>
/// <para>LEFT JOIN 未命中那一档不受这件事影响，理由记在这儿：<c>IncludePlan.Materialize</c> 在读该节点
/// <em>任何</em>字段之前就按 <c>nullCheckOrdinal</c> 返回，未命中的子实体根本不进臂。全库主动问空值的
/// 地方只剩 <c>Materializer.cs</c> 那一处与门（<c>ValueConverter</c>）自己。</para>
/// <para><b>可空值类型各有一条自己的臂</b>（<c>ReadNullableInt32</c> 那一族，11 首），理由是语义而不是版式：
/// 非空臂的返回类型是底层型，NULL 那一格由门给 <c>default(DateTime)</c>，把它交给 <c>DateTime?</c> 成员
/// 就成了一次静默的摊平（0001-01-01 冒充"没有值"）。那是 <c>ZeroBoxingTests.A_null_keeps_the_nullability_of_the_member</c>
/// 钉住的一条回归线 —— 它是这一族臂存在的唯一理由，删掉任何一首都会让对应那一型重新摊平。
/// <c>string</c> 不在这一族里：可空注解层面的 <c>string?</c> 与 <c>string</c> 是同一个运行期类型，
/// 非空臂交回的 <c>null</c> 本来就是门给的答案。</para>
/// <para><b>编号与臂的对应关系只在本文件里定义</b>（<see cref="ReadOps.ByType"/> 给编号，这里给访问器），
/// 生成器模板按名字调这里的公开方法，所以名字写错是编译错误而不是运行期静默；
/// 而假如生成期给一列挑错了臂（比如给 <c>short</c> 列写 <c>ReadInt32</c>），
/// 探测落的编号是 <c>Int16</c>、臂的条件是 <c>op == ReadOps.Int32</c>，两边对不上就退回装箱口 ——
/// <b>错的方向是"没有变快"，不是"读错值"</b>。</para></summary>
public static class DirectRead
{
    /// <summary>按<em>声明类型原样</em>取臂（<c>int</c> 与 <c>int?</c> 是两支不同的臂）。
    /// <see cref="Delegate.Method"/> 就是 <c>Expression.Call</c> 要的那个方法句柄，取自方法组转换，
    /// 不是反射查找（门禁第 8 行的字样数因此不动）。null = 这一型没有直读臂，调用方继续走装箱口。</summary>
    static readonly Dictionary<Type, Delegate> Arms = new()
    {
        [typeof(bool)] = new Func<DbDataReader, int, byte, bool>(ReadBoolean),
        [typeof(byte)] = new Func<DbDataReader, int, byte, byte>(ReadByte),
        [typeof(short)] = new Func<DbDataReader, int, byte, short>(ReadInt16),
        [typeof(int)] = new Func<DbDataReader, int, byte, int>(ReadInt32),
        [typeof(long)] = new Func<DbDataReader, int, byte, long>(ReadInt64),
        [typeof(float)] = new Func<DbDataReader, int, byte, float>(ReadSingle),
        [typeof(double)] = new Func<DbDataReader, int, byte, double>(ReadDouble),
        [typeof(decimal)] = new Func<DbDataReader, int, byte, decimal>(ReadDecimal),
        [typeof(char)] = new Func<DbDataReader, int, byte, char>(ReadChar),
        [typeof(DateTime)] = new Func<DbDataReader, int, byte, DateTime>(ReadDateTime),
        [typeof(string)] = new Func<DbDataReader, int, byte, string>(ReadString),
        [typeof(Guid)] = new Func<DbDataReader, int, byte, Guid>(ReadGuid),
        [typeof(bool?)] = new Func<DbDataReader, int, byte, bool?>(ReadNullableBoolean),
        [typeof(byte?)] = new Func<DbDataReader, int, byte, byte?>(ReadNullableByte),
        [typeof(short?)] = new Func<DbDataReader, int, byte, short?>(ReadNullableInt16),
        [typeof(int?)] = new Func<DbDataReader, int, byte, int?>(ReadNullableInt32),
        [typeof(long?)] = new Func<DbDataReader, int, byte, long?>(ReadNullableInt64),
        [typeof(float?)] = new Func<DbDataReader, int, byte, float?>(ReadNullableSingle),
        [typeof(double?)] = new Func<DbDataReader, int, byte, double?>(ReadNullableDouble),
        [typeof(decimal?)] = new Func<DbDataReader, int, byte, decimal?>(ReadNullableDecimal),
        [typeof(char?)] = new Func<DbDataReader, int, byte, char?>(ReadNullableChar),
        [typeof(DateTime?)] = new Func<DbDataReader, int, byte, DateTime?>(ReadNullableDateTime),
        [typeof(Guid?)] = new Func<DbDataReader, int, byte, Guid?>(ReadNullableGuid),
    };

    /// <summary>声明类型 → 臂。<b>不做 <c>Nullable</c> 摊平</b>（摊平正是上面那段"为什么要有可空臂"要防的事），
    /// 枚举与 <c>Nullable&lt;枚举&gt;</c> 都不在表里：前者成员型与列型永远不同型，后者要在门里
    /// 按 <c>Enum.ToObject</c> 落（未登记的数值也读得出来，§9.3）。</summary>
    public static Delegate? ArmFor(Type declared) => Arms.GetValueOrDefault(declared);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ReadBoolean(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Boolean ? reader.GetBoolean(ordinal) : ValueConverter.Read<bool>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ReadByte(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Byte ? reader.GetByte(ordinal) : ValueConverter.Read<byte>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short ReadInt16(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Int16 ? reader.GetInt16(ordinal) : ValueConverter.Read<short>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadInt32(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Int32 ? reader.GetInt32(ordinal) : ValueConverter.Read<int>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadInt64(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Int64 ? reader.GetInt64(ordinal) : ValueConverter.Read<long>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ReadSingle(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Single ? reader.GetFloat(ordinal) : ValueConverter.Read<float>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ReadDouble(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Double ? reader.GetDouble(ordinal) : ValueConverter.Read<double>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static decimal ReadDecimal(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Decimal ? reader.GetDecimal(ordinal) : ValueConverter.Read<decimal>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static char ReadChar(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Char ? reader.GetChar(ordinal) : ValueConverter.Read<char>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DateTime ReadDateTime(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.DateTime ? reader.GetDateTime(ordinal) : ValueConverter.Read<DateTime>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string ReadString(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.String && !reader.IsDBNull(ordinal) ? reader.GetString(ordinal) : ValueConverter.Read<string>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Guid ReadGuid(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Guid ? reader.GetGuid(ordinal) : ValueConverter.Read<Guid>(reader, ordinal);

    // ── 可空值类型那一族：非直读分支交回 Read<T?>，所以 NULL 是 null 而不是 default(T) ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool? ReadNullableBoolean(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Boolean && !reader.IsDBNull(ordinal) ? reader.GetBoolean(ordinal) : ValueConverter.Read<bool?>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte? ReadNullableByte(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Byte && !reader.IsDBNull(ordinal) ? reader.GetByte(ordinal) : ValueConverter.Read<byte?>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short? ReadNullableInt16(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Int16 && !reader.IsDBNull(ordinal) ? reader.GetInt16(ordinal) : ValueConverter.Read<short?>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int? ReadNullableInt32(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Int32 && !reader.IsDBNull(ordinal) ? reader.GetInt32(ordinal) : ValueConverter.Read<int?>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long? ReadNullableInt64(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Int64 && !reader.IsDBNull(ordinal) ? reader.GetInt64(ordinal) : ValueConverter.Read<long?>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float? ReadNullableSingle(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Single && !reader.IsDBNull(ordinal) ? reader.GetFloat(ordinal) : ValueConverter.Read<float?>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double? ReadNullableDouble(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Double && !reader.IsDBNull(ordinal) ? reader.GetDouble(ordinal) : ValueConverter.Read<double?>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static decimal? ReadNullableDecimal(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Decimal && !reader.IsDBNull(ordinal) ? reader.GetDecimal(ordinal) : ValueConverter.Read<decimal?>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static char? ReadNullableChar(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Char && !reader.IsDBNull(ordinal) ? reader.GetChar(ordinal) : ValueConverter.Read<char?>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DateTime? ReadNullableDateTime(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.DateTime && !reader.IsDBNull(ordinal) ? reader.GetDateTime(ordinal) : ValueConverter.Read<DateTime?>(reader, ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Guid? ReadNullableGuid(DbDataReader reader, int ordinal, byte op)
        => op == ReadOps.Guid && !reader.IsDBNull(ordinal) ? reader.GetGuid(ordinal) : ValueConverter.Read<Guid?>(reader, ordinal);
}
