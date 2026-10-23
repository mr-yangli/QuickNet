# QuickSql ORM 使用说明

> 面向使用者的一份操作文档：怎么集成、怎么配置、怎么使用。设计理由与判据在 `docs/quicksql-orm-design.md`（本文简称"规格"），本文不重复论证，只在需要指认出处时给出 `文件:行`。
>
> 本文里出现的每一个 API 名字都在工作树的 `src/` 下读到过真实定义；凡属"设计意图而尚未交付"的地方一律写明。

## 0. 这份文档是什么，现在真实的状态是什么

三段：§1 集成、§2 配置、§3 使用；§4 是边界与坑，§5 是一张 provider 总表。它是用法文档，不是设计文档 —— 想知道"为什么是这个形状"请读规格。

真实状态（三句，句句可核）：

1. **文本层落地的 provider 有 18 颗叶子**：`src/` 下 18 个 `QuickSql.Providers.<库种>` 工程各暴露一个 `public const string DriverKey`（各家取值见 §5）。另有 3 个血统基类工程（`PostgreSqlBase`/`OracleBase`/`MySqlBase`）不是可寻址的库种。`DbKind` 枚举今天有 18 个值（`src/QuickSql.Abstractions/DbKind.cs`），与 18 颗叶子一一对应、**没有"有值而无 provider"的空档**（`Sqlite` 于 v0.37 第四笔落地补齐当时那一代的最后一格；`DuckDb` 于 v0.39 第一笔落地，是 PG 血统的第七颗；`ClickHouse` 于 v0.39 第二笔落地——第一颗不挂任何血统基类、11 个文件从头自写的单层叶子（§5），`DbKind.ClickHouse = 17`；原本占着的 `MariaDb` 值于 v0.37 第五笔从枚举撤下——本仓库裁"不立 MariaDB 这一档"，理由与重开条件见设计文档 §14）。
2. **十七家里，真库见证只挂在五颗叶子上，而且挂得都很窄。** SQL Server 一族的每一位能力都由集成层在两台实例上跑过（`CapabilityLedgerTests.cs` 类注释明写"T-SQL 那一族不进这份台账，不是漏记"）；虚谷有 7 位中的 4 位被钉为 `Live`，见证是把基线文本逐句敲进那台库；SQLite 有 5 位中的 3 位被钉为 `Live`（`Paging_LimitOffset`／`Returning`／`MultiStatement`），而这三颗走的是**另一条路**——集成册里 ORM 自己拼出来的语句真在临时文件库上跑过，没有一行手写文本参与，其中 `MultiStatement` 是这本账第一颗落在 DDL 侧的 `Live`（见证是事后翻库看到 `IX_QuickSqlOrder_CreatedAt` 在场）；PostgreSQL 与 MySQL 两颗本尊各自只有 1 位 `Live`（都是 `Bulk_Copy`，批量写侧那一轮钉的，读数与来路记在 §2.4 那张表）。其余十二家 `PinnedLive` 全是 0——其中十家的驱动从未在一台机器上解析成功过（金仓与 Oracle 两家例外：§1.9 那一趟冒烟里各呼应过一次的解析成功，也只是文本层读数；内建表 `BuiltinDrivers.cs` 各行注释逐行自陈）。§4.2 把这几处的口径差异原样列出。
3. 离线证据是自产的：`tests/QuickSql.Tests.Dialects/golden/` 下九个目录共 156 份基线（sqlserver 57 / kingbase 18 / dameng 19 / mysql 11 / gbase8s 11 / xugu 7 / sqlite 12 / duckdb 11 / clickhouse 10）。除 §4.2 点到的那几份之外，它们是"本仓库自己认为应该长这样"的锁，不是厂商确认——**SQLite 那一套 11 份也是自产基线：本家有活库，而活库跑的是 ORM 产出的语句（第 2 点那三颗 `Live`），不是把这 11 句原样重放**，所以"黄金样本 ↔ 真库执行"这一对在本家仍然没配上，配上的是另一条证据链（provider 产出 ↔ 真库执行）。**clickhouse 那一套 10 份同 duckdb 一档、且比它少一份**：insert-with-keys 在本家没有可发的文本（`ClickHouseKeys.EmitReturning` 整格抛），基线改判"断言抛、不落盘"。其余九颗叶子（postgresql/opengauss/gaussdb/highgo/gbase8c/oracle/shentong/tidb/oceanbase）没有自己的 golden 目录，同形部分由 `DialectParityTests` 逐字对拍钉住（ClickHouse 与任何一族都无同形可对，整档走自家基线）。

另有一句不在上面三句之内的：**与 SqlSugar 的对照曾另有一份册子，该稿已下线删除**（82d6525 那一笔「fastsql-vs-sqlsugar.md 对照稿下线」；全文可用 `git show 82d6525^:sql/docs/fastsql-vs-sqlsugar.md` 读回）。本文凡引到它的地方（§3.2 批量块宽那一格）一律按**历史读数**对待——引用指的是出处，不是说盘上有这份文件；那十四格实测与六面读码对照的结论本文不搬，本文的口径也不改。

所以本文的读法是：**接口形状可信；除 SQL Server 之外的执行结果基本未证。**

## 1. 怎么集成

### 1.1 SDK 与目标框架

| 项 | 值 | 出处 |
| --- | --- | --- |
| SDK | `10.0.400`，`rollForward: latestFeature` | `global.json` |
| TFM | `net10.0`（全仓库单值） | `Directory.Build.props` |
| 语言版本 | `preview`；`Nullable enable`、`ImplicitUsings enable` | `Directory.Build.props` |
| 警告策略 | 库工程 `TreatWarningsAsErrors true`，且开 `IsAotCompatible`/`EnableTrimAnalyzer` | `Directory.Build.props` |

唯一的例外是 `QuickSql.Generators`，它是 `netstandard2.0` —— Roslyn 分析器主机的硬约束，不是没改干净。

### 1.2 程序集清单与职责

`QuickSql.Abstractions`（命名空间 `QuickSql` 与 `QuickSql.*`）是公开契约：`DbKind`、`DbCapabilities`、`IDbProvider` 与它身上那 11 颗子 SPI（连 `Kind` 与 `Capabilities` 共 13 格）、元数据契约、查询/写/跟踪接口。`QuickSql.Core`（命名空间 `QuickSql`）是内核：`QuickSqlOrm`、`DbContext`、QueryModel→AST→文本、驱动加载、物化、事务。`QuickSql.Generators` 是 G1 那张 Roslyn 增量生成器，把带 `[Table]` 的实体在编译期翻成契约。18 个 `QuickSql.Providers.<库种>` 叶子工程各家一册（方言、类型映射、键策略、DDL 发射），另有 `QuickSql.Providers.{PostgreSql,Oracle,MySql}Base` 三个血统工程承载族内共享的那一层，叶子只补差异。

引用链单向：provider → `QuickSql.Core` → `QuickSql.Abstractions`（`src/QuickSql.Providers.SqlServer/QuickSql.Providers.SqlServer.csproj` 只有对 Core 的一行 `ProjectReference`）。

### 1.3 应用侧怎么装：NuGet 主路径

**装 QuickSql 只有一条主路径：NuGet。** 23 颗库工程全部打包上 nuget.org，包 ID 与工程名逐字相同。一个普通应用要跑起来只需在 csproj 里落三行 `PackageReference`（外加各库自己的 ADO.NET 驱动，见 §1.4）——内核、provider、源生成器各一，Abstractions 作为传递依赖跟着进来，不用单独写。

先立两条与版本口径有关的纪律，它们决定了装包命令怎么写：

- **预发布档不进默认解析**：全家都是 `-beta.N` 系列，`dotnet add package` 必须带 `--prerelease`，或者在 `PackageReference` 里写死版本号——二选一，否则会报"找不到稳定版本"。
- **每重打一次包升一颗 beta**（`sql/Directory.Build.props` 注释里那条纪律）：同版本号不同内容的两颗包，消费方全局缓存里那颗会压住新打的——还原照样绿，拿到的却是旧二进制。对版本以源上实读为准：2026-10-20 把攒了九颗 beta 的那一批一次性发了上去，nuget.org 上现在有 `QuickSql` 那一族全系列 `0.39.0-beta.24`（24 颗，含这一代新建的 `QuickSql.Providers.DuckDb` 与 `QuickSql.Providers.ClickHouse`）。**源与包已经走岔，对账要两袖分开**：此后源上又落了 `beta.25`（ClickHouse 第一台引擎对账）∶ `beta.26`（ClickHouse 注释两槽翻真）∶ `beta.27`（链上子实体自己在会话建档）∶ `beta.28`（`EntityWritten` ∶ `SoftDeleteValues` 两颗 AOP 钩子）∶ `beta.29`（按实体写入口 `orm.Update(实体)`/`orm.Delete(实体)` ∶ `Any` 存在性探针等性能四笔），`sql/Directory.Build.props` 源上声明的是 `0.39.0-beta.29`，**而 nuget 上能装的仍是这一批 `beta.24`**——本册写的 beta.25 之后那些源上行为（§2.5 的 `SoftDeleteValues` ∶ §3.3 的 `EntityWritten` 在内）今天都还没有对应的包，底下 csproj 示例那三行按 `beta.24` 写照样可装可跑。**两句要留着**：① 中间那几颗号（`beta.16`–`beta.23`）没有补发，源上从 `beta.15` 直接跳到 `beta.24`，包内 `<repository commit>` 钉的是 `3fd3ca2` 那一笔；② 刚推完那十几分钟，`dotnet add package --prerelease`（不带 `--version`）读到的还是上一代 —— 两半原因分开说：源侧注册文档要追八到六十分钟，而本机 `%LOCALAPPDATA%\NuGet\v3-cache` 里那份旧文档会一直端出来。2026-10-20 实测：清掉它（`dotnet nuget locals http-cache --clear`）之后，五颗探针全部直接拿到这一代那颗；等不及就把版本号写死。

主路径的最小 csproj（SQL Server 为例，换库只把 provider 那颗换成目的库、驱动包跟着换）：

```xml
<ItemGroup>
  <!-- ① 内核：QuickSqlOrm / DbContext / 查询与写链都在这颗里 -->
  <PackageReference Include="QuickSql.Core" Version="0.39.0-beta.24" />
  <!-- ② provider：只装你真要连的那家（nuspec 把同版本的 Core 与血统基类包一起带进来） -->
  <PackageReference Include="QuickSql.Providers.SqlServer" Version="0.39.0-beta.24" />
  <!-- ③ 源生成器：普通包安装即挂进编译（包内走 analyzers/dotnet/cs，不用再手写 OutputItemType） -->
  <PackageReference Include="QuickSql.Generators" Version="0.39.0-beta.24" />
  <!-- ④ 驱动：provider 不带你自己的 ADO.NET 驱动，应用侧显式装（§1.4） -->
  <PackageReference Include="Microsoft.Data.SqlClient" Version="7.1.1" />
</ItemGroup>
```

或者用命令行装（`--prerelease` 省得写版本号，装完 csproj 里落成 `Version="*-*"`）：

```bash
dotnet add package QuickSql.Core --prerelease
dotnet add package QuickSql.Providers.SqlServer --prerelease
dotnet add package QuickSql.Generators --prerelease
```

三颗各管一件事，缺一颗的症状不同，装的时候心里有数：

- **①`QuickSql.Core`**：装配根（`QuickSqlOrm`、`DbContext`）、QueryModel→AST→文本、驱动加载、物化、事务都在里面。严格说单装 provider 也会把它带进来（provider 的 nuspec 依赖 Core），显式写出来只是为了版本口径一眼可见、三颗同版本。
- **② provider（`QuickSql.Providers.<库种>`）**：方言、类型映射、键策略、DDL 发射。装哪一家 = 你能 `Create` 到哪一家。**provider 是编译期登记的，引用图上多一家就多一颗可寻址库种，不碰运行期**，所以"要连哪几座就装哪几颗"。18 颗可寻址叶子对应 18 个 `DbKind`；另有`QuickSql.Providers.{PostgreSql,Oracle,MySql}Base` 三颗血统基类包也在源上，但它们是叶子包的编译期依赖，**应用侧不直接装**。
- **③ `QuickSql.Generators`**：决定 G1 那张 Roslyn 源生成器在不在。装了它，带 `[Table]` 的实体在编译期生成契约、物化零反射（接线见 §2.2「G1 接线」），§1.8 那两行 `QuickSqlOrm.Create` 才有东西可分派；不装则实体全落 G2 反射兜底，且 `Create` 当场抛"生成器未挂进编译"。NuGet 装它就是一行——包内自带 `analyzers/dotnet/cs`，还原即挂进编译器，不必像源码引用时代那样手写 `OutputItemType="Analyzer"`。

**驱动包 provider 不替你别装**（§1.2 原则 3）：23 颗 QuickSql 包编译期一律不引用任何 ADO.NET 驱动，各家的驱动由应用侧自己按上面④那样显式装。哪些驱动包对应哪些库种、装完之后驱动怎么被找到，全在 §1.4。

> **辅助路径（只有仓库内开发、贡献者、`tests`∶`samples`∶`benchmarks` 才走）：源码 `ProjectReference`。** 引 `QuickSql.Core` + 用到的 provider 工程就够，Abstractions 跟着传递进来；生成器这时不能当普通引用（那样宿主会看见生成器的内部类型），要写成分析器挂载：`<ProjectReference Include="..\src\QuickSql.Generators\QuickSql.Generators.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />`。形态取自 `tests/QuickSql.Tests.Common/QuickSql.Tests.Common.csproj`（那里为了跨族对拍引了 20 个 provider 工程——17 颗叶子加 3 颗血统基类，普通应用不该这么干）。**普通消费者请用上面的 NuGet 主路径**——本节的源码引用只为改库、写样例的人留着，不是推荐安装法。

### 1.4 驱动怎么装：两条路，与仓库真实布局

`QuickSql.slnx` 收 23 个 `src/` 工程 + 两个测试工程（`QuickSql.Tests.Dialects`、`QuickSql.Tests.Integration`）；`QuickSql.Tests.Common` 是被引用方而不是解决方案节点，基准测试工程刻意不入册。

**路 A：NuGet 装驱动包（绝大多数库种走这条，与 §1.3 主路径一致）。** 装上之后驱动程序集落在应用输出目录，解析链（§1.6）第 3 步那句 `Assembly.Load` 直接命中。各库种对应的包（「程序集」列是内建默认表 `BuiltinDrivers`（`src/QuickSql.Core/DriverLoading/BuiltinDrivers.cs`）点的名，解析就按这个名字找；「源上读数」列是 2026-10-14 实查 nuget.org 的最新稳定档，「本仓测试档」列是 `sql/Directory.Packages.props` 钉的版本）：

| `DbKind` | 驱动包（NuGet ID） | 程序集 | 源上读数 | 本仓测试档 |
| --- | --- | --- | --- | --- |
| `SqlServer` | `Microsoft.Data.SqlClient` | 同名 | 7.1.1 | 7.1.0 |
| `MySql` ∶ `TiDb` ∶ `OceanBase` | `MySqlConnector` | 同名 | 2.6.2 | 2.6.2 |
| `PostgreSql` ∶ `OpenGauss` ∶ `GaussDb` ∶ `Highgo` ∶ `Gbase8c` | `Npgsql` | 同名 | 10.0.3 | 10.0.3 |
| `DuckDb` | `DuckDB.NET.Data.Full`（官方驱动；NuGet 一般装的是含原生绑定的 Full 那颗【文档·DuckDB】） | `DuckDB.NET.Data` | — | 无测试档 |
| `ClickHouse` | `ClickHouse.Client`（官方 ADO.NET 客户端【文档·ClickHouse，https://clickhouse.com/docs/integrations/language-clients/dotnet/ 】；NuGet 上有 DarkWanderer 原著与 ClickHouse/clickhouse-cs 改名后两代的同名包，没查过源上读数） | `ClickHouse.Client` | — | 无测试档 |
| `Sqlite` | `Microsoft.Data.Sqlite` | 同名 | 9.0.x 稳定档 | 9.0.0 |
| `Kingbase` | `Kdbndp`（金仓官方） | `Kdbndp` | 8.0.0 | 无测试档 |
| `Oracle` | `Oracle.ManagedDataAccess.Core`（Oracle 官方） | `Oracle.ManagedDataAccess` | 23.26.301 | 无测试档 |
| `Dameng` | `DmProvider`（达梦官方；**net10.0 上实测死路**，见下） | `DmProvider` | 2.0.0 | 无测试档 |
| `Xugu` ∶ `Gbase8s` ∶ `ShenTong` | 无公开 NuGet 包（本仓可见范围内） | `XuguClient` ∶ `GBase.DataDB` ∶ `Oscar` | — | 厂商给件，走路 B |

这张表的口径要说白：**「包在源上」≠「本仓连通过」。** `Kdbndp` ∶ `DmProvider` ∶ `Oracle.ManagedDataAccess.Core` 三颗的存在性是逐颗实查过的，而 §4.1 那十四家的驱动从未在一台机器上解析成功过——装上驱动包不改变那个口径，第一次连通仍要自己量。

实查比"存在性"再深一层，两颗过了、一颗翻车（2026-10-14，包内容逐颗开验 + 消费侧冒烟）：`Kdbndp` 8.0.0 包内是 `lib/net6.0/Kdbndp.dll`、`Oracle.ManagedDataAccess.Core` 23.26.301 是 `lib/net8.0/Oracle.ManagedDataAccess.dll`——两家程序集名与内建表逐字相同，`Create` 实测解析成功（文本层，读数在 §1.9）。**反例是达梦**：`DmProvider` 2.0.0 的包里只有 `lib/Dm.dll`（没有 TFM 文件夹，net10.0 上按 .NET Framework 回退还原、报 NU1701），程序集名 `Dm` 与内建表的 `DmProvider` 对不上，`Assembly.Load` 实测抛 `FileNotFoundException`——达梦的 NuGet 路今天不通，正解是路 B：厂商 .NET 版驱动按内建表点的名 `DmProvider.dll` 丢进 `drivers/`，或用 `DriverRegistry.Register("dameng", factory)` 注入那颗 `Dm` 程序集里的工厂自己验（那颗 Framework 时代的 IL 在 net10.0 上能不能走，本仓不背书）。

**路 B：`drivers/` 目录。** 信创现场换库不改代码、不改 csproj：把厂商那份 ADO.NET 程序集丢进**输出目录下的 `drivers/`**即可，文件名按内建表 `BuiltinDrivers` 点的那颗程序集名（探测位 `<root>/drivers/<assembly>.dll`，见 §1.5）。这是唯一那层"放对位置就认"的约定——内建表已经写死 18 颗 key 各自要找的程序集名，没有第二份配置文件要维护。

零配置就是「`DbKind` + 连接串」装配起来（§1.8）：（`DriverRegistry.Factory(key)` 先问内建表 `BuiltinDrivers.Find(key)`（18 颗 key 全在表上），拿到程序集名后按 §1.6 那条探测链找 DLL。**内建表是写死在代码里的默认，不是可编辑的配置**——要覆盖程序集名或工厂类型，只有一条 AOT 干净的路：`DriverRegistry.Register(key, factory)` 把已在进程里的工厂交进去（§1.6 末尾那一条）。

`drivers/` 只解决"托管程序集在哪"。虚谷那一族还要一份**原生同伴**（`xugusql.dll` / `libxugusql.so`），必须和托管那份一起放：只放托管时装载照样成功，要到 `Open` 才抛 `DllNotFoundException`（内建表 `BuiltinDrivers.cs` 的 xugu 行注释，v0.32 实测）。

### 1.5 内建默认表 `BuiltinDrivers` 逐行

v0.39 起这份表写死在代码里（`src/QuickSql.Core/DriverLoading/BuiltinDrivers.cs`），不再是可编辑的配置文件——旧的 `QuickSqlDrivers.json` 那一层（含 `DriverManifest` 手解、`QUICKSQL_DRIVERS_CONFIG` 环境变量、`DriverRegistry.Load(configPath)`）已整层删除。17 颗 key 各一行，形状是 `record DriverRef(string Key, string AssemblyName, string? FactoryType)`：

```csharp
new("sqlserver", "Microsoft.Data.SqlClient", "Microsoft.Data.SqlClient.SqlClientFactory"),
new("dameng",    "DmProvider",                null),
```

| 字段 | 必填 | 含义 |
| --- | --- | --- |
| `Key` | 是 | 与 `DbKind` 名同形的小写字符串；`BuiltinDrivers.Find` 大小写不敏感 |
| `AssemblyName` | 是 | 托管程序集名，也是 `drivers/<assembly>.dll` 的探测名 |
| `FactoryType` | 否 | `DbProviderFactory` 子类全名；省略（`null`）时扫导出类型里第一个非抽象子类 |

`FactoryType` 常被刻意留 `null`：达梦行那句理由写得很直白 —— 按文档猜一个全名，可能把一次本来能成的解析变成"找不到类型"。要改这三格里的任何一格（换程序集名 ∶ 补工厂类型），今天只有一条路：`DriverRegistry.Register(key, factory)` 把宿主自己构造好的工厂注入进去（§1.6 末尾），内建表本身不给运行期覆盖。

### 1.6 解析顺序与失败行为

名字来源只有一处：内建表 `BuiltinDrivers.Find(key)`（`src/QuickSql.Core/DriverLoading/DriverRegistry.cs` 的 `Factory` 第一步）。表上没有这颗 key 就当场抛 `DriverNotAvailableException`，消息里点出内建表不认识它、并给出两条补救（丢 `drivers/` ∶ 用 `Register` 注入）；表上有才继续找那颗程序集。

单颗 `key` 的解析顺序：

1. 探 `<root>/drivers/<assembly>.dll`，命中就用独立的 `DriverLoadContext` 加载（`<root>` 缺省是 `AppContext.BaseDirectory`，可由 `DriverRegistry(root)` 构造器改）。
2. 没命中 ⇒ `Assembly.Load(new AssemblyName(assembly))`，`Microsoft.Data.SqlClient` 这类公开包走这一支。
3. 工厂取法：声明了 `FactoryType` 就按全名 `GetType`，否则扫导出类型里第一个非抽象的 `DbProviderFactory` 子类；再看目标类型上公开的静态 `Instance` 字段，没有才退到无参构造器。

失败会被缓存（同一颗 key 第二次不会重新试），异常消息带"程序集 {AssemblyName}，内建默认，探测 {root/drivers}"，所以能一眼看出是谁替谁背的锅。入口是构造器 `new DriverRegistry(root?)`，取工厂用 `Factory(key)`（标了 `RequiresUnreferencedCode`）与 `TryFactory(...)`。

另一条路完全绕开探测链：`DriverRegistry.Register(key, factory)` 注入已经在进程里的工厂。生产上更常见的形态其实是这个 —— 宿主自己引用驱动包，把 `DbProviderFactory` 交出来；provider 的构造函数本来就吃工厂（`new SqlServerProvider(factory)`，见 `tests/QuickSql.Tests.Common/SqlServer.cs`）。

### 1.7 一条真实的 AOT 约束

内核不做 `Type.GetType(string)`、不按名字反射装配 provider：**provider 由调用方构造并交进 `QuickSqlOrmOptions.Provider`**（`tests/QuickSql.Tests.Common/SqlServer.cs` 的类注释把这件事叫"组装根本该做的事"）。运行期唯一带反射的入口是驱动解析，而那一步是读内建表 + 扫导出类型 + 读 `Instance` 字段，不在热路径上。

代价也写清楚了：`QuickSqlOrm` 的单参构造器（`src/QuickSql.Core/Orchestration/QuickSqlOrm.cs:94`）标了 `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]`，因为它顺带建 G2 反射注册表；干净口是 `QuickSqlOrm(QuickSqlOrmOptions, IEntitySchemaRegistry)`（同文件 `:84`），**构造期零反射**。`Select<T>()`（`:185`）今天整条读路径仍带同一串标注 —— 那是入口税，G1 预翻译落地之前摘不掉（设计意图，不是已交付）。

### 1.8 一行注册：`QuickSqlOrm.Create(DbKind, ...)`

上面 §1.6 与 §1.7 那两段的合起来是"手写注册"的路径：宿主自己 new 一颗 provider、把它塞进 `QuickSqlOrmOptions.Provider`。v0.37 第七笔在此之上补了一等公民口，两颗重载（`src/QuickSql.Core/Orchestration/QuickSqlOrm.cs:107`、`:125`）：

