# FastSql ORM 使用说明

> 面向使用者的一份操作文档：怎么集成、怎么配置、怎么使用。设计理由与判据在 `docs/fastsql-orm-design.md`（本文简称"规格"），本文不重复论证，只在需要指认出处时给出 `文件:行`。
>
> 本文里出现的每一个 API 名字都在工作树的 `src/` 下读到过真实定义；凡属"设计意图而尚未交付"的地方一律写明。

## 0. 这份文档是什么，现在真实的状态是什么

三段：§1 集成、§2 配置、§3 使用；§4 是边界与坑，§5 是一张 provider 总表。它是用法文档，不是设计文档 —— 想知道"为什么是这个形状"请读规格。

真实状态（三句，句句可核）：

1. **文本层落地的 provider 有 16 颗叶子**：`src/` 下 16 个 `FastSql.Providers.<库种>` 工程各暴露一个 `public const string DriverKey`（各家取值见 §5）。另有 3 个血统基类工程（`PostgreSqlBase`/`OracleBase`/`MySqlBase`）不是可寻址的库种。`DbKind` 枚举今天有 16 个值（`src/FastSql.Abstractions/DbKind.cs`），与 16 颗叶子一一对应、**没有"有值而无 provider"的空档**（`Sqlite` 于 v0.37 第四笔落地补齐最后一格；原本占着的 `MariaDb` 值于 v0.37 第五笔从枚举撤下——本仓库裁"不立 MariaDB 这一档"，理由与重开条件见设计文档 §14）。
2. **十六家里只有三家有真库见证，而且见证很窄。** SQL Server 一族的每一位能力都由集成层在两台实例上跑过（`CapabilityLedgerTests.cs` 类注释明写"T-SQL 那一族不进这份台账，不是漏记"）；虚谷有 7 位中的 4 位被钉为 `Live`，见证是把基线文本逐句敲进那台库；SQLite 有 5 位中的 3 位被钉为 `Live`（`Paging_LimitOffset`／`Returning`／`MultiStatement`），而这三颗走的是**另一条路**——集成册里 ORM 自己拼出来的语句真在临时文件库上跑过，没有一行手写文本参与，其中 `MultiStatement` 是这本账第一颗落在 DDL 侧的 `Live`（见证是事后翻库看到 `IX_FastSqlOrder_CreatedAt` 在场）。其余十三家 `PinnedLive` 全是 0，**驱动从未在一台机器上解析成功过**（`FastSqlDrivers.json` 各行 `$comment` 逐行自陈）。§4.2 把这三处的口径差异原样列出。
3. 离线证据是自产的：`tests/FastSql.Tests.Dialects/golden/` 下七个目录共 125 份基线（sqlserver 51 / kingbase 18 / dameng 18 / mysql 11 / gbase8s 10 / xugu 6 / sqlite 11）。除 §4.2 点到的那几份之外，它们是"本仓库自己认为应该长这样"的锁，不是厂商确认——**SQLite 那一套 11 份也是自产基线：本家有活库，而活库跑的是 ORM 产出的语句（第 2 点那三颗 `Live`），不是把这 11 句原样重放**，所以"黄金样本 ↔ 真库执行"这一对在本家仍然没配上，配上的是另一条证据链（provider 产出 ↔ 真库执行）。其余九颗叶子（postgresql/opengauss/gaussdb/highgo/gbase8c/oracle/shentong/tidb/oceanbase）没有自己的 golden 目录，同形部分由 `DialectParityTests` 逐字对拍钉住。

所以本文的读法是：**接口形状可信；除 SQL Server 之外的执行结果基本未证。**

## 1. 怎么集成

### 1.1 SDK 与目标框架

| 项 | 值 | 出处 |
| --- | --- | --- |
| SDK | `10.0.400`，`rollForward: latestFeature` | `global.json` |
| TFM | `net10.0`（全仓库单值） | `Directory.Build.props` |
| 语言版本 | `preview`；`Nullable enable`、`ImplicitUsings enable` | `Directory.Build.props` |
| 警告策略 | 库工程 `TreatWarningsAsErrors true`，且开 `IsAotCompatible`/`EnableTrimAnalyzer` | `Directory.Build.props` |

唯一的例外是 `FastSql.Generators`，它是 `netstandard2.0` —— Roslyn 分析器主机的硬约束，不是没改干净。

### 1.2 程序集清单与职责

`FastSql.Abstractions`（命名空间 `FastSql` 与 `FastSql.*`）是公开契约：`DbKind`、`DbCapabilities`、`IDbProvider` 与它身上那 11 颗子 SPI（连 `Kind` 与 `Capabilities` 共 13 格）、元数据契约、查询/写/跟踪接口。`FastSql.Core`（命名空间 `FastSql`）是内核：`FastSqlOrm`、`DbContext`、QueryModel→AST→文本、驱动加载、物化、事务。`FastSql.Generators` 是 G1 那张 Roslyn 增量生成器，把带 `[Table]` 的实体在编译期翻成契约。15 个 `FastSql.Providers.<库种>` 叶子工程各家一册（方言、类型映射、键策略、DDL 发射），另有 `FastSql.Providers.{PostgreSql,Oracle,MySql}Base` 三个血统工程承载族内共享的那一层，叶子只补差异。

引用链单向：provider → `FastSql.Core` → `FastSql.Abstractions`（`src/FastSql.Providers.SqlServer/FastSql.Providers.SqlServer.csproj` 只有对 Core 的一行 `ProjectReference`）。

### 1.3 应用侧要引用什么

`ProjectReference` 传递，所以引 Core + 用到的 provider 就够，Abstractions 跟着进来；生成器以分析器身份挂进去：

