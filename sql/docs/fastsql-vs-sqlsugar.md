# FastSql 与 SqlSugar 对照

> 这份文档只做一件事：把 FastSql 与 SqlSugar 放在**同一台真库、同一副表、同一批活**上比，并且把每个结论钉到可核的出处上。
>
> 设计论证在 `docs/fastsql-orm-design.md`（规格），日常用法在 `docs/fastsql-orm-usage.md`（用法）。本文不是第三份设计文档，也不改那两份的口径。

## 0. 口径：三件事分开说，不许混

这一节是全篇的地基。凡是后面出现的数字或结论，都能回到这里对上某一条口径；对不上的，本文一律写成"无法确定"。

| 面 | 口径 | 出处 |
| --- | --- | --- |
| 读码所据的 SqlSugar | 源码 **5.1.4.211**（打包描述写 **5.1.4.215**），本机快照 `E:\Resource\SqlSugar-master` | `Src/Asp.NetCore2/SqlSugar/SqlSugar.csproj:5` 写 `<Version>5.1.4.211</Version>`；同目录 `SqlSugarForCoreNoDrive.nuspec:5` 写 `5.1.4.215`。全文 `Src/…` 坐标以快照为根 |
| 实测所据的 SqlSugar | **5.1.4.218**，NuGet 包 `SqlSugarCoreNoDrive` | 量具输出第一行打出装载到的程序集版本 |
| 驱动 | 两家同走 **Microsoft.Data.SqlClient 7.1.0** | FastSql 经运行期清单 `sql/FastSqlDrivers.json` 解析（驱动自报 7.0.0.0），SqlSugar 由量具工程直接 `PackageReference` 同一颗 |
| 实例 | `192.168.0.102,1433` 上的 SQL Server，开发库 | 量具只打印 `DataSource`，不打印口令 |
| 表 | 四张对照表，**两两一副、DDL 逐字相同，都由裸 ADO 建** | 不走任何一家的 CodeFirst，避免"谁的建表更聪明"混进写入对照里 |

**坐标的写法**：FastSql 的 `src/…:行` 相对本仓库根 `E:\Resource\Fast\sql`；SqlSugar 的坐标一律相对 `E:\Resource\SqlSugar-master\Src\Asp.NetCore2\SqlSugar`（下文只写这个根以下的相对路径，例如 `Realization/SqlServer/SqlBuilder/SqlServerInsertBuilder.cs:16`，个别处以 `文件:行` 简写、按该根检索可定位）。

**版本口径有三个数**（211 源码 / 215 打包描述 / 218 实测到的包）。这不是笔误，是快照的真实样子：`SqlSugarCoreNoDrive` **不是独立工程**，而是同一颗 dll 的第二份打包描述——`SqlSugarForCoreNoDrive.nuspec:4-18` 的依赖只有 `Newtonsoft.Json` 13.0.2 与 `SystemCommon` 8.5.0，两行里没有任何驱动，而工程自己的 `SqlSugar.csproj:24-33` 反倒**直接引用**了 `Microsoft.Data.SqlClient` 5.2.1 等一串。也就是说本文读的源码是"带驱动"的那一份，实测跑的是"不带驱动"的那一份；两者是同一份 C# 编出来的不同包装，`Fastest.BulkCopy` 在 NoDrive 包上照样可用（本文量具就是这么跑出来的）。快照根目录**没有 `.git`**，只有 `.gitattributes`/`.gitignore`，所以"这份源码对应上游哪个 commit"**无法从源码确定**。

因此本文的分工是硬的：**所有 `Src/…:行` 只对 211 那份源码负责，所有毫秒数只对 218 那颗包负责。** 凡属"这个行为是包里没带、还是 211 之后改了"的问题，一律标注无法从源码确定。

## 1. 量具与重跑法

量具是一次性控制台工程，放在 **`sql/obj/sqlsugar-compare/`**（`obj/` 被 `sql/.gitignore` 挡住，不进版本库）：

- `Program.cs`：八节对照活；
- `fscompare.csproj`：`SqlSugarCoreNoDrive` 5.1.4.218 + `Microsoft.Data.SqlClient` 7.1.0 + `ProjectReference` 到工作树的 FastSql 三颗程序集，并挂 `FastSql.Generators` 为 Analyzer（否则消费者程序集里没有 G2 注册表，`FastSqlOrm.Create` 会报"DbKind.SqlServer 未在 ProviderRegistry 中登记"）；
- `run.txt`：最近一次跑出来的原始输出。

重跑（已验：从该目录 `dotnet build` 成功）：

```powershell
cd E:\Resource\Fast\sql\obj\sqlsugar-compare
dotnet build -v q --nologo          # 0 警告 0 错误
$env:FASTSQL_DRIVERS_CONFIG = "E:\Resource\Fast\sql\FastSqlDrivers.json"
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new()   # 不清这行，控制台见证会是 GBK
bin\Debug\net10.0\fscompare.exe
```

连接串的取法和集成层判据同一条：`FASTSQL_TEST_CONNECTION` 优先，其次 `sql/fastsql-test.connection.local.txt`（本地文件，gitignored）。量具全程不打印口令。

**计时纪律**（三处容易做假的地方，都堵上了）：

1. 每格取**三趟最小值**；
2. 计时段**只包住活本身**，`TRUNCATE`、建连接、建表都在计时段外——这条不是洁癖：早前一版把 `TRUNCATE` 算进去了，量出 FastSql L1 207 ms 对 SqlSugar 38 ms 的 5 倍差距，重排后真实差距是 39.92 对 36.81；
3. 表里的"句"指**一次命令文本**（一次 `ExecuteNonQuery`）。SqlSugar 的分块会把多段 `INSERT` 用 `;` 串进同一颗命令文本里，所以"1 句"**不等于**"1 条 INSERT"，本文在下面会写清到底几段。

