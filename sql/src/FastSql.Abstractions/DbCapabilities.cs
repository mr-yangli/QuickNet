namespace FastSql;

/// <summary>
/// 方言能力的唯一真相源（§4）。禁止用 try/catch 试探数据库支持什么语法。
/// </summary>
[Flags]
public enum DbCapabilities
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
}