```xml
<ProjectReference Include="..\src\FastSql.Core\FastSql.Core.csproj" />
<ProjectReference Include="..\src\FastSql.Providers.SqlServer\FastSql.Providers.SqlServer.csproj" />
<!-- 只挂进编译器，不作为引用；写成普通引用会让宿主看到生成器的内部类型 -->
<ProjectReference Include="..\src\FastSql.Generators\FastSql.Generators.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

形态取自 `tests/FastSql.Tests.Common/FastSql.Tests.Common.csproj`（那里为了跨族对拍引了 18 个 provider 工程，普通应用只引自己要的那家）。

**驱动程序集不要引。** `Directory.Packages.props` 里 `Microsoft.Data.SqlClient` 那行写着"驱动包只出现在 tests/"，`src/` 下任何 provider 都不编译期引用驱动（§1.2 原则 3）。要连库时驱动走 §1.4 那条运行期链，或者由宿主自己注入工厂（§1.6）。

### 1.4 仓库真实布局与驱动摆放约定

`FastSql.slnx` 收 21 个 `src/` 工程 + 两个测试工程（`FastSql.Tests.Dialects`、`FastSql.Tests.Integration`）；`FastSql.Tests.Common` 是被引用方而不是解决方案节点，基准测试工程刻意不入册。

驱动 DLL 的约定目录是 **输出目录下的 `drivers/`**。信创现场换库不改代码、不改 csproj：把厂商那份 ADO.NET 程序集丢进 `drivers/` 即可，这是 `FastSqlDrivers.json` 里 `path` 省略时的第一探测位（§1.5）。

清单文件本身**必须复制到输出目录**，否则 `DriverManifest.Load()` 静默返回 `Empty`，症状是"驱动不存在"这种看不出原因的红。仓库内的做法是从 `tests/FastSql.Tests.Integration/FastSql.Tests.Integration.csproj` 那行 `None Include + Link + CopyToOutputDirectory` 复刻的。清单的默认落点由 `DriverManifest.DefaultPath` 给出。

`drivers/` 只解决"托管程序集在哪"。虚谷那一族还要一份**原生同伴**（`xugusql.dll` / `libxugusql.so`），必须和托管那份一起放：只放托管时清单解析照样成功，要到 `Open` 才抛 `DllNotFoundException`（`FastSqlDrivers.json` 的 xugu 行 `$comment`，v0.32 实测）。

### 1.5 `FastSqlDrivers.json` 逐字段

```json
{ "drivers": [
  { "key": "sqlserver", "assembly": "Microsoft.Data.SqlClient",
    "factoryType": "Microsoft.Data.SqlClient.SqlClientFactory" },
  { "key": "dameng", "assembly": "DmProvider" }
] }
```

| 字段 | 必填 | 含义 |
| --- | --- | --- |
| `drivers` | 是 | 数组，缺它整份清单不认 |
| `key` | 是 | 与 `DbKind` 名同形的小写字符串；`DriverRegistry.Find` 大小写不敏感 |
| `assembly` | 是 | 托管程序集名，也是 `drivers/<assembly>.dll` 的探测名 |
| `path` | 否 | 显式文件路径；省略时走探测链 |
| `factoryType` | 否 | `DbProviderFactory` 子类全名；省略时扫导出类型里第一个非抽象子类 |
| `$comment` | 否 | 纯说明，解析器不读（15 行里 14 行带，sqlserver 那行没有） |

字段口径来自 `src/FastSql.Core/DriverLoading/DriverRegistry.cs`：清单用 `JsonDocument` 手解（不引 `JsonSerializer`，避免反射），解出来的形状是 `record DriverRef(string Key, string AssemblyName, string? Path, string? FactoryType)`。

`factoryType` 常被刻意省略：达梦行那句理由写得很直白 —— 按文档猜一个全名，可能把一次本来能成的解析变成"找不到类型"。

### 1.6 解析顺序与失败行为

清单文件的查找顺序：显式传入的 `path` → 环境变量 `FASTSQL_DRIVERS_CONFIG` → `AppContext.BaseDirectory/FastSqlDrivers.json`；三处都没有 ⇒ `DriverManifest.Empty`（不抛）。

单颗 `key` 的解析顺序：

1. 声明了 `path` ⇒ `Path.GetFullPath` 归一化后判存在；不存在直接抛 `DriverNotAvailableException`，消息里带那句"声明的驱动文件不存在"。归一化是 v0.32 补的分隔符坑：写 `drivers/XuguClient.dll`（正斜杠）时托管加载会成功、原生同伴却找不到，一路绿到 `Open` 才炸。
2. 未声明 `path` ⇒ 探 `<root>/drivers/<assembly>.dll`，命中就用独立的 `DriverLoadContext` 加载。
3. 没命中 ⇒ `Assembly.Load(new AssemblyName(assembly))`，`Microsoft.Data.SqlClient` 这类公开包走这一支。
4. 工厂取法：先看目标类型上公开的静态 `Instance` 字段，没有再退到无参构造器。

失败会被缓存（同一颗 key 第二次不会重新试），异常消息带"程序集 {AssemblyName}，清单路径 {Path ?? "<probing>"}"，所以能一眼看出是谁替谁背的锅。入口是 `DriverRegistry.Load(configPath?)`，取工厂用 `Factory(key)`（标了 `RequiresUnreferencedCode`）与 `TryFactory(...)`。

另一条路完全绕开清单：`DriverRegistry.Register(key, factory)` 注入已经在进程里的工厂。生产上更常见的形态其实是这个 —— 宿主自己引用驱动包，把 `DbProviderFactory` 交出来；provider 的构造函数本来就吃工厂（`new SqlServerProvider(factory)`，见 `tests/FastSql.Tests.Common/SqlServer.cs`）。

### 1.7 一条真实的 AOT 约束

内核不做 `Type.GetType(string)`、不按名字反射装配 provider：**provider 由调用方构造并交进 `FastSqlOrmOptions.Provider`**（`tests/FastSql.Tests.Common/SqlServer.cs` 的类注释把这件事叫"组装根本该做的事"）。运行期唯一带反射的入口是清单解析，而那一步是手解 JSON + 读 `Instance` 字段，不在热路径上。

代价也写清楚了：`FastSqlOrm` 的单参构造器（`src/FastSql.Core/Orchestration/FastSqlOrm.cs:52`）标了 `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]`，因为它顺带建 G2 反射注册表；干净口是 `FastSqlOrm(FastSqlOrmOptions, IEntitySchemaRegistry)`（同文件 `:58`），**构造期零反射**。`Select<T>()`（`:121`）今天整条读路径仍带同一串标注 —— 那是入口税，G1 预翻译落地之前摘不掉（设计意图，不是已交付）。

### 1.8 一行注册：`FastSqlOrm.Create(DbKind, ...)`

上面 §1.6 与 §1.7 那两段的合起来是"手写注册"的路径：宿主自己 new 一颗 provider、把它塞进 `FastSqlOrmOptions.Provider`。v0.37 第七笔在此之上补了一等公民口，两颗重载（`src/FastSql.Core/Orchestration/FastSqlOrm.cs:76`、`:88`）：

```csharp
// (a) 工厂已经在手：DI 容器给了、或者 DriverRegistry 已经 Load 过
var orm = FastSqlOrm.Create(DbKind.MySql, "Server=...;Database=app;", SqlClientFactory.Instance);

// (b) 一行到库：DbKind + 连接串，工厂由 FastSqlDrivers.json 现场解析（走 §1.6 那条探测链）
var orm = FastSqlOrm.Create(DbKind.PostgreSql, "Host=...;Database=app;");
```

**触发条件是"生成器挂进了编译"。** `src/FastSql.Abstractions/Providers/ProviderRegistration.cs` 里那两颗（`[FastSqlProvider(DbKind)]` 属性、`ProviderRegistry` 静态表）是登记面的运行时结构，实际填表的是 `src/FastSql.Generators/ProviderRegistrationGenerator.cs`——它订阅 `context.CompilationProvider`、扫被引用程序集上带属性的 leaf 类（每颗 provider 类今天都同时带 `public const string DriverKey` 与 `public XxxProvider(DbProviderFactory)` 构造器，缺一颗就跳过），产一段带 `[ModuleInitializer]` 的 `FastSql.Generated.GeneratedProviders`，每行一句 `ProviderRegistry.Register(DbKind.<X>, "<driverKey>", static f => new <Ns>.<Xxx>Provider(f));`。产出全是静态 `new` 的委托，零反射、AOT 与 trim 都安全（§1.7 那条原则一字未破）。

**没挂生成器就零登记**，此时两行 `Create` 抛的是 `InvalidOperationException` 且消息点名"生成器 FastSql.Generators 未作为 Analyzer 挂进编译"——症状可查，不是"驱动不存在"那种看不出原因的红。挂法与 §1.3 里 Roslyn 生成器那三行 `ProjectReference` 同一条：

```xml
<ProjectReference Include="..\src\FastSql.Generators\FastSql.Generators.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

**只登记你真引用的那几家。** provider 是编译期直接调用、不是按名字反射装配，所以引用图上没有的库种不会污染表；`ProviderRegistry.Registered` 读回来正好是"这个消费者能 `Create` 到哪几颗"的清单。这一句也就是 §1.7 那句"provider 由调用方构造"的编译期兑现：调用方从"亲手 new"降级到"引用一份程序集"，构造仍然静态、仍然可 AOT，只是这一行不写了。

`Create` 走的是 §1.7 那条带 `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]` 的 G2 兜底口（因为要顺带建反射注册表）。AOT 干净路径仍然是自己 new provider + `new FastSqlOrm(options, schemas)`——`Create` 是给非 AOT 场景省事的糖，不是替代那条。

## 2. 怎么配置

### 2.1 `FastSqlOrmOptions`：六格，全在这

`src/FastSql.Core/Orchestration/FastSqlOrm.cs`，从第 23 行起：

| 成员 | 行 | 默认 | 说明 |
| --- | --- | --- | --- |
| `Provider` | 25 | 必填 | `IDbProvider`，由调用方构造 |
| `ConnectionString` | 28 | 必填 | 只走驱动的 ADO.NET 语义；只产 SQL 时填占位串（§3.0） |
| `Aop` | 30 | `new()` | 钩子容器，见 §2.5 |
| `Model` | 33 | `null` | Fluent 映射，见 §2.2 |
| `CommandTimeoutSeconds` | 35 | `30` | 透传给命令 |
| `PagingOrdering` | 38 | `PagingOrderingPolicy.Throw` | `Skip`/`Take` 无 `OrderBy` 时怎么办：`Throw`/`AutoOrderPrimaryKey`/`Allow` |

