# TDengine 超级表(STABLE)落地(文本层·文档口径)

## Context

超级表是 TDengine provider 落笔时挂起的可选面,现在正式接。设计已按三路探查收敛:仓内两条先例(SkipIndex 声明流、`Emit(in InsertStatement)` 整句覆写点)给出全部接缝;TDengine 厂商事实已钉——`CREATE STABLE ... TAGS (...)` 语法、**部分标签列形式**(`USING stb (groupId) TAGS (2)`,官方推荐的 NULL 表达法)、TAGS 子句在列清单之后 VALUES 之前、已存在子表忽略 TAG 值、**STMT2 参数化路径对自动建表有明文行为缺口**(子表不存在时报错)、`INS_COLUMNS` 对稳定表含不含 TAG 列官方无明文、DECIMAL 不可作 TAG、变长 TAG 上限 16 382 字节。

**建模**:实体 = 稳定表。`[Table]` 名即稳定名(DDL + 查询面),子表名由声明期模板 `[TDengineStable(Subtable = "xxx_{device}")]` 在插入时用行内 TAG 值解析(用户未答命名问题,按推荐方案走)。TAG 列是正常契约列(查询/物化/写值全通),发射点由方言摘出进 TAGS 子句。

## 设计决策

1. **声明流照 SkipIndex 先例全形复制**:`[TDengineStable]`(类,`Subtable` 可选模板)+ `[TDengineTag]`(属性)进 `ProviderAttributes.cs`;`StableFacts` 声明载体;G1(EntityFactsBuilder/Writer/EntityModel/GeneratedEntityMetadata 追加可选参数)与 G2(ContractResolver)两侧各抄一份;`EntityContractFactory.Build` 收口换算(唯一一份):模板占位符必须命中已声明 TAG 成员(未知占位符抛)、TAG 成员必须已映射列。契约新增 `EntityContract.Stable : StableDeclaration?`(默认 null,含 `SubtableTemplate` + `TagColumns` 列表(member/col 名对,声明序=TAGS 序));**零 Core 写路径改动**。
2. **DDL**:`EmitCreateTable` 见 `contract.Stable` 发 `CREATE STABLE`(数据列)TAGS(TAG 列);首列 TIMESTAMP 校验改为"首颗**数据列**"(TAG 可声明在前),键形状校验同步对齐;TAG 门禁:DECIMAL 拒(厂商明文)、变长 TAG 上限 16 382;稳定实体带 `[SplitTable]` 拒(两套"一实体多表"不并用);`EmitAddColumn`/`EmitAlterColumnPrecision` 对稳定实体发 `ALTER STABLE ...`(能力位仍不声明);`TryEmitTableComment` 同形 `ALTER STABLE COMMENT`。
3. **插入**:`TDengineDialect` 覆写 `Emit(in InsertStatement)`(`InsertEngine.cs:717` 的 `TableRef` 自带契约,全信息可用)——`contract.Stable` 非空时:① 同批行全 TAG 值一致性校验(不一致抛,指路"一次一句一颗子表");② 子表名 = 模板填 TAG 值,经 `IdentifierPolicy` 引号(反引号转义形兜住特殊字符),占位符遇 NULL 拒;③ 发 `INSERT INTO 子表 USING 稳定 [TAGS 非空名清单] TAGS (内联值) (数据列) VALUES (数据值)`——TAG 值一律走 `Values.TryInlineLiteral` 内联(规避 STMT2 参数化自动建表的行为缺口;不可内联档如 byte[] 抛点名);④ 数据列清单照常带时间戳,参数照常位置形 `?`。普通实体(`Stable` 为空)→ `base.Emit`。普通表的既有 golden 全部原样回归。
4. **读面不动**:`TDengineSchemaReader` 照旧读 `INS_COLUMNS`(稳定名照查,返回什么引擎说了算——官方无明文);稳定实体的结构同步=尽力而为,TAG 列差异"判不了"记 notes,活库复核。
5. **子表脚本口**(宿主手工口,照 ClickHouse `EmitAddSkipIndex` 先例):`TDengineCodeFirst.EmitCreateSubtable(writer, contract, subtable, tagValues)` → `CREATE TABLE 子表 USING 稳定 TAGS (...)`;不在 SPI 上,测试强转直调。
6. **能力位不动**(稳定面是 provider 本地发射,无 Core 分派点)——台账零改;`DialectParityTests` 不进(与 ClickHouse/DuckDb 同判据)。

