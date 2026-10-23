# 结构同步功能（Schema Sync）使用指南

## 概述

结构同步可以按配置自动或手动同步实体模型到数据库表结构（§9.5）。它最初（v0.37 第九笔）只做"表是否缺失"这一档；v0.38 落地轮（2026-10-06）之后升级为**逐列比形状**：已经在的表会补缺失的列、改长度/标度/可空性、重发备注、补缺失的索引；0.39.0-beta.7 起，收紧 `NOT NULL` 之前还会先问库一句"存量里有没有空值"。

一句话版本：

- **缺的表**：整张建出来（`CREATE TABLE` + 能发出去的列/表备注）。
- **在的表**：逐列比对契约与库里的现形状，只发"没有的那一半"（补列、改列、发备注、建索引），**会丢数据的那一档一律不发**（不删列、不改主键与自增；删索引要显式开）。
- **判不了的档**（这一家读不回来）进 `NotJudged` 清单并指名，不冒充"已同步"。
- **分表实体跳过**并记名，片由 `orm.CreateSplitTables<T>(…)` 按业务给的范围建。

## 配置项

`QuickSqlOrmOptions` 里与结构同步相关的四格（DI 侧 `QuickSqlOrmBuilder` 的同名字段一一对应）：

```csharp
public sealed class QuickSqlOrmOptions
{
    // ... 其他配置 ...

    /// <summary>结构同步策略。默认 Disabled。</summary>
    public SchemaSyncPolicy SchemaSync { get; init; } = SchemaSyncPolicy.Disabled;

    /// <summary>非 Development 环境下强制执行结构同步的开关。默认 false。</summary>
    public bool ForceSchemaSync { get; init; } = false;

    /// <summary>已经在的表要不要逐列比形状（补列、改长度与标度、改可空、重发备注、补索引）。
    /// 默认 true（opt-out 而不是 opt-in）。关掉即退到"只有缺表才建"那一档。
    /// 会丢数据的那两档（删列、改主键与自增）不受它管——它们在计划层就拒了，任何开关都放行不了。</summary>
    public bool SchemaSyncColumns { get; init; } = true;

    /// <summary>库里多出来的那条索引要不要 DROP。默认 false：它与 SchemaSyncColumns 不是同一个
    /// 风险档——补一列不丢数据，删一条正在被用的索引会当场拖慢查询，所以这一档只能显式开。
    /// 唯一约束即便开了也不删（DROP INDEX 删不动约束）。</summary>
    public bool SchemaSyncDropIndexes { get; init; } = false;
}
```

DI 注册同样四格都给：

```csharp
services.AddQuickSqlOrm(DbKind.Sqlite, connectionString, builder =>
{
    builder.SchemaSync = SchemaSyncPolicy.Execute;  // 开发环境自动建表
    builder.ForceSchemaSync = true;                 // 示例跳过环境检查
    builder.SchemaSyncColumns = true;               // 默认已开
    builder.SchemaSyncDropIndexes = true;           // 删索引那一档，显式开
});
```

## 三档策略

### 1. Disabled（默认）

不自动同步。要手动调用 `SyncStructureAsync()`（同步壳 `SyncStructure()`）并自行决定执行方式；策略已是 Disabled 时它直接交回空结果。

```csharp
var orm = new QuickSqlOrm(new QuickSqlOrmOptions
{
    Provider = provider,
    ConnectionString = connectionString,
    SchemaSync = SchemaSyncPolicy.Disabled,  // 默认值
});

// 手动调用
var result = await orm.SyncStructureAsync();
```

### 2. Execute

直接执行 DDL。这一档**连库**：先按每张表自己声明的那座集群分组，一座一趟；每趟先把库确认存在（provider 声明 `CanCreateDatabase` 时自动建库，SQLite 那一族打开连接即建文件），然后对每张实体表：

- 库里没有 → 建表（`CREATE TABLE`，并顺手发这一家写得出的列/表备注）；
- 库里已有且 `SchemaSyncColumns` 开 → 逐列比形状，把计划里的每一句发出去（见下文"一张已有的表会改什么"）。

**只在 Development 环境有效**；非 Development 环境要求 `ForceSchemaSync=true`，否则启动期抛错。