`FastSqlOrm` 上可读的属性：`Options`、`Provider`、`Schemas`、`Aop`、`Compiler`、`Ado`、`PagingOrdering`、`Filters`。`orm` 是单例级对象（不持有连接），`DbContext` 才是 Scoped。

### 2.2 实体映射：两条路

**路 A：标注。** 三种类 + 一个枚举，都在 `src/FastSql.Abstractions/Metadata/FastSqlAttributes.cs`：

- `[Table(string name)]`，属性 `Name`、`Schema?`、`Comment?`
- `[Column(string? name = null)]`，属性 `Name`、`DbTypes`、`Length`、`Precision`、`Scale`、`IsNullable`/`IsPrimary`/`IsIdentity`（`TriState`：`Unset`/`Yes`/`No`）、`IsIgnore`、`Comment`
- `[Index(string name, params string[] memberNames)]`，属性 `Unique`、`Clustered`、`Descending`
- `[ConcurrencyToken]`（只能挂属性上；语义是"只比较不书写"）

DataAnnotations 同样认（`[Key]`、`[Required]`、`[StringLength]`、`Schema.Table`、`Schema.Column(name/TypeName)`、`NotMapped`），现场见 `tests/FastSql.Tests.Common/Model.cs` 里的 `LegacyShipment`。

约定的那一层：整型主键 `Id` ⇒ 主键 + 自增；C# 可空 ⇒ 可空列；无长度字符串 ⇒ provider 默认（SQL Server 落 `NVARCHAR(MAX)`）。导航属性不成列。

**路 B：Fluent。** `src/FastSql.Abstractions/Metadata/Fluent/ModelConfiguration.cs`，今天真实存在的成员只有这些：

```csharp
var model = new ModelConfiguration();

model.Entity<Order>().ToTable("FastSqlOrder", "app");            // 第二参是 schema
model.Entity<Order>().HasComment("订单主表");
model.Entity<Order>().HasIndex(nameof(Order.CreatedAt));
model.Entity<Order>().HasUniqueIndex(nameof(Order.CustomerName));
model.Entity<Order>().Property(o => o.Note).HasDbType("xml");
model.Entity<Order>().Property(o => o.Amount).HasPrecision(18, 4);
model.Entity<InventoryItem>().Property(i => i.RowVersion).IsConcurrencyToken(false);
```

`PropertyBuilder` 全量：`HasMaxLength`、`HasPrecision(int, int)`、`HasDbType(string)`、`HasComment`、`IsRequired(bool = true)`、`IsKey(bool = true)`、`IsIdentity(bool = true)`、`IsIgnore()`、`IsConcurrencyToken(bool = true)`。**没有** `HasColumnName`（列名走 `ToTable`/`[Column]` 那条），也没有关系配置、鉴别器、影子属性。

**层序**（`MetadataLayer`，低层不覆高层）：`ProviderFallback=0` → `Convention=1` → `DataAccessAnnotation=2` → `FastSqlAttribute=3` → `Fluent=4`。每一列的来历记在 `ColumnContract.Origin`（`record OriginTrace(MetadataLayer Layer, string Note)`），能读出"并发 ← Fluent IsConcurrencyToken"这种结论。同一层重复声明同一事实会抛 `MetadataConflictException`。

**G1 接线**：挂上分析器之后，带 `[Table]` 的实体会在编译期生成 `FastSql.Generated.FastSqlGeneratedMetadata.TryGet`。要用它得把注册表交给干净口：

```csharp
var schemas = new GeneratedEntitySchemaRegistry(provider, FastSqlGeneratedMetadata.TryGet);
var orm = new FastSqlOrm(options, schemas);
```

形状取自 `tests/FastSql.Tests.Dialects/MetadataEquivalenceTests.cs`。没标注的实体（如 `BareReport`）`TryGet` 返回 false，落回 G2 反射；两条路径的契约逐字段相等由该文件对拍钉住。注意：**被 Fluent 配置过的实体也走 G2**（生成器注释自陈）。

### 2.3 标识符策略：迁移时最硬的一格

`IdentifierPolicy` 不是枚举而是 record（`src/FastSql.Abstractions/Metadata/IdentifierPolicy.cs`）：位置参数依次是 `OpenQuote`、`CloseQuote`、`CaseSensitive`、`FoldToUpperCase`、`QuoteWhenMixedCase`、`QuoteAlways = false`；成员 `Quote(string)`、`NeedsQuote(string)`、`Normalize`。七颗预设与后果（行号为该文件内）：

| 预设 | 行 | 引号 | CS | 折大写 | 混合大小写才引 | 一律引 |
| --- | --- | --- | --- | --- | --- | --- |
| `SqlServer` | 32 | `[` `]` | false | false | false | **true** |
| `PostgreSQL` | 34 | `"` `"` | true | false | true | false |
| `OracleFamily` | 36 | `"` `"` | true | **true** | true | false |
| `MySql` | 49 | 反引号 | false | false | false | **true** |
| `Xugu` | 83 | `"` `"` | true | **true** | true | false |
| `Gbase8s` | 101 | `"` `"` | true | false | true | false |
| `Sqlite` | 136 | `"` `"` | false | false | false | **true** |

这不是纸面分类，产出上直接看得境：同一份 `Order` 模型，金仓发 `"t0"."CustomerName"`，达梦与虚谷发 `"T0"."CUSTOMERNAME"`（折大写），MySQL 发反引号原样，GBase 8s 发 `"t0"."CustomerName"` 且别名不带 `AS`。四份文本都在 golden 里。

一个已知的空档：`FoldToUpperCase` **只有"折大写"这一格，没有"折小写"**。GBase 8s 与 PG 在库里折小写，而发射器不折 —— 也就是说这一族的名字大小写后果要现场自己承担（规格 §9.2 记过，本文按代码现状复述）。

### 2.4 能力位：`DbCapabilities`

31 颗具名位 + `None`（`src/FastSql.Abstractions/DbCapabilities.cs`），每颗一个 `1<<n`：`Paging_LimitOffset`、`Paging_RowNum`、`Paging_OffsetFetch`、`Returning`、`Returning_OnlyExpression`、`Upsert_OnConflict`、`Upsert_MergeInto`、`Upsert_OnDuplicate`、`Boolean_Type`、`Guid_Raw16`、`Guid_Char36`、`Sequences`、`Identity_ExplicitInsert`、`Schemas`、`Cte`、`WindowFunctions`、`SkipLocked`、`Lock_NoWait`、`Json_Type`、`Array_Type`、`Bulk_Copy`、`Bulk_ArrayBind`、`RefCursor`、`MultiStatement`、`MultiResultSets`、`Count_Over_Window`、`CaseSensitive_Identifiers`、`Comment_On_Column`、`Alter_Column_Precision`、`Truncate_Cascade`、`TempTable`。

真实分布与证据档位（同一张表；掩码声明处就是那几行，15 份台账里 SQL Server 一族不入册，理由见 §4.2）：

| 血统 | 声明处 | 位 | 离线钉 | 活库钉 | 仅厂商文档 | 谁继承 |
| --- | --- | --- | --- | --- | --- | --- |
| SQL Server | `src/FastSql.Providers.SqlServer/SqlServerProvider.cs:39` | 17 | 不入册 | 集成层两台真库 | — | — |
| PG 系 | `src/FastSql.Providers.PostgreSqlBase/PostgreSqlProviderBase.cs:52` | 14 | 7 | 0 | 7 | 6 颗叶子，无一覆写 |
| Oracle 系 | `src/FastSql.Providers.OracleBase/OracleProviderBase.cs:84` | 6 | 6 | 0 | 0 | Oracle / Dameng / ShenTong |
| MySQL 系 | `src/FastSql.Providers.MySqlBase/MySqlProviderBase.cs:113` | 5 | 5 | 0 | 0 | MySQL / TiDb / OceanBase |
| 虚谷 | `src/FastSql.Providers.Xugu/XuguProvider.cs:121` | 7 | 3 | **4** | 0 | — |
| GBase 8s | `src/FastSql.Providers.Gbase8s/Gbase8sProvider.cs:136` | 4 | 4 | 0 | 0 | — |
| SQLite | `src/FastSql.Providers.Sqlite/SqliteProvider.cs:193` | 5 | 1 | **3** | 1 | — |