```csharp
// (a) 工厂已经在手：DI 容器给了、或者 DriverRegistry 已经 Load 过
var orm = QuickSqlOrm.Create(DbKind.MySql, "Server=...;Database=app;", SqlClientFactory.Instance);

// (b) 一行到库：DbKind + 连接串，工厂由内建表 BuiltinDrivers 现场解析（走 §1.6 那条探测链）
var orm = QuickSqlOrm.Create(DbKind.PostgreSql, "Host=...;Database=app;");
```

**触发条件是"生成器挂进了编译"。** `src/QuickSql.Abstractions/Providers/ProviderRegistration.cs` 里那两颗（`[QuickSqlProvider(DbKind)]` 属性、`ProviderRegistry` 静态表）是登记面的运行时结构，实际填表的是 `src/QuickSql.Generators/ProviderRegistrationGenerator.cs`——它订阅 `context.CompilationProvider`、扫被引用程序集上带属性的 leaf 类（每颗 provider 类今天都同时带 `public const string DriverKey` 与 `public XxxProvider(DbProviderFactory)` 构造器，缺一颗就跳过），产一段带 `[ModuleInitializer]` 的 `QuickSql.Generated.GeneratedProviders`，每行一句 `ProviderRegistry.Register(DbKind.<X>, "<driverKey>", static f => new <Ns>.<Xxx>Provider(f));`。产出全是静态 `new` 的委托，零反射、AOT 与 trim 都安全（§1.7 那条原则一字未破）。

**没挂生成器就零登记**，此时两行 `Create` 抛的是 `InvalidOperationException` 且消息点名"生成器 QuickSql.Generators 未作为 Analyzer 挂进编译"——症状可查，不是"驱动不存在"那种看不出原因的红。挂法两条：NuGet 就是 §1.3 那行 `dotnet add package QuickSql.Generators --prerelease`（包内自带 `analyzers/dotnet/cs`，装上即挂）；仓库内源码引用是那三行 `ProjectReference`：

```xml
<ProjectReference Include="..\src\QuickSql.Generators\QuickSql.Generators.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

**只登记你真引用的那几家。** provider 是编译期直接调用、不是按名字反射装配，所以引用图上没有的库种不会污染表；`ProviderRegistry.Registered` 读回来正好是"这个消费者能 `Create` 到哪几颗"的清单。这一句也就是 §1.7 那句"provider 由调用方构造"的编译期兑现：调用方从"亲手 new"降级到"引用一份程序集"，构造仍然静态、仍然可 AOT，只是这一行不写了。

`Create` 走的是 §1.7 那条带 `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]` 的 G2 兜底口（因为要顺带建反射注册表）。AOT 干净路径仍然是自己 new provider + `new QuickSqlOrm(options, schemas)`——`Create` 是给非 AOT 场景省事的糖，不是替代那条。

### 1.9 换数据库：动的是哪三处，不动的是什么

这一节的口径对着一个目标立：**同一份应用、同一副实体、同一批查询与写入代码，从一座库挪到另一座库**。动的只有三处：

1. **csproj**：装目的库那颗 `QuickSql.Providers.*`（原来那颗去留随意——provider 是编译期登记，引用图上多一家就多一颗登记，不碰运行期），外加目的库的驱动（§1.4 路 A 或路 B）；
2. **代码一处**：`DbKind.SqlServer` → 目的库的枚举值（入口就是 §1.8 那两颗 `Create` 重载或 DI 的 `AddQuickSqlOrm`）；
3. **配置一处**：连接串。

实体类 ∶ lambda ∶ `Select/Insert/Update/Delete` 链 ∶ `DbContext` ∶ 分表与读写分离的声明**一字不动**——引号 ∶ 分页 ∶ 参数前缀 ∶ `IDENTITY` 措辞这些方言差由 provider 吃掉。已知代价全部在两张现成的表里：§4.4 跨库迁移最容易踩的五格（折大写 ∶ 别名 ∶ 占位符 ∶ `OFFSET` 哨兵 ∶ `IDENTITY` 措辞），与 §3.2 的键回填能力表（**Oracle 血统要键即抛**，是换库时最硬的一格）。换库前还要读一遍 §2.4 的能力位口径：十七家里，SQL Server ∶ 虚谷 ∶ SQLite 三家有整族的活库见证，PostgreSQL 与 MySQL 两颗本尊各另钉了 1 位 `Live`（§2.4 表里有来路），其余十二家的方言文本层"该长这样"是本仓库自己认为的，不是厂商确认的。

**不改代码的前提，是代码里没写库私有的东西。** 三类东西换库时不会被改写、也不会被报警：`WithSql` 的文本 ∶ `orm.Ado` 的文本（§3.1 逃生舱，"文本一颗不动"是它自己的纪律）、`[Column(TypeName = "jsonb")]` 这种原生类型名直通（§2.2）。逃生舱每开一处，换库时就多一处要人工核对——这不是库的欠账，是舱的定价。

**可重跑读数（2026-10-14，消费侧冒烟，包全部来自 nuget.org 的 `0.39.0-beta.1`）**：空 `dotnet new console` 工程，装 `QuickSql.Providers.SqlServer` ∶ `QuickSql.Generators` ∶ `Microsoft.Data.SqlClient` 三包，一只 `[Table]` 实体加一条 lambda 链（`Where`＋`OrderBy`＋`Skip/Take`），`QuickSqlOrm.Create(DbKind.SqlServer, 占位串)` 离线发出 `SELECT [t0].[Id], … OFFSET @p1 ROWS FETCH NEXT @p2 ROWS ONLY`——生成器普通包安装即挂进编译、驱动走内建表解析、零配置文件在场。再补装 `QuickSql.Providers.Kingbase`＋`Kdbndp` 与 `QuickSql.Providers.Oracle`＋`Oracle.ManagedDataAccess.Core`，**实体与 lambda 一字不改**、只换 `DbKind` 和连接串：金仓发 `LIMIT @p1 OFFSET @p2`（双引号、混合大小写原样），Oracle 发三层 `ROWNUM` 分页（标识符折大写 ∶ `:p0` 前缀 ∶ 别名不带 `AS`）——§4.4 那张五格表里的三格当场可见。反面也量到一颗：没装 provider 包的 `DbKind` 在 `Create` 当场抛，消息列已登记清单（"已登记：SqlServer, Dameng"），这就是 §1.8"只登记你真引用的那几家"那条设计。全程是 `ToSql()` 文本层读数，没有一条语句发进真库；达梦在 NuGet 驱动档上是死路（§1.4 那颗反例）。

## 2. 怎么配置

### 2.1 `QuickSqlOrmOptions`：十格，全在这

`src/QuickSql.Core/Orchestration/QuickSqlOrm.cs`，从第 23 行起：

| 成员 | 行 | 默认 | 说明 |
| --- | --- | --- | --- |
| `Provider` | 27 | 必填 | `IDbProvider`，由调用方构造 |
| `ConnectionString` | 32 | 必填 | 只走驱动的 ADO.NET 语义；只产 SQL 时填占位串（§3.0）。**v0.38 第十一笔起它的含义是"缺省集群 `main` 的主库"** |
| `Replicas` | 35 | `[]` | `main` 的从库清单，按声明顺序轮询。为空即"这座不分读写"（§3.7） |
| `Clusters` | 39 | `[]` | `main` 之外的具名集群，每条是 `ClusterConfig(string Name, string Primary, IReadOnlyList<string>? Replicas = null)`（同文件 :65）（§3.7） |
| `Aop` | 41 | `new()` | 钩子容器，见 §2.5 |
| `Model` | 44 | `null` | Fluent 映射，见 §2.2 |
| `CommandTimeoutSeconds` | 46 | `30` | 透传给命令 |
| `PagingOrdering` | 49 | `PagingOrderingPolicy.Throw` | `Skip`/`Take` 无 `OrderBy` 时怎么办：`Throw`/`AutoOrderPrimaryKey`/`Allow` |
| `SchemaSync` | 53 | `SchemaSyncPolicy.Disabled` | 规格 §9.5 的结构同步策略；`Execute` 才自动发 DDL |
| `ForceSchemaSync` | 56 | `false` | 非 Development 环境放行 DDL 的那颗额外开关，与上一颗要同时给 |

`QuickSqlOrm` 上可读的属性：`Options`、`Provider`、`Schemas`、`Aop`、`Compiler`、`Ado`、`Sources`、`PagingOrdering`、`Filters`（行 134–158）。`Sources` 是 :148 新添的那一颗，`ConnectionSources` 上读得出 `ClusterNames`（:68，按 Ordinal 排过）、`Knows(cluster)`（:70）、`ReplicaCount(cluster)`（:72）、`PrimaryConnectionString(cluster)`（:75）。`orm` 是单例级对象（不持有连接），`DbContext` 才是 Scoped。

### 2.2 实体映射：两条路

**路 A：标注。** 自研注解 + 一颗枚举，都在 `src/QuickSql.Abstractions/Metadata/QuickSqlAttributes.cs`，命名空间是 **`QuickSql.Metadata`**（Fluent 那一路在 `QuickSql.Metadata.Fluent`）——只 `using QuickSql;` 拿不到 `[QuickColumn]`，NuGet 冒烟里这一格实测绊过一次（§1.9）。**名字、类型名、排除、主键、必填、长度、备注这些系统注解接得住的语义一律不重复造注解**：表名与模式用 `System.ComponentModel.DataAnnotations.Schema` 的 `[Table]`，列名与 provider 原生类型名用同一命名空间的 `[Column]`，排除用 `[NotMapped]`，主键用 `[Key]`，必填用 `[Required]`，长度用 `[StringLength]/[MaxLength]`，备注（表与列同一格）用 **`System.ComponentModel`** 的 `[Description]`——它就是从前的自研 `[Table(Comment=…)]/[Column(Comment=…)]` 退场后的接棒者，座次照旧排在 Fluent 之后。自研层只剩系统注解表达不了的语义：

- `[QuickColumn]`（只能挂属性/字段）：`Length`、`Precision`、`Scale`，以及三态 `IsNullable`/`IsPrimary`/`IsIdentity`（`TriState`：`Unset`/`Yes`/`No`）——尺寸与三态是 DataAnnotations 表达不了的（`[Required]` 只有"非空"一个方向，`[Key]` 只管"谁是键"管不到"值谁给"）
- `[Index(params string[] memberNames)]`，属性 `IsUnique`、`Clustered`、`Descending`——形状与 EF Core 的 `[Index]` 同一版：**不给索引名**，名字由约定生成（无模板的表按 `IX_{表}_{列}`，分表的表名是 `{year}` 样模板，按类名算，见 §10.3）
- `[SplitTable(SplitKind kind, string field)]`，可选属性 `Buckets`（只有 `Hash` 档读它）；它要求同一颗 `[Table]` 的名字是一张带占位符的模板（用法与判据见 §3.6）
- `[DataSource(string cluster)]`（`QuickSqlAttributes.cs:94`，只能挂类）：这一张实体住哪一座集群。**注解里只有集群名、没有连接串**，连接串全在 §2.1 那三颗上（用法与判据见 §3.7）。不写即 `main`，而 `main` 就是那条 `ConnectionString` —— 今天全部调用方行为一字不变
- `[ConcurrencyToken]`（只能挂属性上；语义是"只比较不书写"）

DataAnnotations 照常认（`[Key]`、`[Required]`、`[StringLength]`、`Schema.Table`、`Schema.Column(name/TypeName)`、`NotMapped`），现场见 `tests/QuickSql.Tests.Common/Model.cs` 里的 `LegacyShipment`。**自研的 `[Table]/[Column]` 已整个删除**（没有 Obsolete 过渡期）：还在写 `[QuickSql.Metadata.Table("…")]` 的代码，把 using 换成 `System.ComponentModel.DataAnnotations.Schema` 即 `[Table]` 同形；`[Column(DbTypes=…)]` 换 `[Column(TypeName=…)]`（同一格、同一优先级档）；`[Column(IsIgnore=true)]` 换 `[NotMapped]`；`[Column(IsPrimary=TriState.Yes)]` 首选 `[Key]`（QuickColumn 里那格三态保留，是为了"写了 No"与"点名非空"这两件 `[Key]` 表达不了的事）。

约定的那一层：整型主键 `Id` ⇒ 主键 + 自增；C# 可空 ⇒ 可空列；无长度字符串 ⇒ provider 默认（SQL Server 落 `NVARCHAR(MAX)`）。导航属性不成列。

- **无参构造器不问可见性**：物化装配用的那一颗，公共查不到就退到内部/受保护/私有
  （`MemberAccessors.ParameterlessConstructor`，四处消费点共用：G2 的实例工厂、G2 的行读取器、原生 SQL 的行出口、
  成员初始化投影）。这一族的来头是 ABP/DDD 的 `protected X()` + 有参创建构造器——把可见性当门槛得到的不是"更安全"，
  是整颗实体读写两侧一起拒（实测：`Insert` 与 `Select` 响同一句话）。**带参的那一颗不在这一格**：
  构造器 DTO 与 record 走 `ViaConstructor` 那一臂，仍只认公共构造器，因为参数表是给人对位的协议。
  两条实测边界：`Activator.CreateInstance<T>()` 对非公共无参构造抛 `MissingMethodException`（它要 `new()` 那一档），
  `RuntimeHelpers.GetUninitializedObject` 出得来实例但**不跑构造器也不跑字段初始化器**（`= []` 那一格会留 null），
  所以走 `Expression.New(非公共 ctor).Compile()`。G1 生成侧的门槛留在公共（生成的是字面 `new T()`，非公共编不出来），
  被挡下时那条 QuickSql003 说的是"少一份编译期事实册"，不是"不能物化"。
- **没有写口的成员既不是列也不是关系**：`public bool NeedsGrading => Type == …` 这一形有公共 getter、类型又可映射，
  旧那一版因此把它收进契约当一列；而<em>读取器</em>建到它就 `Expression.Assign` 不起
  （<c>ArgumentException: Expression must be writeable</c>），症状不是"这一列读不出来"而是整个列表接口 500，
  堆栈里连成员名都没有。判据按"有没有写口"（<c>PropertyInfo.CanWrite</c> ∶ Roslyn 的 <c>SetMethod is null</c>），
  <b>不按"写口公不公开"</b> —— `{ get; protected set; }`、`{ get; private set; }`、`{ get; init; }` 都落得回值
  （编译后的表达式树不受可访问性限制），按后者判会把整族 ABP 实体的主键与明细列一起摘掉。
  两侧同一次序（反射侧 <c>ReadMembers</c> 在显式排除之后、类型判定之前；生成器同位置）。
  钉在 <c>ComputedMemberTests</c>（离线五臂：列清单、三种非公开写口仍在场、读取器建得起来、建表文本没有那一列、
  集合边没被顺手摘）+ <c>MetadataEquivalenceTests</c> 的 G1∶G2 对拍。
- **分页与预容量都不许照抄调用方给的数**：<c>ToPagedAsync(page, size)</c> 的偏移与"探下一页要多取一行"
  都按 <c>long</c> 算再收回 <c>int</c> —— <c>size + 1</c> 与 <c>(page - 1) * size</c> 在
  <c>int.MaxValue</c> 那一档翻成 <c>int.MinValue</c>，发出去的句子是 <c>LIMIT -2147483648</c>，
  库以 <c>2201W: LIMIT must not be negative</c> 收尾（现场：一颗"取全部"的缺省 <c>pageSize = int.MaxValue</c>
  的下拉列表 500）。偏移真超出 <c>int</c> 时是<b>当场抛并点名那一页</b>，不是发一条负 OFFSET。
  <c>size == int.MaxValue</c> 那一档不再多取一行，于是 <c>HasNext</c> 恒 <c>false</c>（2^31-1 行下面放不下第二整页）。
  读取器的预容量另有天花板（8192 行）：<c>Take(n)</c> 说的是"最多读 n 行"，不是"给我准备 n 行的数组"——
  照抄那一颗会在读第一行之前就 <c>OutOfMemoryException</c>。
  钉在 <c>SqlServerPagingTests</c>（闸口两臂：溢出那一臂响、正好落在上限那一臂放行）
  \+ <c>PagingOverflowPathTests</c>（活库两臂：<c>LIMIT</c> 是正的最大值而行数与全量链对得上、
  普通那一页仍按 <c>size + 1</c> 探）。

- **时刻交出去之前要对上列的形状（PG 系）**：Npgsql 6 起按 `DateTime.Kind` 分型，`Kind=Utc` 只肯进
  `timestamp with time zone`，写进裸 `timestamp` 在绑定当场抛 `ArgumentException`。本家给 `DateTime`
  选的存储型是裸 `TIMESTAMP(3)`，所以 `PostgreSqlValues` 在绑定前按<em>列声明的那个型</em>折一次：
  裸列把 `Utc` 的说法摘成 `Unspecified`（Ticks 逐字节不动，换 `Local` 才是数据改写）；
  列显式声明成 `timestamptz`/`with time zone` 时换成 `DateTimeOffset`（驱动按 `DbType` 认型，
  `DbType.DateTime2` 说不了带时区那一型），偏移按 `Kind` 说：`Utc` 与没表过态的记零偏移、`Local` 留本机偏移。
  `DbType` 只在那一形真的换了的时候跟着换，其余仍按原值取（null 那一格不许被折成 `DBNull` 的型）。
  SQL 文本一个字不动（列清单、占位符编号、参数颗数逐字节同形）。读侧对应的两臂在 `ValueConverter`
  （库里交回的裸时刻不猜成 UTC）。钉在 `PostgreSqlTimestampBindingTests`（离线五臂）
  \+ `PostgreSqlTimestampPathTests`（真 PG：一行进去、库里读回同一串钟面；带时区那一列过一个瞬时来回）。
- **整型主键归谁给号可以装配**：上面那条"整型主键 ⇒ 自增"是**约定**，而有一族库的主键由客户端生成
  （雪花编号那一类），库里那一列只是 `bigint` 加一个默认值，库既不给号也不许写号。装配口是
  `ContractResolver(provider, model, integralPrimaryKeyAutoIncrement: false)`，走 `QuickSqlOrm` 那颗
  "注册表由外部提供"的干净口交进去（`QuickSqlOrmOptions` 里没有这一格）。
  它只挪**约定**那一档：`[QuickColumn(IsIdentity = TriState.Yes)]` 与 Fluent `IsIdentity(true)` 照旧赢过它，
  真自增表不需要为这颗开关改回来。开着默认值时两侧形状与这一格落地之前逐字同文，
  关掉之后 `ExplainKeys()` 那一档**当场抛并点名"没有自增列"**（不是静默回空包）。
  钉在 `ApplicationKeyConventionTests`（离线五臂）+ `ApplicationKeyPathTests`（活库：两行各带自己那个号进库，
  第二行不撞键 —— 单行插入在两种契约下都能绿，所以这一案必须两行）。
- **"非空"是谁说的，必填尺子只量说过话的那一格**：`NullabilityInfoContext` 对可空上下文之外的成员
  （`#nullable disable`、没带可空元数据的引用程序集 —— 整份 ABP/DDD 框架代码天天在这一格）交回
  `Unknown`，而 `Unknown` 说的是"我没表过态"，不是"我说了非空"。旧那一版把它一并折成非空，
  于是每一次合法写入（租户号、备注、客户端令牌那几列本来就是 null）都被一把没人宣布过的尺子整批拒掉。
  今天 `ColumnContract.NullabilityDeclared` 记下这一格：`false` 时 §10.6 的必填尺子不落，
  **而列的形状一字不动**（DDL 照旧 `NOT NULL` —— 翻那一格等于替现库批量 `DROP NOT NULL`）。
  说过话的三形照旧挡：可空上下文里的 `string`、`[Required]`、`[QuickColumn(IsNullable = TriState.No)]`；
  `string?` 与 Fluent `IsRequired(false)` 照旧放行。两侧同一把尺子由 §13.11 的对拍钉住
  （G1 问 Roslyn 的 `NullableAnnotation.None`，G2 问 `NullabilityState.Unknown`，两个名字不同的东西要落成同一个值）。
  钉在 `NullabilityDeclarationTests`（离线四臂，含"发出去的是 `DBNull` 不是空串"与建表文本未变）
  \+ `NullabilityDeclarationPathTests`（活库：那行 null 真进了库、真读回是 null，而被拒的批次一行都没写进去）。

**路 B：Fluent。** `src/QuickSql.Abstractions/Metadata/Fluent/ModelConfiguration.cs`，今天真实存在的成员只有这些：

```csharp
var model = new ModelConfiguration();

model.Entity<Order>().ToTable("QuickSqlOrder", "app");            // 第二参是 schema
model.Entity<Order>().HasComment("订单主表");
model.Entity<Order>().HasIndex(nameof(Order.CreatedAt));
model.Entity<Order>().HasUniqueIndex(nameof(Order.CustomerName));
model.Entity<Order>().Property(o => o.Note).HasDbType("xml");
model.Entity<Order>().Property(o => o.Amount).HasPrecision(18, 4);
model.Entity<InventoryItem>().Property(i => i.RowVersion).IsConcurrencyToken(false);
```

`PropertyBuilder` 全量：`HasMaxLength`、`HasPrecision(int, int)`、`HasDbType(string)`、`HasComment`、`IsRequired(bool = true)`、`IsKey(bool = true)`、`IsIdentity(bool = true)`、`IsIgnore()`、`IsConcurrencyToken(bool = true)`。**没有** `HasColumnName`（列名走 `ToTable`/`[Column]` 那条），也没有关系配置、鉴别器、影子属性。**分表与分库都故意不落 Fluent**（`[SplitTable]`、`[DataSource]` 只有注解那一条路，理由同写在那两颗注解的注释里：Fluent 侧看不到它们就得换一套校验时机，而分表最不能有的是"两条路径算出两种表名"，分库同理——那一格错是把语句发到另一座库里去）。

**层序**（`MetadataLayer`，低层不覆高层）：`ProviderFallback=0` → `Convention=1` → `DataAccessAnnotation=2` → `QuickSqlAttribute=3` → `Fluent=4`。每一列的来历记在 `ColumnContract.Origin`（`record OriginTrace(MetadataLayer Layer, string Note)`），能读出"并发 ← Fluent IsConcurrencyToken"这种结论。同一层重复声明同一事实会抛 `MetadataConflictException`。

**G1 接线**：挂上分析器之后，带 `[Table]` 的实体会在编译期生成 `QuickSql.Generated.QuickSqlGeneratedMetadata.TryGet`。要用它得把注册表交给干净口：

```csharp
var schemas = new GeneratedEntitySchemaRegistry(provider, QuickSqlGeneratedMetadata.TryGet);
var orm = new QuickSqlOrm(options, schemas);
```

形状取自 `tests/QuickSql.Tests.Dialects/MetadataEquivalenceTests.cs`。没标注的实体（如 `BareReport`）`TryGet` 返回 false，落回 G2 反射；两条路径的契约逐字段相等由该文件对拍钉住。注意：**被 Fluent 配置过的实体也走 G2**（生成器注释自陈）。

### 2.3 标识符策略：迁移时最硬的一格

`IdentifierPolicy` 不是枚举而是 record（`src/QuickSql.Abstractions/Metadata/IdentifierPolicy.cs`）：位置参数依次是 `OpenQuote`、`CloseQuote`、`CaseSensitive`、`FoldToUpperCase`、`QuoteWhenMixedCase`、`QuoteAlways = false`；成员 `Quote(string)`、`NeedsQuote(string)`、`Normalize`。七颗预设与后果（行号为该文件内）：

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

DuckDb 没有新增预设：它是 PG 血统的第七颗叶子，沿用血统的 `PostgreSQL` 预设发射（双引号、混合大小写原样），而本家标识符**大小写不敏感、且按输入原样保留**【文档·DuckDB】——与该预设那一组位的偏差记在设计文档 §14（§9.1 那一行点名"偏差不删位"），本文与血统文本一字不改。

一个已知的空档：`FoldToUpperCase` **只有"折大写"这一格，没有"折小写"**。GBase 8s 与 PG 在库里折小写，而发射器不折 —— 也就是说这一族的名字大小写后果要现场自己承担（规格 §9.2 记过，本文按代码现状复述）。