```csharp
// Development 环境
Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Development");

var orm = new QuickSqlOrm(new QuickSqlOrmOptions
{
    Provider = new SqliteProvider(SqliteFactory.Instance),
    ConnectionString = "Data Source=app.db",
    SchemaSync = SchemaSyncPolicy.Execute,
    // ForceSchemaSync = false,  // Development 环境下不需要
});

var result = await orm.SyncStructureAsync();
```

### 3. Script

只生成 DDL 脚本，不执行、**不连库**。**这是生产环境的推荐做法**（§9.5）：返回的 `SchemaSyncResult.Ddl` 包含全部 `CREATE TABLE` 与建表趟写得出的备注语句，可交给 DBA 审批后手动执行。

注意这一档的边界：**列级 diff 在 Script 档判不了**——diff 的另一半是"库里的现形状"，不连库就没有读数。所以 Script 档只出建表脚本，并在结果的 `NotJudged` 清单里写明这一格（不是静默少做）。要列级的脚本，出路是给一个只读连接走 Execute。

```csharp
var orm = new QuickSqlOrm(new QuickSqlOrmOptions
{
    Provider = new SqlServerProvider(SqlClientFactory.Instance),
    ConnectionString = connectionString,
    SchemaSync = SchemaSyncPolicy.Script,
});

var result = await orm.SyncStructureAsync();

// 输出脚本
Console.WriteLine(result.Ddl);

// 或者保存到文件
File.WriteAllText("schema.sql", result.Ddl);
```

多集群时脚本按集群分段，每段带一行段头（只有缺省集群 `main` 一座时不加段头，脚本与不分库的旧形状逐字相等）：

```
-- 集群 reporting：下面这几句要落在这座集群的主库上

CREATE TABLE ...
```

## 一张已有的表会改什么

Execute 档里，已经在的表按 `StructureDiff` 出一张**计划**（`StructurePlan`），计划里的每一句发出去，**清单里没有的一句都不会发**。

### 会发的动作

| 动作 | 说明 |
|---|---|
| `ADD COLUMN` 补列 | 缺的列补上。三种拒（见下）之外都能加；用 `rowversion` 那一族"值归库给"的列是例外——NOT NULL 也不会撞 |
| `ALTER COLUMN` 改列 | 长度、标度、可空性对不上的列改回来。T-SQL 上整列定义重写，所以改精度与收紧可空性在同一句里 |
| 重发列/表备注 | 三类并集：新增的列、被改的列、备注本身变了的列。被 `ALTER` 过的列必须重发——ALTER 会连带抹掉那一列的备注，不发回去就是"同步成功而备注没了" |
| `CREATE INDEX` 建索引 | 契约上有、库里没有的索引。判据是**列的序列**而不是名字（人给索引换过名而列没换，不再建一条） |
| `DROP INDEX` 删索引 | 库里多出来、契约上没有的索引。**只在 `SchemaSyncDropIndexes=true` 才发**，否则只进"要人工"清单 |

### 计划层就拒掉的（永远不做，进 `Refused`）

- **库里多出一列**：结构同步不删列，删列会带走那一列的数据。
- **主键有任何不一致**（缺列、形状不同、列序不同）：改主键要连外键与索引一起重建，"建新表、拷数据、换名"那一族重建在本仓库未建模（§14）。
- **自增 / 值归库给的列**缺在库里或形状不符：加自增列要重建取值机制，T-SQL 压根不允许往已有表上加。
- **NOT NULL 的新列**而库里已有行没值可填：契约没有默认值那一格，Core 变不出一句 `DEFAULT`（出路：先按可空建、回填、再收紧）。
- **索引撞名**：库里的那条同名索引指向别的列序列，既不改它也不建撞名的那一条。
- **库里有契约外的唯一约束**：它不是索引，`DROP INDEX` 删不动，拆它要走 `ALTER TABLE … DROP CONSTRAINT`（与改主键同一处境）——即便开了删索引那一档也不碰。

### 这一家读不回来的（判不了，进 `NotJudged`）

- 列形状（`ColumnShapeMatches` 没接的档）、列备注读不回、表备注读不回、主键列读不回、索引清单读不回——逐档点名，**不冒充"判过了"**。
- 没有 `ALTER COLUMN` 语句的能力位（`Alter_Column_Precision`，今天只有 SQLite）时，改列整格不出：一句都不发、点名"要人工"，宿主不会死在发射点上。
- 备注写不出的那一族（建表趟与补备注趟同理）：语句根本不发，备注只留在契约里。