**关键判据：能力位不是配置开关，而是一份已落地事实的账本。** 全仓库没有一处 `override DbCapabilities`（叶子只改 `DriverKey` 与构造参数，达梦额外改 `CreateCodeFirst` 与 `IdentityClause`，`src/FastSql.Providers.Dameng/DamengProvider.cs:49`、`:61`）；而运行期的分派读的是方言上的成员（`ISqlDialect.SupportsJoinedWrites`、`WriteTargetAlias`），**不是这颗掩码**。

台账在 `tests/FastSql.Tests.Dialects/CapabilityLedgerTests.cs`：一颗一家一行，出处分三档 —— `Provenance.Live`（本 provider 产出的那条语句被那台库执行过）、`Provenance.OfflineText`（文本被基线或断言钉住）、`Provenance.VendorDoc`（只有厂商文档；`Ledger.VendorDocOnly` 是算出来的那一格）。三颗计数常量（`Declared`/`PinnedOffline`/`PinnedLive`）是钉子，改一位就红。

所以：不要按位选路，也不要因为某位是 0 就断定"这家做不到"。它说的是"本仓库今天为这颗位拿得出手的证据在哪一档"。

### 2.5 钩子、日志与全局过滤器

`FastSqlAop`（`src/FastSql.Abstractions/Aop/FastSqlAop.cs`）不是事件总线，是一组可空委托字段，订阅就是赋值（`aop.CudBuilding = e => ...;`、`aop.CudBuilding += ...;`，见 `tests/FastSql.Tests.Dialects/SqlServerCudBuildingTests.cs`）：

| 成员（行号） | 类别 | 拿到什么 |
| --- | --- | --- |
| `Executing` 13 / `Executed` 14 | 观察 | `CommandLogEvent`（含 `ElapsedMilliseconds`、`TableNames`） |
| `Error` 15 / `SlowQuery` 16 | 观察 | `CommandErrorEvent(Exception, string Sql, long)`、`SlowCommandEvent`；阈值 `SlowQueryThresholdMilliseconds`（默认 500，行 31） |
| `Materialized` 17 | 观察 | `MaterializedEvent(object Entity, string EntityTypeName, int OrdinalInBatch)` |
| `AuditValue` 20 / `ParameterBinding` 21 | 改写 | `AuditValueEvent`（`SetValue`/`WasSet`）、`ParameterBindingEvent`（`SetValue`/`ChangeType`） |
| `CudBuilding` 27 | 改写 | `CudBuildingEvent { OperationKind, Sql, Rewritten }` |

改写过的步骤进 `HookTrace(int Step, string HookName, HookCategory Category, string Before, string After)`，在 `Explain()` 与 `SaveChangesReport.HookReplay` 里回放。观察类钩子的异常被 `Observe(...)`（行 40）吞掉并上报，不让日志代码打挂写入。

**没有的**：`QueryBuilding`、`CommandCreating` 在 `FastSqlAop` 里只是注释点名的桩（类注释自陈，无发射点）；决策类钩子、注册式钩子、读写分离、重试都没有。`ICacheAdapter Cache`（行 29）有槽位，`WithCache(string, TimeSpan)` 有入口，仓库内没有实现，要接自己的缓存得自己写这一颗。

**全局过滤器**是另一只对象：`orm.Filters`（`FastSqlOrm.cs:94`）返回 `FilterRegistry`，注册口是

```csharp
orm.Filters.Add<Ticket>(t => !t.Deleted);                  // 读、Update、Delete 三条链都带
orm.Filters.Add<Customer>(c => c.TenantId == 7, mandatory: true); // 链上的 IgnoreQueryFilters() 压不住
```

签名 `Add<TEntity>(Expression<Func<TEntity, bool>> predicate, bool mandatory = false)`，可链式，注册期先试翻译一次（`src/FastSql.Core/Query/Filters/FilterRegistry.cs`）。**它管不到 `SaveChanges` 与批量**（谓词长在 `EntityTracker` 上），软删的改写入口 `orm.Behaviors.SoftDelete` **今天不存在**，`FastSqlOrm` 没有 `Behaviors` 成员 —— 所以 `Delete` 到今天还是物理删除，那句自陈在 `src/FastSql.Core/Write/UpdateEngine.cs:315`。

### 2.6 事务：三形态

`src/FastSql.Core/Orchestration/FastSqlOrm.cs`：

```csharp
using var tran = orm.BeginTransaction();            // (1) 显式，:214，默认 ReadCommitted
await orm.Insert(row).ExecuteAffrowsAsync();
tran.Commit();                                      // IFastSqlTransaction { Completed, Commit(), Rollback() }

using var ambient = orm.BeginAmbientTransaction();  // (3) 环境事务，:218，同一 AsyncLocal 槽位

var result = await orm.ExecuteInTransactionAsync(async ct => { ... }); // (2) 返回 TransactionResult
if (!result.Success) Log(result.Exception);         // TransactionResult { Success, ErrorMessage, Exception }
```

泛型重载 `ExecuteInTransactionAsync<T>(...)` 返回 `TransactionResult<T>`（多一颗 `Value`）。`DbContext` 会加入环境事务；`DbContext` 本身不是 `IDisposable`。

## 3. 怎么使用

### 3.0 最小可离线跑通的例子

生成 SQL **不需要驱动，也不需要库**。测试层的做法是注一根"碰一下就抛"的探针工厂（`tests/FastSql.Tests.Common/SqlServer.cs` 的 `NoDriverFactory`），应用侧最省事的就是照抄这个姿势：

```csharp
using FastSql;
using FastSql.Providers.SqlServer;
using Microsoft.Data.SqlClient;                 // 只有要真连库时才需要这一行

var orm = new FastSqlOrm(new FastSqlOrmOptions
{
    Provider = new SqlServerProvider(SqlClientFactory.Instance),
    ConnectionString = "Server=offline;Database=never-connected;Integrated Security=True",
});

var sql = orm.Select<Order>().Where(o => o.Id == 7).ToSql();
Console.WriteLine(sql);
```

只产文本的话工厂可以换成任意不吃连接的 `DbProviderFactory`，或者按 §1.6 由清单解析（`SqlServer.DriverFromManifest()` 那一句就是走完整条链）。

### 3.1 读侧

链式接口是 `IQuery<T>`（`src/FastSql.Abstractions/Query/IQuery.cs`）。它**刻意不是 `IQueryable`**，不能塞进 LINQ 生态，也没有非泛型口。可用的成员：

```csharp
var q = orm.Select<Order>()
    .Where(o => o.State == OrderState.Paid && o.Amount > 100m)
    .OrderByDescending(o => o.CreatedAt).ThenBy(o => o.Id)
    .Skip(20).Take(10)
    .Select(o => new OrderSummary(o.Id, o.CustomerName, o.Amount));
```

- 谓词/投影/排序：`Where`（可多条，左到右折成一个 AND 链）、`OrderBy`/`OrderByDescending`/`ThenBy`/`ThenByDescending`、`Select<TResult>`（实体、匿名、构造器 DTO 三种）
- 分页：`Skip`/`Take`，或一次到位 `ToPagedAsync(int page, int size, PagedOptions? = null, CancellationToken)` → `PagedResult<T> { Items, Page, Size, Total, HasNext, TotalPages }`；计数控件是 `PagedOptions(CountMethod CountMethod = Auto, bool StripOrderingForCount = true, string? CacheKey = null)`，`CountMethod` 五值 `Auto`/`CountStar`/`CountOverWindow`/`SkipTotal`/`SeparateTransaction` —— 后两档里 `CountOverWindow` 与 `SeparateTransaction` 今天**直接抛**（§4.3 第 4 条那格），可点的只有前三颗
- 联表：arity 递增的 `LeftJoin<T2>(on)` / `InnerJoin<T2>(on)`，形参按 FROM 位置解析，产出 `IQuery<T1, T2>`（再到 `IQuery<T1, T2, T3>`），其后 `Select((a, b) => ...)`
- 导航：`Include<TProp>`、`IncludeMany<TDetail>`、`AsSplitQuery(int navigateBatchSize = 20)`
- 其它：`As(string alias)`、`Distinct()`、`AsNoTracking()`、`IgnoreQueryFilters()`、`WithCache(string, TimeSpan)`
- 出口：`ToListAsync`/`FirstOrDefaultAsync`/`CountAsync`/`ToAsyncEnumerable(int batchSize)`，以及同步的 `ToList`/`FirstOrDefault`/`Count`；类型本身还是 `IAsyncEnumerable<T>`