## 2. 实测：八格原始读数

### 2.1 批量插入 2000 行 × 7 列

| 走法 | 墙钟 | 留痕读数 |
| --- | --- | --- |
| FastSql L1 装载通道 | **39.92 ms** | `CopyChannel ∶ 2000 行 ∶ 1 次递交 ∶ 库里 2000` |
| FastSql L3 多值语句 | 1748.66 ms | `MultiValue ∶ 7 块 ∶ 每块 315 行 ∶ 语句 7 句 ∶ 参数 12000 颗 ∶ SQL 文本累计 93263 字` |
| SqlSugar `Fastest<T>().BulkCopy` | **36.81 ms** | `OnLogExecuting 响 1 次 ∶ 参数 0 颗 ∶ SQL 文本累计 79 字` |
| SqlSugar `Insertable(list)` | 43.80 ms | `OnLogExecuting 响 1 次 ∶ 参数 0 颗 ∶ SQL 文本累计 328580 字` |
| 裸 `SqlBulkCopy`（地板） | 25.47 ms ／只算复制 **25.14 ms** | DataTable 造出来 2000 行 ∶ 库里 2000 |

SqlSugar 批量那一档写出去的形状（原文截断）：

```sql
INSERT [cmp_sugar]  ([CustomerName],[CustomerId],[Amount],[State],[CreatedAt],[Note])
   SELECT N'客户1' AS [CustomerName],N'7' AS [CustomerId],1.25 AS [Amount],N'1' AS [State],'2026-10-05 01:02:03.000' AS [CreatedAt],N'cmp' AS [Note]
   UNION ALL
   SELECT N'客户2' ...
```

四个读数要合起来读：

- **装载通道这一档两边打平**（39.92 对 36.81，差 3.1 ms）。地板在 25 ms，所以两家各自都有约 11–15 ms 的"把自己那层套上去"的开销，这一层两家同量级。
- **FastSql 慢的是 L3，不是 L1。** 这一格 FastSql 自己就给出了阶梯的价值：同一批活 L1 39.92 ms、L3 1748.66 ms，差 43 倍。
- **SqlSugar 的批量默认根本没走参数。** 参数 0 颗、文本 32.8 万字：它把值全部内联成字面量，2000 行拼成 10 段 `INSERT ... SELECT ... UNION ALL`（每段 200 行，按 `SqlServerInsertBuilder.cs:34-42` 的默认一档推）串进一颗命令文本。**一次往返**送出去。它另有 `.UseParameter()` 那一条口（§3.1），本文量的是默认这一条。这条读数直接翻掉一句旧说法，见 §5.1。
- **文本量是另一笔账**：同一批活 SqlSugar 写出 328,580 字，FastSql L3 写出 93,263 字加 12,000 颗参数。内联换到的是"少一次一次的参数往返"，付出的是"服务端要为 33 万字的语句本文做解析，且这颗文本每次数据不同就不同、计划缓存基本复用不上"。本文没有量这一笔的后续代价（见 §6）。

### 2.2 逐条插入 500 行

| 走法 | 墙钟 |
| --- | --- |
| FastSql 逐条（`SingleRow`） | 365.24 ms |
| SqlSugar `Insertable` 单条循环 | 358.90 ms |

**持平**（0.74 ms/行 对 0.72 ms/行）。这一格两边都走参数化单条语句，量的其实是"一次往返多少钱"，谁的内核都省不掉。

### 2.3 宽表 30 列 × 500 行

| 走法 | 墙钟 | 读数 |
| --- | --- | --- |
| FastSql L3 多值 | **1898.09 ms** | 8 块 ∶ 每块 65 行 ∶ 每块参数 1885 颗 ∶ 库里 500 |
| SqlSugar `Insertable(list)` | **76.56 ms** | 1 句（内含 **5** 段 INSERT）∶ 每段 100 行 ∶ 参数 0 颗 ∶ 文本累计 242,442 字 |
| SqlSugar 走装载通道 | 见 §2.1 | 同一件事它也有 L1 那一档，本文未在宽表上重跑 |

**这是全篇差距最大的一格：24.8 倍。** FastSql 的块边界是按**参数上限**算的（29 列 ⇒ 每块 65 行 ⇒ 1885 颗，压在 2100 以下，8 块 8 次往返），SqlSugar 的块边界是按**固定行数**算的（30 列不触发 `>30`、但触发 `>20` ⇒ 每段 100 行 ⇒ 5 段），且因为内联，它压根不受参数上限约束——段与段之间用 `;` 串在同一颗命令文本里，**5 段只有 1 次往返**。

（段数这一条是**从源码推的**，不是量出来的：留痕只给到"一次命令文本"，段数要按 `SqlServerInsertBuilder.cs:34-42` 那三档算。要把它变成实测，得在 `OnLogExecuting` 里数 `;` 的颗数——本文没做，所以这里明写是推的。）

一格之内讲不清因果，但两格合起来有个很直的读数：把 §2.1 和这一格除以参数颗数，1748.66/12000 = **0.146 ms/颗**，1898.09/15080 = **0.126 ms/颗**——**耗时几乎只跟着参数颗数走，不跟着行数走**（行数差 4 倍，颗数接近，时间接近）。本文把这一条标为**推出来的读数，不是量出来的因果**：要钉死它，需要一趟"固定行数、只扫参数颗数"的对照，那一趟还没跑。

