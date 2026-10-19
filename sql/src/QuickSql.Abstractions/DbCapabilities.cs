namespace QuickSql;

/// <summary>
/// 方言能力的唯一真相源（§4）。禁止用 try/catch 试探数据库支持什么语法。
/// </summary>
[Flags]
public enum DbCapabilities : uint
{
    None = 0,

    Paging_LimitOffset = 1 << 0,
    Paging_RowNum = 1 << 1,
    Paging_OffsetFetch = 1 << 2,

    Returning = 1 << 3,
    Returning_OnlyExpression = 1 << 4,

    Upsert_OnConflict = 1 << 5,
    Upsert_MergeInto = 1 << 6,
    Upsert_OnDuplicate = 1 << 7,

    Boolean_Type = 1 << 8,
    Guid_Raw16 = 1 << 9,
    Guid_Char36 = 1 << 10,

    Sequences = 1 << 11,
    Identity_ExplicitInsert = 1 << 12,
    Schemas = 1 << 13,
    Cte = 1 << 14,
    WindowFunctions = 1 << 15,
    SkipLocked = 1 << 16,
    Lock_NoWait = 1 << 17,

    Json_Type = 1 << 18,
    Array_Type = 1 << 19,

    Bulk_Copy = 1 << 20,
    Bulk_ArrayBind = 1 << 21,

    RefCursor = 1 << 22,
    MultiStatement = 1 << 23,
    MultiResultSets = 1 << 24,
    Count_Over_Window = 1 << 25,
    CaseSensitive_Identifiers = 1 << 26,

    Comment_On_Column = 1 << 27,
    Alter_Column_Precision = 1 << 28,
    Truncate_Cascade = 1 << 29,
    TempTable = 1 << 30,

    /// <summary>躺着的事务（§6 (3)）：provider 的驱动能接住 <see cref="System.Data.Common.DbConnection.BeginTransaction"/>
    /// 与真的 <see cref="System.Data.Common.DbTransaction"/>，内核写路径才允许把"一次保存 / 一次 InsertOrUpdate / 一次批量"
    /// 包进一次提交。<b>这是位表上第一颗被内核真正当闸门读的位</b>——此前三十一位的读者只有
    /// <c>SchemaSyncer</c> 那颗 <c>Alter_Column_Precision</c>（§4 那句"分派点必须落在被读的那一格"
    /// 由此第一次在能力位上兑现），这一颗一落宣告"事务可选"那道口开了：<b>没有它在位时，内核写路径不再
    /// 开事务，逐句按库的 autocommit 直发</b>，公开的 <c>BeginTransaction</c> 则照
    /// <see cref="IDbProvider.CanCreateDatabase"/> 那 <see langword="false"/> 先例当场抛
    /// <see cref="NotSupportedException"/>——先例的纪律逐字搬到本位：<b>严禁静默假事务</b>，
    /// "没有的事"要说成"没有的事"。
    ///
    /// <para><b>行为诚实条款（没有这一位时）</b>：一次 <c>SaveChanges</c> / <c>InsertOrUpdate</c> / 批量里
    /// 发出的<b>多条语句不原子</b>——中途失败时已经发出去的语句<b>不回滚</b>，留在库里的是半批；单条语句
    /// 自身的事务性（一句 INSERT 的原子性那一格）按各家库的文档记账，本层不替它打包票。</para>
    ///
    /// <para><b>为什么底层类型换成 <see langword="uint"/></b>：位表此前占满 <c>1&lt;&lt;0</c> 到
    /// <c>1&lt;&lt;30</c>，第三十二颗只能住进有符号 <see langword="int"/> 的符号位——那颗位的组合值会
    /// 变成 <see langword="int"/> 负数，而 <see cref="FlagsAttribute"/> 组合名的解析走符号位扩张，
    /// 带它混排的任何一位掩码 <see cref="object.ToString()"/> 都会静默错拼。换 <see langword="uint"/>
    /// 之后这个枚举的声明形状与按钮面一字不变，仓库里没有一处在数值上收它（2026-10 复核：
    /// <c>(int)</c> 一类数值取用在 <c>src/</c> 与 <c>tests/</c> 对它各 0 命中）。</para></summary>
    Transactions = 1u << 31,
}
