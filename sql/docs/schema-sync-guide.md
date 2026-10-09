# 结构同步功能（Schema Sync）使用指南

## 概述

v0.37 第九笔新增了结构同步功能，可以按配置自动或手动同步实体模型到数据库表结构。今天只做"表是否缺失"这一档 diff（§14 P2 会补列级 diff）。

## 配置项

在 `QuickSqlOrmOptions` 中新增两个配置项：

```csharp
public sealed class QuickSqlOrmOptions
{
    // ... 其他配置 ...
    
    /// <summary>结构同步策略。默认 Disabled。</summary>
    public SchemaSyncPolicy SchemaSync { get; init; } = SchemaSyncPolicy.Disabled;
    
    /// <summary>非 Development 环境下强制执行结构同步的开关。默认 false。</summary>
    public bool ForceSchemaSync { get; init; } = false;
}
```

## 三档策略

### 1. Disabled（默认）

不自动同步。要手动调用 `SyncStructure()` 并自行决定执行方式。

```csharp
var orm = new QuickSqlOrm(new QuickSqlOrmOptions
{
    Provider = provider,
    ConnectionString = connectionString,
    SchemaSync = SchemaSyncPolicy.Disabled,  // 默认值
});

// 手动调用
var result = orm.SyncStructure();
```

### 2. Execute

直接执行 DDL 创建缺失的表。**只在 Development 环境有效**；非 Development 环境要求 `ForceSchemaSync=true`，否则启动期抛错。

```csharp
// Development 环境
Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Development");

var orm = new QuickSqlOrm(new QuickSqlOrmOptions
{
    Provider = provider,
    ConnectionString = connectionString,
    SchemaSync = SchemaSyncPolicy.Execute,
    // ForceSchemaSync = false,  // Development 环境下不需要
});

// 启动时自动同步
orm.SyncStructure();
```

```csharp
// Production 环境
Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Production");

var orm = new QuickSqlOrm(new QuickSqlOrmOptions
{
    Provider = provider,
    ConnectionString = connectionString,
    SchemaSync = SchemaSyncPolicy.Execute,
    ForceSchemaSync = true,  // 必须显式开启，否则抛错
});

orm.SyncStructure();
```

### 3. Script

只生成 DDL 脚本，不执行。返回的 `SchemaSyncResult` 包含完整 DDL 文本，可交给 DBA 审批后手动执行。**这是生产环境的推荐做法**（§9.5）。

```csharp
var orm = new QuickSqlOrm(new QuickSqlOrmOptions
{
    Provider = provider,
    ConnectionString = connectionString,
    SchemaSync = SchemaSyncPolicy.Script,
});

var result = orm.SyncStructure();

// 输出脚本
Console.WriteLine(result.Ddl);

// 或者保存到文件
File.WriteAllText("schema.sql", result.Ddl);
```

## 返回值

`SyncStructure()` 返回 `SchemaSyncResult`：

```csharp
public sealed record SchemaSyncResult(
    IReadOnlyList<string> CreatedTables,  // 已创建（或要创建）的表名清单
    bool Executed,                         // 是否真正执行了 DDL
    string Ddl);                           // Script 模式下的完整 DDL 文本
```

- `IsEmpty`：没有需要同步的表（全部已存在，或没有注册任何实体）
- `CreatedTables`：本次同步涉及的表名
- `Executed`：Execute 模式下为 true，Script/Disabled 模式下为 false
- `Ddl`：Script 模式下包含完整 DDL，Execute 模式下为空字符串

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
3. 如果都为空，视为非 Development 环境

## 类型注册

`SyncStructure()` 同步的是 `IEntitySchemaRegistry.RegisteredTypes` 中的所有实体类型。类型在以下时机被注册：

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
orm.SyncStructure();
```

## 今天的能力边界

**今天做的**：
- 检查表是否存在（`ISchemaReader.ReadTableAsync` 返回 null）
- 生成 CREATE TABLE DDL（`ICodeFirstDialect.EmitCreateTable`）
- 按策略执行或收集 DDL

**今天不做的**（§14 P2）：
- 列级 diff（ADD COLUMN、ALTER COLUMN）
- 索引 diff（CREATE INDEX、DROP INDEX）
- 主键、外键、约束的 diff
- 列注释、默认值的 diff

原因：`ISchemaReader` 读回的 `SchemaColumn` 不含主键、自增、注释、默认值，列级 diff 要等这些字段补齐后才能做。

## 示例：Development 环境自动同步

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);

var orm = new QuickSqlOrm(new QuickSqlOrmOptions
{
    Provider = SqlServer.Provider(SqliteFactory.Instance),
    ConnectionString = builder.Configuration.GetConnectionString("Default"),
    SchemaSync = SchemaSyncPolicy.Execute,  // Development 环境自动执行
});

// 注册实体类型
_ = orm.Schemas.Contract(typeof(Customer));
_ = orm.Schemas.Contract(typeof(Order));

// 启动时自动同步缺失的表
orm.SyncStructure();

builder.Services.AddSingleton(orm);

var app = builder.Build();
app.Run();
```

## 示例：Production 环境生成脚本

```csharp
// Program.cs
var orm = new QuickSqlOrm(new QuickSqlOrmOptions
{
    Provider = SqlServer.Provider(SqliteFactory.Instance),
    ConnectionString = builder.Configuration.GetConnectionString("Default"),
    SchemaSync = SchemaSyncPolicy.Script,  // 只生成脚本
});

// 注册实体类型
_ = orm.Schemas.Contract(typeof(Customer));
_ = orm.Schemas.Contract(typeof(Order));

var result = orm.SyncStructure();

if (!result.IsEmpty)
{
    // 保存脚本供 DBA 审批
    var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
    File.WriteAllText($"schema_{timestamp}.sql", result.Ddl);
    
    Console.WriteLine($"已生成结构同步脚本：schema_{timestamp}.sql");
    Console.WriteLine($"涉及 {result.CreatedTables.Count} 张表：{string.Join(", ", result.CreatedTables)}");
}
```

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