### 2.4 分页（表上 200 行，取第 1 页 20 行）

| 走法 | 语句数 | 读数 |
| --- | --- | --- |
| FastSql `ToPagedAsync(Auto)` | **1** | Items 20 ∶ Total **null** ∶ HasNext True |
| FastSql `ToPagedAsync(CountStar)` | 2 | Total 200 |
| SqlSugar `ToPageList(1,20,ref total)` | **2** | Items 20 ∶ Total 200 |
| SqlSugar `ToPageList(1,20)` 不要总数 | 1 | Items 20 |

这一格量的不是快慢而是**默认替你做什么**。FastSql 默认那一档在**首页**只发一条语句（`QueryEngine.cs:1225` 的 `size + 1`，多取一行探 `HasNext`），代价是 `Total` 为空；到**第二页起 Auto 自己换成 `CountStar`**（`QueryEngine.cs:1264-1271` 的 `Resolve`），也就是两条——这条换档本文没实测，只报源码。要总数必须显式换 `CountMethod`（`src/FastSql.Abstractions/Query/IQuery.cs:19` 的 `PagedOptions` 默认 `CountMethod.Auto`）；`CountOverWindow`（单往返带总数）与 `SeparateTransaction`（同连接同事务两条）**今天直接抛异常**，不是静默降级。SqlSugar 的 `ref total` 是"要总数就固定两条语句"——`QueryableExecuteSql.cs:509` 那一档写得很白：先 `this.Clone().Count()` 再 `this.Clone().ToDataTablePage(...)`，两次往返、两次 `Clone()`。两边各有对面拿不到的东西：SqlSugar 拿不到"不写 `ref total` 之外还能选单往返带总数"，FastSql 拿不到默认给的准确总数。

SqlSugar 不计数那一档也是单条。**首页这一格持平；第二页起两家都是两条，也持平。**

### 2.5 读 200 行 × 7 列并物化

FastSql `ToListAsync` **1.59 ms** ∶ SqlSugar `ToList` **1.59 ms**。三趟取最小，量到同一位。**读侧这一格没有差距可言。**

### 2.6 一次批量调用里留痕响几次

| 走法 | 读数 |
| --- | --- |
| FastSql L1 装载通道 | `Executed` 响 1 次（这一档不产 SQL 文本，交回的是 `BulkReport`） |
| FastSql L3 多值 | `Executed` 响 2 次（两块） |
| SqlSugar `Insertable(list)` 500 行 | `OnLogExecuting` 响 **1 次**（内含多段 INSERT） |
| SqlSugar `Fastest.BulkCopy` 500 行 | `OnLogExecuting` 响 1 次，文本是 `Begin BulkCopy name:[cmp_sugar] ,count: 500,current time: …` |

SqlSugar 那条"伪 SQL"是设计如此，不是日志坏了：`FastestProvider.cs:548` 在装载前发一句 `Begin {title} name:… ,count: N` 给 `OnLogExecuting`，紧接着 `:545` 把 `Ado.IsEnableLogEvent` **按灭**，装载结束再在 `:530` 恢复；收尾那一句走 `OnLogExecuted`（`:533`）。所以装载期间真实的驱动侧动作在它的 SQL 日志流里是**看不见**的。FastSql 走的是另一条路：L1 那一档根本不产 SQL，产的是结构化的 `BulkReport`（`src/FastSql.Abstractions/Providers/Bulk.cs:46-54`：`Requested` 与 `Method` 分开记"要哪一层"和"实际走了哪一层"，再加 `RowCount`、`RoundTrips`、`ChunkSize`、`Degraded`、`Reason` 五颗）。§2.1 那几行读数里的"7 块 ∶ 每块 315 行"来自与它同源的 `InsertPlan`（`Chunks`/`RowsPerStatement`/`ParameterBudget`），量具把两张单一起打了。

要说清的一件事：`OnLogExecuting` 这一条通道在 SqlSugar 里**同时装 SQL 和装载动作的叙述文本**，接钩子做审计的人会在同一支流里看到两种东西；FastSql 的 `Aop.Executed` 只装真实 SQL，装载另走 `BulkReport`。这是形状之差，不是对错之差。

### 2.7 值里带单引号的一行

| 面 | FastSql | SqlSugar |
| --- | --- | --- |
| 单条（参数化） | 正确落库，表还在 | 正确落库，表还在；写出去的是 `INSERT INTO [cmp_sugar] (…) VALUES (@CustomerName,@CustomerId,…)` |
| 批量（内联字面量） | —（走参数，见 §2.1） | 正确落库，表还在；写出去的是 `… SELECT N'O''Brien 客户' … N'''; DROP TABLE cmp_fast; --' AS [Note] …` |

两家的转义都成立，`'; DROP TABLE …'` 这类值都原样存下来了，四张对照表跑完都还在（量具最后按自己的规矩把四张删掉）。SqlSugar 的批量内联靠的是 `InsertBuilder.cs:263` 的 `value.ToString().ToSqlFilter()` 加 `N'…'` 前缀，加上 `GetDbColumn` 那一串分岔（`InsertBuilder.cs:291`）——它不是"随手拼字符串"，本文不打算把它写成 SQL 注入洞。**但它确实把值的正确性压在转义函数一个人身上**，而 FastSql 的批量把这件事压在参数绑定上。这是取舍，两笔代价不同：一面是"转义函数出错就是全库的事"，一面是"颗数多了就慢"（§2.3 那 24.8 倍就是这一面的账单）。