### 2.4 能力位：`DbCapabilities`

32 颗具名位 + `None`（`src/QuickSql.Abstractions/DbCapabilities.cs`，`TempTable` 之后挂到 `1u<<31`，底层类型随本笔换 `uint`），每颗一个 `1<<n`：`Paging_LimitOffset`、`Paging_RowNum`、`Paging_OffsetFetch`、`Returning`、`Returning_OnlyExpression`、`Upsert_OnConflict`、`Upsert_MergeInto`、`Upsert_OnDuplicate`、`Boolean_Type`、`Guid_Raw16`、`Guid_Char36`、`Sequences`、`Identity_ExplicitInsert`、`Schemas`、`Cte`、`WindowFunctions`、`SkipLocked`、`Lock_NoWait`、`Json_Type`、`Array_Type`、`Bulk_Copy`、`Bulk_ArrayBind`、`RefCursor`、`MultiStatement`、`MultiResultSets`、`Count_Over_Window`、`CaseSensitive_Identifiers`、`Comment_On_Column`、`Alter_Column_Precision`、`Truncate_Cascade`、`TempTable`、`Transactions`。

**`Transactions`（第 32 颗，本笔新添）是位表上第一颗被内核真当闸门读的位**：公开的 `BeginTransaction` 与内核三条写路径（`SaveChanges` ∶ `InsertOrUpdate` ∶ 批量链的执行段）都判它——声明了这一位才开真的 `DbTransaction`；**没声明就逐句按库的 autocommit 直发，而且一次批量 / 一次保存里的多条语句不原子——中途失败已发出去的语句不回滚**（单条语句自身的事务性按各家库文档记账）；没声明还想显式开事务，公开口照 `CanCreateDatabase=false` 那颗先例抛 `NotSupportedException` 带明话（严禁静默假事务）。今天在册的 16 家（ClickHouse 于 0.39.0-beta.24 落地之前那一代）全部声明这一位；ClickHouse 是位表之外的"不带它落层"的第一家，见设计文档 §14。

真实分布与证据档位（同一张表；掩码声明处就是那几行，16 份台账里 SQL Server 一族不入册，理由见 §4.2）：

| 血统 | 声明处 | 位 | 离线钉 | 活库钉 | 仅厂商文档 | 谁继承 |
| --- | --- | --- | --- | --- | --- | --- |
| SQL Server | `src/QuickSql.Providers.SqlServer/SqlServerProvider.cs:46` | 18 | 不入册 | 集成层两台真库 | — | — |
| PG 系 | `src/QuickSql.Providers.PostgreSqlBase/PostgreSqlProviderBase.cs:83` | 16 | 7 | 1 | 8 | 7 颗叶子，无一覆写 |
| DuckDb（PG 血统第七颗） | 随血统掩码（`DuckDbProvider` 未覆写；`PostgreSqlProviderBase.cs:83`） | 16 | 8 | 0 | 8 | — |
| Oracle 系 | `src/QuickSql.Providers.OracleBase/OracleProviderBase.cs:85` | 7 | 6 | 0 | 1 | Oracle / Dameng / ShenTong |
| MySQL 系 | `src/QuickSql.Providers.MySqlBase/MySqlProviderBase.cs:155` | 7 | 5 | 1 | 1 | MySQL / TiDb / OceanBase |
| 虚谷 | `src/QuickSql.Providers.Xugu/XuguProvider.cs:127` | 8 | 3 | **4** | 1 | — |
| GBase 8s | `src/QuickSql.Providers.Gbase8s/Gbase8sProvider.cs:138` | 5 | 4 | 0 | 1 | — |
| SQLite | `src/QuickSql.Providers.Sqlite/SqliteProvider.cs:202` | 6 | 1 | **3** | 2 | — |

三处读数的脚注：PG 系那 1 位 `Live` 是 PostgreSQL 本尊那本台账的 `Bulk_Copy`（批量写侧那一轮，集成册 `BulkPathTests` 在真 PG 上全绿）；MySQL 系那 1 位同理是 MySQL 本尊的 `Bulk_Copy`（同一轮，`TiDb`/`OceanBase` 两本该位仍是仅文档）；DuckDb 那一格比血统六本多 1 颗离线钉，是因为类型表四格换名与身份列换形态让它头一回有了自家的 `golden/duckdb/` 基线——其余位与血统同源，位上一位不翻（`CaseSensitive_Identifiers` 与本家实际行为的偏差记设计文档 §14）。**"仅厂商文档"普遍 +1 的那一颗就是 `Transactions`**：它是本笔（0.39.0-beta.23）挂上的内检索引位——各家多语句事务在库里到底怎么成立，仍然由各血统的文档口径与活库见证记账，今天都还在"未实测"那半本上；行为诚实条款见本格上文与位注释。

**关键判据：能力位不是配置开关，而是一份已落地事实的账本。** 全仓库没有一处 `override DbCapabilities`（叶子只改 `DriverKey` 与构造参数，达梦额外改 `CreateCodeFirst` 与 `IdentityClause`，`src/QuickSql.Providers.Dameng/DamengProvider.cs:50`、`:62`）；而运行期的分派读的是方言上的成员（`ISqlDialect.SupportsJoinedWrites`、`WriteTargetAlias`），**不再是这颗掩码——例外只有一颗**：`Transactions` 那颗（本笔起，内核写路径与公开 `BeginTransaction` 都按它分岔，判据取唯一写法 `TransactionCapability.Declared`，`src/QuickSql.Core/Execution/TransactionCapability.cs`；判定册见 `tests/QuickSql.Tests.Dialects/TransactionCapabilityTests.cs`）。

台账在 `tests/QuickSql.Tests.Dialects/CapabilityLedgerTests.cs`：一颗一家一行，出处分三档 —— `Provenance.Live`（本 provider 产出的那条语句被那台库执行过）、`Provenance.OfflineText`（文本被基线或断言钉住）、`Provenance.VendorDoc`（只有厂商文档；`Ledger.VendorDocOnly` 是算出来的那一格）。三颗计数常量（`Declared`/`PinnedOffline`/`PinnedLive`）是钉子，改一位就红。

所以：不要按位选路，也不要因为某位是 0 就断定"这家做不到"。它说的是"本仓库今天为这颗位拿得出手的证据在哪一档"。

### 2.5 钩子、日志、全局过滤器与行为改写

`QuickSqlAop`（`src/QuickSql.Abstractions/Aop/QuickSqlAop.cs`）不是事件总线，是一组可空委托字段，订阅就是赋值（`aop.CudBuilding = e => ...;`、`aop.CudBuilding += ...;`，见 `tests/QuickSql.Tests.Dialects/SqlServerCudBuildingTests.cs`）：

| 成员（行号） | 类别 | 拿到什么 |
| --- | --- | --- |
| `Executing` 24 | 观察 | `CommandExecutingEvent(Sql, OperationKind, TableNames)`：**执行前**的那一位。发射点 `AdoExecutor.BeforeExecute`，排在 `CommandTerminating` 之后 —— 被闸门叫停的句子不播报（顺序就裁在这一步）。手上只有文本与表名：耗时、行数、实际路由这一刻还没被决定，那三格在 `Executed` 上。要"这一句要发出去了"的现场（语句挂住时唯一还留得下的那一行日志）订它 |
| `Executed` 25 | 观察 | `CommandLogEvent`（含 `ElapsedMilliseconds`、`TableNames`，以及 v0.38 第十一笔添的 `Route`——`DataSourceRoute(Cluster, Role, Reason)`，必填的一格，见 :112。所以"这一句实际走了哪座库、哪个角色、按哪一档"是订阅出来的事实，不用翻库猜，见 §3.7） |
| `Error` 26 / `SlowQuery` 27 | 观察 | `CommandErrorEvent(Exception, string Sql, long)`、`SlowCommandEvent`；阈值 `SlowQueryThresholdMilliseconds`（默认 500，行 62） |
| `Materialized` 28 | 观察 | `MaterializedEvent(object Entity, string EntityTypeName, int OrdinalInBatch)` |
| `EntityWritten` 29 | 观察 | `EntityWrittenEvent(object Entity, string OperationKind)`：**这一轮写出去的实体**逐只点名，操作种类 `insert`/`update`/`delete`。发射点是会话的写阶段（`EntityTracker.WritePhaseAsync`），**在语句建出来之前** —— 于是订阅者既能观察（发领域事件、发实体变更事件），也能**改实体**（那一格改出来的值会被紧接着建出来的语句带出去；取值段读的是实体）。逐只一次 ⇒ 收到的事件与「这一轮改了几行」一一对应。**只覆盖会话那一条路**（链式的 `orm.Insert`/`InsertRange`、`orm.Update(实体)`/`UpdateRange`、`orm.Delete(实体)`/`DeleteRange` 都不走跟踪器）。v0.39.0-beta.28 新增，见 §3.3 |
| `SoftDeleteValues` 30 | 改写 | `SoftDeleteValuesEvent { Entity, TableName, ExtraValues }`：软删那一档**往上加的 SET 项**（列 + 值）。软删自己只写两格（置位 + 时间），「谁删的」是调用方的领域事实，库里替不了 —— 这一颗让调用方把它加上去，库负责参数化、写出去，并按与那两格同一把尺子验（自增列与并发列不收）。**逐行触发**（有行才触发）：链式 `Delete<T>().Where(...)` 那一档没有行可给，那一档不触发。v0.39.0-beta.28 新增，见 §2.5 |
| `BulkDegraded` 36 | 观察 | `BulkDegradedEvent(BulkMethod Requested, BulkMethod Method, int RowCount, string Reason)`：只在**真的降级**时派发，见 §3.2 的批量高速层 |
| `AuditValue` 45 / `ParameterBinding` 46 | 改写 | `AuditValueEvent`（`SetValue`/`WasSet`/`OperationKind`）、`ParameterBindingEvent`（`SetValue`/`ChangeType`）。**前者的发射点只有三条"手上有整行实体"的路**：`orm.Insert`/`InsertRange`（`OperationKind` = `insert`）、`orm.UpdateRange(实体集)`（= `update`，v0.38 第十六笔）与 `orm.Update(实体)`（同 = `update`，两扇门问的列与列序逐字相同——筛列的判据、钩子种类、空键那把尺子各只有一份实现）；链式 `orm.Update<T>().Set(...)`、两条删除出口 `orm.Delete<T>()` 与 `orm.Delete(实体)`、以及 `SaveChanges` 的 update 档都不问它，那几条出口的改写槽位仍是 `ParameterBinding`，判据与两处未接的理由见规格 §10.1 同笔记录 |
| `CudBuilding` 52 | 改写 | `CudBuildingEvent { OperationKind, Sql, Rewritten }` |

改写过的步骤进 `HookTrace(int Step, string HookName, HookCategory Category, string Before, string After)`，在 `Explain()` 与 `SaveChangesReport.HookReplay` 里回放。观察类钩子的异常被 `Observe(...)`（行 73）吞掉并上报，不让日志代码打挂写入。

**没有的**：`QueryBuilding`、`CommandCreating` 在 `QuickSqlAop` 里只是注释点名的桩（类注释自陈，无发射点）；**观察档五颗都有派发点**：`Executing`（`QuickSqlAop.cs:24`）在 `AdoExecutor.BeforeExecute` 播报，`Executed`/`SlowQuery` 在 `Succeeded`，`Error` 在 `Failed`，`Materialized` 在物化那一趟。`Executing` 这一格曾被记成"声明在场而不响"（v0.38 第十四笔按 `src/` 全扫复核、`.Executing` 零命中，判据是盘上零派发），今天按同一条口径改判回来：它有发射点了。它交回的是 `CommandExecutingEvent` 而不是 `CommandLogEvent`（那颗记录里的 `ElapsedMilliseconds`/`AffectedRows`/`Route` 在执行前一个都不存在，`Route` 还自陈"报的是结论不是意图"）；整句文本级的改写仍然订 `CudBuilding`，取消仍然订 `CommandTerminating`。决策类钩子、注册式钩子、重试都没有。**"读写分离"今天从这一份名单里划掉了**，但它不是以钩子的形状进来的：它长在 §2.1 那三颗选项 + §3.7 那条路由上，AOP 侧只多了一颗 `CommandLogEvent.Route`（观察者拿读数，不是决策点——决策点做成钩子等于让日志代码决定语句发去哪座库）。`ICacheAdapter Cache`（行 60）有槽位，`WithCache(string, TimeSpan)` 有入口，仓库内没有实现，要接自己的缓存得自己写这一颗。

**全局过滤器**是另一只对象：`orm.Filters`（`QuickSqlOrm.cs:175`）返回 `FilterRegistry`，注册口是

```csharp
orm.Filters.Add<Ticket>(t => !t.Deleted);                  // 读、Update、Delete 三条链都带
orm.Filters.Add<Customer>(c => c.TenantId == 7, mandatory: true); // 链上的 IgnoreQueryFilters() 压不住
```

签名 `Add<TEntity>(Expression<Func<TEntity, bool>> predicate, bool mandatory = false)`，可链式，注册期先试翻译一次（`src/QuickSql.Core/Query/Filters/FilterRegistry.cs`）。**它管不到 `SaveChanges` 与批量**（谓词长在 `EntityTracker` 上，那一档到今天仍未接 —— 它等的是规格 §14 那一格那句裁决「被过滤器挡住的行算不算一次并发冲突」，v0.26 起归用户，没自决）。

**读侧关掉过滤器有两句，射程不同**：

```csharp
orm.Select<Ticket>().IgnoreQueryFilters();                 // 这一条链上一条都不贴
orm.Select<Ticket>().IgnoreQueryFilter<ISoftDelete>();    // 只跳过注册键正是 ISoftDelete 的那几条
orm.Select<Ticket>()
    .IgnoreQueryFilter<ISoftDelete>()
    .IgnoreQueryFilter<Ticket>();                         // 点名可以叠着写
```

`IgnoreQueryFilter<TFilter>()` 的参数吃的就是 `Add<TFilter>` 那一颗**注册键**，判据是键的同一性而不是继承关系：注册在接口上的那一条要点名接口才跳得掉，点名实体碰不到它；反过来点名一个接口也碰不到注册在实现类上的那些条。这一档窄在这里是有理由的 —— 一族式的点名会让"我只想关软删那一条"这句话静默地少掉同一张表上的租户条件，而那正是下面那颗 `mandatory` 要拦的失败形态。要说"全部"就写不带参数那一句（`IgnoreQueryFilter<object>()` 与它同一条路、同一个结果）。

`mandatory: true` 的那一条在两句里都拦得住，但只拦**被它盖到的那一次点名**：一条链上同时有「mandatory 的接口键」与「普通的实体键」时，点名实体照常通过（那一句没关掉任何不可忽略的东西），点名接口当场抛。两次点名叠在一起时，第二次会把第一次那份账一起问一遍，所以多颗 mandatory 不会只有第一颗响。写侧两条链（`IUpdate<T>` / `IDelete<T>`）今天没有对应的口子 —— 与下面那句 arity 1 的限制同源：跳过过滤器在写侧的后果比读侧更重，那一格要先把"叠了哪几条"做成能按条枚举的结构。

**软删的改写入口是第三只对象：`orm.Behaviors`（`QuickSqlOrm.cs:183`，v0.38 第十七笔落地）**，注册口是

```csharp
orm.Behaviors.SoftDelete<Ticket>(t => t.Deleted, t => t.DeletedAt);             // 置位 + 记时间，缺省本地时钟
orm.Behaviors.SoftDelete<Order>(o => o.Deleted, clock: () => DateTime.UtcNow);  // 要 UTC 就自己给一颗
```

签名 `SoftDelete<TEntity>(Expression<Func<TEntity, bool>> deleted, Expression<Func<TEntity, DateTime?>>? at = null, Func<DateTime>? clock = null)`（`src/QuickSql.Core/Behaviors/BehaviorRegistry.cs`），回返 `this` 所以可链式，册子一只 orm 实例一份。注册之后，**四处删除出口**都不再发 `DELETE`：链式 `orm.Delete<T>()`、按实体那一行 `orm.Delete(实体)`、批量 `orm.DeleteRange(rows)`、以及 `SaveChanges` 里被摘掉的那些行。四家一起接不是因为写了四遍，而是因为「这张表是软删表」是一条按实体成立的事实 —— 只接一家就会留下"同一张表走 A 留住行、走 B 丢行"那个洞。**它与 `orm.Filters` 是两只对象、各管一半**：行为管"`Delete` 落库里是哪一句"，过滤器管"读到的集合里有没有已删的行"；只注册行为，`orm.Delete<Ticket>()` 置了位而没人替读侧筛掉那几行，只注册过滤器，读侧筛得掉而那一格永远不会有人置位。

读这一档要知道的六件事：

- **改的是形状，不是范围**：`WHERE` 一个字都不动（全局过滤器照旧叠在同一条 `WHERE` 上，排在调用方条件之后），所以命中范围在改写前后是同一个。`Explain()` 里 `DeletePlan.Ast` 仍是被要求的那棵 `DELETE`（说的是"调用方要的是删除"），`Sql` 才是发出去的那句 `UPDATE`（说的是"库里执行的是置位"）—— 两句都真，读错任一句都会误判。`Reason` 按最终文本分岔并点名 `SET` 里那两列；`SaveChanges` 那份报告的 `Operations[].Columns` 在软删时交出的也是那两列，没注册时仍是空。
- **标记列必须是 `bool`**：那一格写进去的值恒为 `true`，所以 `0/1` 整数标记与 `"Y"/"N"` 字符标记不是这一档（没有第二档值可给）。要那种写法就自己 `orm.Update<T>().Set(o => o.标记, 值).Where(...)`，或者订一颗 `CudBuilding` 做文本改写。
- **`deleted` 与 `at` 要的是成员的读法**（`t => t.Deleted`），不是谓词也不是表达式：`t => !t.Deleted`、`t => t.A.B`、`t => t.DeletedAt!.Value` 都在注册期拒 —— 这一档回答的是"置位哪一列、时间记在哪一列"这两件事。
- **失败点分两档**：具体类键在 `SoftDelete(...)` 那一句就把两列各量一遍（缺列、自增列「不接受写」、并发列「只比较不书写」，与 `UPDATE` 链同一把尺子）；**接口与抽象基类键**（`SoftDelete<ISoftDelete>(e => e.Deleted)` 一次注册覆盖所有实现类）注册期量不出来 —— 它没有列契约，只有第一次真贴上某张表时才响，而消息会同时点名"谁的注册"与"哪张表缺这一列"。
- **同一把键注册两次当场拒**（键就是 `SoftDelete<T>` 的那个 `T`；两张册子对同一张表各说一种写法时，谁赢没有任何依据）。**接口键与具体类键的重叠不在这一格**：那种情况按注册序取第一条匹配得上的（与过滤器的"注册序折叠"同一条纪律）。册子挂在实例上，要换写法就换一只 orm 实例或把两处合成一处。
- **时钟在构建语句时读，一次构建一颗**：注册期一次都不读（冻在它上面等于后面所有删除记同一个时刻）；链式一条链一颗（所以 `ToSql()` 预览两次不会多读一次，预览的那句就是要发出去的那句），`orm.Delete(实体)` 交回的就是链式那一条，所以同一次调用里预览加执行仍只读一颗、而连着删两只实体是两条链各读一颗（单行的两次删除本来就不是同一时刻），一次 `DeleteRange` 整批一颗（切成几句都是同一时刻），一次 `SaveChanges` 一颗。缺省 `DateTime.Now` —— 本库不替你决定本地还是 UTC。

**这一档没有的三件事，别读成有**：① 软删只写那两列，第三格「谁删的」**现在有槽位了**（v0.39.0-beta.28 起：`orm.Aop.SoftDeleteValues` 订阅者往上加，见下）；② 分表实体注册软删按构造打在你 `SplitTable(…)` 点名的那一片上（链式那条闸门见 §3.6），但这一格今天没有用例；③ **开了软删也不会让过滤器管到 `SaveChanges` 与批量** —— 那是上一段那句裁决的事。见证：离线 `tests/QuickSql.Tests.Dialects/SoftDeleteTests.cs` 19 颗 + 一份基线 `golden/sqlserver/soft-delete-update.sql`，真库 `tests/QuickSql.Tests.Integration/SoftDeletePathTests.cs` 7 颗（SQL Server 与 MySQL 两家各一场），可跑示例 `samples/QuickSql.Sample/Program.cs` §15 在真 SQLite 上把链式与批量两处出口逐条打印（含"同一条谓词连跑两次各 1 行"、"批量两行同一颗时刻"、"只注册行为时读侧 4 行都看得见、补上过滤器才 1 行而库里仍是 4 行"）。

**「谁删的」那一格（v0.39.0-beta.28 新增）** —— 软删自己只写两格（置位 + 时间），而「谁删的」是**调用方的领域事实**，库里不知道当前用户是谁、也不该知道。订阅者往上加即可，库负责把它参数化、写出去，并按与那两格同一把尺子验（自增列与并发列不收）：

```csharp
// 订阅者拿到的是表名与那一只实体；列得自己从契约上取
var titleColumn = orm.Schemas.Contract(typeof(Ticket)).ByMember[nameof(Ticket.Title)];

orm.Aop.SoftDeleteValues = e =>
    e.ExtraValues.Add(new KeyValuePair<ColumnContract, object?>(titleColumn, "谁删的"));

orm.Behaviors.SoftDelete<Ticket>(t => t.Deleted, t => t.DeletedAt);
```

**逐行触发**（有行才触发）：链式 `orm.Delete<T>().Where(...)` 那一档没有行可给（它删的是一批行，不是一只），那一档不触发这一颗 —— 那一档要自己写那一格。见证：真库 `tests/QuickSql.Tests.Integration/SoftDeletePathTests.cs` 的 `The_extra_column_a_subscriber_adds_lands_in_the_soft_delete_statement`。

**这三只对象都挂在实例上，所以 DI 里它们只有一个落点：`builder.AfterBuild`**（v0.38 第十八笔）。`AddQuickSqlOrm` 交回的是 Scoped 的 `QuickSqlOrm`，而"当前登录者属于哪一家企业""这一条流要不要打 SQL"恰恰是按请求的事实：注册期那只 builder 手上没有 `IServiceProvider`，实例建好之后宿主又拿不到"还没被用过"的那个时机（谁先解析谁先用，晚一步装的过滤器会漏掉前面那几句）。所以库给的是装配清单，不是让宿主自己盖一层 Scoped 注册去猜内核怎么装配：

```csharp
builder.Services.AddQuickSqlOrm(DbKind.SqlServer, connectionString, b =>
{
    b.AfterBuild.Add((sp, orm) =>                       // 按登记顺序跑，每个作用域一次
    {
        var tenant = sp.GetRequiredService<ICurrentTenant>();   // 拿的是本请求的服务

        orm.Filters.Add<Ticket>(t => t.TenantId == tenant.Id, mandatory: true);
        orm.Behaviors.SoftDelete<Ticket>(t => t.Deleted, t => t.DeletedAt);
        orm.Aop.Executed = e => logger.LogInformation("{Sql}", e.Sql);   // 订阅就是赋值，Aop 每请求一份
    });
});
```

四句口径，每句都有用例（`tests/QuickSql.Tests.Dialects/DiAssemblyTests.cs` 7 颗）：① **每个作用域解析跑一次**，同一个作用域里再取一次不重跑；② **跑的就是交回给调用方的那一只实例**，不是另建一只再装配；③ **清单是 `AddQuickSqlOrm` 那一刻的快照**，返回之后再往同一个 `List` 里添的那些不生效；④ **不查重** —— 同一颗钩子登记两遍就跑两遍，跑到第二遍若撞上 `Behaviors` 那句「同一把键注册两次当场拒」，抛在这次解析上（异常原样透出，`GetRequiredService` 不包一层）。今天它是同步 `Action`，没有 async 落点：装配里要异步初始化（先从远端取租户配置那一类）得宿主自己决定怎么同步等，本库不在解析路径上代开 `await`。**分界在哪**：本来就是启动期常量的那几格（`Replicas` ∶ `Clusters` ∶ `Model` ∶ `PagingOrdering` ∶ `SchemaSync`）照旧在 builder 上直给，走 `AfterBuild` 的只有"要问当前请求才知道"的那三格。

### 2.6 事务：四形态

