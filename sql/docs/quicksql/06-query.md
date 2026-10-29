# 06 · 查询数据

> 本篇解决：条件、排序（含按名字排序）、投影、Distinct、分页与总数、计数与存在性探针、顶层聚合、流式读取、无序分页策略。

读侧链是 `IQuery<T>`，从 `orm.Select<T>()` 起手。它**刻意不是 `IQueryable`**——不能塞进 LINQ 生态，也没有非泛型口。所有能力都是链上的显式方法，出口才连库。

## 1. 基础链

```csharp
var rows = await orm.Select<Order>()
    .Where(o => o.State == OrderState.Paid && o.Amount > 100m)   // 可多条 Where，左到右折成 AND
    .OrderByDescending(o => o.CreatedAt)
    .ThenBy(o => o.Id)
    .Skip(20).Take(10)
    .ToListAsync();
```

### 出口一览

| 异步 | 同步 | 返回 |
| --- | --- | --- |
| `ToListAsync(ct)` | `ToList()` | `List<T>` |
| `ToArrayAsync(ct)` | `ToArray()` | `T[]`（与 `ToList` 同一条路，多一次拷贝；要"交出去后改不动"用这颗） |
| `FirstOrDefaultAsync(ct)` | `FirstOrDefault()` | 第一行或 null（没有 `First`/`FirstAsync`——"没有行"不是错误） |
| `CountAsync(ct)` / `LongCountAsync(ct)` | `Count()` / `LongCount()` | 计数 |
| `AnyAsync(ct)` | `Any()` | **存在性探针**：`SELECT 1 … 取第一行`，不数全表（`Skip(n)` 语义保留） |
| `ToPagedAsync(page, size, options?, ct)` | — | `PagedResult<T>` |
| `ToAsyncEnumerable(batchSize)` | — | 流式逐行（大结果集不整段驻留内存） |

类型本身实现 `IAsyncEnumerable<T>`，可以 `await foreach`。

## 2. 条件表达式（Where）

```csharp
.Where(o => o.State == OrderState.Paid)                  // 枚举按底层值绑定
.Where(o => o.Note == null)                              // → IS NULL（null == o.Name 反向写法同折）
.Where(o => o.CustomerName.Contains("张") && !o.Deleted) // → LIKE '%张%' ESCAPE（StartsWith/EndsWith 同族）
.Where(o => o.CustomerName.ToLower() == name)            // 字符串函数（见下表）
.Where(o => o.CreatedAt.Year == 2026)                    // 时间成员（见下表）
.Where(o => o.Amount > (o.Note == null ? 0m : 100m))     // 条件表达式
.Where(done, o => o.State == OrderState.Done)            // 条件重载：done=false 时整条不参与
.Where(o => ids.Contains(o.Id))                          // 列值 ∈ 集合 → IN (…)，按预算分块（见 §7）
```

翻译不出的写法当场抛 `UntranslatableExpressionException`，消息带建议改法——**没有客户端求值兜底**，绝不静默拉全表。

**lambda 里可直接用的函数：**

| 类别 | 成员 |
| --- | --- |
| 字符串 | `Length`、`ToUpper/ToUpperInvariant`、`ToLower/ToLowerInvariant`、`Trim/TrimStart/TrimEnd`、`Substring`、`Replace`、`Concat` 与 `+` 拼接、`string.IsNullOrEmpty`、`StartsWith/EndsWith/Contains`（→ `LIKE`，带转义；**带 `StringComparison` 的重载拒**——大小写口径各家不同，要比大小写不敏感两侧都 `ToLower()`） |
| 时间（`DateTime`/`DateTimeOffset`/`DateOnly`） | `Year/Month/Day/Hour/Minute/Second`、`.Date`、`AddYears/AddMonths/AddDays/AddHours/AddMinutes/AddSeconds`、`DateTime.Now`、`DateTime.UtcNow` |
| 数学 | `Math.Abs`、`Math.Round`、`Math.Floor`、`Math.Ceiling` |
| 集合 | `Enumerable.Contains`（列值 ∈ 集合 → `IN (…)`，按 provider 预算自动分块）；**子查询**：先把 `IQuery` 捕获进变量，再在外层谓词里调 `.Count()/.Contains(值)/.Sum()/.Max()/.Min()/.Average()` → 标量/IN 子查询（见 §5 与 [07 篇](07-joins-and-navigation.md)） |
| 类型转换 | 两侧同型或枚举↔底层值、安全加宽直接绑定不包 `CAST`；**收窄与 `char` 故意发 `CAST`**（让它响比静默错行好，代价是那一侧索引作废） |