顺带一条实测到的形状细节：SqlSugar 的内联把**整型也写成 `N'7'`**——`InsertBuilder.cs:218` 的 `FormatValue` 只列了 DateTime/byte[]/enum/bool/string/DateTimeOffset/float/decimal，其余全落 `:279` 那条兜底分支 `N + "'" + value.ToString() + "'"`。列上写的是 `N'7' AS [CustomerId]`，靠服务端隐式转换吃进去，同时把 `nvarchar` 塞进整型列的比较面。本文只报读数，不替它算代价。

### 2.8 装载通道的地板

裸 `SqlBulkCopy`（自己造 DataTable，不经任何一家）：**25.47 ms**；把造 DataTable 的时间摘出去：**25.14 ms**。

这一格存在的意义只有一个：给 §2.1 那两个 30+ ms 提供一个参照。**两家离地板都不远（11–15 ms），所以装载通道这一档谁也没有把驱动的钱花掉第二遍。**

## 3. 读码对照：六面

下面每一格都是"两边各自在源码里怎么做的"，配 `文件:行`。FastSql 的坐标对工作树现状负责；SqlSugar 的坐标对 §0 那份 211 快照负责。

### 3.1 批量写入的分块

| 面 | FastSql | SqlSugar |
| --- | --- | --- |
| 分块依据（默认那一档） | 参数上限（每块行数 = 上限 ÷ 列数，压在 2100 以下） | **固定行数**：默认 200，列数 >20 ⇒ 100，>30 ⇒ 50 |
| 出处 | `InsertEngine` 的批量决策段（§12.1 阶梯） | `SqlServerInsertBuilder.cs:34-42`（与基类 `InsertBuilder.cs:181-189` 同一套数） |
| 有没有第二条写法 | 阶梯今天只有三档可选加一档自动：`BulkMethod { Auto, CopyChannel, MultiValue, SingleRow }`（`src/FastSql.Abstractions/Providers/Bulk.cs:14,17,20,23`），**L2 数组绑定与 L4 临时表在这一家没有实现**（`SqlServerBulk.cs:8-9` 明写），所以枚举里根本没有那两个成员 | 有，而且是**显式 opt-in**：`Insertable(list).UseParameter()`（口在 `InsertableProvider.cs:519`）换到参数化那一族，那颗口里**才**有 2100 的处理——`ParameterInsertable.cs:171-187`：`pageSize * count > 2100` 连压三档 50⇒20⇒10 |
| 值的写法 | 参数绑定 | 默认那一条**内联字面量**，例外才回落到参数（`InsertBuilder.cs:291` 起：服务器时间、坏小数、`SqlParameterDbType` 转换器） |
| 多段的拼法 | 一块一句，**分次往返** | 每段独立 `INSERT`，段间 `;\r\n`（`SqlServerInsertBuilder.cs:65`），**全串进一颗命令文本、一次往返**；SqlServer 末尾还追一句 `select SCOPE_IDENTITY();`（`:70`） |
| 要回主键时 | 与阶梯无关，走 returning 那一族 | `IsNoPage && IsReturnPkList` 时把 `pageSize` 抬成全量、**不再分块**（`:43-46`）——2000 行要回 PK 就是一颗 2000 段的大文本 |
| 身份列 | L1 需显式 `KeepIdentity`，列类型不过闸门就整批回落 | `OffIdentity`：整句前后包 `SET IDENTITY_INSERT <表> ON/OFF`（`:74-78`）；`.PageSize(n)` 另有一档，默认 1000 且**外层自开事务**（`InsertablePage.cs:27,29-52`） |

一句话概括这一面：**SqlSugar 的 L3 是"一次往返送一大坨本文"，FastSql 的 L3 是"几次往返送参数"。** 前者不怕列多，后者怕（§2.3）。

### 3.2 装载通道