`src/QuickSql.Core/Orchestration/QuickSqlOrm.cs`：

```csharp
using var tran = orm.BeginTransaction();            // (1) 显式，:386，默认 ReadCommitted
await orm.Insert(row).ExecuteAffrowsAsync();
tran.Commit();                                      // IQuickSqlTransaction { Completed, Commit(), Rollback() }

using var ambient = orm.BeginAmbientTransaction();  // (3) 环境事务，:399，同一 AsyncLocal 槽位

using var pinned = orm.BeginSessionTransaction();   // (4) 钉在这只 orm 上，:412（另有一颗点名集群的 :416）
await orm.Insert(row).ExecuteAffrowsAsync();
pinned.Commit();

var result = await orm.ExecuteInTransactionAsync(async ct => { ... }); // (2) 返回 TransactionResult；:422
if (!result.Success) Log(result.Exception);         // TransactionResult { Success, ErrorMessage, Exception, RollbackFailure }
                                                    // 业务原文永远在 Exception；回滚那一步自己的失败（锁冲突/连接断）住 RollbackFailure 旁路，两条都在时两个都看得到
```

泛型重载 `ExecuteInTransactionAsync<T>(...)` 返回 `TransactionResult<T>`（多一颗 `Value`）。`DbContext` 会加入环境事务（会话里的语句走的正是那条被事务独占的连接，`AdoExecutor.cs` 的 `RentAsync` 那一支）。

**事务是可选的，判源是能力位那一颗 `Transactions`（0.39.0-beta.23 起）**：provider 声明了它才走上面四个形态；没声明的库上四个公开口全部当场抛 `NotSupportedException`（照 `CanCreateDatabase=false` 那颗先例，消息指名，不静默开一条假事务），而内核写路径（`SaveChanges` ∶ `InsertOrUpdate` ∶ 批量链）自动退成**逐句 autocommit 直发**。这一退有一个要用实话记住的代价：**一次 `SaveChanges` / 一次 `InsertOrUpdate` / 一次批量里的多条语句不原子——中途失败时已经发出去的语句不回滚**，留在库里的是半批；单条语句自身的事务性按各家库的文档记账（例如 ClickHouse 的单块 INSERT 原子这一格【文档·ClickHouse：https://clickhouse.com/docs/concepts/features/operations/insert/transactions ——其多语句事务是实验档且默认关，照位注释的诚实条款不声明、不假装原子】）。要"一次整批落库"，在无事务的库上请用单条语句表达或自己分批排程——那是库给不给的事，不是 ORM 能补的。0.39.0-beta.24 起，位表之外的"没位逐句 autocommit 直发"档有了第一家在册的住户：ClickHouse（§2.5 那句点名那一家的落层详情在设计文档 §14 行 2335 与 §13.5 第十七本台账）。

**形态 (1)(2)(3) 的归属是「开它的那一刻所在的执行流」**，而 `AsyncLocal` 那一笔只由**同步被调方**回到调用方的帧——异步被调方哪怕全程只 `await` 已完成的 Task，写进去的值父帧与兄弟帧都看不见（`IQuickSqlTransaction` 抬头 v0.12 那条实测；`CreateSharedContext` 那一格同一条纪律的另一面）。于是有一种写法必然踩空：**装配层在自己的 async 帧里开事务，语句却由调用方那一帧发**。ABP 的工作单元正是这一形——`IDbContextProvider` 解析上下文时 `Begin`，回到仓储那一帧槽位已经空了，后面的语句各拿各的池连接自动提交。症状不是报错，是「一场工作单元一次提交」静默退化：中途抛异常时前半段的写已经落库。

**形态 (4) 补的就是这一格：归属从「流」扩成「流 *或* 实例」**。`BeginSessionTransaction()` 在 `AdoExecutor` 上挂一场事务（`AdoExecutor.cs:67` 那颗 `SessionTransaction`，`QuickSqlTransaction.For(executor)` 在帧里找不到自己的环境事务时回退读它，`QuickSqlTransaction.cs:97`），所以从这只 orm 发出的语句无论落在哪一帧都进同一场。三条边界：

- **槽位挂在实例上而不是类型上**：两只 orm 共用一条流时各钉各的，跨 executor 想塞进对方的事务仍然当场拒（与形态 (1) 那句「两个 orm 实例不能互相塞」同一条判据）；
- **一只实例只钉一场**：已经钉着再钉当场抛（"同一只实例上钉两场，一次提交就不成立了"）；提交/回滚/`Dispose` 一律摘钩，摘完之后下一条语句归它自己，也还能再钉一场；
- **同一只实例上不要并发使**：一条连接一个 `DbTransaction`，`Task.WhenAll` 扇出的分支要各自开流或各自建实例（与 `CreateSharedContext` 抬头那条一样，不设闸门）。

活库见证在 `tests/QuickSql.Tests.Integration/SessionTransactionPathTests.cs`：子帧钉的场，父帧的写进得去（回滚后第二条连接一行都读不到）；同一形状换成只认执行流的形态 (3)，那句写就泄漏成一次自提——这一格要 MVCC，SQLite 上写事务占住整库，三颗按引擎的界跳过。

**事务钉在一座集群上**（v0.38 第十一笔，§3.7）：`BeginTransaction()` 开在缺省集群 `main`，另有第二颗重载 `BeginTransaction(string cluster, IsolationLevel = ReadCommitted, TransactionPropagation = Requires)`（:324）点名开在哪一座。规则是**一场事务只有一条连接**，所以：

- 事务内的一切语句都落在那一座集群的主库连接上（换一条连接去读说的就不是这个事务里的数据，所以事务内的读连从库都不换，`RouteReason.InsideTransaction`）；
- 事务里出现另一座集群的语句 → 当场抛（离线与活库各有见证，见 §3.7 的拒绝名单）；
- **同一座集群上嵌套是保存点，跨集群的嵌套不是**（两阶段提交与 XA 规格没裁，所以那一格不给形状）；`BeginTransaction("report")` 在 `main` 的事务还开着时抛，消息写的是"请按集群分别开事务"。

`DbContext` 是 `IDisposable` 也是 `IAsyncDisposable`（`DbContext.cs:25`——早先那句"本身不是 IDisposable"作废）。**这一格与 §3.7 的写后读直接相关，所以不是补充说明**：会话构造时在调用方的同步帧里挂上"本条流上哪几座集群写过"那枚 `AsyncLocal` 戳子，释放必须把它注销掉，而 `async` 被调方换 `AsyncLocal` 的值写不回调用方的帧（哪怕排在第一个 `await` 之前）。所以 `DisposeAsync` 拆成两段——同步注销两颗槽位，异步只留给连接回收。落错那一格的形状不是崩，是"会话早已 dispose、这条流却永远留着写过"，读写分离从此在这条流上静默失效；示例第一跑正是那个形状，来历写在 `DbContext.cs:238` 附近那段注释里。

## 3. 怎么使用

下面这张速查把"要做的事"对到"用哪条链"，每行都给了能直接抄的最小写法与详解去处。装配根（`new QuickSqlOrm(options)` 或 `QuickSqlOrm.Create(DbKind, 连接串)`）与 NuGet 装包见 §1，`orm` 是单例、`DbContext` 才按请求 Scoped（§2.1）。**先记一条总口径**：接口形状可信，执行结果除五家有库的 provider 外基本未证（§0、§4.1）。

- **查询（读侧）** —— `orm.Select<T>()` 起手，`Where` / `OrderBy` / `Skip`·`Take` / `Select` 投影 / `Include`·`IncludeMany` 导航 / `GroupBy`·`Having` 分组，出口 `ToListAsync`·`ToArrayAsync`·`FirstOrDefaultAsync`·`CountAsync`·`ToPagedAsync`。库**不是 `IQueryable`**，别塞进 LINQ 生态。`orm.Ado.QueryAsync<T>(文本, new {…})` 是写不出的句子走原生 SQL 的逃生舱。→ 详见 §3.1
  ```csharp
  var page = await orm.Select<Order>()
      .Where(o => o.State == OrderState.Paid)
      .OrderByDescending(o => o.CreatedAt)
      .ToPagedAsync(page: 1, size: 20);          // PagedResult<Order> { Items, Total, HasNext, … }
  ```
- **新增（单行 / 批量）** —— `orm.Insert(实体)` 单行、`orm.InsertRange(集合)` 多行（一条多值 `INSERT … VALUES`，按参数预算自动分块）。出口：`ExecuteAffrowsAsync()` 行数；`ExecuteIdentityAsync()` 单行自增键；`ExecuteReturnKeysAsync()` 批量回填自增键并交回实体；`ExecuteBulkAsync()` 走装载通道那一档。**自增键回填分三家**（§3.2 那张表：SQL Server `OUTPUT` / PG 句尾 `RETURNING` / MySQL·虚谷·GBase 8s 语句后回读；**Oracle 血统要键即抛**）。
  ```csharp
  var added = await orm.InsertRange(orders).ExecuteReturnKeysAsync();   // 回填 Id 后交回同一批实体
  ```
- **改（按条件 / 按实体那一行 / 整行 / 一句 upsert）** —— 四条链各有分工：`orm.Update<T>().Set(列, 值).Where(条件)` 点名改几列（带 lambda，入口有反射税）；`orm.Update(实体)` 按主键改**这只实体带来值的那些列**（值为 null 的属性不进 `SET`，读作"这一列这次没说"；入口不建 lambda，只有全局过滤器在场时出口仍要翻那几句谓词）；`orm.UpdateRange(实体集)` 按主键整行改（除主键·自增·并发列全部写回，**那一格是 null 就真写 NULL**，零反射税，可用 `.WithBulk(Merged∶SingleRow∶Upsert)` 选档）；`orm.InsertOrUpdate<T>().SetSource(实体∶集合)` 主键命中就改、不命中就插。
  ```csharp
  await orm.Update<Order>().Set(o => o.Note, "只改备注").Where(o => o.Id == 7).ExecuteAffrowsAsync();
  await orm.Update(order).ExecuteAffrowsAsync();                     // 按主键改这只实体填了值的那几列
  await orm.UpdateRange(orders).WithBatchTransaction().ExecuteBulkAsync();   // 按主键整行改，一批一句
  ```
  `orm.Update(实体)` 与 `orm.UpdateRange(实体集)` 的分工是**两种意图而不是两种写法**：前者不把 null 写进库，
  所以"清空某一格"只有链式那一条做得到（`.Set(o => o.Note, null)`）；值类型列永远带来值（`int Amount = 0` 不是"没填"），
  每一趟都进 `SET`。交回的仍是 `IUpdate<T>`，所以后面还能 `.Where(...)` 再钉一句（叠在主键之后、全局过滤器之前）；
  而实体带着值的那一列再 `.Set` 一次会被"设置了两次"那道守卫拦下——本家答案不是后写的覆盖先写的，是说清哪个值算。
- **删（按条件 / 按实体那一行 / 按主键整批 / 软删）** —— `orm.Delete<T>().Where(条件)` 按条件删；`orm.Delete(实体)` 按主键删那一行，**复合主键在这一档支持**（逐列 `=` 再 `AND`，与跟踪器摘行同一个形状——`orm.DeleteRange(实体集)` 拒复合键靠的是"一句 `WHERE 键 IN (…)` 装不下元组"，而那一句在单行上根本不成立），分表实体按这只实体自己的分片键换算那一张片；`orm.DeleteRange(实体集)` 按主键删整批。**软删**不是删链的参数而是在 `orm.Behaviors.SoftDelete<T>(置位列, 时间列)` 注册后，上面四条删除出口一起改写成"置位 + 记时间"的 `UPDATE`（与 `orm.Filters` 是两只对象、各管一半，见 §2.5）。
  ```csharp
  await orm.Delete<Order>().Where(o => o.CreatedAt < cutoff).ExecuteAffrowsAsync();
  ```
- **工作单元（`DbContext` + `SaveChanges`）** —— 要"改一批实体、由库判定脏列、一个批次提交"用会话：派生一个 `DbContext`、`Set<T>()` 跟踪读、`Add`·`Remove`·`Attach`、末尾 `SaveChangesAsync()` 交回 `SaveChangesReport`。只写脏列、无变更不发语句不开事务、乐观锁冲突抛 `DbUpdateConcurrencyException`。分表实体可读进会话、不可由会话写出去（§3.3、§3.6）。
  ```csharp
  var db = new AppDb(orm);
  var o = await db.Orders.Where(x => x.Id == 7).FirstOrDefaultAsync();
  o.Note = "改一下";
  await db.SaveChangesAsync();
  ```
- **事务** —— `orm.BeginTransaction()` 显式、`orm.ExecuteInTransactionAsync(async ct => {…})` 包一段、`orm.BeginAmbientTransaction()` 环境事务（`DbContext` 会并入）、`orm.BeginSessionTransaction()` 把事务钉在这只 orm 上（装配层隔着一层 async 开事务、语句却由调用方那一帧发时用的就是这颗；→ §2.6）。事务钉在一座集群上，跨集群嵌套当场拒。→ 详见 §2.6、§3.7。**收口两条（v0.39）** ∶ 形态 (2) 的回滚失败不再盖掉业务异常原文（`RollbackFailure` 承旁路）；同一只 orm 上嵌套的 `DbContext` 共享一条流的会话连接工厂——先到当主人、后到不动字，里层先 `Dispose` 拆不了外层的槽与连接
- **分表 / 读写分离 / 换库** —— 分表 `[SplitTable]` 注解 + 链式 `.SplitTable(片)`（§3.6）；读写分离在 `QuickSqlOrmOptions.Replicas`∶`Clusters` 声明、运行期按主从路由（§3.7）；换库只动 csproj 那颗 provider ∶ 一处 `DbKind` ∶ 连接串，实体与链一字不动（§1.9，踩坑见 §4.4）。

### 3.0 最小可离线跑通的例子

生成 SQL **不需要驱动，也不需要库**。测试层的做法是注一根"碰一下就抛"的探针工厂（`tests/QuickSql.Tests.Common/SqlServer.cs` 的 `NoDriverFactory`），应用侧最省事的就是照抄这个姿势：

```csharp
using QuickSql;
using QuickSql.Providers.SqlServer;
using Microsoft.Data.SqlClient;                 // 只有要真连库时才需要这一行

var orm = new QuickSqlOrm(new QuickSqlOrmOptions
{
    Provider = new SqlServerProvider(SqlClientFactory.Instance),
    ConnectionString = "Server=offline;Database=never-connected;Integrated Security=True",
});

var sql = orm.Select<Order>().Where(o => o.Id == 7).ToSql();
Console.WriteLine(sql);
```

只产文本的话工厂可以换成任意不吃连接的 `DbProviderFactory`，或者按 §1.6 由内建表解析（`SqlServer.Driver()` 那一句就是走完整条链）。

### 3.1 读侧

链式接口是 `IQuery<T>`（`src/QuickSql.Abstractions/Query/IQuery.cs`）。它**刻意不是 `IQueryable`**，不能塞进 LINQ 生态，也没有非泛型口。可用的成员：

```csharp
var q = orm.Select<Order>()
    .Where(o => o.State == OrderState.Paid && o.Amount > 100m)
    .OrderByDescending(o => o.CreatedAt).ThenBy(o => o.Id)
    .Skip(20).Take(10)
    .Select(o => new OrderSummary(o.Id, o.CustomerName, o.Amount));
```

- 谓词/投影/排序：`Where`（可多条，左到右折成一个 AND 链）、`OrderBy`/`OrderByDescending`/`ThenBy`/`ThenByDescending`（排序另有<em>按名字</em>那一形：`OrderBy(string, bool)` 与 `ThenBy(string, bool)`，吃前端那一整串，见下面那一格）、`Select<TResult>`（实体、匿名、构造器 DTO 三种）；谓词里 `x.Id.Equals(id)` 与 `object.Equals(x, y)` 与 `==` 同形落 `=`（ABP 那一类仓储的常态写法：`Id` 是 `long`/`Guid` 时 C# 绑的是 `Int64.Equals` 或 `Object.Equals`，v0.39 起每一族都认，产出与参数值都和写成 `==` 时逐字相同）。**但只认两侧同型的那一种**：`int` 列 `.Equals(某个 long)` 在 CLR 上恒为 false，库照 `=` 发射就是把「一行都不该中」翻成「值相同就中」，所以它拒并说明该改哪；要按值比就写 `==` 让编译器做提升。带 `StringComparison` 或自定义比较器的那一颗同样拒（大小写口径各家不同，不替你换规则）
- 分页：`Skip`/`Take`，或一次到位 `ToPagedAsync(int page, int size, PagedOptions? = null, CancellationToken)` → `PagedResult<T> { Items, Page, Size, Total, HasNext, TotalPages }`；计数控件是 `PagedOptions(CountMethod CountMethod = Auto, bool StripOrderingForCount = true, string? CacheKey = null)`，`CountMethod` 五值 `Auto`/`CountStar`/`CountOverWindow`/`SkipTotal`/`SeparateTransaction` —— 后两档里 `CountOverWindow` 与 `SeparateTransaction` 今天**直接抛**（§4.3 第 4 条那格），可点的只有前三颗
- 联表：arity 递增的 `LeftJoin<T2>(on)` / `InnerJoin<T2>(on)`，形参按 FROM 位置解析，产出 `IQuery<T1, T2>`（再到 `IQuery<T1, T2, T3>`），其后 `Select((a, b) => ...)`
- 导航：`Include<TProp>` 与它那一条链上的 `ThenInclude<TProp>`（每层一张 `LEFT JOIN`）、`IncludeMany<TDetail>`（v0.39 第三笔起可以再往<em>下一层</em>挂：`ThenIncludeMany<TProp>`；第四笔起同一层<em>旁边</em>还能挂一根引用：`ThenInclude<TProp>` —— 两颗都见下面"集合导航可以一层一层挂上去"那一格）、`AsSplitQuery(int navigateBatchSize = 20)`；**另加 v0.39 起"不点名也带"的那一档** —— 在谓词 / 投影 / 排序键里读到一条**引用**导航的**成员**，库按契约自动补 `LEFT JOIN`，不用 `Include`、也不用自己写 `LeftJoin`（下面那一格）
- 其它：`As(string alias)`、`Distinct()`、`AsNoTracking()`、`IgnoreQueryFilters()`、`IgnoreQueryFilter<TFilter>()`（按注册键点名，可叠；见上面「全局过滤器」那一格）、`WithCache(string, TimeSpan)`、`WithSql(string sql, object? args = null)`（把 FROM 那一段换成你自己写的 SELECT，外面一层仍归库；见下面"表源那一格"）
- 出口：`ToListAsync`/`ToArrayAsync`/`FirstOrDefaultAsync`/`CountAsync`/`ToAsyncEnumerable(int batchSize)`，以及同步的 `ToList`/`ToArray`/`FirstOrDefault`/`Count`；类型本身还是 `IAsyncEnumerable<T>`。**数组那一颗与列表那一颗是同一颗咽喉**（同一条模型、同一句 SQL、同样只发一条语句、同一处跟踪与缓存落点），换的只是交回来的 CLR 形状，所以它**不省一趟、反而多一次整段拷贝**——物化那一路落的本来就是 `List<T>`，缓存里存的也是那一只；要"少一次拷贝"用 `ToListAsync`，要"交出去之后改不动"（长度定死、而且拿到的不是缓存里那一只，调用方 `Add`/`Clear` 改不到下一次命中）用 `ToArrayAsync`。同步那一颗 `ToArray()` 与 `ToList()` 同一档 `sync-over-async`，**多的那趟拷贝与异步那颗逐字相同**（它不是"少付一次拷贝"的路）。联表 arity 上没有带投影的对应物，要数组就 `Select(projector)` 回到单表链再调。计数一族（`Count`/`LongCount` 与它们的异步对照）遇到分组、`Distinct` 或带 `Skip/Take` 的链时，会把整句包成派生表内层再数（上面那一段与下面"表源那一格"），不会照着原模型换一个 `COUNT(*)`；`Any`/`AnyAsync` 已经不在这张名单里了。这一族的后四颗（`AnyAsync`/`LongCountAsync`/`Any`/`LongCount`）到 v0.38 第十九笔才真的长在 `IQuery<T>` 上。**0.39.0-beta.29 起 `Any`/`AnyAsync` 从 COUNT 族改道**：发的是「`SELECT 1 … 取第一行`」的存在性探针，一行都不数——COUNT 在大表上是全量计数，存在性拿到第一行就收口，最坏（空集）才与 COUNT 同阶；`Offset` 原样保留（`Skip(n).Any()` 问的还是"跳过 n 行之后还有没有"），序剥掉而 T-SQL 那族分页要的 `ORDER BY` 由方言占位序补，分组与 `HAVING` 天然成立（`SELECT 1 … GROUP BY k LIMIT 1` 交回一行 ⟺ 至少有一组），Include 机器整体旁路（一条 JOIN 不改变存在性）。文本离线钉在 `tests/QuickSql.Tests.Dialects/AnyProbeTests.cs`（借 `Aop.Executing` 播报位取句：`SELECT 1 ` 起句、句内无 `COUNT`），真库行为钉在 `ReadPathTests.Any_and_LongCount_answer_on_the_same_rows_the_count_family_reads`（有行/空集/偏移/带序四档）；`LongCount` 仍在 COUNT 那一族里

**按名字的排序：一颗口吃下前端那一整串（v0.39，规格 §5.7 那条落地记录）。**

```csharp
// request.Sorting 是 HTTP 查询串上原样过来的一颗，例如 "Id,Amount desc"
var page = await orm.Select<Order>()
    .Where(o => o.State == OrderState.Paid)
    .OrderBy(request.Sorting)
    .ToPagedAsync(1, 20);
```

- 形状：逗号分颗，每一颗是 `名字` 或 `名字 asc` / `名字 desc`（`ascending` / `descending` 同义，大小写不敏感）；空颗跳过（`"Id,,Name,"` 那种 `join(',')` 的尾巴），多余的空格、制表与换行只当分隔。同一条链上 `OrderBy` 之后再 `OrderBy` 是整串替换（LINQ 的口径），`ThenBy(string, bool)` 同样吃整串、只追加，而列序 = 书写序
- 那颗 `descending` 形参给的是<em>串里没点方向的那些颗</em>：`OrderBy("Id", true)` 与 `OrderBy("Id desc")` 同一句 SQL，而 `OrderBy("Id asc", true)` 发的是升序 —— 串里显式写的方向不会被形参翻回来。认不出方向词的整段按列名去认，所以 `OrderBy("desc")` 认的可以是那颗名叫 `desc` 的列
- 它**不是 `ORDER BY` 片段的入口**：每一颗名字拿去契约上认列（先属性名、再列名、最后大小写不敏感的唯一命中），发出去的是那一颗列加方言引号；认不出当场抛，<b>没有"原样拼进去"的退路</b>，所以这一格不需要黑名单。半串认得出、半串认不出也照样响，不会退成"只按认得的那颗排"
- 射程与 lambda 那一族的差别：只落在 **arity 1** 的链上（联表链上"一个名字属于哪张表"要先有别名规则），`Select` 投影之后照旧拒。要排另一张表那一侧的列改用 lambda 形态（读引用导航会自动联表，下面那一格）；`IncludeMany` 那一族的子行序用的是 `OrderByMany(表达式, bool)`，它没有串可拆

**引用导航在谓词 / 投影 / 排序键里自动补 `LEFT JOIN`（v0.39，规格 §5.2 那条落地记录）。**

```csharp
// 跨表显示列：投影里读到 o.Customer.City，库自己把 Customer 那张表联进来
var rows = await orm.Select<Order>()
    .Where(o => o.Customer!.City == "上海")              // 谓词里读 → 同一张表位
    .OrderBy(o => o.Customer!.Name)                      // 排序键里读 → 还是那一张
    .Skip(20).Take(10)                                   // 页码数的是主表行
    .Select(o => new { o.Id, o.Amount, City = o.Customer!.City })
    .ToListAsync();

// 发出去的（SQL Server，实测产物原样，只折了行）：
// SELECT [t0].[Id], [t0].[Amount], [t1].[City] FROM [QuickSqlOrder] AS [t0]
//   LEFT JOIN [QuickSqlCustomer] AS [t1] ON [t0].[CustomerId] = [t1].[Id]
// WHERE [t1].[City] = @p0 ORDER BY [t1].[Name] ASC OFFSET @p1 ROWS FETCH NEXT @p2 ROWS ONLY
```