`x.Id.Equals(id)` / `object.Equals(x, y)` 与 `==` 同形（ABP 常见写法）；两侧不同型的 `Equals` 拒。

**日期三口（`QuickSql.Sql.QuickFx`，SqlSugar `SqlFunc` 日期封装的同族移植）：**

```csharp
.Where(s => QuickFx.DateRange(s.BeginAt, from, to))                        // 列按天圈进 [from, to]，两端都含
.Where(s => QuickFx.DateRange(s.BeginDate, from, to))                      // DateOnly 列同闩
.Where(s => QuickFx.DateIntersection(s.BeginDate, s.EndDate, uFrom, uTo))  // 两段闭区间有无同一天
.Where(s => QuickFx.DateTimeIntersection(s.BeginAt, s.EndAt, uFrom, uTo))  // DateTime 两端 × 用户给的 DateOnly 段
```

- 发出的都是**比较 + `AND`**（范围形 `date >= @p0 AND date < @p1`；交集形 `{0} <= {3} AND {1} >= {2}`）：译文里不落任何方言日期函数，十八家 provider 零登记通用；与等价的手写比较**逐字节相同**。
- `DateRange` 的 `endDate` **传当天就含当天**（终态半开式的"+1 天"由内核在**参数侧**算好——上界参数已是 `to+1 天`，列侧零表达式，索引照走；老 SqlSugar 封装要调用方自己 +1 的那笔账在这里了结）。交集两口本来就是闭端语义，无 +1 可言。
- `DateRange` 的两颗边界必须能**当场求值**：局部变量、闭包、内联常量都行；拿查询行上的成员当边界要落方言日期算术，当场拒——改用 `DateIntersection`，或手写 `date >= begin && date < end.AddDays(1)` 自己控制。
- `endDate == DateOnly.MaxValue` 时上界那半边不发（它没有"下一天"）；`begin > end` 是合法的空段照发不拦（区间数学的空集）。列可空时 NULL 行不中——与手写 `>=` 同一比较语义。
- 三颗方法体没有实现：树外直接调（内存过滤）抛 `NotSupportedException` 并指路——先 `ToList()/ToListAsync()` 落到内存，再用 `DateOnly` 普通算术比。

## 3. 排序

```csharp
.OrderBy(o => o.CreatedAt).ThenByDescending(o => o.Amount)   // lambda
.OrderBy("CreatedAt desc, Amount")                            // 字符串：吃前端传来的整串排序键
.OrderBy(request.Sorting)                                     // 典型用法：HTTP 查询串原样进来
.ThenBy("Id", descending: true)                               // 形参给串里没写方向的颗
```

- 字符串形：逗号分颗，每颗 `名字` / `名字 asc|desc`（大小写不敏感）；每颗名字拿去契约上认列（先属性名、再列名、最后大小写不敏感唯一命中），**认不出当场抛，没有"原样拼进去"的退路**——不需要黑名单。
- 同一条链 `OrderBy` 之后接 `OrderBy` 是整串替换（LINQ 口径）；`ThenBy` 才是追加。
- 只在单表链（arity 1）上有；联表链要排另一侧的列用 lambda（引用导航自动联表，见 [07 篇](07-joins-and-navigation.md)）。
- `IncludeMany` 的子行序用 `OrderByMany(表达式, bool)`，没有字符串形。

## 4. 投影

```csharp
// 三种投影目标：匿名类型 / 具名 DTO（公共构造器按参数对位）/ 实体
var rows = await orm.Select<Order>()
    .Where(o => o.Amount > 100m)
    .Select(o => new OrderSummary(o.Id, o.CustomerName, o.Amount))
    .ToListAsync();
```

投影里读引用导航的成员会自动补 `LEFT JOIN`（见 [07 篇](07-joins-and-navigation.md)）。`Distinct()` 去重；`As("o")` 改主表别名（一般不用）。

## 5. 分页与总数