| 面 | FastSql | SqlSugar |
| --- | --- | --- |
| 走的是哪颗实现 | 反射解析驱动里的 `SqlBulkCopy` 类型（`SqlServerBulkCopy.cs:161`，先试 `Microsoft.Data.SqlClient` 再试 `System.Data.SqlClient`） | **两条路**：`Fastest<T>().BulkCopy` 走 `SqlServerFastBuilder.ExecuteBulkCopyAsync`（`SqlServerFastBuilder.cs:19-35`，builder 由类型名字符串反射装配，`Private.cs:62-65`）；老的 `Insertable(...).ExecuteBulkCopy` 走 `SqlServerBlukCopy.cs`。两条都**编译期直接** `new SqlBulkCopy(...)`（`using Microsoft.Data.SqlClient` 在 `SqlServerBlukCopy.cs:4`） |
| 为什么形状不同 | provider 层零编译期驱动引用（§3 的分层规矩），所以反射 + 缓存是代价 | 主库工程直接引驱动（`SqlSugar.csproj:32`，5.2.1）；NoDrive 只是同一颗 dll 换包装（§0），所以它同样带编译期引用——实测可用就是这一条的旁证 |
| 列对齐 / 表形状从哪来 | 自己带 `ColumnMappings` 按名字映射（`SqlServerBulkCopy.cs:64` 那条注释写明了不给映射时按序号对齐的风险） | **向库里要一份 schema**：实体列表那一档是 `Where(it => false).Select("*").ToDataTable()` 拿空表壳，并按 CLR 类型缓存（`Private.cs:73-85`）；DataTable 入参那一档每次 `select top 0 * from <表>`（`FastestProvider.cs:507-520` → `SqlServerBlukCopy.cs:82`） |
| 事务内 | 由 `BulkConnection` 把连接与事务一起交给通道 | `Ado.Transaction != null` 时 `SqlBulkCopyOptions.CheckConstraints`（`SqlServerBlukCopy.cs:112`、`SqlServerFastBuilder.cs:59`）；无事务时默认构造（`:108`、`:47`） |
| 身份列 | 显式 `KeepIdentity`，且列类型不过闸门就整批回落 | `Fastest` 认 `OffIdentity()`：开了就传 `SqlBulkCopyOptions.KeepIdentity`（`SqlServerFastBuilder.cs:41-43`，事务内 `:52-54`）；老那条 `ExecuteBulkCopy` 路径**完全没有** KeepIdentity（`SqlServerBlukCopy.cs:103-120`） |
| 可配的开关 | `CheckConstraints \| FireTriggers` 两颗都从**驱动自己的枚举**上读（`SqlServerBulk.cs:31` 的 `Describe`，读枚举那一段在 `SqlServerBulkCopy.cs:210`） | 没有可配面。而且有**源码上的一条互斥**：事务内开 `OffIdentity()` 那一支只传 `SqlBulkCopyOptions.KeepIdentity`（`SqlServerFastBuilder.cs:54`），同一支里 `CheckConstraints`（`:59`）就没了——身份列与约束检查二选一。本文没在事务里跑过这条，所以它是读码结论，不是实测结论 |
| 超时 | 驱动的 `BulkCopyTimeout`，取不到就用请求里的 `TimeoutSeconds` | `copy.BulkCopyTimeout = Context.Ado.CommandTimeOut`（`SqlServerBlukCopy.cs:118`） |
| 结果 | 结构化 `BulkReport` | `int`（且是 `DbColumnInfoList.Count`，即"送了几行"，不是"库里落了几行"，`SqlServerBlukCopy.cs:40`） |
| 早退 | 空批不进通道 | `DbColumnInfoList` 空 ⇒ 直接返回 0（`:21`） |
| sync-over-async | 两条口各自到底 | 同步 `BulkCopy(List<T>)` 是 `BulkCopyAsync(...).ConfigureAwait(true).GetAwaiter().GetResult()`（`FastestProvider.cs:59`）；`ExecuteBulkCopyAsync` 在 DataTable 那一支里**同步**调 `WriteToServer()`（`SqlServerBlukCopy.cs:49`） |
| 分块 | 由阶梯一次性决定，`BulkReport` 里交回 | `Fastest.Size` 决定，`Size > 0` 时按 Size 切成多次复制（`FastestProvider.cs:63-74`） |
| 附加行为 | 无 | `DateTime.MinValue` 夹到 `1753-01-01`（`SqlServerBlukCopy.cs:133`） |

这一面的实质差别是**那一次"要 schema"的往返放在哪**：SqlSugar 实体列表那一档把它缓存住了（每个 CLR 类型一次），FastSql 自己带映射表所以一次都不发。这也决定了 §2.1 那个 36.81 ms 里**不含**取壳查询——三趟取最小把首趟的一次性开销挑掉了，而 FastSql 的首次反射装载同样被挑掉，两边同待遇（见 §1 计时纪律）。

### 3.3 事务

| 面 | FastSql | SqlSugar |
| --- | --- | --- |
| 嵌套 | `SAVEPOINT` 两条路：方言报驱动支持就交出去，否则自己发文本（`AdoExecutor.cs:309-314`，`fastsql_sp_<n>` 命名）；事务对象带 `IsSavepoint`（`IFastSqlTransaction.cs:21`） | `BeginTran` 里 `if (this.Transaction == null)` 才真开（`AdoProvider.cs:265-270`）⇒ **内层嵌套是静默空转**，而内层 `CommitTran` 会真的提交外层那条（`:308`）。整棵 `Src/Asp.NetCore2` 检索 `SAVEPOINT`/`Savepoint` **命中 0**，保存点这一档不存在 |
| 传播语义 | 显式枚举：`Requires`（并入环境事务）/ `RequiresNew`（挂起、新开、结束后恢复）（`TransactionPropagation.cs:4-12`） | 没有这一档；`SqlSugarClient.BeginTran()` 是"对所有配置的连接各开一条"（`SqlSugarClient.cs:1005-1009` 的 `AllClientEach`）——跨库不是分布式事务，是各管各的 |
| 失败形状 | 异常外抛（回滚后） | `UseTran(Action)` 里 catch ⇒ `RollbackTran()` ⇒ 返回 `DbResult{IsSuccess=false}`，**异常不外抛**，回调可选（`AdoProvider.cs:343-366`） |
| `using` 那一档 | 由事务对象自己的语义定 | `Ado.UseTran()` 返回的 `SqlSugarTransactionAdo.Dispose()` 是**无条件** `RollbackTran()`（`Entities/SqlSguarTransaction.cs:48-51`）：`using` 走完不显式 `CommitTran()` 就什么都没留下 |
| 命名事务 | — | `BeginTran(string transactionName)` 标着 "Only SqlServer"（`AdoProvider.cs:334-335`，实现在 `SqlServerProvider.cs:46-60`）；那是命名事务，**不是保存点** |
| 工作单元 | 会话 + `SaveChanges` 那一族 | `SugarUnitOfWork.cs` 存在，同样没有保存点 |

这一面对 FastSql 最有利，而且有利得很具体：**SqlSugar 的嵌套在源码里就是不安全的**（内层提交 = 外层真提交，内层回滚 = 外层全没了），而 `UseTran` 吞异常这一条会让"以为出事了所以都回滚了"的调用方拿到一个 `IsSuccess=false` 的对象——不看返回值就当无事发生。