- **ON 由契约推出来，不是"属性名 + Id"那句猜测**：外键列与目标主键列都取自那条关系的登记（`[ForeignKey]` 与约定命中的都算），别名按表位自动编号。**同一条边读几次都只联一张** —— 一个 lambda 里读两次算一次，`Where` 里联上的 `[t1]` 后面 `Select`／`OrderBy` 接着读也算一次（上面那一句三处读的都是同一个 `[t1]`），**`Include` 那一颗表位也算**（见下面那一格）；**两条不同的边各占一个表位**，`FROM` 后面的先后 = 第一次读到的先后
- **是 `LEFT` 而不是 `INNER`**：主表的行不许因为对端没有就消失。代价是"对端为空"成了正常读数，投影出来那一列会是 `NULL`，所以 DTO 的槽位要按可空声明（外键本身不可空时，写 `o.Customer!.Name` 那个 `!` 只是为了过编译器的可空检查，运行期照样发 `LEFT JOIN`）
- **分页在联表之后**：`OFFSET`/`FETCH` 数的是联完的主表行，而 `LEFT` 保证一条主行不会因对端缺失而少掉 —— 换成 `INNER` 的话同一页在两种数据上会给出不同的行数，而这件事在投影结果里读不出来
- **仍然当场抛的四格**（宁可报错，不发一条看着合理的错句子）：① **集合导航**（`o.Items.Count`、`o.Items.Any(…)`）—— 一条 `LEFT JOIN` 明细会把每行主表复制成明细行数，页码、行数与聚合一起改语义，所以那一档今天没有自动联表，要的是相关子查询或 `IncludeMany`；② **整只导航当值读**（`(object)o.Customer == null`）—— 消息会说"再往下写一格"（`o.Customer!.Id == null`）；③ **显式 `LeftJoin`/`InnerJoin` 的 ON 里**读导航 —— ON 的翻译发生在右表进 FROM 之前；④ **写侧**（`Update`/`Delete`）不联表
- **与 `Include` 的分工 ∶ 同一根边只有一张 `LEFT JOIN`**：要把**整只实体**取回来（`o.Customer` 那一档、以及集合导航）仍然只有 `Include`/`IncludeMany`；这一档给的是"跨表读几列"，不往结果里塞实体。而两根同时压在**同一根引用边**上时不发两张 —— `Include` 先写、读导航后写，还是读导航先写、`Include` 后写，都并成一张（扩展列照旧发）。**收养落成的是模型里那条真 `JOIN`，不是照抄 `Include` 那一条**：`Include` 的那一条只在"实体投影且没拆查"时发，照抄过去，投影之后的 `Select`、`AsSplitQuery()`、`Count` 自己包的那层内行三形都会把它从 `FROM` 里摘掉，而欠着它的谓词还在（那是一句引用了不存在表位的话）。集合边不吃这一格：`Include` 与 `IncludeMany` 同句时主查询里仍只有那一张 `Customers`，明细走另发的 `SELECT`
- **相关子查询里读导航**：联表进的是**内层**那一句的 `FROM`，外层一个字不多（内层自成一个模型，账记在自己头上）
- **`Explain()` 里读得出这条边**：它进的是模型的 `Joins`，不是编译期才补的一层，所以"这一句联了谁"在模型文本里就能看（`JOIN Left QuickSqlCustomer AS t1 ON (t0.CustomerId = t1.Id)`）
- **成本**：扁平实体（契约里一条关系都没有）连探测现场都不建，一次数组拷贝也不付；有关系的链是每查询 +8 B、每谓词 +16 B（门禁读数与归属在设计文档 §12.8 那一格 v0.39 那两段）
- **见证**：离线册 `tests/QuickSql.Tests.Dialects/NavigationAutoJoinTests.cs` **23** 颗（自动联的形状、跨算子复用、`Explain()`、分页在联表之后、子查询内层、`Guid` 外键那条边用自己的列、上面四格拒绝各一颗，另有六颗是共用那一张那一族：`Include` 前后两序各一颗、三形派生句子一颗、两层链各一张一颗、没人读时照旧发一颗、集合边不并一颗），基线 `golden/sqlserver/navigation-auto-join-projection.sql`，真库那一颗是集成册 `ReadPathTests.A_navigation_read_lands_a_left_join_without_being…`
- **可重跑**：`samples/QuickSql.Sample` 的 §16（谓词/序+投影读导航 → `Include` 与读导航共用一张（句子与 `Explain()` 各数一次）→ 集合读法被拒那一句 → `Include` 两层两张 → `AsSplitQuery` 那一形不发扩展列而谓词那张照发 → `IncludeMany`＋旁边引用边＋下一层集合一句链端出四条 `SELECT`），跑在 SQLite 上，读的是句子与行数不是厂商库

**CLR 自己插的转换节点：哪四形放行、哪两形仍发 `CAST`（规格 §12.9 那一格的延伸）。**

```csharp
OrderState? state = OrderState.Paid;                              // 枚举列 ↔ 可空枚举量
await orm.Select<Order>().Where(o => o.State == state).ToListAsync();   // `State` = @p0，两侧都不包 CAST

short? small = 7;                                                 // int 列 ↔ short? 量（加宽）
await orm.Select<RepresentationProbe>().Where(p => p.Id == small).ToListAsync();   // `Id` = @p0

await orm.Select<RepresentationProbe>().Where(p => (short)p.Id == 3).ToListAsync(); // 收窄：CAST(`Id` AS SMALLINT)，故意留着
```

- **放行判据是 `ExpressionTranslator.RepresentationOnly`（`src/QuickSql.Core/Translation/ExpressionTranslator.cs:452`）**：一律先看穿一层 `Nullable`，再认三形 —— ①任一侧是枚举，②两侧同一个 CLR 型（`T ↔ T?`），③整型**安全加宽**（位宽 8∶16∶32∶64 严格变大，且 `ulong` 单独排除：CLR 里没有比它更宽而又不改符号语义的整型）。加上调用点那颗装箱豁免（`:425` 的 `node.Type == typeof(object)`，最常从 `string.Concat(object, object)` 里冒出来），一共四形不进 SQL
- **只放宽、不收紧**：这一判据不能顺手把旧豁免收回去。曾写过"枚举只许转到自己的 `Enum.GetUnderlyingType`"那一版，短血统枚举（`enum ShortState : short`）反而更坏 —— 提升比较转到的是 `Int32?` 而不是 `Int16?`，于是 CAST 套到了**列**那一侧
- **真转换仍发 `CAST`，那是裁定不是漏**：收窄（`Int32 → Int16` 会真丢值）与 `char`（列是 `CHAR(1)` 存文本，拿数值比字符是库级静默的字符串↔数字降级）都不豁免，让它们响，比静默错行好。看得见的代价是列被包进函数 ⇒ 那一侧索引作废
- **为什么这一格以前是硬错而不是难看**：`CAST` 的目标型名取自 `Provider.StoreTypeOf`，那是**DDL 建表型名**（`MySqlTypes.cs:123` 给 `int` → `INT`、`:121` 给 `short` → `SMALLINT`），而 MySQL 的 `CAST` 不认 `INT`，整条句子被库拒【实测·`obj/logs/castprobe-mysql-1.log` 第 5 行 P-05 交 `errno=1064 … near 'INT)'`，同形换 `AS SIGNED` 交回 1 = 第 6 行 P-06】。"型名取自 DDL 那一张表"这件事本身仍是一格欠（`decimal` → `DECIMAL(18,4)` 恰好能用而 `SMALLINT` 不能），**而规格里今天还没有它的名字**（`quicksql-orm-design.md` 里 `StoreTypeOf` 一词命中 0），所以这一格不在上面那条判据的射程内，也别当成已经修过
- **顺带够得着的一格语义**：可空枚举量取 `null` 时那一格折成 `IS NULL`（`:349` 那道折法）—— 豁免之前那颗 `Convert` 正挡在中间，折不到，所以这不是观感问题
- **见证**：离线册 `tests/QuickSql.Tests.Dialects/NullableValueComparisonTests.cs` **6** 颗（裸绑定、短血统与两档加宽、列侧加宽后索引还在、`IS NULL`、反向探针一颗钉"收窄与 `char` 仍发 CAST"、SQL Server 同判据逐字同形只换引号）。探针表 `QuickSqlRepresentationProbe` 只放列不放关系，为的是不动 `Order` 的列清单（黄金册里每张 `SELECT` 列表都挂着它）。活库那一颗在集成册（`ReadPathTests.A_nullable_value_compared_directly_returns_the_same_r…`：非空那一档筛出一行、与点明非空的写法逐行相同、`null` 那一档折成 `IS NULL` 后筛出零行）—— **它在 MySQL 上判得更硬**：老句子（`State = CAST(1 AS INT)`）在那一家被库拒 `1064`（实测见上面那一格那两行），所以那里跑绿直接证的就是"这一句没带 `CAST`"【实测·`obj/logs/nullable-mysql-int-1.log`，`QUICKSQL_TEST_PROVIDER=mysql` ∶ `8.4.8` ∶ `1 / E0 / F0`】；而 SQL Server 两形都发得出、都跑得通，那边判的只是新判据没把语义改坏

**集合导航可以一层一层挂上去，每一层旁边还挂得上引用导航（v0.39 第三笔 ∶ 第四笔，规格 §5.7 那两条落地记录）。**

```csharp
// 模板 → 分组 → 明细：三张表、三条 SELECT，而主查询与"只挂一层"时逐字节相同
var template = await orm.Select<ExamTemplate>()
    .IncludeMany(t => t.Groups!)                  // 第二条：SELECT … FROM ExamGroup  WHERE TemplateId IN (@p0)
    .ThenIncludeMany(g => g.Details!)             // 第三条：SELECT … FROM ExamDetail WHERE GroupId   IN (@p0, @p1)
    .ThenInclude(d => d.Material)                 // 第四条：SELECT … FROM Material   WHERE Id        IN (@p0, @p1, @p2)
    .Where(t => t.Id == id)
    .FirstOrDefaultAsync();
```

- **`Include`/`ThenInclude` 那一条链是引用导航专用的，不接受集合**：要集合就用 `IncludeMany`。两家的产物不同 —— 前者往主查询加 `LEFT JOIN` 与扩展列（每层一张表），后者<em>一个字都不加</em>，另发查询再内存缝合
- **`ThenIncludeMany` 的形参是<em>一只</em>上一层的子实体**：`g => g.Details` 里 `g` 是 `ExamGroup` 而不是 `List<ExamGroup>`（与 `Include(…).ThenInclude(…)` 同形）。下一层挂在谁的契约上由类型系统在编译期定死，认错父端接不上去
- **深度就是查询的条数**：根一条 + 每一层一条，两层链 = 三句；第二句 `IN` 里躺的是上一层刚缝出来的那一批父键，不是根表主键。上一层没行就当场停，所以空集拿不到第二句、更不会有空 `IN`
- **外键怎么找（每一层各判一次，三档逐字节优先）**：子实体上指向父类型的回指关系 → 集合成员上点名的 `[ForeignKey("列名")]` → 约定 `{父类型名}Id`；三档都命不中当场抛，不猜
- **投影之后不能挂**：`IncludeMany` 与 `ThenIncludeMany` 拒的是同一句话（"把 `IncludeMany` 放到 `Select` 之前"）
- **`ThenInclude` 挂的是<em>旁边</em>那一根，不是<em>下面</em>那一层**：形参还是这一层的<em>一只</em>子实体（`d => d.Material` 里 `d` 是一只 `ExamDetail`），挂的是它<em>手里</em>那根引用导航。重复调用是在同一层并列多挂几根（`.ThenInclude(d => d.Material).ThenInclude(d => d.Warehouse)`），而 `ThenIncludeMany` 仍然往下一层集合走 —— 那颗 `ThenInclude` 返回的是原来那一家，类型参数 `TItem` 不变。<b>再往引用导航的第二层（物资的类别）今天没有出口</b>，那一形走 `Include`/`ThenInclude` 那条 JOIN 链或 `AsSplitQuery()`
- **那一根的查询形状**：`SELECT 目标各列 FROM 目标 WHERE 主键 IN (这一批子实体手里的外键值)` —— 方向与集合那一档相反（集合按父键筛子行，引用按子键捞目标），<b>按键去重</b>（四只明细只有三种键值就只绑三颗参数），而配不到行的那一只<em>留在结果里、导航为 `null`</em>，不会被筛掉
- **集合边不能打进 `ThenInclude`**：那一格当场响并指名换 `ThenIncludeMany` —— 编译期看不出来（`List<SlotHook>` 是一只完全合法的返回值），放过去的话执行层按主键缝引用，一根集合边只会剩下最后一只
- **`AsSplitQuery()` 与 `IncludeMany` 现在不要混在同一句里**：两者同时在场时分派按拆分那一支先赢，集合那一家今天会被静默丢掉（规格 §5.7 第四笔那条"没给的三件"里记着）
- **会话跟踪要知道的那一格**：`IncludeMany` 缝上来的子实体<em>自己在会话里建档</em>（v0.39.0-beta.27 起：接链那一刻把「按编译期子型建档」的那把委托交给节点，执行期自己建档，不必调用方先摸一句 `db.Set<ExamGroup>()`）。所以"改完子实体再 `SaveChanges`、由会话落库"这条路对链上的子实体<em>成立</em>；从集合里摘掉一只，那一行也会跟着删（裁决③：摘行就是要把那一行删掉）—— 前提是那根集合边是级联的（默认是，`[NoCascade]` 才退出），而摘掉的那一只自己名下的子串也已捞进会话（裁决①：捞不到就拒）
- **可重跑**：`samples/QuickSql.Sample` 的 §16.7（订单 → 明细 → 明细旁边那根回指订单 → 明细的包裹：一句链端出四条 `SELECT`，而主查询里 `OrderItems` 与 `Parcels` 各出现 0 次；第三层那张表是示例自己的 `Parcels`，它挂在 `OrderItem` 上而打了 `[NoCascade]`，为的是让"往下长一层"演示得了又不回头改 §4 那几段级联读数）

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
- **分组之后的计数走派生表**（§5.12 那一格已接）：`CountAsync`/`LongCountAsync` 与 `ToPagedAsync` 的总数会把整份分组原样包进内层 —— `SELECT COUNT(*) FROM (SELECT … GROUP BY …) AS [t0]`，而内层没有分页时顺手剥掉 `ORDER BY`（T-SQL 与 PG 血统都拒绝"带序而无页"的派生表，count 又不关心序）。`Any`/`LongCount` 这两颗同步口与它们的异步对照到 v0.38 第十九笔才一起长在接口上【0.39.0-beta.29∶ `Any`/`AnyAsync` 从这一档离场——「`SELECT 1 … GROUP BY … LIMIT 1`」交回一行 ⟺ 至少有一组，不包派生表、不再数完（账记上一条"计数一族"的注）】。**这一档只接 COUNT 一族**：外层要引用内层的某一列（`SUM(t0.Total)`）就得先有派生行的列契约，那一步今天没有形状，所以分组之后再 `SumAsync`/`MaxAsync`/`MinAsync`/`AverageAsync` 仍然当场抛，消息点名的就是缺的那一格
- 同一个咽喉管着另外两种形状：`Distinct()` 之后的计数数的是**去重后的行**，带 `Skip/Take` 的链上 `CountAsync` 数的是**那一页**（与 LINQ 的 `Skip(…).Take(…).Count()` 同一语义）。`ToPagedAsync` 的总数不是这一格 —— 它先把偏移与上界摘掉再数，得的是跨页总数
- 今天会**当场抛**（宁可报错，不发一条看起来合理的错句子）：组内去重计数 `g.CountDistinct(…)` 与带条件的计数 `g.Count(o => …)`（`COUNT(DISTINCT x)` 与 `FILTER` 各家不同）；无序分页时 `PagingOrderingPolicy.AutoOrderPrimaryKey` 那颗补序兜底（主键不在分组键里，补出来不合法）；分组链上 `Any`/`First` 这类没有 `GROUP BY` 对应物的方法
- 见证：离线册 `tests/QuickSql.Tests.Dialects/GroupByTests.cs` 19 颗（版式四判 + 三家引号 + 分组与集合导航相关子查询的分工 + 上面每一格报错），派生表那一段 `DerivedTableTests.cs` 16 颗（三形文本 + 各家分页搬进内层 + 别名同时改名 + 非 COUNT 一族的报错），可跑示例 `samples/QuickSql.Sample/Program.cs` §8（单键五聚合、`Having`、复合键进 DTO、排序取前 N 组、`Explain()` 的模型文本）与 §10（分组总数、分组分页、`Distinct` 计数、带页计数在真 SQLite 上）

**原生 SQL 逃生舱（规格 §11）**：链式模型写不出的句子——窗口函数、递归、方言专有语法、分组之后那一列的 `SUM`/`Max`（上一格说的那半步缺）——自己写文本，走 `orm.Ado` 这一族字符串口。

```csharp
// 行按「列名对属性名」装配；参数名 = 本家前缀 + 属性名
var report = await orm.Ado.QueryAsync<CustomerReport>(
    """
    SELECT o.CustomerId AS CustomerId, c.Name AS Name, COUNT(*) AS Orders, SUM(o.Amount) AS Total
      FROM Orders o JOIN Customers c ON c.Id = o.CustomerId
     WHERE o.Amount >= @minAmount
     GROUP BY o.CustomerId, c.Name
    """,
    new { minAmount = 100m });                        // 同族还有 FirstAsync<T> / StreamAsync<T>

var total = await orm.Ado.ExecuteScalarAsync("SELECT COUNT(*) FROM Orders");            // 第一行第一列，DBNull → null
var rows  = await orm.Ado.ExecuteNonQueryAsync("UPDATE Orders SET Note = @note WHERE Id = @id", new { note = "甲", id = 7 });
await orm.Ado.ExecuteBatchAsync(new[] { "CREATE TABLE …", "INSERT …" });                 // 多句共用一条连接；它不开事务
```

- 三条纪律：**文本一颗不动**（不重写占位符、不补引号、不校验语法——那三件事任一做了，用户写的就不再是他写的那一句）；参数只从属性名取名字，所以文本里的占位符必须与属性名同形（连大小写）；行的落点是「可直读第 0 列的标量形状」或「公共无参构造 + 公共可写属性」按名字装配
- 匹配的两条规则与一条报错规则：结果集里对不上属性的列**忽略**（报表 SQL 常带辅助列），属性拿不到列**留默认值**（那句 SQL 少选一列的正当结果），而**一颗属性都没对上时抛**——整行默认值的对象读起来像「查到了三行空壳」而不像「列名对不上」
- 会**当场抛**的还有四格：本家占位符是位置式 `?`（GBase 8s）时带参数对象；`CancellationToken` 出现在参数对象那一位或藏在某个属性里；参数对象没有一颗公共实例属性；形状是 record / 构造器 DTO（参数顺序在这一口没有对应物，列序由那句 SQL 决定）
- **AOP、日志、会话连接复用、事务环境、`CommandTimeout`、摘参数前缀全部走同一条链**：这一族口只把 `(文本, 参数对象)` 折成 `DialectSql` 再交给已有的低层口，没有第二份执行实现。上一格那句「record 请走低层口」就是 `orm.Ado.ToRawSql(sql, args)` 交回 `DialectSql`，配 `QueryAsync<T>(DialectSql, Func<DbDataReader, T>, ExecutionTag, int?, ct)` 自己给行委托（`QuickSql.Execution.RawSql.Tag` 是这一族在 AOP 与日志里的操作名 `raw`）
- 值的那一半复用 `ValueConverter`（库里唯一的取值门）：枚举、`Guid` 的混合字节序、`DateOnly`/`TimeOnly` 折自 `DateTime`、`byte[]` 读成 UTF-8 文本，在原生口与链式口是同一份代码
- 见证：离线册 `tests/QuickSql.Tests.Dialects/RawAdoTests.cs` 18 颗（六家前缀命名 + 上面每一格报错 + 行的四格 + 「缓存按型、对位按场次」），可跑示例 `Program.cs` §9（JOIN+GROUP BY 的报表行、标量、流式、非查询之后用链式口读回、三句批量、低层口装 record）
- trim/AOT：这一族带 `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]`（属性清单与读写委托按运行期类型建），与链式 DTO 投影同一档；AOT 发布时这一族是要显式承认的那一格

**表源那一格：`WithSql`** —— 上一族的文本是"整条查询我自己写"，这一格是**只把 FROM 那一段换掉**，外面那一层仍然归库。

```csharp
var query = orm.Select<Order>()                                     // 外层按 Order 的列契约选列
    .WithSql("SELECT * FROM Orders WHERE CustomerId = @customerId",  // 内层这一段一颗不动
             new { customerId = 7 })
    .Where(o => o.Amount >= 200m)                                   // → 外层 WHERE [t0].[Amount] >= @p1
    .OrderBy(o => o.Id);

var rows = await query.ToListAsync();
var groups = await query.CountAsync();                              // 派生表套原生源：SELECT COUNT(*) FROM ( … ) AS [t0]
```

- 参数的折法与上一族是**同一份实现**（`RawSql`）：文本不动、名字 = 本家前缀 + 属性名、位置式那一家（GBase 8s）当场拒。多一条：参数名**不许长成 `前缀 + p + 数字`** —— 那是 `SqlWriter` 给生成参数留的号段（编号长在项目参数集合的长度上），撞上去是"两处绑定抢同一个名字、值串到隔壁列"
- 原生那几颗先登记、生成的接着编号，所以同一句里出现的是 `@customerId`、`@p1`、`@p2`（`@p0` 不存在）。这条纪律是 `ToSql(out parameters)` 里能直接看到的
- **外层按契约选列**，所以内层必须交回契约里那些列名（`SELECT *` 那形天然成立）。想要内层列名与契约不同的形状，就接 `Select` 投影，或者干脆走上一族整句自己写
- `As("o")` 写在 `WithSql` 之后也安全：覆盖源里存的那个 `TableRef` 会跟着改写，不会出现"FROM 挂 `[o]` 而谓词引用 `[t0]`"
- 只有 `IQuery<T>`（arity 1）有这一颗口，与 `IgnoreQueryFilters` 同一格留口
- 见证：`DerivedTableTests.cs` 后半 7 颗（外层由库写 + 前缀折法 + 两格报错 + 别名改写 + `Explain()` 里的 `SOURCE RAW[…]`），示例 `Program.cs` §10.5

别名由位置生成（`t0`/`t1`/…），C# 里那个 lambda 形参名不影响产出；表名与列序来自契约。占位符名字统一是 `@p0`/`@p1` 这种由 `SqlWriter` 生成的形式，但**前缀按家换**：`@`（SQL Server / PG 系 / MySQL 系）、`:`（Oracle 系 / 虚谷）、`?`（GBase 8s，位置式，不接受带前缀参数名）。取参数用 `ToSql(out IReadOnlyList<AdoParameter> parameters)`，`AdoParameter { Name, Value, DbType, Size, Hint, DriverName }`。

无 `ORDER BY` 的分页默认抛（`PagingOrderingPolicy.Throw`）：T-SQL 的 `OFFSET..FETCH` 硬性要求 `ORDER BY`，而虚谷/MySQL 单独 `OFFSET` 要补一颗上界哨兵。想放开就改 `QuickSqlOrmOptions.PagingOrdering`。

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

`Delete` 发的是物理 `DELETE`，除非那一个实体在 `orm.Behaviors.SoftDelete` 上注册过软删（§2.5）——注册过则这一句在构建时换成"置位 + 记时间"的 `UPDATE`，`Explain()` 的 `Sql` 与日志里看到的都是那句 `UPDATE`，而 `DeletePlan.Ast` 仍是你要求的 `DELETE`。

`IInsert<T>` 的出口：`ExecuteAffrowsAsync`、`ExecuteIdentityAsync` → `Task<long>`、`ExecuteReturnKeysAsync` → `Task<List<T>>`、`ExecuteBulkAsync` → `Task<BulkReport>`（层由下面那句 `WithBulk` 请求）；诊断口 `Explain()` → `InsertPlan`、`ExplainKeys()`、`ExplainBulk()` → `BulkReport`。旋钮 `WithBulk(BulkMethod, FallbackPolicy, rowsPerRoundTrip)` 今天只在这条链上。