### 收紧 NOT NULL 之前先问库一句（0.39.0-beta.7）

把一条可空列收紧成 `NOT NULL` 的 `ALTER`，发之前逐列问一句"存量里有没有空值"（`ColumnHasNullsAsync` 三态探针）：

- **有空值** → 整句 `ALTER` 不发（精度/长度差也一并还挂着），写进 `SkippedNullabilityTightening` 并给出路：回填那一列，下一趟结构同步自己把它收紧，不需要谁改契约。
- **没有空值 / 本来就一样紧 / 是放宽方向** → 照发（放宽把约束放松，存量多少行都收得下，不问）。
- **探针答不出**（这一家没接，默认实现交回 null）→ 照发，并在 `NotJudged` 里欠一行"判不了"。

### 幂等

判据是**归一后的结构事实**（可空性 + 各家自己的形状匹配），不是类型文本逐字符比——目录视图回小写 `numeric`、发射侧是大写 `DECIMAL`，同一型不该重发一句。同一趟连跑两次，第二次**一个字节都不发**。

## 返回值

`SyncStructureAsync()` 返回 `SchemaSyncResult`：

```csharp
public sealed record SchemaSyncResult(
    IReadOnlyList<string> CreatedTables,   // 已创建（或要创建）的表名清单
    bool Executed,                          // 是否真发出去过至少一句
    string Ddl);                            // Script 模式下的完整 DDL 文本
```

- `IsEmpty`：这一趟一个字节都没发。六份动作清单（建表/补列/改列/备注/建索引/删索引）全空才算空；**判不了与要人工那两份话单不算动作**（判不了与拒的档本来就发不出东西，算进去会让幂等裁判永远红）。
- `Executed`：说的是"真发过话"，不是"连了库"——一趟只读的同步连了库、判了所有档、一个字节没发，这一格是 false。
- 新增五份动作清单：
  - `AddedColumns`：补上的列，逐项 `表.列`
  - `AlteredColumns`：`ALTER COLUMN` 过的列（长度、标度或可空性），逐项 `表.列`
  - `AppliedComments`：发过（或重发过）备注的列，逐项 `表.列`；表自身那条只写表名
  - `CreatedIndexes`：建出来的索引，逐项 `表.索引名`
  - `DroppedIndexes`：删掉的索引（默认配置下恒空——那一档要显式开）
- `DdlStatements`：这一趟的 DDL 原文，逐句一条、顺序 = 发送顺序。**Execute 档也看得到**：DDL 不走 `Aop.Executing` 那条日志钩子（同步连的是自己的连接），调用方要在日志外看见发了什么，只有这一格。
- 两份话单：
  - `NotJudged`：这一家**读不出**所以判不了的档，逐条指名。它非空就是"这一趟有几档根本没同步过"。
  - `Refused`：读得出而**故意不发**的格（删列、改主键与自增、删索引默认关、唯一约束），逐条指名并给出路。
- 两份跳过清单：
  - `SkippedSplitTemplates`：被跳过的分表模板（模板名、分片档与分片键列）。跳过不是漏掉——"要建哪几段"只有业务知道，见下文"分表实体"。
  - `SkippedNullabilityTightening`：没发出去的收紧 `NOT NULL` 那几句 `ALTER`，逐项 `表.列` 与原因。它与 `Refused` 不是一格：拒是"规则永远不做"，这一格是"库里现在有挡着它的东西"——回填完下一趟自己收紧。
- `Ddl`：Script 模式包含全部建表与备注语句；Execute 模式为空字符串（原文逐句在 `DdlStatements` 里）。

## 同步失败时报什么

Execute 档是一句一句各自发出去的（这一趟不把整批包进事务，而 MySQL 那一族本来也回滚不了 DDL），
所以失败之后库的形状是"前 N 句成了、第 N+1 句没成"。被库拒绝时抛 `SchemaSyncFailedException`，
消息里同时给出失败的哪一句、它改的是哪一格、以及在此之前已经生效的哪几句（清单最多逐句列 5 条，
首行报的是全部句数，全量在 `AppliedStatements` 属性里）：