**分组（`GroupBy`/`Having`）走的是另一条类型链，出口只有 `Select`。**

```csharp
var top = await orm.Select<Order>()
    .Where(o => o.Amount > 100m)                        // 分组之前的条件 → WHERE
    .GroupBy(o => o.CustomerId)                         // → ISelectGrouping<int, Order>
    .Having(g => g.Count() > 1)                         // → HAVING（可多条，折成 AND）
    .OrderByDescending(g => g.Sum(o => o.Amount))
    .Take(5)                                            // 页与组同一单位：5 是"五组"
    .Select(g => new { g.Key, Total = g.Sum(o => o.Amount) })
    .ToListAsync();
```

- 链上可用的成员：`Having`、`OrderBy`/`OrderByDescending`（`IOrderedSelectGrouping` 上再加 `ThenBy`/`ThenByDescending`）、`Skip`/`Take`、`Select<TResult>`。**没有 `Where`**——分组之前的条件属于 `IQuery<T>` 的 `Where`，分组之后的条件属于 `Having`，这一点由类型决定，不由书写顺序决定
- 组里能读的只有 `g.Key` 与聚合：`g.Count()`/`g.LongCount()` → `COUNT(*)`，`g.Sum(o => o.列)`/`g.Max`/`g.Min`/`g.Average` → 各家的 `SUM({0})`/`MIN`/`MAX`/`AVG` 模板（`Fn` 注册表里本来就有，方言侧没有为分组新登记任何东西）。聚合的 lambda 形参绑回同一张表位，所以产出是 `SUM([t0].[Amount])` 而不是子查询
- 复合键：`GroupBy(o => new { o.OrderId, o.Sku })`，读法只能是 `g.Key.OrderId` 这样**到成员**的一层；`g.Key` 整体（那个匿名对象）在库里没有对应物，当场抛而不是静默取第一颗列
- **不需要 `ONLY_FULL_GROUP_BY` 风格的校验开关**（规格 §5.3 原稿那颗）：lambda 形参的类型是 BCL `IGrouping<TKey, TElement>`，实体成员不在它上面，所以"组里选非键列"根本编译不过去。同理 `Having` 写在 `GroupBy` 之前也写不出来
- 联表之后的分组**没有入口**：`GroupBy` 只长在 `IQuery<T>` 单表那一份契约上，`IQuery<T1, T2>` 上没有，所以那是编译期过不去而不是运行期报错。分组与联表要合用（`SELECT … FROM a JOIN b … GROUP BY a.x`）今天没有对应物
- 今天会**当场抛**（宁可报错，不发一条看起来合理的错句子）：分组之后再聚合（`CountAsync`、分页的总数）——那要先包成派生表 `SELECT COUNT(*) FROM (SELECT … GROUP BY …)`，AST 还没有派生表入口；`DISTINCT` 计数与带条件的计数（`g.Count(o => …)`）；无序分页时 `PagingOrderingPolicy.AutoOrderPrimaryKey` 那颗补序兜底（主键不在分组键里，补出来不合法）；分组链上 `Any`/`First` 这类没有 `GROUP BY` 对应物的方法
- 见证：离线册 `tests/FastSql.Tests.Dialects/GroupByTests.cs` 19 颗（版式四判 + 三家引号 + 分组与集合导航相关子查询的分工 + 上面每一格报错），可跑示例 `samples/FastSql.Sample/Program.cs` §8（单键五聚合、`Having`、复合键进 DTO、排序取前 N 组、`Explain()` 的模型文本）

别名由位置生成（`t0`/`t1`/…），C# 里那个 lambda 形参名不影响产出；表名与列序来自契约。占位符名字统一是 `@p0`/`@p1` 这种由 `SqlWriter` 生成的形式，但**前缀按家换**：`@`（SQL Server / PG 系 / MySQL 系）、`:`（Oracle 系 / 虚谷）、`?`（GBase 8s，位置式，不接受带前缀参数名）。取参数用 `ToSql(out IReadOnlyList<AdoParameter> parameters)`，`AdoParameter { Name, Value, DbType, Size, Hint, DriverName }`。

无 `ORDER BY` 的分页默认抛（`PagingOrderingPolicy.Throw`）：T-SQL 的 `OFFSET..FETCH` 硬性要求 `ORDER BY`，而虚谷/MySQL 单独 `OFFSET` 要补一颗上界哨兵。想放开就改 `FastSqlOrmOptions.PagingOrdering`。

### 3.2 写侧

```csharp
// 单行
await orm.Insert(new Order { CustomerName = "甲", Amount = 12.5m })
       .ExecuteAffrowsAsync();

// 批量：同一份列清单的多值 VALUES，按参数预算分块
var plan = orm.InsertRange(rows).Explain();

// 改与删（链式；Set 可多次，没有 Where 会在构造期抛）
await orm.Update<Order>().Set(o => o.Note, "只改备注").Where(o => o.Id == 7).ExecuteAffrowsAsync();
await orm.Delete<Order>().Where(o => o.CreatedAt < cutoff).ExecuteAffrowsAsync();
```

`IInsert<T>` 的出口：`ExecuteAffrowsAsync`、`ExecuteIdentityAsync` → `Task<long>`、`ExecuteReturnKeysAsync` → `Task<List<T>>`；诊断口 `Explain()` → `InsertPlan`、`ExplainKeys()`。

**分块是看得境的事实，不是玄学。** `InsertPlan { Chunks, RowCount, ColumnsPerRow, RowsPerStatement, ParameterBudget, Reason, HookReplay }`，`Chunks` 是 `InsertChunk(FirstRow, RowCount, Ast, Sql)`。预算来自 `IBulkStrategy.ParameterBudget`：SQL Server `2100`（`src/FastSql.Providers.SqlServer/SqlServerBulk.cs:10`）、PG 系 `32767`（`PostgreSqlBulk.cs:13`）、MySQL 系 `65535`（`MySqlBulk.cs:41`）；**Oracle 系、虚谷、GBase 8s 三家的 `Bulk` 是 null**（`OracleProviderBase.cs:142`、`XuguProvider.cs:148`、`Gbase8sProvider.cs:162`），于是落到共用层那颗保守值 `StatementEmitter.ConservativeParameterBudget = 100`（`src/FastSql.Core/Write/StatementEmitter.cs:21`）。同一批 600 行 × 7 列：达梦切 50 块（每块 12 行），金仓一块，T-SQL 三块，MySQL 一块；`tests/FastSql.Tests.Dialects/DamengDialectTests.cs:626` 那格钉的就是这组数。`plan.Reason` 会自陈"参数预算 100"与"跨块不是原子的"—— 跨块不原子意味着批量插入没有事务保证，这是要自己补的。

**自增主键回填是今天真实的限制。** 三条链各家能走到哪一档，取决于两颗槽位（`IKeyStrategy.SupportsBulkKeyRetrieval` 与 `SupportsPostStatementKeyRead`）：

| 家 | 批量 `RETURNING`/`OUTPUT` | 语句后回读 | 要键会怎样 |
| --- | --- | --- | --- |
| SQL Server | `true`（`SqlServerKeys.cs:12`） | `false`（同文件 `:48`） | `OUTPUT inserted.[Id]` |
| PG 系 | `true`（`PostgreSqlKeys.cs:13`） | `false`（`:39`） | 句尾 `RETURNING` |
| MySQL 系 | `false`（`MySqlKeys.cs:45`） | `true`（`:71`） | 语句后补 `SELECT LAST_INSERT_ID()`（`:76`） |
| 虚谷 | `false`（`XuguKeys.cs:63`） | `true`（`:70`） | 同上 |
| GBase 8s | `false`（`Gbase8sKeys.cs:95`） | `true`（`:67`） | 语句后回读，`EmitReturning` 抛（`:59`） |
| **Oracle 系** | `false`（`OracleKeys.cs:46`） | `false`（`:86`） | **两个口子都关，直接抛** |