**分块是看得境的事实，不是玄学。** `InsertPlan { Chunks, RowCount, ColumnsPerRow, RowsPerStatement, ParameterBudget, Reason, HookReplay }`，`Chunks` 是 `InsertChunk(FirstRow, RowCount, Ast, Sql)`。预算来自 `IBulkStrategy.ParameterBudget`：SQL Server `2100`（`src/QuickSql.Providers.SqlServer/SqlServerBulk.cs:29`）、PG 系 `32767`（`PostgreSqlBulk.cs:13`）、MySQL 系 `65535`（`MySqlBulk.cs:41`）；**Oracle 系、虚谷、GBase 8s 三家的 `Bulk` 是 null**（`OracleProviderBase.cs:142`、`XuguProvider.cs:149`、`Gbase8sProvider.cs:163`），于是落到共用层那颗保守值 `StatementEmitter.ConservativeParameterBudget = 100`（`src/QuickSql.Core/Write/StatementEmitter.cs:21`）。同一批 600 行 × 7 列：达梦切 50 块（每块 12 行），金仓一块，T-SQL 三块，MySQL 一块；（块数从来都由 `StatementEmitter.RowsPerStatement` 那一式当场算，不是注释里写死的数：500 行 × 30 列在 SQL Server 上是 8 块、每块 65 行，29 × 65 = 1 885 压在预算内——v0.38 第十四笔把 `SqlServerBulk.cs:9` 那颗注释里旧的"5 块"改到这个形状，用法册与规格都不另立数。）`tests/QuickSql.Tests.Dialects/DamengDialectTests.cs:626` 那格钉的就是这组数。`plan.Reason` 会自陈"参数预算 100"与"跨块不是原子的"—— 跨块不原子意味着批量插入没有事务保证，这是要自己补的。

**批量高速层：今天真有一条不走 SQL 文本的通道。** `IInsert<T>` 上另三颗口是 §12 那一节今天的落地形态 —— `WithBulk(BulkMethod, FallbackPolicy = DegradeAndLog, int? rowsPerRoundTrip = null)`（`src/QuickSql.Abstractions/Write/IInsert.cs:48`）、`ExplainBulk(BulkMethod? = null)` → `BulkReport`（`:63`）、`ExecuteBulkAsync(ct)` → `Task<BulkReport>`（`:76`）。阶梯只有四档：`Auto` / `CopyChannel` / `MultiValue` / `SingleRow`（`src/QuickSql.Abstractions/Providers/Bulk.cs:11`），§12.1 的 L2 数组绑定与 L4 临时表、MERGE 那一整档**没有成员**，因为那颗枚举的自陈是"只有今天真有实现的层才进来"（同文件 7-10）。

- **通道只有一家接了。** `SqlServerProvider.cs:37` 挂上 `SqlServerBulk`，`ResolveCopyChannel()`（`src/QuickSql.Providers.SqlServer/SqlServerBulk.cs:40`）第一次取时才按类型名在**注入的那只工厂所在的程序集**里找装载类（`SqlServerBulkCopy.TryResolve`）；其余十五家在这一格是两种形状之一 —— `Bulk` 直接是 null（`SqliteProvider.cs:220`、`OracleProviderBase.cs:142`、`XuguProvider.cs:149`、`Gbase8sProvider.cs:163`），或 `Bulk` 只报参数预算（`MySqlProviderBase.cs:141`、`PostgreSqlProviderBase.cs:81`）而 SPI 上那颗成员是接口的默认值 `=> null`（`src/QuickSql.Abstractions/Providers/IDbProvider.cs:228`）。两种都让请求 `CopyChannel` 变成整批退 L3。`DbCapabilities.Bulk_Copy` 从此有对象（`SqlServerProvider.cs:49`）；`Bulk_ArrayBind`（`src/QuickSql.Abstractions/DbCapabilities.cs:38`）今天**没有任何一家置它**，阶梯枚举里也没有对应成员 —— 那两家在类注释里点名说了这一点（`Gbase8sProvider.cs:91`、`SqliteProvider.cs:134`）。
- **两扇判据，都是整批的。** `InsertEngine.Refusal`（`src/QuickSql.Core/Write/InsertEngine.cs:243-255`）先问通道在不在，再问**每一列**收不收：SQL Server 那一族收 14 种 CLR 形状（`SqlServerBulkCopy.cs:110-127`，`char`／`DateOnly`／`TimeOnly`／无符号那一族／`object` 不在内，枚举按底层整数判）。一列不合格 ⇒ 整批退 L3，**不是把那一列省掉**（省一列 = 那一列的值不进库）。
- **`ExecuteAffrowsAsync` 走不了高速层，是拒而不是默默降级。** 它返回一颗 `int`，装不下"走了哪一层、几次往返、为什么"，而装载通道那一档根本不产文本 —— 所以 `_requested` 是 `Auto`/`CopyChannel` 时在方法体第一句拒（`InsertEngine.cs:107-110`；`WithBulk` 那一句当时不判，判的是执行口，而它是 `async` 方法，异常从 `await` 那里回来。建文本那句 `Plan(Backfill.None)` 在 `:112`，拒的那一条走不到它）。不调 `WithBulk` 时它默认文本层，`ExecuteBulkAsync` 默认 `Auto`（`InsertEngine.cs:33-34`）。`src/` 下今天唯一的调用点是跟踪器那一句（`src/QuickSql.Core/Tracking/EntityTracker.cs:815`），它不经过 `WithBulk`，`_requested` 是 null，走不到那句拒；而这条出口自己的签名上没有修剪税（对照另两颗带 `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]` 的：`:89-90`、`:122-123`），所以上游不必跟着加警告。另外这一颗旋钮只在插入链上：`WithBulk` 今天只有 `IInsert<T>` 有成员，`IUpdate<T>` / `IDelete<T>` 上没有。
- **`CopyChannel` 那一档付的三条语义。** ① 不产 SQL 文本：`Explain()` 交出的永远是文本层语句，层的问题问 `ExplainBulk()`（它不连库，但那一次反射查找照旧，所以修剪后的产物上它会报"没有通道"）；② 改写类钩子 `CudBuilding` / `ParameterBinding` 在这一档不适用（`InsertEngine.cs:224` 把这句写在 `Reason` 里）；③ 回填口 `ExecuteReturnKeysAsync` / `ExecuteIdentityAsync` **永不借道**，装载通道给不了库里生成的那些列（`IInsert.cs:34-37`）。
- **`rowsPerRoundTrip` 只压不抬**（`IInsert.cs:44-47`）。文本层它是每块行数，装载通道那一档它是"每次递交几行"，实现为切成多次递交（`InsertEngine.cs:212-213`、`:154-157`）；通道内部怎么分批是驱动的事。**原子性照文本层那一条**：内核不为这一串递交开事务，所以没有环境事务时块与块之间不原子；有事务时每一次递交都重新走 `RentAsync`，而它见着环境槽位就交回那一条被事务独占的连接与事务（`src/QuickSql.Core/Execution/AdoExecutor.cs:510-516`），于是整串递交挂在同一颗事务上（真库那册的回滚用例断的就是这一格：库里没有那三行，而不是"没报错"）。**块宽的来源今天写死了口径**：默认每句参数颗数 = 预算的九成（SQL Server 恒 1 890 颗，与列数无关），而实测的"每句耗时 vs 每句颗数"**不是线性**——T-SQL 一台实例上同一批 2000 行 × 6 列扫出：每句 120 颗 151–224 ms、每句 1 890 颗 1 215–1 405 ms（八趟区间，出处是已下线的 SqlSugar 对照稿 §2.5，读回方式见 §0 那句）。所以"贴着预算上限"**不等于"划算的块宽"**：大批量要自己显式压 `rowsPerRoundTrip`（v0.38 第十四笔按用户裁决记下这一句，默认值与那一式都不改，裁决留给调用方；规格 §12.3 末那一格是同一条账的另一面）。
- **通道少报了行就抛，但那一批不会撤销。** `written != report.RowCount` ⇒ `QuickSqlException`「报回 4/5 行…不能假设这一批完整落库」（`InsertEngine.cs:165-168`）；拦下的是"悄悄交出一个假数"，不是回滚。
- **降级留痕今天只有两颗。** `BulkReport.Requested/Method/Degraded/Reason` + 订阅位 `QuickSqlAop.BulkDegraded`（`src/QuickSql.Abstractions/Aop/QuickSqlAop.cs:36`，只在真降级时派发；`Auto` 取到文本层**不算**降级）。规格里那句 `LogLevel.Warning` + OTel 标签（`docs/quicksql-orm-design.md:1487`）在这份代码里没有对象：仓库既无日志器也无 OTel。**防静默逐条的手段因此不是那句警告，而是 `FallbackPolicy.Never`**（`InsertEngine.cs:231-232`）+ CI 里订阅 `BulkDegraded` 并断言它一次都没响。
- **反射找成员这件事踩过一次，写在这里当坑看。** 真库那支驱动的构造子收的是**具体**类型 `(SqlConnection, options, SqlTransaction)` 而不是 `(DbConnection, …, DbTransaction)`，超时属性叫 `BulkCopyTimeout` 而不是 `BulkTimeout`；按名字点错一处，`TryResolve` 就交回 null，而那个形状与"这一家没有装载类"**逐字同形**（同一条 `Reason`、同一个退层），离线册看不见。所以现在构造子按形状找（三个参数、连接与事务分别是 `DbConnection`/`DbTransaction` 的子类、中间那颗是驱动自己的 options 枚举），成员名一律由真库跑验。
- **证不到的那一格**：`CheckConstraints | FireTriggers` 两颗开关确实是从驱动的枚举上按名字读出来再置上的（不写死位值），但夹具那张表上既没有 CHECK 约束也没有触发器，所以"装载通道与逐条在约束/触发器上语义等价"这一半今天**没有可观测的对象**。上生产前要在自家带约束的表上量一次。
- **见证。** 离线册 `tests/QuickSql.Tests.Dialects/BulkLayerTests.cs` 17 颗（通道在与不在、列被拒、`Never`、块宽只压不抬、决策与执行同数、留痕时机），driver-only 那一支靠转发替身 `CopyChannelProvider.cs` 才能在无库环境里判；真库册 `tests/QuickSql.Tests.Integration/BulkPathTests.cs` 9 颗（整批落库逐列对、与 L3 写的内容等值、客户端 Guid 键进批、事务带得动且回滚由库里没有那几行背书、回填口零借道、递给通道的 payload 形状、2+2+1 三次递交行序不串、少报一行拦下、墙钟只报数不判）；可跑示例 `samples/QuickSql.Sample/Program.cs` §11 在真 SQLite 上把这一格能显示的部分逐条打印出来（`ExplainBulk` 的层与 `Reason`、无通道那一家上 `CopyChannel` 的降级与一次留痕、`Never` 抛、`ExecuteAffrowsAsync` 拒、`rowsPerRoundTrip: 20` 的往返数，最后以库里读回的行数收尾）。现场一次读数：2000 行 × 7 列同一张表，`CopyChannel` 一次递交约 60-170 ms，L3 多值约 1.7-1.8 s —— 单次读数，没钉成断言。

**自增主键回填是今天真实的限制。** 三条链各家能走到哪一档，取决于两颗槽位（`IKeyStrategy.SupportsBulkKeyRetrieval` 与 `SupportsPostStatementKeyRead`）：

| 家 | 批量 `RETURNING`/`OUTPUT` | 语句后回读 | 要键会怎样 |
| --- | --- | --- | --- |
| SQL Server | `true`（`SqlServerKeys.cs:12`） | `false`（同文件 `:48`） | `OUTPUT inserted.[Id]` |
| PG 系 | `true`（`PostgreSqlKeys.cs:13`） | `false`（`:39`） | 句尾 `RETURNING` |
| MySQL 系 | `false`（`MySqlKeys.cs:45`） | `true`（`:72`） | 语句后补 `SELECT LAST_INSERT_ID()`（`:77`） |
| 虚谷 | `false`（`XuguKeys.cs:63`） | `true`（`:70`） | 同上 |
| GBase 8s | `false`（`Gbase8sKeys.cs:95`） | `true`（`:67`） | 语句后回读，`EmitReturning` 抛（`:59`） |
| **Oracle 系** | `false`（`OracleKeys.cs:46`） | `false`（`:86`） | **两个口子都关，直接抛** |

也就是说：在 Oracle / 达梦 / 神通上要自增键（`ExplainKeys()`、`ExecuteReturnKeysAsync`、`SaveChanges` 里需要回填的插入档）今天**必然**拿到 `QuickSqlException`，消息是"没有可作为结果集发出的 OUTPUT/RETURNING"与那句"回读表达式从未被本仓库问证过"（`src/QuickSql.Providers.OracleBase/OracleKeys.cs:56`、`:92`）。抛而不是发一条"看着像在回填"的语句，是刻意的。

**一句 upsert：`orm.InsertOrUpdate<T>().SetSource(…)`（v0.39）。** FreeSql 式独立链，数据走 `SetSource(一只实体)` 或 `SetSource(一批)` 而不是入口形参；出口 `ExecuteAffrowsAsync`（受影响行数）与诊断口 `Explain`（→ `BulkWritePlan`）、`ToSql`、`ToLogSql`。语义是"按主键没有就插、有就改"，写入清单等于可写列加主键列，主键既进插入清单又是冲突判定的键。**入口零反射税**（列来自契约、值来自编译好的委托；全局过滤器在场时是当场拒而不是叠进 `WHERE`，所以整条链一次表达式翻译都不做），与 `InsertRange`、`UpdateRange` 同档。它是 `UpdateRange(...).WithBulk(Upsert)` 那一句文本的另一扇门，同一把列尺子、同两道当场拒（一道是全局过滤器在场，一道是主键由库给值而调用方没带键，那种键的冲突判定出不来、写进去的会是一行新行而不是改了那一行），独立出来只为让"我要 upsert"这件事写在方法名上。回填那一档不在这条链上，要键走 `Insert` 的 `ExecuteReturnKeysAsync` 或先插后查。取值段的审计种类与 `CudBuilding`、`ParameterBinding` 回放都报 `update`（与 `UpdateRange` 的 upsert 档同一条），所以"只在插入时补 `CreatedAt`"那一类钩子语义今天分不开命中与否，留作后续。

**发射形态由这一家自己定、调用方不选，共三形。** 句尾那一族（MySQL 血统三家 `ON DUPLICATE KEY UPDATE`、PG 血统六家 `ON CONFLICT … DO UPDATE SET … = EXCLUDED.…`）复用 `IBulkStrategy.UpsertClause`，发的是内核那条 `INSERT … VALUES` 再接一段尾巴，与已经跑绿的 `UpdateRange` upsert 档逐字同形，本条链不另拼一遍 `VALUES`。SQLite 也发 `ON CONFLICT` 那一形，可它整颗 `Bulk` 是 null（就是上面那颗预算表底下那句"没有可举证的参数预算 ⇒ `Bulk` 交回 null"），拿不到句尾那颗槽，于是这段尾巴长在方言的 `SqliteDialect.Emit(UpsertStatement)` 上，与 PG 那一份逐字同形而各写一份，只因为 §2 那条"provider 之间禁止互引"。`MERGE INTO` 那一族（SQL Server 的 T-SQL `MERGE … USING (VALUES …) … ;`、Oracle 血统三家与虚谷的 `MERGE … USING (SELECT … FROM dual UNION ALL …)`）由方言发一整句不同形状的语句，尾巴 `ON … WHEN MATCHED … WHEN NOT MATCHED …` 四家共用 `DialectBase.EmitMergeTail` 一处真相。GBase 8s 是唯一的例外，它没有 MERGE，本仓库对这一族的文档研究只立了 `UPDATE … WHERE EXISTS` 那条等价路，所以这一条链在它身上发的是每行一对语句：`UPDATE … WHERE 键 …` 紧跟 `INSERT … SELECT … FROM systables WHERE tabid = 1 AND NOT EXISTS (…)`，那颗 `systables WHERE tabid = 1` 是与 `Gbase8sKeys` 那颗 `DBINFO` 同源的单行字面源。

**档位口径与见证，不许读成"全十七家实测过"。** 这条链这次在五家连得上库的 provider 上各跑了一回集成：SQL Server、MySQL、PostgreSQL、SQLite、虚谷五家的命中改与缺键插两支都落库读回（虚谷 180 里那 3 红是 `CascadePathTests` ∶ `SoftDeletePathTests` 的读侧 `unexpected LIKE` 与并发列回填两处既有缺口、与这条链无关）；没实例的 Oracle 血统三家与 GBase 8s 仍到文本层为止。SQL Server 那颗 T-SQL `MERGE` 已经在活库跑过、集成册那两条 `InsertOrUpdate_…` 用例命中改与缺键插两支都落库读回过逐格（`Microsoft.Data.SqlClient` 真驱动、授权实例）。这一格是活库抓出来的修法：SQL Server 编译整条 `MERGE` 时就验缺键那一支写了显式自增键、连"全命中而那一支根本不触发"也报 544，所以主键是自增列时方言把整句裹一对 `SET IDENTITY_INSERT … ON/OFF`（本家声明了那颗 `Identity_ExplicitInsert`），这一裹让显式自增键合法、两支才跑得通；目标位带 `WITH (HOLDLOCK)` 仍是那条有名并发缺口的文档缓解、是否真挡住要在活库并发跑一次才算结清；SQLite 这一颗已经在活库跑过——集成册 `QUICKSQL_TEST_PROVIDER=sqlite` 那一场里，命中改与缺键插两支各落库读回过逐格（`BulkWritePathTests` 那两条用例，SQLite 自建 scratch 库、Microsoft.Data.Sqlite 真驱动），本家那四臂探针旁证因此升成"这条链产出的那一句实测过"；Oracle 血统三家的 `MERGE` 连一台授权实例都没有，是纯文档口径；虚谷那颗 `MERGE` 的骨架早先只在活库跑过单行手工句（v0.32 探针 F-19，同一份日志里 `USING (VALUES …)` 反而是语法错，行集只能从 `USING (SELECT …)` 带），而这条链的命中改与缺键插两支今天都在活虚谷跑绿了（多行 `UNION ALL` 写显式主键这一格先前只当未复点而跳过，去掉那道守卫实测两支都过）；GBase 8s 那一对两条语句要的是一颗分号批，而本家 `SupportsMergedStatements` 取默认 false、这一族从未在一台机器上解析成功过。逐家新产出的文本已在离线黄金样本 `tests/QuickSql.Tests.Dialects/golden/` 下那五家（sqlserver、dameng、xugu、gbase8s、sqlite）的 `insert-or-update.sql` 钉成基线，形态与拒收断言在 `tests/QuickSql.Tests.Dialects/InsertOrUpdateLayerTests.cs`。黄金样本把"文本变了要有人显式承认"这件事钉住，不替"这一句在真库上跑得动"背书；把每家对应那颗 `Upsert_` 前缀的位补成 declared 是一颗后续账，分派不读那三位（读的是有没有句尾槽、方言覆没覆 `Emit(UpsertStatement)`），口径见 §2.4 与规格 §14。

### 3.3 会话、跟踪与 `SaveChanges`

`DbContext`（`src/QuickSql.Core/Orchestration/DbContext.cs`）只做一件事：把实体变更攒成语句批次。

```csharp
sealed class AppDb(QuickSqlOrm orm) : DbContext(orm)
{
    public DbSet<Order> Orders => Set<Order>();   // 官方推荐的派生形态（DbContext 类注释里那句）
}

var db = new AppDb(orm);
var order = await db.Orders.Where(o => o.Id == 7).FirstOrDefaultAsync();  // 跟踪读
order!.Note = "改一下";
var report = await db.SaveChangesAsync();
```

- 入口：`Set<T>()`（按类型缓存）、`Add`/`AddRange`/`Attach`/`Update`/`Remove`/`RemoveRange`/`Entry`/`InsertRaw`，`orm.CreateDbContext<TContext>()` 存在但走 `Activator`，AOT 场景建议自己 `new`
- `orm.CreateSharedContext<TContext>()`（v0.37 第八笔，`QuickSqlOrm.cs:256`）：**同一条 async 执行流上取到的一直是同一只会话**，用于不做 DI 注册也要跨方法传的场景（控制台、后台服务）。三条边界要用之前先知道：① 共享范围是**一条流**，不是进程也不是线程，槽位挂在 orm 实例上，所以两只 `QuickSqlOrm` 在同一条流里各拿各的会话；`await` 之后的子任务里建的那只**不会回到父帧**（父帧之后自己取，取到的是另一只）；② 反过来，父帧先建、之后扇出的并发分支拿到的是**同一只**，而 `ChangeTracker` 不是线程安全的（今天没有闸门，要并发就各自开流或各自 `CreateDbContext`）；③ 会话没有 `Dispose`，"结束"就是让那条流出作用域，或者按 `ChangeTracker.Clear()` 清账。八条判据见 `tests/QuickSql.Tests.Dialects/SharedContextTests.cs`，口径见设计文档 §7 末
- `DbSet<T>` 直接继承查询链（`QuickSql.Core/Orchestration/DbSet.cs`）并实现 `ITrackedSet<T>`：`Local`（`LocalView<T> : IReadOnlyList<T>`，有 `Entries` 与 `Find(params object?[])`）、`Load<TProp>(navigation)` —— **后者引用与集合两档都接**（分岔判据是导航属性的 CLR 型是不是 `IEnumerable<T>`）：对会话里每一只实体发 SQL 把子实体拿回来、按 FK↔PK 缝回导航属性，返回 `this` 可续链。它真发 SQL——`DbSet.cs` 顶上那条旧账（「调了 `Load` 就以为导航有值」是最难查的一种假象）解释的就是为什么没有"静默空档"那一版。补载读侧与 `Include` 那一族同一把尺（0.39.0-beta.29 起）：父集合超过这家库的参数预算时按 `IN` 分块、每块一条子 `SELECT`，SQL Server 2100 ∶ Oracle IN 1000 那两档不再整条撞驱动
- `ChangeTracker`：`Count`、`HasChanges`、`Entries()`、`Entries<T>()`、`DetectChanges()`、`Clear()`；状态是 `EntityState { Detached, Added, Unmodified, Modified, Deleted }`；点名某列用 `Entry(entity).Property(nameof(X)).IsModified = true`，`AutoDetectChangesEnabled` 可关
- **三档先后有一格会让步：同一张表这一轮既插又删时，删除整档提前**（规格 §8.2 那句"先插、再改、最后删"的<em>唯一</em>例外，`src/QuickSql.Core/Tracking/ChangeTracker.cs` 的 `FlushDueAsync`）。要腾开的是<em>业务</em>唯一键 —— 明细的行号、顺序位、单内编码那一类，库里那条 `UNIQUE (父键, 位置)` 管的就是它，而"摘掉第 3 行、再插一行号仍然是 3 的新行"（换物资、换供应商、换槽位）是编辑界面的常规动作。按原先后发句子，那句 `INSERT` 撞的是<em>本轮自己就要删掉</em>的那一行：`23505 duplicate key`，逐条看每句都对、串起来是错的。提前的是**整档删除**而不是那一张表的删除，所以"被依赖的那一行最后删"那条不变量一字未动（只搬某一张表就会把父与子的删除拆开，换来一句删不掉的父）；触发条件只看状态（这一档同时有 `Added` 与 `Deleted`），不去问库里有没有那条索引 —— 没有时换一下先后也是中性的。只插不删、只删不插的表照旧走三档，一个字节没改。见证：`tests/QuickSql.Tests.Integration/RowSwapOrderingPathTests.cs` 两颗（一删一插同键、一删两插同键），唯一索引由结构同步从 `[Index(IsUnique = true)]` 现场建出并<em>当场撞一次</em>证明它活着；把触发条件按死重跑，两颗都红在库的那句唯一约束上
- 只写脏列：UPDATE 的 SET 里只有 `ModifiedMembers`；没有主键的实体**拒绝跟踪并指路**（`Attach(new AuditRow{...})` 会抛）
- `SaveChanges()` 是同步壳（sync-over-async）；干净无事时**不发语句、不开事务**（`tests/QuickSql.Tests.Dialects/TrackingTests.cs:527` 那格用探针工厂证的就是这条）；发语句前先过 `EnsureWriteOrderResolvable()`（外键依赖成环就在这里红）
- 分表实体**可以读进会话、不可以由会话写出去**：`SaveChanges` 在开事务之前逐行问一遍跟踪器，碰到挂在分表模板上的待写实体就整批拒绝并指路（`src/QuickSql.Core/Tracking/ChangeTracker.cs:231`，被 `Orchestration/DbContext.cs:183` 调用），所以拒绝发生在"发语句之前"而不是"发到一半"；链式侧不受影响，`InsertRange`/`Update.SplitTable` 照写。为什么是拒绝而不是定位到片：见 §3.6 与 §4.3 第 10 条末项（"跨座/跨片的一行由什么指认"那一格规格留白）
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