### 3.4 分页

| 面 | FastSql | SqlSugar |
| --- | --- | --- |
| 限行写法 | 单条 `TOP n+1` 探 `HasNext`（**只有首页**：Auto 在第二页起换成 `CountStar`，两条语句，`QueryEngine.cs:1264-1271`） | **三档**：只有 `Take` ⇒ `TOP n`；`Take==1` 且首页 ⇒ 也走 `TOP`；一旦带 `Skip`（第 2 页起，**以及 `ToPageList(1,20)`——它的 Skip 是 0 而不是 null**）⇒ `ROW_NUMBER() OVER(…) AS RowIndex` 外壳再 `BETWEEN`（`SqlServerQueryBuilder.cs:60-77`，外壳模板 `QueryBuilder.cs:162-168`） |
| 没有 ORDER BY 时 | 由方言决定，不替你补一个 | **注入 `ORDER BY GetDate()`**（`SqlServerQueryBuilder.cs:60` 一行写死）：窗口函数要有排序，它给了个每行都不同的表达式。翻页在并发写压下不保证不漏不重，本文**没实测**这一条，只报源码写法 |
| OFFSET/FETCH | — | 属另一套口 `ToOffsetPage`／`ToOffsetDataTablePage`（`QueryableHelper.cs:1740-1744` 拼 ` OFFSET … ROWS FETCH NEXT …`，调用点 `QueryableExecuteSql.cs:477,491,497`），**不是 `ToPageList` 走的路** |
| 默认条数 | 由 `PagedOptions` 选（`IQuery.cs:19`） | 由调用形态选（写 `ref total` 就要两条） |
| 计数 | 显式换 `CountMethod` 才发第二句；`StripOrderingForCount` 默认去掉排序 | `Clone().Count()` 再 `Clone().ToDataTablePage()`（`QueryableExecuteSql.cs:509-515`）；计数模板是 `SELECT COUNT(1) FROM ({0}) CountTable`（`QueryBuilder.cs:634-641`），本文实测那句被化简成了直查表 |

两家在这一面**能力对等，默认相反**：FastSql 默认省钱（一条语句、无总数），SqlSugar 默认给全（两条语句、有总数）。形状上各有一笔本文量不到的账：FastSql 的 `TOP n+1` 只取一行多余数据，SqlSugar 的第 2 页起要把整行集过了窗口函数再剥壳。

### 3.5 留痕 / AOP

| 面 | FastSql | SqlSugar |
| --- | --- | --- |
| 事前钩子 | `Aop.Executing` 声明在 `src/FastSql.Abstractions/Aop/FastSqlAop.cs:13`，**`src/` 下派发点 0 处**（本文用 grep 复核过）——挂在规格 §10.1 的名单上，是个空挂 | `OnLogExecuting` 是 `Action<string, SugarParameter[]>`（`ConnectionConfig.cs:91`）：返回 void，**不能取消、不能回退**，但拿到的参数数组是引用，改值得到的是改后的执行。每语句一次（`AdoProvider.cs:1556-1593` 的 `ExecuteBefore`→`LogEventStarting`，调用点散在 `:487,528,562,600,638,682,721`） |
| 事前改写 | 没有这一档（上一条既然没派发，就什么都做不了） | 另有专口 `OnExecutingChangeSql`（`ConnectionConfig.cs:93`，`Func<SQL, 参数, KeyValuePair<SQL, 参数>>`；接线点 `AdoProvider.cs:82`）——**能换 SQL 也能换参数** |
| 事后钩子 | `Aop.Executed` 只装真实 SQL（`ExecutedEvent` 带 `DialectSql` + 耗时） | `OnLogExecuted`（`ConnectionConfig.cs:92`）与装载收尾共用一条流（`FastestProvider.cs:533`） |
| 装载期能否看见 | 不产 SQL，产 `BulkReport` | **看不见**：装载期间日志被按灭（`FastestProvider.cs:545`），前后各留一句伪 SQL（`:548`、`:533`） |
| 命中缓存时 | 与 AOP 无关（本家没有查询缓存层） | **整条 Ado 都不走**，所以 `OnLogExecuting`/`OnLogExecuted` 一律不响（`CacheSchemeMain.cs:11-15` 把执行包在 `cacheService.GetOrCreate` 里） |
| 慢查询 | `Aop.SlowQuery`（阈值在 `FastSqlOrmOptions.SqlLogging`） | `OnLogExecuted` 自己算；另有 `OnDiffLogEvent`（`ConnectionConfig.cs:89`）记差异 |
| 错误 | `Aop.Error` | `OnError`（`ConnectionConfig.cs:90`，派发 `AdoProvider.cs:1749-1753`） |
| 数据面钩子 | 全局过滤器（`FastSql.Core/Query/Filters/FilterRegistry.cs`） | `DataExecuting` / `DataChangesExecuted` / `DataExecuted`（`ConnectionConfig.cs:94+`），装载路径也读它（`FastestProvider.cs:550-552`） |

FastSql 这一面的账很干脆：**"事前拦截"今天不存在**——`Executing` 是空挂（§5.2），对面能改写 SQL 的那颗 `OnExecutingChangeSql` 本家没有对应物。用法文档 §2.5 把 `Executing` 与 `Executed` 并排列进"观察"类而没标注它没有派发点，这条本文在 §5.2 补上。

### 3.6 实体注解与建表