也就是说：在 Oracle / 达梦 / 神通上要自增键（`ExplainKeys()`、`ExecuteReturnKeysAsync`、`SaveChanges` 里需要回填的插入档）今天**必然**拿到 `FastSqlException`，消息是"没有可作为结果集发出的 OUTPUT/RETURNING"与那句"回读表达式从未被本仓库问证过"（`src/FastSql.Providers.OracleBase/OracleKeys.cs:56`、`:92`）。抛而不是发一条"看着像在回填"的语句，是刻意的。

### 3.3 会话、跟踪与 `SaveChanges`

`DbContext`（`src/FastSql.Core/Orchestration/DbContext.cs`）只做一件事：把实体变更攒成语句批次。

```csharp
sealed class AppDb(FastSqlOrm orm) : DbContext(orm)
{
    public DbSet<Order> Orders => Set<Order>();   // 官方推荐的派生形态（DbContext 类注释里那句）
}

var db = new AppDb(orm);
var order = await db.Orders.Where(o => o.Id == 7).FirstOrDefaultAsync();  // 跟踪读
order!.Note = "改一下";
var report = await db.SaveChangesAsync();
```

- 入口：`Set<T>()`（按类型缓存）、`Add`/`AddRange`/`Attach`/`Update`/`Remove`/`RemoveRange`/`Entry`/`InsertRaw`，`orm.CreateDbContext<TContext>()` 存在但走 `Activator`，AOT 场景建议自己 `new`
- `orm.CreateSharedContext<TContext>()`（v0.37 第八笔，`FastSqlOrm.cs:201`）：**同一条 async 执行流上取到的一直是同一只会话**，用于不做 DI 注册也要跨方法传的场景（控制台、后台服务）。三条边界要用之前先知道：① 共享范围是**一条流**，不是进程也不是线程，槽位挂在 orm 实例上，所以两只 `FastSqlOrm` 在同一条流里各拿各的会话；`await` 之后的子任务里建的那只**不会回到父帧**（父帧之后自己取，取到的是另一只）；② 反过来，父帧先建、之后扇出的并发分支拿到的是**同一只**，而 `ChangeTracker` 不是线程安全的（今天没有闸门，要并发就各自开流或各自 `CreateDbContext`）；③ 会话没有 `Dispose`，"结束"就是让那条流出作用域，或者按 `ChangeTracker.Clear()` 清账。八条判据见 `tests/FastSql.Tests.Dialects/SharedContextTests.cs`，口径见设计文档 §7 末
- `DbSet<T>` 直接继承查询链（`FastSql.Core/Orchestration/DbSet.cs`）并实现 `ITrackedSet<T>`：`Local`（`LocalView<T> : IReadOnlyList<T>`，有 `Entries` 与 `Find(params object?[])`）、`Load<TProp>(navigation)` —— **后者今天抛 `FastSqlException`**（导航补载没接线）
- `ChangeTracker`：`Count`、`HasChanges`、`Entries()`、`Entries<T>()`、`DetectChanges()`、`Clear()`；状态是 `EntityState { Detached, Added, Unmodified, Modified, Deleted }`；点名某列用 `Entry(entity).Property(nameof(X)).IsModified = true`，`AutoDetectChangesEnabled` 可关
- 只写脏列：UPDATE 的 SET 里只有 `ModifiedMembers`；没有主键的实体**拒绝跟踪并指路**（`Attach(new AuditRow{...})` 会抛）
- `SaveChanges()` 是同步壳（sync-over-async）；干净无事时**不发语句、不开事务**（`tests/FastSql.Tests.Dialects/TrackingTests.cs:527` 那格用探针工厂证的就是这条）；发语句前先过 `EnsureWriteOrderResolvable()`（外键依赖成环就在这里红）
- 报告：`SaveChangesReport { Operations, HookReplay, AffectedRows, StatementCount }`，每档是 `SaveChangesOp(WriteOperation Operation, string Table, int Rows, int StatementCount, IReadOnlyList<string> Columns, string Sql)`。`WriteOperation { Insert, Update, Delete }`。**"没有欠语句"与"发了但没命中行"是两件事**：`AffectedRows` 在 UPDATE/DELETE 档上是谓词命中数
- 乐观锁冲突抛 `DbUpdateConcurrencyException`；`[ConcurrencyToken]` 列不进 SET、不进 INSERT 列清单，进 WHERE 与回填集，NULL 那一档折成 `IS NULL`

### 3.4 读契约（不用库就能看见映射结果）

```csharp
var contract = orm.Schemas.Contract<Order>();

Console.WriteLine($"{contract.Table.Schema}.{contract.Table.Name}");   // TableInfo
foreach (var c in contract.Columns)
    Console.WriteLine($"{c.ColumnName} {c.ClrType.Name} len={c.MaxChars} null={c.IsNullable} id={c.IsIdentity} :: {c.Origin.Note}");
Console.WriteLine(contract.PrimaryKey.Length);                          // 关系见 Relationships / Indexes
```

`ColumnContract` 上可读的还有 `MemberName`、`LengthSemantics`（`Char`/`Byte`）、`Unicode`、`MaxBytes`、`Precision`、`Scale`、`IsConcurrencyToken`、`StoreFillsValue`、`StoreTypeName`、`Comment`、`ValueFromStore`、`NullableInStore`、`FitsChars`、`FitsBytes`。`IEntitySchemaRegistry` 另外三面是 `Schema<T>()`、`Validator<T>()`、`Contract(Type)`、`ReadDelegate(Type)`；校验档位 `ValidationMode { Off, ContractOnly, DataAnnotations, Custom }`。

DDL 发射有 SPI（`ICodeFirstDialect.EmitCreateTable` / `EmitAddColumn` / `EmitAlterColumnPrecision` / `TryEmitColumnComment` / `EmitCreateIndex`），测试里就这么直接调（`tests/FastSql.Tests.Dialects/DialectParityTests.cs:565`）：

```csharp
var writer = new SqlWriter(orm.Provider.Dialect);
orm.Provider.CodeFirst.EmitCreateTable(writer, orm.Schemas.Contract<Order>());
```

**但没有迁移层**：没有 migrate diff / migrate script 这类命令、没有 CLI、没有版本表，`CodeFirst` 与 `SchemaReader` 在规格里都挂在 P2 那一行。

反向读结构只有 SPI 形状：`ISchemaReader.ReadTableAsync` / `ProbeLengthSemanticsAsync`，产出 `SchemaTable`/`SchemaColumn`/`LengthSemanticsReading`。`SchemaColumn` 只有六格（`src/FastSql.Abstractions/Providers/IDbProvider.cs:128`），**读不回主键、读不回注释、也接不住自增标记与默认值**（§4.3）。

### 3.5 诊断：有什么、缺什么

有：`IQuery.Explain()` → `QueryPlan { Ast, Sql, ModelText, HookReplay, Provider }`，`Provider.ToLogSql()` 出内联字面量版；`IInsert/IUpdate/IDelete.Explain()` 出各自的 plan；`InsertPlan.ToLogSql()` 会打 `-- 块 n：第 a~b 行`；`SaveChangesReport.HookReplay`；`orm.Aop.Executed`/`Error`/`SlowQuery` 三颗观察口；`orm.Provider.Connectors.TryEnsureLoaded(out string? failure)` 作启动期自检（注意它只创建不打开连接，v0.32 实测它挡不住原生依赖缺失）。

**没有 `doctor`。** 全仓库 `doctor` 只出现在注释与规格里（§9.5 那格），`src/` 下没有任何诊断命令实现。同理没有 `FastSql.Testing` 内存 provider（规格里已被移出承诺）。

## 4. 当前边界与坑

### 4.1 十三家的状态口径统一是"离线文本层，未实测"

十六颗叶子里，十三家的驱动从未在一台机器上解析成功过，也没有一条语句在真库上执行过（`FastSqlDrivers.json` 各行 `$comment` 逐行自陈）。达梦的 `DmProvider`、神通的 `Oscar`、GBase 8s 的 `GBase.DataDB` 这些程序集名各只有一处出处，而那不是现场读数。**把 provider 的存在读成"已适配/可直接上生产"是本文最不希望发生的事。**