被校验闸门整批拒时抛 `ValidationFailedException`，**那一刻语句还没生成、库里什么都没发生**，所以没有 SQL 可报，能定位的只有契约那几条尺：明细在 `Failures`（一行一条，批量时每条前缀 `[第 N 行]`），而消息摘要按「成员 + 规则」归并、项数多的排前、最多逐组列 5 条（`GroupLimit`，组数与项数报的都是全部）——一次 134 行的种子报出 656 项时，按行取前几项只会给同一列的连续几行，归并才答得出"契约哪几条太严"。只有一项时不出这份清单（抬头本就点到成员与列）。

```
3 行插入被拒绝：2 项违反库契约（ValidationMode.ContractOnly）。整批先校验后执行，一行都没写进库。
按「成员 + 规则」归并（共 1 组）：
  1. CustomerName / length：2 项 —— 例：[第 1 行] CustomerName 超出列 CustomerName 的 64 字符
```

DDL 发射有 SPI（`ICodeFirstDialect.EmitCreateTable` / `EmitAddColumn` / `EmitAlterColumnPrecision` / `TryEmitColumnComment` / `EmitCreateIndex`），测试里就这么直接调（`tests/QuickSql.Tests.Dialects/DialectParityTests.cs:565`）：

```csharp
var writer = new SqlWriter(orm.Provider.Dialect);
orm.Provider.CodeFirst.EmitCreateTable(writer, orm.Schemas.Contract<Order>());
```

**但没有迁移层**：没有 migrate diff / migrate script 这类命令、没有 CLI、没有版本表，`CodeFirst` 与 `SchemaReader` 在规格里都挂在 P2 那一行。

反向读结构只有 SPI 形状：`ISchemaReader.ReadTableAsync` / `ProbeLengthSemanticsAsync`，产出 `SchemaTable`/`SchemaColumn`/`LengthSemanticsReading`。`SchemaColumn` 只有六格（`src/QuickSql.Abstractions/Providers/IDbProvider.cs:128`），**读不回主键、读不回注释、也接不住自增标记与默认值**（§4.3）。

### 3.5 诊断：有什么、缺什么

有：`IQuery.Explain()` → `QueryPlan { Ast, Sql, ModelText, HookReplay, Provider }`，`Provider.ToLogSql()` 出内联字面量版；`IInsert/IUpdate/IDelete.Explain()` 出各自的 plan；`InsertPlan.ToLogSql()` 会打 `-- 块 n：第 a~b 行`；`SaveChangesReport.HookReplay`；`orm.Aop.Executed`/`Error`/`SlowQuery` 三颗观察口（`Executed` 手上还有 `Route` 那一颗，见 §2.5）；`orm.Provider.Connectors.TryEnsureLoaded(out string? failure)` 作启动期自检（注意它只创建不打开连接，v0.32 实测它挡不住原生依赖缺失）；`orm.Sources`（`ClusterNames`/`Knows`/`ReplicaCount`，§3.7）读得出**声明侧**的集群清单——库里现有哪些库仍然读不出，那一格在 §4.3 第 9 项。

**没有 `doctor`。** 全仓库 `doctor` 只出现在注释与规格里（§9.5 那格），`src/` 下没有任何诊断命令实现。同理没有 `QuickSql.Testing` 内存 provider（规格里已被移出承诺）。

### 3.6 分表：一张实体铺在几张同构表上

v0.38 第十笔落地的能力，三条裁决由用户给全（逐字）：范围＝「读写全套，跨片聚合明确拒绝」，分片键＝「时间 + 哈希取模都内建」，交付口径＝「一轮做完：规格／台账／src／用例全开」。**做的是一个库里的多张表，不是多个库**（多库那一格由 §3.7 的具名集群承接，与这里正交：一张片落在模板自己声明的那座集群上，见 `SchemaSyncer.cs:175` 那颗 `CreateShardsAsync`）。

声明是类上两颗注解，一条模板加一条算法：

```csharp
[Table("AccessLog_{year}{month}")]                          // 表名写的是模板，不是一张能建的表
[SplitTable(SplitKind.Month, nameof(OccurredAt))]           // 分片键 = 属性名，档 = 月
public class AccessLog { … }

[Table("TicketRow_{shard}")]
[SplitTable(SplitKind.Hash, nameof(TenantId), Buckets = 4)] // 哈希档要有桶数
public class TicketRow { … }
```

- 占位符只认四颗：`{year}` `{month}` `{day}` `{shard}`。每颗至多一次，本档要的必须在、本档不要的不许有（月档写 `{day}` 当场抛），认不出的花括号（`{hour}`）也当场抛 —— 症状本来是"部署之后库里多一张名字叫 `…_{hour}` 的表"，所以这一格走启动期抛而不是发 SQL 时才发现。六档：`Day`／`Week`／`Month`／`Season`／`Year`／`Hash`。
- **启动期四条判据**：分片键必须是一列（被 `[NotMapped]` 排除掉的属性不行）；模板得配得上那一档；哈希档少于 2 个桶等于没分；**主键不许自增**（跨片唯一库给不了，两张片各自能发出同一个号，要嘛客户端生成 `Guid`，要嘛这一张不分——声明写在 `[QuickColumn(IsIdentity = TriState.No)]`）。
- 片名只有一份换算实现（`SplitRouting`），写侧按行的分片键值算、读侧按范围算、DDL 按范围建，三处同一颗 —— 所以不存在"同一个月在两处算出两个表名"。哈希档不用 `GetHashCode()`（它随进程随机化，同一行两次运行会进两张表）：整数折成无符号取模、字符串走 FNV-1a。时间档先下整到周期起点再填模板：`Week` 档的片名记的是那周的周一，行可以落在那周任何一天。
- 模板**不许原样发出去**：`SplitTables` 没给范围就读、或 UPDATE/DELETE 没钉片就写，发射口当场抛（消息给两条出路，不是一句"不支持"）。整库同步遇到模板是"跳过并记名"（`SyncStructureAsync` 的 `SkippedSplitTemplates`），不是建一张带花括号的表。

读侧：选片范围就是"打哪几张表"。

```csharp
var q = orm.Select<AccessLog>().SplitTables(new DateTime(2026, 1, 1), new DateTime(2026, 3, 31));
var byBucket = orm.Select<TicketRow>().SplitTables(0, 1);      // 哈希档给桶号，重复的自动去掉
var rows = await q.ToListAsync();                              // 三片 → 一句 UNION ALL，外层只端一颗通配选列
var page = await q.OrderBy(o => o.OccurredAt)
    .ToPagedAsync(1, 2, new PagedOptions(CountMethod.SkipTotal));  // 跨片要"有没有下一页"就按这一档
```

- **单片时不包派生表**：范围只落一张片时，发出去的句子与一张不分表的实体逐字同形（`FROM [AccessLog_202601] AS [t0]`），既不为跨片重定列序，也不收"包了一层没用的派生表"的性能税。
- 序与分页只长在外层，不在每片里。剪枝是真的少问：范围只覆盖二月时，一月与三月的片根本不出现在文本里。
- **跨片明确拒绝三件事**（都是抛，不是静默给一份错数）：没选片就读；跨片 `JOIN`／`Include`（每片各连一遍会把"一条主行配几份子行"变成片内的事）；跨片聚合／分组／`HAVING`（`COUNT(*)` 打在 `UNION ALL` 外面是对的，`AVG` 与"分组之后再数有几组"不是包一层能救的，而"每片各分各的组、键不合并"正是隔壁那一族的形态）。拒绝消息里写着走得通的那条：把范围收到一片就是普通聚合；分页只要"有没有下一页"就传 `CountMethod.SkipTotal`。单片不受这一格限制。

写侧：一片一句。

```csharp
await orm.InsertRange(logs).ExecuteAffrowsAsync();   // 跨三月的六行 = 三句各两个 VALUES，不是一句六个
var upd = orm.Update<AccessLog>().SplitTable(new DateTime(2026, 2, 9))   // 改与删钉住一片，且必须在链首
            .Set(o => o.Status, 299).Where(o => o.Status == 404);
await orm.Delete<AccessLog>().SplitTable(new DateTime(2026, 2, 9)).Where(o => o.Status == 500).ExecuteAffrowsAsync();
```

- 分块计划里每块多了一颗 `RowOrdinals`：那块覆盖的入参下标（分表后不再是连续区间）。单行批也带着它，不留"只有一片时省掉"的特例。
- 装载通道（批量高速层那一档，§3.2）在分表上是一片一次递交，往返次数逐片取整再相加。
- **分片键为空的那一行当场抛、一条都不写**（不把空值"当作今天"送进今天那张表）。

建片：范围只有业务知道，所以库不猜。

```csharp
var shards = await orm.CreateSplitTablesAsync<AccessLog>(new DateTime(2026, 1, 1), new DateTime(2026, 3, 31));
var script = orm.SplitTablesScript<TicketRow>(0, 1, 2, 3);   // 不连库，交回文本，走 §9.5 那套脚本模式
```

- `Created`／`AlreadyThere` 逐张先问存在再发 DDL，所以同一范围跑第二趟是"新建 0 张、已在 3 张"，不是撞一条"表已存在"。
- **`IndexesNotCreated` 永远有内容**：片上不随片建索引。"只给第一张片建索引"是一次看起来建好了的假绿，而索引名在同一模式的 N 张片上本来就会撞。
- 一次选片的片数上限 512（日档跨十年是 3 653 张表，`UNION ALL` 挂那么多分支在任何一家都是编译到死），越界报一句可行动的错。
- 这一口是 DDL，走 `SyncStructure` 那道同一扇门（要么 `QuickSqlOrmOptions.ForceSchemaSync`，要么 `DOTNET_ENVIRONMENT=Development`），生产启动期不会自己建片。

**与变更跟踪器互斥**：分表实体可以被读进会话（`Attach`，不发语句，因此放行），但 `SaveChanges` 会在开事务之前逐行问"这一行此刻会不会被写出去"，会写就把整次保存拒掉（报的是 `QuickSqlException`）。理由是会话的身份映射按主键记账，而同一个主键在分表里可以占着两张片上的各一行 —— 要先定"跨片的一行由什么指认"才谈得上写回，那一格规格还没裁。

**这一档现在证到了哪里**：活库见证只有 SQL Server 上的 `Month` 与 `Hash` 两档（集成册 `SplitTablePathTests` 9 颗）；`Day`／`Week`／`Season`／`Year` 四档今天只有离线文本与换算用例；示例 §12 在真 SQLite 上跑过三片合并读与哈希逐桶读，那是文件库不是厂商库。离线 97 颗（`SplitTableTests` 63 + `TableNamingEmissionTests` 34），黄金样本 2 份（`golden/sqlserver/split-union-three-shards.sql`、`split-update-one-shard.sql`）。可重跑：`samples/QuickSql.Sample` 的 §12（建三月三片 → 六行三句 → 合并读 6 行 → 剪枝 2 行 → 跨片翻页 → 跨片数总数被拒 → 钉片改 → 四桶逐桶读回）。

### 3.7 读写分离与分库：一座集群一个主库若干从库

v0.38 第十一笔落地的能力，四条裁决由用户给全（逐字）：范围＝「读写分离 + 分库一轮做完」，解析键＝「实体声明 + 具名数据源」，选路形状＝「默认从库 + 三格强制主库」，交付口径＝「一轮做完 + 替身真库见证」。**两件事共用同一条路由**（一次 `Resolve` 只判一回"哪座集群、哪个角色、为什么"），所以它们不是两个功能，是一格的两侧：`Replicas` 那一颗是"同一座集群里的第二条连接"，`Clusters` 那一颗是"另一座集群"。跨集群的连接与跨集群的事务**明确拒绝**（判据在下面的那张名单里）。

**这一格与 §3.6 的接缝**：一张分片表落在它的模板自己声明的那座集群上（`SchemaSyncer.cs:178` 那句 `PrimaryConnectionString(ClusterOf(template.Table))`，注释写的正是"分片只改表名，不改住在哪座库"），所以"按月分片的报表表住在 report 那座"是配得起来的，两档各自算各自的。

声明只有一颗注解，而它**只有名字、没有连接串**：

```csharp
[Table("SalesTotals")]
[DataSource("report")]        // 类上（QuickSqlAttributes.cs:94）；不写即在 main
public class SalesTotal { … }
```

- 集群名要与 `QuickSqlOrmOptions.Clusters` 里那一条对上。对不上的那一格不在启动期全量校验（注册表是惰性的，实体第一次被 `Select`/`Set` 触及时才进册），而在**第一次碰它时**抛：`集群 report 不在册（在册的是 main、report）。…`（`ConnectionSources.cs:91`，消息把在册清单打出来）。显式写 `main` 与不写同义。
- 不落 Fluent，与 `[SplitTable]` 同一分工：两条路径算出两种归属，在分库上是把语句发到另一座库里去，比表名算错更贵。
- G1/G2 同形：生成器抄那一个字符串（`EntityFactsBuilder.cs:506` 起），`[DataSource(null)]` 这种写法两侧交回同一种空值、到同一句启动期判据撞停（`DataSourceTests.cs:224` 那颗对拍钉住两侧）。

输入是三颗选项（§2.1）加 DI 上同名的两颗公开属性：

```csharp
var orm = new QuickSqlOrm(new QuickSqlOrmOptions
{
    Provider = provider,
    ConnectionString = mainPrimary,            // 缺省集群 main 的主库
    Replicas = [mainReplica1, mainReplica2],   // main 的从库，按给出的顺序轮询
    Clusters = [new ClusterConfig("report", reportPrimary, [reportReplica])],
});

// DI 同一条输入
services.AddQuickSqlOrm(DbKind.SqlServer, primary, builder =>
{
    builder.Replicas = [replica];
    builder.Clusters = [new ClusterConfig("report", reportPrimary)];
});
```

- `ClusterConfig(string Name, string Primary, IReadOnlyList<string>? Replicas = null)`（`QuickSqlOrm.cs:81`）：**一个名字、一颗主库、若干从库**。加一座库就是往清单里添一条，没有 `UseReplicaOn()` 那一族方法。
- 启动期四判据（`ConnectionSources.cs:41` 起）：具名集群里不许出现 `main`（那座由 `ConnectionString` 占着，:55）；`Primary` 空的整条不许静默留着一座没主的库（:58）；从库清单里的空串/空白**不许静默少一颗**（症状是"轮询少一台"，那种账要到容量出问题时才看得见，:95 那颗 `Sanitize`）；实体侧名字空或全空白当场抛。
- **按请求解析连接串的那颗工厂与 `Replicas`/`Clusters` 不能同时在场**：注册期就抛（`QuickSqlServiceCollectionExtensions.cs:149`），因为从库与具名集群是启动期常量，而工厂每请求换一次 `main` 的主库串——混用会让一座集群的主库与从库属于不同租户。要多租户各配一座从库，为每一座另建一颗 orm。

选路是 `DataSourceRouting.Resolve(cluster, intent, choice, inTransaction, writtenInFlow, replicaCount)`（`DataSource.cs:91`），**顺序就是裁决**，而全仓库只这一份判据：它只有一个调用方——`AdoExecutor.RentAsync`（`AdoExecutor.cs:503`），而读侧（`OpenAsync` 缺省那一支 :478）、写侧（:208/:241）、装载通道（`CopyAsync` :304）全都从那里过一遍。**没有从它取的只有两处，而两处都恒主库、没有可判的档**：开事务自己拿 `PrimaryConnectionString`（:419），DDL 那侧同样（`SchemaSyncer.cs:100` 与分片那口 :179）。结论是 `DataSourceRoute(Cluster, Role, Reason)`（:62），`Reason` 那七档逐字是 `WritePrimary`／`ChainPrimary`／`ChainReplica`／`InsideTransaction`／`ReadAfterWrite`／`ReadNoReplicaConfigured`／`ReadReplica`（:30 起）。

缺省档：写永远主库；**读默认从库**。三格强制主库（除此之外没有第四格）：

1. **事务内**（`InsideTransaction`）：不换连接。换一条连接去读说的就不是这个事务里的数据——未提交的自己看不见、别人看不见的自己看见了，都是。
2. **同一条执行流上这座集群刚写过**（`ReadAfterWrite`）：库测不到复制延迟，所以写过就当从库还旧着。这一格只在**会话**上成立：那枚戳子是 `DbContext` 构造时在调用方的同步帧里挂上的（`AdoExecutor.InstallFlowWrites`，:60，全仓库唯一的调用方是 `DbContext.cs:34`），裸链（`orm.Select`/`orm.Insert`）没有会话可问，所以那条路上写后读**不生效**（示例 §13 把这一格打成了两行对照读数）。释放会话时必须把它注销，而注销只能在同步那一段——来历见 §2.6。
3. **链上显式 `FromPrimary()`**（`ChainPrimary`，`IQuery.cs:141`）；对称的另一颗是 `FromReplica()`（:150），它压得过写后读——显式表态压过保守档，库不替你兜那份"新读旧"。

- 从库**按声明顺序轮询**，`Interlocked` 递增后取模（`ConnectionSources.cs:32`）。没有权重、不做延迟探测、也不问哪颗活着：那两件事都要 SPI 槽位，而 `IDbProvider` 那十一颗子 SPI 里没有一格承接库侧延迟（§4.3 那份名单点的就是这类空档），所以本家不假装会挑"最不老的那一台"。
- 会话内一个从库是**钉住一颗**而不是每句换一颗：会话级连接的键是 `(集群, 角色)`（`AdoExecutor.cs:34` 那段注释），所以"轮询"落在跨会话之间，而同一会话里两次读互相看得见顺序。

五格拒绝，全在动手之前（连驱动都没碰），每条消息写着走得通的那条：

| 那一格 | 报的是 | 消息给的两条出路 |
| --- | --- | --- |
| 跨集群 `JOIN`／`Include`（链式） | `QuickSqlException`（`QueryCompiler.cs:145`） | 把两个实体声明到同一座集群；或用内存缝合那一族（集合导航 `Include` 走第二条 `SELECT`，跨集群天然成立） |
| 事务里点 `FromReplica()` | `QuickSqlException`（`DataSource.cs:91` 那一族判据；活库见证在 `DataSourcePathTests`） | 在这条事务之外读 |
| 事务在位，而这条语句要落另一座集群 | `QuickSqlException`（`AdoExecutor.cs:514`，读／写／装载三条路都过这一格） | 分别开事务、各自提交 |
| 一批 `SaveChanges` 跨两座集群 | `QuickSqlException`（`ChangeTracker.cs:283`，"一场事务只有一条连接"） | 按集群分两次 `SaveChanges` |
| 一场事务还开着，去另一座集群再开一场 | `QuickSqlException`（`AdoExecutor.cs:368`） | 按集群分别开事务、各自提交。**同座集群上嵌套才是保存点**；跨座的两阶段提交规格没裁 |

DDL 按集群分组（`SchemaSyncer.cs:130` 的 `GroupByCluster`）：一次 `SyncStructureAsync` 落在几座库上、每座建哪几张，是分组的结果而不是调用方的 arrange。`_provider.CanCreateDatabase` 为真时**每座**先各自 `EnsureDatabaseExistsAsync`（:103；分片那一口 :182），脚本模式则在多于一座集群时每段加一行 `-- 集群 main：下面这几句要落在这座集群的主库上`（:72 那颗 `segmented`），因为那段文本是给 DBA 的、他要的正是"这一段贴到哪座库"。`SkippedSplitTemplates`（:287）的口径不变：整库同步遇到分表模板是跳过并记名，不是建一张带花括号的表，也不是把它路由到别处。

`orm.Sources`（`QuickSqlOrm.cs:165`，公开）读得出在册清单：`ClusterNames`（按 Ordinal 排过）、`Knows(cluster)`、`ReplicaCount(cluster)`、`PrimaryConnectionString(cluster)`。这是**声明侧**的读数；"库里现有哪些库"仍然反查不了（§4.3 第 9 项）。观察侧那一颗是 `CommandLogEvent.Route`（§2.5），所以"这一句实际走了哪座库、哪个角色、按哪一档"是订阅出来的事实——示例 §13 的每一行读数都这么来。

**这一档现在证到了哪里**：离线 50 颗（`DataSourceTests` 33 个成员，其中三颗 `Theory` 带 20 行——决策表逐档、名字与在册清单、两侧元数据同形、上面那五格拒绝（离线钉判据、活库钉库里的内容）、DDL 分段、"点角色不改变一个字节的 SQL"）；活库 9 颗（`DataSourcePathTests`）——读写分离那 5 颗**只在 SQLite 上**（两颗文件库当主/从，从库"有表而没行"就是那份替身的落后），分库那 4 颗在两族上跑（SQL Server 是同实例两座真库 `QuickSqlClusterA`/`QuickSqlClusterB`，由本仓库自己的 `CanCreateDatabase` 建出来；SQLite 是两颗文件）。裁判一律走 raw 连接问库里的内容与表的归属，**从不走被测的那条路由**。示例 §13 可重跑（三座库一次建表 → 写落主库缺省读落从库 → `FromPrimary`/`FromReplica` 两读 → 会话内写后读 → 没配从库那座 → 三条拒绝 → 脚本分段）。**不证的三格写白**：复制延迟（没有承接它的 SPI 槽位，所以"从库旧着"是构造出来的替身而不是量出来的）；权重与健康检查（没有实现）；跨厂商的多集群（一个 ORM 实例一颗 provider，`ConnectionSources` 连 provider 都不持有——异构要两颗 orm，见 §4.3 第 10 项）。



## 4. 当前边界与坑

### 4.1 十四家的状态口径统一是"离线文本层，未实测"

十七颗叶子里，十四家的驱动从未在一台机器上解析成功过，也没有一条语句在真库上执行过（内建表 `BuiltinDrivers.cs` 各行注释逐行自陈；金仓与 Oracle 两家例外——§1.9 那一趟冒烟里各自解析成功过一次，也只是文本层读数，不改变本节口径）。达梦的 `DmProvider`、神通的 `Oscar`、GBase 8s 的 `GBase.DataDB`、DuckDB 的 `DuckDB.NET.Data` 这些程序集名各只有一处出处，而那不是现场读数。**把 provider 的存在读成"已适配/可直接上生产"是本文最不希望发生的事。**

### 4.2 三家例外的读数走到哪里停（三处口径不一致，原样列出）