| 面 | FastSql | SqlSugar |
| --- | --- | --- |
| 表/列名 | `TableAttribute` / `ColumnAttribute(name?)`（`FastSqlAttributes.cs:6,25`，列名可缺省）+ 流式 `ModelConfiguration`（`Metadata/Fluent/ModelConfiguration.cs`） | `[SugarTable]`（`Entities/Mapping/SugarMappingAttribute.cs:10`）+ `[SugarColumn(ColumnName=…)]`（同文件 `:42`）；无注解时列名取属性名（`EntityMaintenance.cs:336`），有注解但 `ColumnName` 空也回退属性名（`:343`） |
| 主键/自增的默认 | **约定**：`Id` 直接当主键自增，不用标注 | **只认注解**：`IsPrimarykey`/`IsIdentity` 仅从 `SugarColumn` 落（`EntityMaintenance.cs:344-345`），全仓检索没有 "Id 名 ⇒ 主键" 的兜底 |
| 这条差价的实证 | 量具的宽表把注解去掉照样跑 | 同一份 DDL 把注解去掉，SqlSugar 立刻报 `IDENTITY_INSERT 设置为 OFF`（本文量具第一次翻车就是这里，补上 `[SugarColumn(IsPrimaryKey=true, IsIdentity=true)]` 才过） |
| 建表 | provider 发 DDL（`SqlServerCodeFirst.cs:17` 起，索引在 T-SQL 里独立成语句、用同批次 `;` 串），比对与执行在 `Schema/SchemaSyncer.cs` | `InitTables` 判存在走 `DbMaintenance.IsAnyTable(name,false)`（`CodeFirstProvider.cs:316`），SqlServer 那一档是**先查 `select object_id(N'…')`**（`SqlServerDbMaintenance.cs:521`），**不用 `IF NOT EXISTS`**（全仓只有 Sqlite 用）；无长度的字符串落 `varchar(max)`，`MoreSettings.SqlServerCodeFirstNvarchar` 为真才换 `nvarchar(max)`（这颗开关在 `:499`、`:727` 两处读）；实体名解析见 `EntityMaintenance.cs:100-135` |

建表这一面本文**没做实测**（§1 明写四张表都由裸 ADO 建，就是为了不把建表聪明度混进写入对照）。所以只报形状，不排名次。

## 4. 名次：分四档说

| 档 | 内容 |
| --- | --- |
| **FastSql 明显在前** | 事务面：保存点存在且两条路（`AdoExecutor.cs:309-314`）+ 显式传播 `Requires`/`RequiresNew` + 失败外抛，对面**全树检索 `SAVEPOINT` 命中 0**、嵌套静默（`AdoProvider.cs:265-270`）、`UseTran` 吞异常（`:343-366`）、`using` 那一档 `Dispose` 无条件回滚（`SqlSguarTransaction.cs:48-51`）；装载的决策可交回（`BulkReport` vs 一个 `int`）；装载开关彼此独立（`CheckConstraints`/`FireTriggers`/`KeepIdentity` 三颗分开拨，对面事务内是 `KeepIdentity` **或** `CheckConstraints` 二选一，`SqlServerFastBuilder.cs:52-59`）；约定优于配置的实体映射（注解更少，§3.6）；AOT 口径（provider 零编译期驱动引用，见 §3.2 的代价与理由） |
| **持平** | 装载通道墙钟（39.92 对 36.81）；逐条写入（365 对 359）；读侧物化（1.59 对 1.59）；转义正确性；分页能力面 |
| **SqlSugar 明显在前** | 参数化多值批量写入（§2.3 的 24.8 倍；§2.1 的 40 倍）；把多段塞进一次往返；**事前改写 SQL 的口**（`OnExecutingChangeSql`，对面 §3.5 那格；FastSql 的 `Executing` 是空挂）；公开面数量与生态（导航属性、主从分离、缓存、分表、`DynamicBuilder`、批量 upsert，见 §6） |
| **各走各路** | 分页默认（省钱 vs 给全）；留痕流形状（SQL 一条流 + 报告另一条 vs 一条流两种内容，且对面缓存命中时根本不留痕）；批量正确性的落点（转义函数 vs 参数颗数） |

**一句不许漂的话**：FastSql 今天唯一真正的速度优势在 L1 那一档，而那一档 SqlSugar 也有。参数化多值路径在这台机器上比不过对面，且**差距随参数颗数增长**。这不是"加把劲就追上"的量级——它意味着 §12.1 的阶梯必须有 L1 接得住，也意味着 L3 这一层还需要一次专门的参数成本剖析（见 §6 末）。

## 5. 被这次对照翻掉或坐实的旧说法

### 5.1 打折：规格里那句"SqlSugar 固定行数分块 ⇒ 受参数上限约束"

规格 `docs/fastsql-orm-design.md:1510` 那句话原文是：**"多值语句的参数个数 = 行数 × 列数。500 行 × 30 列 = 15000 参数 —— 在 DM/SQL Server 上直接爆。FreeSql / SqlSugar 都用固定行数分块，换宽表就出事。"** 前一半（我们自己的算法）没错，后一半这次对照下来要拆成两半说：

- **翻掉的是默认那一条。** 分块规则确实是固定行数（`SqlServerInsertBuilder.cs:34-42`），但默认路径把值全部内联（`:61` 走 `FormatValue`），那颗命令文本里**一颗参数都没有**（§2.1 的"参数 0 颗"），2100 这一关它进不去。所以正确陈述是：**SqlSugar 的默认批量不受参数上限约束，受的是"本文长度 + 服务端解析 + 计划缓存"的约束**，而这三样本次只量到了本文长度（328,580 字）。
- **坐实的是它另外那一条口。** `Insertable(list).UseParameter()`（口在 `InsertableProvider.cs:519`）换到参数化实现，那里**确实**按 2100 分块：`ParameterInsertable.cs:171-187` 拿 `pageSize * count > 2100` 连压三档 50⇒20⇒10。所以"对面也有参数上限意识"这句不算冤枉它，只是**默认路径用不上**。