## 落地件

**Abstractions**
- `ProviderAttributes.cs`:+`TDengineStableAttribute`(AttributeTargets.Class,`Subtable` 属性)、`TDengineTagAttribute`(AttributeTargets.Property)、`StableFacts`(声明载体:`SubtableTemplate?` + 逐 TAG 成员名,init-only)
- `EntityContract.cs`:+`TagColumnContract(string MemberName, string ColumnName)` 与 `StableDeclaration(string? SubtableTemplate, IReadOnlyList<TagColumnContract> Tags)` record;`EntityContract` +`Stable` 槽(构造可选参数,默认 null)
- `EntityContractFactory.cs`:`Build` 追加可选参数;收口换算(唯一一份:模板占位符校验、TAG↔列绑定、声明序保持)
- `Schema/GeneratedEntityMetadata.cs`:构造追加可选参数

**Generators / Core**
- `EntityModel.cs` +`StableModel`;`EntityFactsBuilder.cs` +解析(类特性 + 属性特性,照 `SkipIndexesOf` 形);`EntityFactsWriter.cs` +写出;`ContractResolver.cs` +`ResolveStable`(反射侧同序);`GeneratedEntitySchemaRegistry.cs` 透传

**Provider**
- `TDengineCodeFirst.cs`:稳定 DDL 分岔 + 门禁 + `EmitCreateSubtable` 手工口
- `TDengineDialect.cs`:`Emit(in InsertStatement)` 覆写(上述 ③ 的全部语义)

**测试**
- `tests/QuickSql.Tests.Common/Model.cs`:+`StableMeter` 夹具(`[Table("QuickSqlStableMeter")]` + `[TDengineStable(Subtable = "QuickSqlStableMeter_{device}")]` + `[Key] DateTime Ts` 首成员 + `double Current` + `[TDengineTag] string Device` + `[TDengineTag] int GroupId`);`MetadataEquivalenceTests` +`InlineData(typeof(StableMeter))`(G1∶G2 对拍;契约构建是 provider 中性的,SqlServer 侧照常解析)
- `TDengineDialectTests.cs` +稳定面:golden 三份(`ddl-stable`/`insert-using-tags`/`insert-using-partial-tags`)+ 拒面断言(未知占位符、DECIMAL TAG、混 TAG 批、稳定+SplitTable、不可内联 TAG、子表脚本口直调)
- 首跑 `QUICKSQL_GOLDEN=update` 落盘再裸跑验证

**文档**
- `15-multi-database.md`:TDengine 行补稳定面一段(声明形/发射形/STMT2 缺口)
- `notes.md`:追加稳定面条目(声明流、TAG 内联的理由、结构同步"判不了"口径、G1 编译期诊断欠格、INS_COLUMNS 明文缺口)
- `quicksql-orm-design.md` §9.1 TDengine 行尾追加稳定面一段

## 不做(出界)

分表/稳定并用、`TBNAME` 伪列与 `SELECT TAGS ...` 查询面、`ALTER STABLE ADD/DROP TAG` 的同步面、多子表一条语句、`Into()` 式显式点名 API——全部记 notes 重开条件。能力位、台账、集成 `Target` 均不动。

## 验证

1. `dotnet build Quick.slnx` → 0 错 0 警
2. 离线册 `QUICKSQL_GOLDEN=update` 落新 golden → 裸跑全绿(既有 12 份 tdengine golden 逐字不变=普通表路径零回归)
3. `MetadataEquivalenceTests` 对 StableMeter 绿(G1∶G2 声明流同形);台账三条 Theory 照旧绿(位掩码未变)