```
结构同步失败：CreateTable SYNC_BOOM 那一句被库拒绝。目标库：sync-probe-db。
失败的句子：
CREATE TABLE [dbo].[SYNC_BOOM] ([Id] INT IDENTITY(1,1) NOT NULL, [Name] NVARCHAR(MAX) NULL, …)
已经生效的 1 句（DDL 不回滚，失败前发出去的都留下了）：
  1. CREATE TABLE [dbo].[SYNC_OK] ([Id] INT IDENTITY(1,1) NOT NULL, [Name] NVARCHAR(MAX) NULL, …)
库的回答：MySqlException: …
```

属性：`Sql`（失败那一句原文）、`Target`（表名或 `表.列`）、`ChangeKind`（`CreateTable` / `AddColumn` /
`AlterColumn` / `ColumnComment` / `TableComment` / `CreateIndex` / `DropIndex`）、`AppliedStatements`、
`Database`。驱动原话在 `InnerException` 里。

取消（`CancellationToken` 被叫停）不算库的拒绝，仍按 `OperationCanceledException` 原样抛出。

## 环境检查

Execute 模式在非 Development 环境下要求 `ForceSchemaSync=true`，否则抛 `InvalidOperationException`：

```
非 Development 环境下执行结构同步需要 ForceSchemaSync=true（当前环境：Production）。
生产库常禁 DDL 权限，请先用 SchemaSync=Script 生成脚本、交 DBA 审批后手动执行（§9.5）。
```

环境判断按以下优先级：
1. `DOTNET_ENVIRONMENT` 环境变量
2. `ASPNETCORE_ENVIRONMENT` 环境变量
3. 如果都为空，视为非 Development 环境（报错里写 `(未设置)`）

同一道闸门也拦着分表建片的各档入口（`CreateSplitTables` / `CreateSplitTablesAsync`，时间档与哈希档同理）——建表是 DDL，理由相同。

## 类型注册

`SyncStructure` 同步的是 `IEntitySchemaRegistry.RegisteredTypes` 中的所有实体类型。注册表是惰性的：类型在以下时机被解析进册：

- 调用 `orm.Schemas.Contract<T>()`
- 调用 `orm.Set<T>()`
- 调用 `orm.Select<T>()`、`orm.Insert<T>()` 等

**建议在调用 `SyncStructure()` 之前，先触发所有实体的契约解析**：

```csharp
// 显式注册
_ = orm.Schemas.Contract(typeof(Customer));
_ = orm.Schemas.Contract(typeof(Order));
_ = orm.Schemas.Contract(typeof(OrderItem));

// 或者用查询触发
_ = orm.Select<Customer>().ToSql();
_ = orm.Select<Order>().ToSql();

// 然后再同步
await orm.SyncStructureAsync();
```

## 分表实体

分表实体（`[SplitTable]`）在整库同步里是**跳过并记名**的（进 `SkippedSplitTemplates` 清单）：模板名带着占位符，它不是一张能建的表，而"把全部片都建出来"要的范围只有业务知道——默认建全部片在一颗日档的表上等于凭空造几千张。

片由专门的入口建，范围必填：

```csharp
// 时间档：按时间范围建缺失的片（起与止各自下整到本档的周期，中间逐周期展开）
var shards = await orm.CreateSplitTablesAsync<Order>(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31));

// 哈希档：按桶号建
await orm.CreateSplitTablesAsync<Order>([0, 1, 2]);
```

两颗都有不连库的脚本版 `orm.SplitTablesScript<T>(…)`——分表实体在 Script 档整库同步里也进不了脚本，这一格是它们唯一的出脚本口。逐张先问存在再发 DDL，同一个范围跑两次第二趟一张也不建（幂等）。产物 `SplitTablesResult` 三格分得清：`Requested`（换算出的片清单）/ `Created`（真建了的）/ `AlreadyThere`（本来就在的）；另有 `IndexesNotCreated`——**索引不随片发**（索引名在 PG 与 Oracle 那一族是模式内唯一的，第二张片就撞），错误的理由与出路都在那一格里点名。

## 多集群

实体用 `[DataSource("集群名")]` 点名自己住哪座库（§16.4）。整库同步**一次调用 = 在册的每座集群各一趟**：每张表按它自己的声明落库，缺省集群那张顺序与不分库的旧形状逐字同形。分组判据与读写共用同一颗路由判据，所以"这张表建在哪座库"与"这条 INSERT 发往哪座库"不会是两件事——表建在 A 库、写进 B 库，症状本来就是运行期"表不存在"。

实体写了配置里没有的集群名时，**同步开始时就抛**（消息里同时带着那张表的名字与在册的那几座库），连库之前就把这笔账算清。