### 4.2 三家例外的读数走到哪里停（三处口径不一致，原样列出）

- **SQL Server。** 装载链走完整过（`DriverLoadingTests` 按类型名断言解析到 `Microsoft.Data.SqlClient.SqlClientFactory`），集成层 `tests/FastSql.Tests.Integration/` 那 14 个 .cs 是真打库的（目标串由环境变量 `FASTSQL_TEST_CONNECTION` 或仓库根那份不入库的凭据文件给，两处都没有就抛）。能力台账类注释里那句是它的自我口径：**"T-SQL 那一族不进这份台账，这不是漏记：它的每一位都由集成套件在两台真库上跑过"**（`CapabilityLedgerTests.cs` 第 100 行附近）。
- **虚谷。** 清单解析成功（`XuguClient`，工厂落到 `XuguClient.XGProviderFactory`）、`Open` 成功、目录可读。台账把 7 位中的 4 位钉成 `Live`，见证是三份基线在活库上执行通过（`paging-skip-only.sql` 1/1 句、`ddl-alter-and-comment.sql` 3/3 句、`ddl-order.sql` 1/1 句）；`ddl-qualified.sql` 那一形跑过而**失败**（`E4002` 模式不存在紧随 `E5021`），台账写明败因在"不建库、不建模式"的授权侧而不是文本侧，所以本版不修它。剩下的 `Returning` 一位仍是 `OfflineText`，理由是键回填那一整档缺 SPI 槽位、集成层按名 skip。
- **SQLite。** 清单解析成功（`Microsoft.Data.Sqlite`，工厂落到 `Microsoft.Data.Sqlite.SqliteFactory`），而这是这份清单上第一颗 `factoryType` 从活机器读回来而非照文档写的键。集成层用 `FASTSQL_TEST_PROVIDER=sqlite` 打到一座临时文件库（`Sqlite.ScratchConnectionString`，本家"实例"就是一个文件，夹具只在被点名的这条显式通路上自建，`TestConnectionString` 那道闸门一个字不动）。台账把 5 位中的 3 位钉成 `Live`（`Paging_LimitOffset`／`Returning`／`MultiStatement`），见证与前两家不同——是 ORM 自己拼出的语句跑过，不是基线文本被手敲进库；`MultiStatement` 那颗靠事后翻库（`IX_FastSqlOrder_CreatedAt` 在场）认。剩下两位不翻档且各因不同：`Schemas` 是"发得出而带限定的那一形今天没被执行过"（留在 `OfflineText`），`Identity_ExplicitInsert` 连可发的文本都没有（Core 直接抛，留在 `VendorDoc`）。集成那一场是 `95 / E0 / F0 / S14`（14 颗具名 skip：并发令牌 12 + T-SQL 裁判文本 2）。**注意：本家有活库，但 `golden/sqlite/*.sql` 那 11 份没被原样重放过——翻 `Live` 的那条链与"黄金样本↔真库执行"那条链在这里是两条，别把前者读成后者。**
- **三处口径要指认：** `FastSqlDrivers.json` 的 xugu 行 `$comment` 写的是"跑的是 SELECT 1 与两颗 GetSchema，provider 发的 SQL 一条都没有"，而上面那份台账把 4 位记为 `Live`；SQL Server 那半同样只有测试侧的自述；SQLite 那半的 `$comment`（若仍是接入时那句"未实测"）也与本笔翻的三颗 `Live` 不同步。规格 §13.4/§13.5 才是那三本账的落点。**本文不替三边裁哪个更准**，要上生产的话这三处得先对齐。
- 顺带量出的两格约束：`TryEnsureLoaded` 只创建不打开连接，挡不住原生同伴缺失（bare 那颗反证）；清单 `path` 用正斜杠时托管加载会成功、原生库找不到，要到 `Open` 才炸。

### 4.3 已知的模型空档（都在 `src/` 里可读）

1. **SPI 没有"库的版本"槽位**。虚谷的 `COMPATIBLE_MODE`、PG 的内核版本段、MySQL 的 `sql_mode` 都改方言假设，而 `IDbProvider` 那 11 颗子 SPI 里没有一格承接（规格 §14 那格点名）。后果：同一家不同版本上，本文的文本层结论可能整体不成立。
2. **SPI 没有"兼容模式"槽位**，今天已有 13 颗未建模参数在册（§14 那份名单）。最要紧的三颗：金仓 Oracle 兼容模式（一个连接串参数换掉整族方言假设，`src/FastSql.Providers.Kingbase/KingbaseProvider.cs:19` 附近类注释自陈）、MySQL 的 `GuidFormat=BinaryUuid`（`CHAR(36)` 变 `BINARY(16)`，`MySqlTypes.cs:22`）、OceanBase 的"双模"（清单上那颗 `oceanbase` 键只在连接建在 MySQL 模时成立）。
3. **三颗 UPSERT 位里两颗置着、一颗空着，而三句发射器都没有。** `Upsert_MergeInto` 在 SQL Server 那行（`src/FastSql.Providers.SqlServer/SqlServerProvider.cs:40`）、`Upsert_OnConflict` 在 PG 那行（`PostgreSqlProviderBase.cs:53`）、`Upsert_OnDuplicate` 在枚举里有位而 MySQL 系的掩码没置它（`MySqlProviderBase.cs:113` 那五行里没有）。链式接口 `IInsert<T>`/`IUpdate<T>` 上没有任何对应成员，而 `.OnConflict(` 这一形在 `src/` 下一处都没有 —— 全仓库 `OnConflict` 一词只有三处命中：枚举那颗（`DbCapabilities.cs:18`）、PG 血统的掩码（`PostgreSqlProviderBase.cs:53`）、类注释里那一句（`PostgreSqlProviderBase.cs:18`）。位说的是"库有这个能力"，不是"本库会发这句"，两者之间今天没有闸门 —— 别按位选路。
4. **`Cte` 与 `WindowFunctions` 有声明、没接线**：掩码上 PG 系与 SQL Server 各置了这两位（`PostgreSqlProviderBase.cs:54`、`SqlServerProvider.cs:42`），而 `IQuery<T>` 上没有 CTE 入口、窗口函数也没有可点名的 API（`CountMethod.CountOverWindow` 那一档连自家 count 优化都没接：`src/FastSql.Core/Query/QueryEngine.cs:446` 直接抛，那颗 switch 的注释自陈"没实现就别让它看起来能用"）。
5. **`MultiStatement` 有第二份声明了，但仍是一颗没人读来分派的位**：v0.37 第四笔之前只有 SQL Server 声明（`SqlServerProvider.cs:43`），今天 SQLite 也声明它（`SqliteProvider.cs`，七家第二份），而这一份背后第一次挂上真实发射——`SqliteCodeFirst.EmitCreateTable` 把建表＋建索引串成一句 ` ; `-分隔的文本，集成夹具 `tests/FastSql.Tests.Integration/Database.cs:812` 那句 `ExecuteNonQuery` 把它整个发进活库跑过（这就是 §4.2 那三颗 `Live` 里 `MultiStatement` 那颗的来路）。但 `grep -rn "MultiStatement" src/` 命中的仍是声明与注释、**没有一处按这颗位走岔路**：走的是"这一族的发射器本来就把两句串起来"那条硬路径，不是"读了位才决定串不串"。所以它从"空判"改口成"有产出、仍零决策消费者"，性质与前几版一样——别按这颗位选路。写侧真正按方言分派的是 `ISqlDialect.SupportsJoinedWrites`。
6. **合并写档七族里只有两族有**（T-SQL 与 PG 系那六颗叶）：`SupportsJoinedWrites` 为 false 的是 MySQL 系（`MySqlDialect.cs:77`）、Oracle 系（`OracleDialect.cs:57`）、虚谷（`XuguDialect.cs:105`）、GBase 8s（`Gbase8sDialect.cs:87`）、SQLite（`SqliteDialect.cs:109`）—— 那五族的 `SaveChanges` 多表更新整桶退回一句一实体；`EmitJoinedSources` 在这五家里都是抛（走到那里说明开关被绕过）。**SQLite 这一格第一次逼出"合并写不是一句子句而是两个出口"的读法**：它的 `UPDATE … FROM` 有、而 `DELETE` 的 `FROM` 与 `USING` 两形全无，于是同一颗 `SupportsJoinedWrites` 在这一家要问两遍而两遍答案不同，取 false 是因为只要有一半发不出就得整桶退回。
7. **反向工程读不全**：`SchemaColumn` 六格没有主键位（全仓库 `PRIMARY KEY` 的非注释命中一共七处、全在七份 CodeFirst 的建表清单里，目录侧 0 处）、注释七家一家都读不回（`SchemaColumn` 没有那一格，七份 `*SchemaReader` 一家都不例外；发射侧倒是只有三家发原生 `COMMENT ON COLUMN`：`PostgreSqlCodeFirst.cs:109`、`OracleCodeFirst.cs:139`、`XuguCodeFirst.cs:140`；另两家用别的形状 —— MySQL 系的列内子句 `COMMENT '…'`（`MySqlCodeFirst.cs:192`）、SQL Server 的扩展属性 `sys.sp_addextendedproperty`（`SqlServerCodeFirst.cs:62`），而 GBase 8s（`Gbase8sCodeFirst`）与 SQLite（`SqliteCodeFirst.cs:217`）的 `TryEmitColumnComment` 都直接返回 false —— SQLite 更硬，`COMMENT` 这个词在本族是语法错、根本没有列注释的落处）、虚谷那份目录查询里的 `IS_SERIAL` 与 `COMMENTS` 两列没有槽承接（`XuguSchemaReader.cs:68` 自陈"不是漏，是没有槽"）。所以别指望 CodeFirst + SchemaReader 做双向。