```csharp
var page = await orm.Select<Order>()
    .Where(o => o.State == OrderState.Paid)
    .OrderByDescending(o => o.CreatedAt)
    .ToPagedAsync(page: 1, size: 20);
// PagedResult<T> { Items, Page, Size, Total, HasNext, TotalPages }
```

`PagedOptions` 控制计数策略：

```csharp
public record PagedOptions(CountMethod CountMethod = CountMethod.Auto,
                           bool StripOrderingForCount = true, string? CacheKey = null);
public enum CountMethod { Auto, CountStar, CountOverWindow, SkipTotal, SeparateTransaction }
```

- `Auto`：默认。`Distinct` / 分组 / 带 `Skip/Take` 的链把整句包成派生表内层再数；普通链直接 `COUNT(*)`。
- `CountStar`：强制 `COUNT(*)`。
- `SkipTotal`：不算总数（`Total` 为 null），`HasNext` 按"多取一行"判断——跨片分页（[12 篇](12-split-tables-and-sharding.md)）与超大表翻页用这档。
- `CountOverWindow` / `SeparateTransaction`：占位未实现，选了当场抛。
- `StripOrderingForCount`（默认 true）：数总数时剥掉 `ORDER BY`。
- **无 `ORDER BY` 的分页默认抛**（T-SQL `OFFSET..FETCH` 硬性要求）：放开用 `Options.PagingOrdering = PagingOrderingPolicy.AutoOrderPrimaryKey`（自动补主键序）或 `Allow`。页码与大小按 `long` 防溢出，偏移超 `int` 当场抛并点名那一页。

## 6. 顶层聚合

```csharp
var total = await orm.Select<Order>().Where(o => o.CustomerId == 7).SumAsync(o => o.Amount);
var max   = await orm.Select<Order>().MaxAsync(o => o.Amount);
var avg   = await orm.Select<Order>().AverageAsync(o => o.Amount);   // Sum/Max/Min/Average 同形 + 同步版
var any   = await orm.Select<Order>().Where(o => o.Amount > 0m).AnyAsync();
```

按条件聚合的场景（"分组之后的 `SUM`"）当前没有链式入口——用 [14 篇原生 SQL](14-raw-sql.md) 或 [08 篇](08-groupby-aggregate.md)分组投影。

## 7. `IN` 查询与分块

```csharp
var ids = new List<int> { 1, 2, 3, /* … 3000 个 */ };
var rows = await orm.Select<Order>().Where(o => ids.Contains(o.Id)).ToListAsync();
// → Id IN (@p0,…,@p2098) OR Id IN (@p2099,…)：按 provider 参数预算自动分块（SQL Server 2100 / Oracle IN 1000 不再撞驱动）
```

`query.Contains(row)` 与子查询 `Contains` 见 [07 篇](07-joins-and-navigation.md)。

## 8. 跟踪与过滤器

- 链式默认**不跟踪**（裸 `orm.Select` 不进变更跟踪）；工作单元里用 `db.Set<T>()` 才跟踪，`AsNoTracking()` 显式声明不跟踪。
- `IgnoreQueryFilters()` 本链一条过滤器都不贴；`IgnoreQueryFilter<TFilter>()` 按注册键点名跳过（见 [11 篇](11-filters-behaviors-aop.md)）。
- `WithCache(key, ttl)` 要接缓存需实现 `ICacheAdapter` 赋给 `Aop.Cache`（仓库不带实现）。

## 9. 诊断

```csharp
var plan = orm.Select<Order>().Where(o => o.Id == 7).Explain();
Console.WriteLine(plan.ModelText);   // 查询模型文本（联了谁、选了哪些列一眼可见）
Console.WriteLine(plan.Sql);         // 方言语句
var sql = orm.Select<Order>().Where(o => o.Id == 7).ToSql(out var ps);   // 语句 + 参数值
```

**不连库也能产 SQL**：连接串填占位串即可（文本层不碰连接），单元测试与评审常用：

```csharp
var orm = new QuickSqlOrm(new QuickSqlOrmOptions
{
    Provider = new SqlServerProvider(SqlClientFactory.Instance),
    ConnectionString = "Server=offline;Database=never-connected;Integrated Security=True",
});
var sql = orm.Select<Order>().Where(o => o.Id == 7).ToSql();
```

---

[← 上一篇：05 删除与软删除](05-delete-and-soft-delete.md) · [下一篇：07 联表与导航 →](07-joins-and-navigation.md)