- **SQL Server。** 装载链走完整过（`DriverLoadingTests` 按类型名断言解析到 `Microsoft.Data.SqlClient.SqlClientFactory`），集成层 `tests/QuickSql.Tests.Integration/` 那 17 个 .cs 是真打库的（目标串由环境变量 `QUICKSQL_TEST_CONNECTION` 或仓库根那份不入库的凭据文件给，两处都没有就抛）。**v0.38 第十一笔在这一家跑出一处源码缺陷并当场改掉**：`EnsureDatabaseExistsAsync` 原先只读连接串里的 `Database` 那一颗键（`src/QuickSql.Providers.SqlServer/SqlServerProvider.cs:81`），对着写 `Initial Catalog` 的串**静默不建库**（裸 `DbConnectionStringBuilder` 不做同义词归一，所以取不到值就 return），症状要到"连不上那座库"才现形——现在两颗键都读（:118 那颗 `ReadCatalog`），同值取一，两颗给出不同名字时抛并点名两颗。这一格的见证是活库（两座真库由这条通路建出来，见 §3.7），**离线册今天没有它**：那一条要的是"字符串进、决策出"的可断言形状，而这一格断的是"库建出来了没有"。能力台账类注释里那句是它的自我口径：**"T-SQL 那一族不进这份台账，这不是漏记：它的每一位都由集成套件在两台真库上跑过"**（`CapabilityLedgerTests.cs` 第 100 行附近）。
- **虚谷。** 内建表解析成功（`XuguClient`，工厂落到 `XuguClient.XGProviderFactory`）、`Open` 成功、目录可读。台账把 7 位中的 4 位钉成 `Live`，见证是三份基线在活库上执行通过（`paging-skip-only.sql` 1/1 句、`ddl-alter-and-comment.sql` 3/3 句、`ddl-order.sql` 1/1 句）；`ddl-qualified.sql` 那一形跑过而**失败**（`E4002` 模式不存在紧随 `E5021`），台账写明败因在"不建库、不建模式"的授权侧而不是文本侧，所以本版不修它。剩下的 `Returning` 一位仍是 `OfflineText`，理由是键回填那一整档缺 SPI 槽位、集成层按名 skip。
- **SQLite。** 内建表解析成功（`Microsoft.Data.Sqlite`，工厂落到 `Microsoft.Data.Sqlite.SqliteFactory`），而这是内建表上第一颗 `FactoryType` 从活机器读回来而非照文档写的键。集成层用 `QUICKSQL_TEST_PROVIDER=sqlite` 打到一座临时文件库（`Sqlite.ScratchConnectionString`，本家"实例"就是一个文件，夹具只在被点名的这条显式通路上自建，`TestConnectionString` 那道闸门一个字不动）。台账把 5 位中的 3 位钉成 `Live`（`Paging_LimitOffset`／`Returning`／`MultiStatement`），见证与前两家不同——是 ORM 自己拼出的语句跑过，不是基线文本被手敲进库；`MultiStatement` 那颗靠事后翻库（`IX_QuickSqlOrder_CreatedAt` 在场）认。剩下两位不翻档且各因不同：`Schemas` 是"发得出而带限定的那一形今天没被执行过"（留在 `OfflineText`），`Identity_ExplicitInsert` 连可发的文本都没有（Core 直接抛，留在 `VendorDoc`）。集成那一场是 `95 / E0 / F0 / S14`（14 颗具名 skip：并发令牌 12 + T-SQL 裁判文本 2）。**v0.38 第十一笔复点：SQLite 那族现在是 `131 / E0 / F0 / S29`，SQL Server 那族是 `131 / E0 / F0 / S5`**——场数每加一册就涨，那一串 95 是 SQLite 那一笔落地时的快照，本文把它留着是为了对上账，不代表现在跑出来的还是它。**注意：本家有活库，但 `golden/sqlite/*.sql` 那 11 份没被原样重放过——翻 `Live` 的那条链与"黄金样本↔真库执行"那条链在这里是两条，别把前者读成后者。**
- **三处口径要指认：** 内建表 `BuiltinDrivers.cs` 的 xugu 行注释写的是"跑的是 SELECT 1 与两颗 GetSchema，provider 发的 SQL 一条都没有"，而上面那份台账把 4 位记为 `Live`；SQL Server 那半同样只有测试侧的自述；SQLite 那半的行注释（若仍是接入时那句"未实测"）也与本笔翻的三颗 `Live` 不同步。规格 §13.4/§13.5 才是那三本账的落点。**本文不替三边裁哪个更准**，要上生产的话这三处得先对齐。
- 顺带量出的一格约束：`TryEnsureLoaded` 只创建不打开连接，挡不住原生同伴缺失（bare 那颗反证）。旧清单那格"`path` 用正斜杠时托管加载会成功、原生库找不到，要到 `Open` 才炸"的坑已随 `path` 字段一起消失——探测名由内建表拼 `<assembly>.dll`，分隔符不再经过任何人的手。

### 4.3 已知的模型空档（都在 `src/` 里可读）

1. **SPI 没有"库的版本"槽位**。虚谷的 `COMPATIBLE_MODE`、PG 的内核版本段、MySQL 的 `sql_mode` 都改方言假设，而 `IDbProvider` 那 11 颗子 SPI 里没有一格承接（规格 §14 那格点名）。后果：同一家不同版本上，本文的文本层结论可能整体不成立。
2. **SPI 没有"兼容模式"槽位**，今天已有 13 颗未建模参数在册（§14 那份名单）。最要紧的三颗：金仓 Oracle 兼容模式（一个连接串参数换掉整族方言假设，`src/QuickSql.Providers.Kingbase/KingbaseProvider.cs:19` 附近类注释自陈）、MySQL 的 `GuidFormat=BinaryUuid`（`CHAR(36)` 变 `BINARY(16)`，`MySqlTypes.cs:22`）、OceanBase 的"双模"（内建表上那颗 `oceanbase` 键只在连接建在 MySQL 模时成立）。
3. **三颗 UPSERT 位里两颗置着、一颗空着，而三句发射器都没有。** `Upsert_MergeInto` 在 SQL Server 那行（`src/QuickSql.Providers.SqlServer/SqlServerProvider.cs:46`）、`Upsert_OnConflict` 在 PG 那行（`PostgreSqlProviderBase.cs:83`）、`Upsert_OnDuplicate` 在枚举里有位而 MySQL 系的掩码没置它（`MySqlProviderBase.cs:155` 那两行里没有）。链式接口 `IInsert<T>`/`IUpdate<T>` 上没有任何对应成员，而 `.OnConflict(` 这一形在 `src/` 下一处都没有 —— 全仓库 `OnConflict` 一词只有三处命中：枚举那颗（`DbCapabilities.cs:18`）、PG 血统的掩码（`PostgreSqlProviderBase.cs:83`）、类注释里那一句（`PostgreSqlProviderBase.cs:18`）。位说的是"库有这个能力"，不是"本库会发这句"，两者之间今天没有闸门 —— 别按位选路。
4. **`Cte` 与 `WindowFunctions` 有声明、没接线**：掩码上 PG 系与 SQL Server 各置了这两位（`PostgreSqlProviderBase.cs:84`、`SqlServerProvider.cs:48`），而 `IQuery<T>` 上没有 CTE 入口、窗口函数也没有可点名的 API（`CountMethod.CountOverWindow` 那一档连自家 count 优化都没接：`src/QuickSql.Core/Query/QueryEngine.cs:446` 直接抛，那颗 switch 的注释自陈"没实现就别让它看起来能用"）。
5. **`MultiStatement` 有第二份声明了，但仍是一颗没人读来分派的位**：v0.37 第四笔之前只有 SQL Server 声明（`SqlServerProvider.cs:49`），今天 SQLite 也声明它（`SqliteProvider.cs`，七家第二份），而这一份背后第一次挂上真实发射——`SqliteCodeFirst.EmitCreateTable` 把建表＋建索引串成一句 ` ; `-分隔的文本，集成夹具 `tests/QuickSql.Tests.Integration/Database.cs:1134-1137` 那句 `ExecuteNonQuery` 把它整个发进活库跑过（这就是 §4.2 那三颗 `Live` 里 `MultiStatement` 那颗的来路）。但 `grep -rn "MultiStatement" src/` 命中的仍是声明与注释、**没有一处按这颗位走岔路**：走的是"这一族的发射器本来就把两句串起来"那条硬路径，不是"读了位才决定串不串"。所以它从"空判"改口成"有产出、仍零决策消费者"，性质与前几版一样——别按这颗位选路。写侧真正按方言分派的是 `ISqlDialect.SupportsJoinedWrites`。
6. **合并写档七族里只有两族有**（T-SQL 与 PG 系那七颗叶，DuckDb 也随血统同开）：`SupportsJoinedWrites` 为 false 的是 MySQL 系（`MySqlDialect.cs:77`）、Oracle 系（`OracleDialect.cs:57`）、虚谷（`XuguDialect.cs:116`）、GBase 8s（`Gbase8sDialect.cs:87`）、SQLite（`SqliteDialect.cs:136`）—— 那五族的 `SaveChanges` 多表更新整桶退回一句一实体；`EmitJoinedSources` 在这五家里都是抛（走到那里说明开关被绕过）。**SQLite 这一格第一次逼出"合并写不是一句子句而是两个出口"的读法**：它的 `UPDATE … FROM` 有、而 `DELETE` 的 `FROM` 与 `USING` 两形全无，于是同一颗 `SupportsJoinedWrites` 在这一家要问两遍而两遍答案不同，取 false 是因为只要有一半发不出就得整桶退回。
7. **反向工程读不全**：`SchemaColumn` 六格没有主键位（全仓库 `PRIMARY KEY` 的非注释命中一共七处、全在七份 CodeFirst 的建表清单里，目录侧 0 处）、注释七家一家都读不回（`SchemaColumn` 没有那一格，七份 `*SchemaReader` 一家都不例外；发射侧倒是只有三家发原生 `COMMENT ON COLUMN`：`PostgreSqlCodeFirst.cs:109`、`OracleCodeFirst.cs:139`、`XuguCodeFirst.cs:140`；另两家用别的形状 —— MySQL 系的列内子句 `COMMENT '…'`（`MySqlCodeFirst.cs:192`）、SQL Server 的扩展属性 `sys.sp_addextendedproperty`（`SqlServerCodeFirst.cs:62`），而 GBase 8s（`Gbase8sCodeFirst`）与 SQLite（`SqliteCodeFirst.cs:217`）的 `TryEmitColumnComment` 都直接返回 false —— SQLite 更硬，`COMMENT` 这个词在本族是语法错、根本没有列注释的落处）、虚谷那份目录查询里的 `IS_SERIAL` 与 `COMMENTS` 两列没有槽承接（`XuguSchemaReader.cs:68` 自陈"不是漏，是没有槽"）。所以别指望 CodeFirst + SchemaReader 做双向。

8. **派生行没有列契约**：派生表的规划入口今天在了（`QueryModel.DerivedFrom` → AST 的 `SourceDerived`，用法见 §3.1 那一段），COUNT 一族发得出 `SELECT COUNT(*) FROM (SELECT … GROUP BY …) AS [t0]`，规格 §5.12 的"总数与页一致性"随之走完。缺的是另一半：外层要引用内层的某一列（分组之后再 `SUM(t0.Total)`），就得先给派生行造一份列契约 —— 派生行的列名与类型今天只活在内层那份投影计划里，没有可引用的形状，所以非 COUNT 的一族仍当场抛。那一格的替代口有两个：`WithSql`（内层自己写、外层仍按契约选列）与 §3.1 的原生 SQL 逃生舱（整句自己写）。

9. **分表与分库都只有正查、没有反查**（§3.6 "库不猜"与 §3.7 "启动期给全清单"共同的另一面）：`SplitTables(范围)` 算的是"这次要问哪几张片"，而全仓库没有一颗 API 问库"这个模式里现有哪些片"；同理，`orm.Sources.ClusterNames`（`QuickSqlOrm.cs:165`）读得出**声明侧**的在册清单，读不出"这座实例上现有哪些库"。所以没选片时只能抛，不能"干脆扫全片"——现存几片只有库知道；同理一座集群的从库清单要在启动期给全。跨片两阶段合并（各片取回再在内存里合）同样既不实现也未裁，所以 §3.6 那三格拒绝今天没有替代口，只有 `CountMethod.SkipTotal` 那一档绕得开。**这一格落地之前的另一半说法已经作废**：`AdoExecutor` 过去在构造时把连接串存成字段（一个实例一条串），现在那一格换成了 `ConnectionSources`（按集群名查表，`AdoExecutor.cs:85` 那颗构造器收的是它而不是字符串），所以"一个实例一条串"不再是跨库的堵点——§3.7 就是它落地之后的形状。

10. **集群这一族今天没有的七格**（都在 `src/` 里可读，写在这里而不是等使用者撞）：
    - **原生 SQL 那一族只认 `main`**：`RawSql.Tag` 是 `new("raw", [])`（`src/QuickSql.Core/Execution/RawSql.cs:14`），`ExecutionTag` 的 `Cluster` 因此走缺省值（`AdoExecutor.cs:22`）——一句字符串 SQL 里没有"哪座库"这一颗可给。而它**照样按缺省档落 `main` 的从库**（同一条 `Resolve`，没有为它开后门），所以配了从库之后 `orm.Ado` 的读会去从库，那是设计而不是 bug；要到 `report` 那座去，今天只有两条路：另建一颗以 `report` 为 `ConnectionString` 的 orm，或用链式（链上的集群是从契约算出来的）。
    - **没有权重、没有延迟探测、没有存活检查**：从库按声明顺序轮询（§3.7），一颗死掉的从库会被一直轮到自己超时为止。本家不假装会挑"最不老的那一台"，因为承接库侧延迟的 SPI 槽位不存在（第 2 项那十三颗未建模参数之外的同一类空档）。
    - **`FromPrimary()`／`FromReplica()` 只在读链上**（`IQuery.cs:141`/`:150`）：`IInsert`/`IUpdate`/`IDelete` 没有这两颗，因为写恒主库、没有第二档可点。
    - **`Error` 与 `SlowQuery` 那两颗观察口手上没有 `Route`**（`QuickSqlAop.cs:117`/`:119`：`CommandErrorEvent(Exception, string Sql, long)`、`SlowCommandEvent(DialectSql, long, long)`）：只有 `Executed` 带（:92）。所以"哪条连接慢了/炸了"今天读不出来——慢日志与错误日志要按库分组统计，得自己从 SQL 文本或 `TableNames` 反推，那是这一族唯一一处"路由只在成功路径上"的不对称。
    - **一座集群一个 provider**：`ConnectionSources` 连 `IDbProvider` 都不持有（它的类注释自陈"同构多集群"），所以"主库 SQL Server、从库虚谷"那种异构拓扑要两颗 orm，而不是在 `ClusterConfig` 里多填一颗字段。
    - **实体声明与在册清单不在启动期对全量**：注册表是惰性的（实体第一次被 `Select`/`Set` 触及时才进册），所以 `[DataSource("report")]` 写错名字的症状是**第一次碰那张表**时抛 `集群 report 不在册（…）`，不是启动期。要启动期挡下来，今天能做的只有把每张实体碰一次（`_ = orm.Select<T>()`，示例 §13 就是这么写的）再读 `orm.Sources.ClusterNames`——而这一句挡得住名字对不上，挡不住"少声明了一座集群"（那种实体照样落在 `main` 上，静默且合法）。
    - **跨集群的一行由什么指认仍未裁**：`SaveChanges` 与事务那两格拒绝（§3.7 表里第三、四行）说的是"没有一条连接能同时管两座库"，而它的根因——会话的身份映射按主键记账、跨座的主键关系没有形状——与 §3.6 末尾分表/跟踪器互斥那一格是同一条，规格 §16.4 两处都留白。

### 4.4 跨库迁移时最容易踩的五格

| 坑 | 现场形状 | 出处（provider 类注释/代码） |
| --- | --- | --- |
| 折大写 | 同一模型在达梦/虚谷得到 `"CUSTOMERNAME"`，在金仓得到 `"CustomerName"`；不加引号的名字在 Oracle 血统里进库即大写，跨家建的表对不上 | `src/QuickSql.Abstractions/Metadata/IdentifierPolicy.cs:36`、`:83`；`src/QuickSql.Providers.OracleBase/OracleProviderBase.cs` 类注释第二段 |
| 别名与写目标 | GBase 8s / Oracle 血统的别名不带 `AS`（`FROM "QUICKSQLORDER" "T0"`），UPDATE 目标一律裸表名；照 T-SQL 的习惯读会以为漏写了 | `src/QuickSql.Providers.Gbase8s/Gbase8sDialect.cs:95`、`:97`；`OracleDialect.cs:62`、`:64` |
| 参数占位符 | GBase 8s 是位置式 `?` 且不接受带前缀参数名，只有 SQL Server 满足 `DriverAcceptsPrefixedParameterNames`；照别家写的 `@p0` 那一家不认 | `src/QuickSql.Providers.Gbase8s/Gbase8sDialect.cs:80`、`src/QuickSql.Abstractions/Sql/ISqlDialect.cs:28` |
| 单独 `OFFSET` 要哨兵 | 只有 `Skip` 时 MySQL 补 `18446744073709551615`、虚谷补 `2147483647`，两颗同形不同义；照别家文本抄会溢出或截断 | `golden/mysql/paging-skip-only.sql`、`golden/xugu/paging-skip-only.sql`；`src/QuickSql.Providers.Xugu/QuickSql.Providers.Xugu.csproj` 的 Description |
| 自增键与 `IDENTITY` 措辞 | Oracle 血统内部都分裂：达梦写 ` IDENTITY(1,1)` 而血统基类写 ` GENERATED BY DEFAULT AS IDENTITY`；跨家要键回填要先按 §3.2 那张表确认那一档开不开 | `src/QuickSql.Providers.Dameng/DamengCodeFirst.cs` 的 `IdentityClause` 覆写（`DamengProvider.cs:61` 挂点）；`src/QuickSql.Providers.OracleBase/OracleCodeFirst.cs:87` |

另外三条日常会撞：软删不是默认行为，要在 `orm.Behaviors.SoftDelete` 上按实体注册（§2.5，v0.38 第十七笔；没注册的那一家 `Delete` 就是物理删除，那句自陈在 `UpdateEngine.cs:433`）；按实体那一行现在有了（**v0.39.0-beta.29∶ 链式侧第一次收实体**——`orm.Update(实体)` ∶ `orm.Delete(实体)` 各交回既有的 `IUpdate` ∶ `IDelete`，软删改写 ∶ 全局过滤器 ∶ 分片换算那五件事一处都不必重写；列尺子恰与 `UpdateRange` 正对面：null 不进 `SET`，"把某一格真清成 NULL"只有链式这条做得到，`orm.Delete(实体)` 还支持复合主键），`DbContext.Update<T>(T)` 做的仍是"标成 Modified"；会话里的补载口 `db.Set<T>().Load(导航)` 引用与集合两档都接得上（级联那句"可达才删"就靠它，示例 §4.5/4.7/4.8 各跑得出）；「`IncludeMany` 缝上来的子实体不进会话建档」是老话——v0.39.0-beta.27 起接链那一刻就把"按编译期子型建档"那把委托交给节点，子实体在会话里建得档（§4"集合导航可以一层一层挂上去"那一格）。

## 5. provider 总表

能力位颗数取自各家掩码（§2.4 的声明处），assembly 取自内建表 `BuiltinDrivers.cs`。状态列除三格例外一律"离线文本层，未实测"（例外的口径见 §4.2）。

| 库种 | `DbKind` | `DriverKey` | 内建表 `assembly` | 血统 | 位 | 连接串示例（占位用 `<…>`） | 状态 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| SQL Server | `SqlServer` | `sqlserver` | `Microsoft.Data.SqlClient` | 独立 | 17 | `Server=<host>,1433;Database=<db>;User Id=<u>;Password=<p>;TrustServerCertificate=True`（Windows 认证换 `Integrated Security=True`） | 全部位有活库执行见证（两台实例，集成层自述；见 §4.2） |
| PostgreSQL | `PostgreSql` | `postgresql` | `Npgsql` | PG | 15 | `Host=<host>;Port=5432;Database=<db>;Username=<u>;Password=<p>` | 离线文本层，未实测 |
| 金仓 KingbaseES | `Kingbase` | `kingbase` | `Kdbndp` | PG | 15 | `Host=<host>;Port=54321;Database=<db>;Username=<u>;Password=<p>` | 离线文本层，未实测 |
| openGauss | `OpenGauss` | `opengauss` | `Npgsql` | PG | 15 | `Host=<host>;Port=5432;Database=<db>;Username=<u>;Password=<p>` | 离线文本层，未实测 |
| GaussDB | `GaussDb` | `gaussdb` | `Npgsql` | PG | 15 | `Host=<host>;Port=5432;Database=<db>;Username=<u>;Password=<p>` | 离线文本层，未实测 |
| 瀚高 HighGo | `Highgo` | `highgo` | `Npgsql` | PG | 15 | `Host=<host>;Port=5432;Database=<db>;Username=<u>;Password=<p>` | 离线文本层，未实测 |
| GBase 8c | `Gbase8c` | `gbase8c` | `Npgsql` | PG | 15 | `Host=<host>;Port=5432;Database=<db>;Username=<u>;Password=<p>` | 离线文本层，未实测 |
| DuckDB | `DuckDb` | `duckdb` | `DuckDB.NET.Data` | PG | 15 | `Data Source=<文件路径>`（内存库 `Data Source=:memory:`）；按【文档·DuckDB】 | 离线文本层，未实测（§4.1） |
| Oracle | `Oracle` | `oracle` | `Oracle.ManagedDataAccess` | Oracle | 6 | `Data Source=<host>:<port>/<service>;User Id=<u>;Password=<p>` | 离线文本层，未实测 |
| 达梦 | `Dameng` | `dameng` | `DmProvider` | Oracle | 6 | `Server=<host>;Port=5236;User ID=<u>;Password=<p>` | 离线文本层，未实测 |
| 神通 | `ShenTong` | `shentong` | `Oscar` | Oracle | 6 | `Server=<host>;Port=<port>;Database=<db>;User ID=<u>;Password=<p>` | 离线文本层，未实测 |
| MySQL | `MySql` | `mysql` | `MySqlConnector` | MySQL | 6 | `Server=<host>;Port=3306;Database=<db>;User ID=<u>;Password=<p>;Charset=utf8mb4` | 离线文本层，未实测 |
| TiDB | `TiDb` | `tidb` | `MySqlConnector` | MySQL | 6 | `Server=<host>;Port=4000;Database=<db>;User ID=<u>;Password=<p>` | 离线文本层，未实测 |
| OceanBase（MySQL 模） | `OceanBase` | `oceanbase` | `MySqlConnector` | MySQL | 6 | `Server=<host>;Port=2881;Database=<db>;User ID=<u>@<租户>;Password=<p>` | 离线文本层，未实测；模式未建模 |
| 虚谷 | `Xugu` | `xugu` | `XuguClient` | 独立 | 7 | `Server=<host>;Port=<port>;Database=<db>;User ID=<u>;Password=<p>` | 装载与连通实测；7 位中 4 位有活库执行见证（§4.2） |
| GBase 8s | `Gbase8s` | `gbase8s` | `GBase.DataDB` | 独立 | 4 | Informix 形：`Database=<db>;Host=<host>;Service=<port>;Protocol=onsoctcp;Server=<实例>;UID=<u>;PWD=<p>` | 离线文本层，未实测；档位按【文档·IBM】 |
| SQLite | `Sqlite` | `sqlite` | `Microsoft.Data.Sqlite` | 独立 | 5 | `Data Source=<文件路径>`（内存库 `Data Source=:memory:`） | 装载/连通实测（临时文件库）；5 位中 3 位有活库执行见证，且三颗都是 ORM 产出的语句跑过而非基线重放（§4.2） |

连接串那列的口径分三档，别混读：SQL Server ∶ MySQL 血统 ∶ PG 血统 ∶ SQLite ∶ 虚谷 五行是本仓集成层在真库上用过的那几形（出处在各家测试基建类注释 `MySql.cs` ∶ `PostgreSql.cs` ∶ `Xugu.cs` ∶ `Sqlite.cs`，键名与端口随现场调）；金仓按 PG 协议族复用、且 `Kdbndp` 在 §1.9 冒烟里 `Create` 解析成功（文本层，未连库）；Oracle ∶ 达梦 ∶ 神通 ∶ GBase 8s ∶ DuckDb 五行按**厂商文档**给形状，本仓从未在那几颗驱动上连通（§4.1，达梦那颗 NuGet 名对不上见 §1.4），第一次连以厂商为准。三个会咬人的细节：① 库落点由串上那颗键决定、不由 provider——SQL Server 上 `Database=` 与 `Initial Catalog=` 并存时驱动不报错、取后出现的那颗（本仓 `CatalogSql` 实测过），MySQL 血统的"模式"就是 `Database=` 那个库；② GBase 8s 用的是 Informix 那一族完全不同的键（`Protocol=onsoctcp` ∶ `Server=<实例名>` ∶ `Service=<端口>`），照 PG/MySQL 抄必然连不上；③ 占位串能产 SQL 不能连库（§3.0），`ToSql()` 那一路不碰连接，配了真串才走执行层。

`DbKind` 枚举的 17 个值今天全部有 provider、内建表行、掩码对应，无空档（原 `MariaDb` 那一个值已于 v0.37 第五笔撤下——本仓库不立 MariaDB 这一档，理由见设计文档 §14）。OceanBase 的 Oracle 模同样没有键（那颗叶子在规格里被裁了后置）。
