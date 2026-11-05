# TDengine provider 落地(文本层·文档口径)

## Context

上一轮已确认 TDengine 是国产库清单里"没接会被人点名"的一颗,且驱动面全部核实过:官方 `TDengine.Connector`(v3.2.1)是全套 ADO.NET(`TDengineConnection : DbConnection`、`TDengineFactory : DbProviderFactory`、无静态 Instance),WebSocket 连接纯托管不落 taos.dll,`BeginDbTransaction` 当场 throw。SQL 面:第一列必须是 TIMESTAMP 且是主键、无事务、无 UPDATE 语句、DELETE 只认首列时间窗、无 identity、无二级索引、LIMIT/OFFSET 可用。

本笔交付**文本层文档口径**(照 ClickHouse 2026-10 落笔先例:整层厂商文档口径+未实测台账,活库探针另计)。照抄的锚点:单层 provider 形态 = `QuickSql.Providers.ClickHouse/` 的 10 文件/11 面;占位符 `?` 位置形 = Gbase8s(`DialectBase.cs:985-988` 已显式支持);拒面异常 = `QuickSqlException`。

## 设计决策

1. **时间戳主键不动共享面**:契约已有 `ColumnContract.IsPrimary` 全部格子。TDengine 侧只在 `TDengineCodeFirst.EmitCreateTable` 做校验:主键恰一颗、CLR 型映射 TIMESTAMP、且是契约首列,否则抛 QuickSqlException 指路;无主键实体放行(首列仍必须映射 TIMESTAMP——insert-only 日志表的合法形状,写路径要 PK 的地方内核本来就拒)。`EntityContractFactory.ApplyKeyConvention` 那条挂点留作以后的便捷约定,第一笔不碰。
2. **拒面(整格抛,照 ClickHouse 形状)**:UPDATE(3.x 无此语句)、DELETE(官方形状只认首列时间窗,本层不建模时间窗分析)、契约索引(无二级索引)、identity 列(无 identity)、列默认值(DefaultValue 在场即拒)、RETURNING/句尾回填。
3. **InsertOrUpdate 不接**:Upsert_* 位不声明,走 DialectBase 默认抛。TDengine"INSERT 同 ts 覆盖"确实是天然 upsert,但 3.x 重复时间戳的确切语义(整行覆盖/部分列)要活库钉死才敢接——notes.md 记为重开面。
4. **能力位只声明有 witness 的**:Paging_LimitOffset、Boolean_Type(原生 BOOL)、Schemas(db.table 限定名)、Comment_On_Column(建表内联 COMMENT),全部 Provenance=VendorDoc、PinnedLive=0。刻意不声明 Transactions/Returning/Upsert_*/Sequences/Alter_Column_Precision(保守,活库复核再翻)/Guid_*/Cte 等。
5. **类型映射**:bool→BOOL、sbyte→TINYINT、byte→TINYINT UNSIGNED、short/ushort/int/uint/long/ulong→(SMALL|INT|BIG)INT(UNSIGNED)、float/double→FLOAT/DOUBLE、decimal→DECIMAL(P,S)(3.3.6+,VendorDoc 注明)、DateTime→TIMESTAMP、char→NCHAR(1)、string→VARCHAR(n)(TDengine 的 VARCHAR 按字节计,宽松上界 chars×4 照 ClickHouse FitsBytes 尺)、枚举→INT;Guid/DateTimeOffset/DateOnly/TimeOnly/TimeSpan 无处可发拒。

## 落地件

**src(全部新建)**
- `sql/src/QuickSql.Abstractions/DbKind.cs`:`TDengine = 18`(末位追加,一行注释)
- `sql/src/QuickSql.Providers.TDengine/`(csproj 只引 QuickSql.Core,Description 按 ClickHouse 那份写法):
  - `TDengineProvider.cs` — `[QuickSqlProvider(DbKind.TDengine)]`、`DriverKey="tdengine"`、组装根、`CanCreateDatabase=false`(CREATE DATABASE 语句存在但"锚点库"路未实测,重开=活库)、`Bulk => null`
  - `TDengineDialect.cs` — `ParameterPrefix='?'`(照 Gbase8s)、EmitPaging(LIMIT/OFFSET)、EmitBooleanEquality、SupportsJoinedWrites=false、Emit(Update/Delete) 整格拒
  - `TDengineCodeFirst.cs` — 上述校验面 + `EmitCreateIndex` 整格拒 + 列 COMMENT 槽 + 无 DEFAULT 拒
  - `TDengineTypes.cs` / `TDengineValues.cs` / `TDengineExpressions.cs`(最小函数表:LENGTH/CHAR_LENGTH/CONCAT/UPPER/LOWER/TRIM/SUBSTRING/CAST,LikeEscapeCharacter='\\')/ `TDengineKeys.cs`(三布尔全 false + EmitReturning/EmitPostStatementKeyRead 虚口保真拒)/ `TDengineConcurrency.cs` / `TDengineChangeGuard.cs`(照 ClickHouse 同形)/ `TDengineSchemaReader.cs`(读 INFORMATION_SCHEMA.INS_COLUMNS,IsIdentity 恒 false,Indexes 留空)/ `TDengineConnectorFactory.cs`(CreateCommand 走 `connection.CreateCommand()`)