8. **AST 有派生表的位置、没有规划入口**：`SelectStatement.Source` 能挂子查询（`SourceSubquery`，相关子查询那条路今天就在用它），而链式算子里没有任何一形产出"把整句包成内层再聚合/再分组"的计划。后果全部落在报错侧（§3.1 那一段的清单）：分组之后的 `CountAsync`、分组查询的分页总数、带 `Skip/Take` 或 `Distinct` 之后再聚合，一家都不发句子。规格 §5.12 的"总数与页一致性"那一格要到这一步才走得完。

### 4.4 跨库迁移时最容易踩的五格

| 坑 | 现场形状 | 出处（provider 类注释/代码） |
| --- | --- | --- |
| 折大写 | 同一模型在达梦/虚谷得到 `"CUSTOMERNAME"`，在金仓得到 `"CustomerName"`；不加引号的名字在 Oracle 血统里进库即大写，跨家建的表对不上 | `src/FastSql.Abstractions/Metadata/IdentifierPolicy.cs:36`、`:83`；`src/FastSql.Providers.OracleBase/OracleProviderBase.cs` 类注释第二段 |
| 别名与写目标 | GBase 8s / Oracle 血统的别名不带 `AS`（`FROM "FASTSQLORDER" "T0"`），UPDATE 目标一律裸表名；照 T-SQL 的习惯读会以为漏写了 | `src/FastSql.Providers.Gbase8s/Gbase8sDialect.cs:95`、`:97`；`OracleDialect.cs:62`、`:64` |
| 参数占位符 | GBase 8s 是位置式 `?` 且不接受带前缀参数名，只有 SQL Server 满足 `DriverAcceptsPrefixedParameterNames`；照别家写的 `@p0` 那一家不认 | `src/FastSql.Providers.Gbase8s/Gbase8sDialect.cs:80`、`src/FastSql.Abstractions/Sql/ISqlDialect.cs:28` |
| 单独 `OFFSET` 要哨兵 | 只有 `Skip` 时 MySQL 补 `18446744073709551615`、虚谷补 `2147483647`，两颗同形不同义；照别家文本抄会溢出或截断 | `golden/mysql/paging-skip-only.sql`、`golden/xugu/paging-skip-only.sql`；`src/FastSql.Providers.Xugu/FastSql.Providers.Xugu.csproj` 的 Description |
| 自增键与 `IDENTITY` 措辞 | Oracle 血统内部都分裂：达梦写 ` IDENTITY(1,1)` 而血统基类写 ` GENERATED BY DEFAULT AS IDENTITY`；跨家要键回填要先按 §3.2 那张表确认那一档开不开 | `src/FastSql.Providers.Dameng/DamengCodeFirst.cs` 的 `IdentityClause` 覆写（`DamengProvider.cs:61` 挂点）；`src/FastSql.Providers.OracleBase/OracleCodeFirst.cs:87` |

另外三条日常会撞：软删没有入口（`Delete` 是物理删除，`UpdateEngine.cs:315`）；`orm.Update(entity)`（按实体整行写回）没有，链式只有 `Set`，`DbContext.Update<T>(T)` 做的是"标成 Modified"；`DbSet.Load` 与 `Include` 的缝合还没接线，导航补载会直接抛。

## 5. provider 总表

能力位颗数取自各家掩码（§2.4 的声明处），manifest assembly 取自 `FastSqlDrivers.json`。状态列除三格例外一律"离线文本层，未实测"（例外的口径见 §4.2）。

| 库种 | `DbKind` | `DriverKey` | 清单 `assembly` | 血统 | 位 | 状态 |
| --- | --- | --- | --- | --- | --- | --- |
| SQL Server | `SqlServer` | `sqlserver` | `Microsoft.Data.SqlClient` | 独立 | 17 | 全部位有活库执行见证（两台实例，集成层自述；见 §4.2） |
| PostgreSQL | `PostgreSql` | `postgresql` | `Npgsql` | PG | 14 | 离线文本层，未实测 |
| 金仓 KingbaseES | `Kingbase` | `kingbase` | `Kdbndp` | PG | 14 | 离线文本层，未实测 |
| openGauss | `OpenGauss` | `opengauss` | `Npgsql` | PG | 14 | 离线文本层，未实测 |
| GaussDB | `GaussDb` | `gaussdb` | `Npgsql` | PG | 14 | 离线文本层，未实测 |
| 瀚高 HighGo | `Highgo` | `highgo` | `Npgsql` | PG | 14 | 离线文本层，未实测 |
| GBase 8c | `Gbase8c` | `gbase8c` | `Npgsql` | PG | 14 | 离线文本层，未实测 |
| Oracle | `Oracle` | `oracle` | `Oracle.ManagedDataAccess` | Oracle | 6 | 离线文本层，未实测 |
| 达梦 | `Dameng` | `dameng` | `DmProvider` | Oracle | 6 | 离线文本层，未实测 |
| 神通 | `ShenTong` | `shentong` | `Oscar` | Oracle | 6 | 离线文本层，未实测 |
| MySQL | `MySql` | `mysql` | `MySqlConnector` | MySQL | 5 | 离线文本层，未实测 |
| TiDB | `TiDb` | `tidb` | `MySqlConnector` | MySQL | 5 | 离线文本层，未实测 |
| OceanBase（MySQL 模） | `OceanBase` | `oceanbase` | `MySqlConnector` | MySQL | 5 | 离线文本层，未实测；模式未建模 |
| 虚谷 | `Xugu` | `xugu` | `XuguClient` | 独立 | 7 | 装载与连通实测；7 位中 4 位有活库执行见证（§4.2） |
| GBase 8s | `Gbase8s` | `gbase8s` | `GBase.DataDB` | 独立 | 4 | 离线文本层，未实测；档位按【文档·IBM】 |
| SQLite | `Sqlite` | `sqlite` | `Microsoft.Data.Sqlite` | 独立 | 5 | 装载/连通实测（临时文件库）；5 位中 3 位有活库执行见证，且三颗都是 ORM 产出的语句跑过而非基线重放（§4.2） |

`DbKind` 枚举的 16 个值今天全部有 provider、清单行、掩码对应，无空档（原 `MariaDb` 那一个值已于 v0.37 第五笔撤下——本仓库不立 MariaDB 这一档，理由见设计文档 §14）。OceanBase 的 Oracle 模同样没有键（那颗叶子在规格里被裁了后置）。