## 示例：Development 环境自动同步

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);

services.AddQuickSqlOrm(DbKind.Sqlite, builder.Configuration.GetConnectionString("Default"), b =>
{
    b.SchemaSync = SchemaSyncPolicy.Execute;  // Development 环境自动执行
});

var app = builder.Build();
var orm = app.Services.GetRequiredService<QuickSqlOrm>();

// 注册实体类型（触发契约解析）
_ = orm.Schemas.Contract(typeof(Customer));
_ = orm.Schemas.Contract(typeof(Order));

// 自动建缺失的表 + 已有表逐列同步
var result = await orm.SyncStructureAsync();

Console.WriteLine($"新建 {result.CreatedTables.Count} 张表 ∶ 补列 {result.AddedColumns.Count} ∶ " +
                  $"改列 {result.AlteredColumns.Count} ∶ 备注 {result.AppliedComments.Count} ∶ " +
                  $"建索引 {result.CreatedIndexes.Count} ∶ 删索引 {result.DroppedIndexes.Count}");

foreach (var line in result.NotJudged)
    Console.WriteLine($"  判不了：{line}");

foreach (var line in result.Refused)
    Console.WriteLine($"  要人工：{line}");

app.Run();
```

同一趟再跑一次应该交回 `result.IsEmpty == true`——那是同步判据写对了的唯一裁判。

## 示例：Production 环境生成脚本

```csharp
// Program.cs
var connectionString = builder.Configuration.GetConnectionString("Default");

var orm = new QuickSqlOrm(new QuickSqlOrmOptions
{
    Provider = new SqlServerProvider(SqlClientFactory.Instance),
    ConnectionString = connectionString,
    SchemaSync = SchemaSyncPolicy.Script,  // 只生成脚本，不连库
});

// 注册实体类型
_ = orm.Schemas.Contract(typeof(Customer));
_ = orm.Schemas.Contract(typeof(Order));

var result = await orm.SyncStructureAsync();

if (result.Ddl is { Length: > 0 })
{
    // 保存脚本供 DBA 审批
    var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
    File.WriteAllText($"schema_{timestamp}.sql", result.Ddl);

    Console.WriteLine($"已生成结构同步脚本：schema_{timestamp}.sql");
    Console.WriteLine($"涉及 {result.CreatedTables.Count} 张表：{string.Join(", ", result.CreatedTables)}");
}
```

这一档是刻意的边界，不是静默少做：列级 diff 的另一半是库里的现形状，没有读数就没有差别可判。要列级的脚本，出路是给一个只读的连接、走 Execute 档（判据和"有没有空值"那颗探针都只需要读）。

## 设计理由

信创项目现实（§9.5）：
- 生产库常禁 DDL 权限，DBA 审批流程不可绕过
- 默认不执行，防止误操作
- Script 模式生成标准 DDL，可交给 DBA 审批后手动执行
- Execute 模式只在 Development 环境有效，降低开发成本

这与 §9.5 的交付形态对齐：
- `quicksql doctor`：探测驱动、连接、字符集、量纲
- `quicksql migrate diff`：只读结构比对，输出差异
- `quicksql migrate script`：生成脚本，生产默认走脚本

风险分档的另一条纪律：**补一列与删一条索引不是一个风险档**。`SchemaSyncColumns` 能默认开，是因为补列、发备注、补索引都不丢数据；`SchemaSyncDropIndexes` 必须显式开，是因为删掉一条正在被用的索引当场把查询拖成全表扫。会丢数据的那两档（删列、改主键与自增）没有开关。

## 落地记

| 时点 | 内容 |
|---|---|
| v0.37 第九笔 | 结构同步首落地：只判"表是否缺失"，缺的建表 |
| 2026-10-05（§16.4） | 分表实体在整库同步里跳过并记名；片由 `CreateSplitTables` / `SplitTablesScript` 按业务范围建 |
| 2026-10-06（v0.38 落地轮） | "只建缺表"升级为逐列比形状：补列、改列、重发备注、建索引；删索引另起 opt-in 档；两档永久拒落进计划层，不留给执行层判断 |
| 0.39.0-beta.7 | 收紧 `NOT NULL` 之前先问库一句（三态探针）；没有改列能力位的家不再死在发射点上；能力位 `Alter_Column_Precision` 有了读者 |