规格这一轮**不动**（本轮没拿到记账例外），此处只把纠正落在本文，等下一轮再并回规格。

### 5.2 坐实：`Aop.Executing` 是空挂

用法文档 §2.5 那张钩子表把 `Executing`（`FastSqlAop.cs:13`）与 `Executed`（`:14`）**并列为"观察"类**，而同节的"没有的"那段只点了 `QueryBuilding`、`CommandCreating` 两颗桩。这次实测把它**补上第三颗**：量具挂在 `orm.Aop.Executing` 上的语句计数器对每一次调用都报 0，换成 `Executed` 才有数；再按 grep 复核，`src/FastSql.Core` 下 `.Executing` **0 处引用**。声明在场、派发点没有——这是一颗待办，不是口径问题。对面同一位置（`OnLogExecuting`）是有派发点的（`AdoProvider.cs` 里七处调用），所以这一格是真差距，不是形状之差。

### 5.3 报告：顺手撞到的两处本家账目对不上

同一次核对里撞到两处文档/注释与工作树对不上的地方。本文只**报告**，不改那两份，也不改 `src/` 里的注释：

1. `TableRemap` / `Register<T>` / `RegisterTable<T>` 在规格里有名，`src/` 下 **0 处命中**（本次用 grep 复核，连 `RegisterTable` 与 `T Register<` 两种写法一起找）；
2. **本家自己的注释与实测对不上**：`src/FastSql.Providers.SqlServer/SqlServerBulk.cs:9` 写"500 行 × 30 列在 2100 上限下是 5 块"，而 §2.3 量到的是 **8 块、每块 65 行**（29 颗参数 × 65 = 1885，压在 2100 下；要 5 块就得每块 100 行 = 2900 颗，超预算）。按那颗 `ParameterBudget => 2100`（`SqlServerBulk.cs:29`）反推，注释里的"5 块"算错。

顺带一条**被本次核对否掉的旧旗标**：先前记过"用法文档 §3.2 引的 `XuguProvider.cs:148` / `Gbase8sProvider.cs:162` 行号已漂"，这次逐行读过去——那两行正是 `public IBulkStrategy? Bulk => null;`，与文中"这三家的 `Bulk` 是 null"逐字对上。**行号没漂，那条旗标作废。**

## 6. 这次没量的，别当成比过了

- **SqlSugar 的装载通道在宽表上没跑**（§2.3 只跑了两家的参数化多值那一档）。要补的是 `Fastest<T>().BulkCopy` 对 30 列的表现。
- **`.UseParameter()` 那一条口没实测**（§3.1 只读了它的分块码）。它撞不撞 2100、撞了之后 50/20/10 三档怎么表现，本文只有源码没有数。
- **装载的两笔一次性开销都被计时纪律挑掉了**：SqlSugar 首趟的"向库里要空表壳"与 FastSql 首次的反射装载。要量稳态之外的那一笔，得改成"取第一趟"重跑。
- **事务与装载的交叉没跑**：`SqlBulkCopy` 在事务里（对面 `SqlServerFastBuilder.cs:52-59` 那三支）与 FastSql 的 `BulkConnection` 带事务递交，本次一条都没量。
- **`ORDER BY GetDate()` 的翻页稳定性没实测**（§3.4 只报源码写法）。要证它得构造并发写入下的连续翻页比对。
- **参数成本的因果没钉死**（§2.3 那条 0.13 ms/颗 是推的）。要补：固定行数、只扫参数颗数的一趟。
- **计划缓存代价没量**：33 万字内联本文对服务端编译与缓存复用的影响，本文只报了文本长度。
- **完全没碰的面**：导航属性、主从分离、缓存层（§3.5 只说明它会绕过留痕）、分表、`DynamicBuilder`、`BulkMerge`（源码里有：`FastestProvider.cs:266-304` 是临时表 + 装载 + `MERGE` 三步的实现，同步口 `:207-211` 依旧是 `GetAwaiter().GetResult()`）、`SetColumns` 批量更新、批量删除、`DbFirst`、`CodeFirst` 的建表结果。
- **只有 SQL Server 一家**：FastSql 有 16 颗叶子 provider，本文只证了 T-SQL 这一家，而 SqlSugar 的批量写法是**每家一套 override**（如 `Realization/PostgreSQL/SqlBuilder/PostgreSQLInsertBuilder.cs:31-33` 的 `SqlTemplateBatchUnion` 是 `" VALUES "`，即 PG 那一家的批量是参数化还是内联，本文没读也没量）。
- **一台实例、一组数据形状、三趟取最小**：换实例、换列类型（`varchar(max)`、`datetime2`、`sql_variant`）、换批量大小都可能换结论。
- **211 / 215 / 218 的差异**：见 §0，本文不猜；快照没有 `.git`，连"对应上游哪个 commit"都无法确定。

## 7. 这份文档怎么被维护

- 想更新数字：跑 §1 的量具，把 `run.txt` 的读数搬进 §2 的表格，**保留每格的"读数"原文**（那才是可核的东西，毫秒数只是它的摘要）。
- 想更新名次：§4 的四档只允许由 §2 的格子推动；§3 的读码格只允许由 `文件:行` 推动。
- 规格与用法文档的口径冲突：按 §5 的格式记在本文里，等一次记账轮一起并回去，不要在这里顺手改那两份。