- `sql/src/QuickSql.Core/DriverLoading/BuiltinDrivers.cs`:`new("tdengine", "TDengine", null)` + 注释——**程序集名是 `TDengine` 不是包名 `TDengine.Connector`**(csproj 未设 AssemblyName 的坑)、TDengineFactory 无静态 Instance 走兜底扫描、`protocol=websocket` 纯托管 6041、整行未实测

**接线**
- 根 `Quick.slnx` 登记 `QuickSql.Providers.TDengine`(照 ClickHouse 行注释风格);`sql/tests/QuickSql.Tests.Common.csproj` 加对新 provider 的 ProjectReference(照 ClickHouse 现例)
- `sql/tests/QuickSql.Tests.Common/TDengine.cs`:离线组装根,照 `ClickHouse.cs` 同形(NoDriverFactory + OfflineConnectionString)

**测试(Dialects 册)**
- `sql/tests/QuickSql.Tests.Dialects/TDengineDialectTests.cs`:照 `ClickHouseDialectTests.cs` 形——注册面、能力位掩码、类型表逐 CLR 型、拒面(Raised 断言不走 golden)、槽位读数、CRUD/DDL golden;`const string Gold = "tdengine"`
- `sql/tests/QuickSql.Tests.Dialects/golden/tdengine/*.sql`:约 12 份(建表带时间戳主键/无主键、insert-plain/multi、select-entity、where-boolean、paging 三式、ddl-column-comment、ddl-qualified);首跑用 `QUICKSQL_GOLDEN=update` 落盘后复跑验证
- `CapabilityLedgerTests.cs`:加 "tdengine" 台账(逐位 Row、VendorDoc、`7, 0, 7` 同形钉数)+ 交集测试加 `Flags("tdengine")` 行 + 头注"第 N 笔"
- DialectParityTests 不进(ClickHouse/DuckDb 先例);BuiltinDriverTests 自动卷入;ConnectionStringCatalogTests 无需行(无 catalog 字段,同 ClickHouse)

**文档**
- `sql/docs/quicksql/15-multi-database.md`:Provider 总表一行(DbKind.TDengine | QuickSql.Providers.TDengine | TDengine.Connector | `protocol=websocket;host=…;port=6041;username=root;password=…;db=…`)+ 差异速查一行 + "诚实的能力口径"段加 TDengine 档
- `sql/docs/quicksql-orm-usage.md` §92 清单加一家
- `sql/docs/quicksql/notes.md`:按 Part 格式追加 TDengine 槽位条目(程序集名坑、protocol/db 键、DELETE 整格拒的理由、时间戳主键校验面、InsertOrUpdate 重开条件、超表/TAGS/stmt2 批量可选面、decimal 3.3.6+、VARCHAR 计字节疑点)
- `sql/docs/quicksql-orm-design.md` §9.1 加一行(落地清单逐格出处);根 README 如有已支持清单同步

## 不做(出界)

活库探针(docker taosd+taosAdapter,golden 回放、台账 VendorDoc→Live 翻位)——另计;Integration `Target` 枚举不加值(ClickHouse 先例:不预留);超表/TAGS/INTERVAL/stmt2 全留可选面。

## 验证

1. `dotnet build Quick.slnx` → 0 错 0 警(sql/Directory.Build.props 开着 TreatWarningsAsErrors)
2. 跑 `sql/tests/QuickSql.Tests.Dialects/bin/Debug/net10.0/QuickSql.Tests.Dialects.exe` 全绿(先 `QUICKSQL_GOLDEN=update` 落 golden,再裸跑验证逐字)
3. 台账三条 Theory(Every_declared_bit 互判/Live witness 钉数/Witness 文件存在)对 tdengine 行通过