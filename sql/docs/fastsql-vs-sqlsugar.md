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
| 表 | **五张对照表**，写入用的四张**两两一副、DDL 逐字相同，都由裸 ADO 建**；第五张 `cmp_chk` 只给 §2.12 那几颗约束探针用（与窄表逐字相同，多一颗 `CHECK (Amount >= 0)`） | 不走任何一家的 CodeFirst，避免"谁的建表更聪明"混进写入对照里 |

**坐标的写法**：FastSql 的 `src/…:行` 相对本仓库根 `E:\Resource\Fast\sql`；SqlSugar 的坐标一律相对 `E:\Resource\SqlSugar-master\Src\Asp.NetCore2\SqlSugar`（下文只写这个根以下的相对路径，例如 `Realization/SqlServer/SqlBuilder/SqlServerInsertBuilder.cs:16`，个别处以 `文件:行` 简写、按该根检索可定位）。

**版本口径有三个数**（211 源码 / 215 打包描述 / 218 实测到的包）。这不是笔误，是快照的真实样子：`SqlSugarCoreNoDrive` **不是独立工程**，而是同一颗 dll 的第二份打包描述——`SqlSugarForCoreNoDrive.nuspec:4-18` 的依赖只有 `Newtonsoft.Json` 13.0.2 与 `SystemCommon` 8.5.0，两行里没有任何驱动，而工程自己的 `SqlSugar.csproj:24-33` 反倒**直接引用**了 `Microsoft.Data.SqlClient` 5.2.1 等一串。也就是说本文读的源码是"带驱动"的那一份，实测跑的是"不带驱动"的那一份；两者是同一份 C# 编出来的不同包装，`Fastest.BulkCopy` 在 NoDrive 包上照样可用（本文量具就是这么跑出来的）。快照根目录**没有 `.git`**，只有 `.gitattributes`/`.gitignore`，所以"这份源码对应上游哪个 commit"**无法从源码确定**。

因此本文的分工是硬的：**所有 `Src/…:行` 只对 211 那份源码负责，所有毫秒数只对 218 那颗包负责。** 凡属"这个行为是包里没带、还是 211 之后改了"的问题，一律标注无法从源码确定。

## 1. 量具与重跑法

量具是一次性控制台工程，放在 **`sql/obj/sqlsugar-compare/`**（`obj/` 被 `sql/.gitignore` 挡住，不进版本库）：

- `Program.cs`：**十六段对照活**（1–10 十段，另加 `3b`…`3f` 五段与 `10b` 一段 ∶ 批量、逐条、宽表、`UseParameter`、颗数因果、每语句行数扫、裸 ADO 对照、**每趟换一批全新值的同一形状**、分页首页与第二页、读物化、留痕次数、引号、地板、**留痕存活探针**、**事务内装载与约束二选一**）；
- `fscompare.csproj`：`SqlSugarCoreNoDrive` 5.1.4.218 + `Microsoft.Data.SqlClient` 7.1.0 + `ProjectReference` 到工作树的 FastSql 三颗程序集，并挂 `FastSql.Generators` 为 Analyzer（否则消费者程序集里没有 G2 注册表，`FastSqlOrm.Create` 会报"DbKind.SqlServer 未在 ProviderRegistry 中登记"）；
- `run.txt`：最近一次跑出来的原始输出；`run-A.txt` / `run-B.txt`：**本轮同一份二进制连跑两趟的原始输出**。

**读数的出处怎么算（四趟，两份原始文件 + 一份旧文档）**：本节表格里写"四趟"的区间，下界与上界各自由两副读数钉住 ∶ 本轮的 `run-A.txt`/`run-B.txt`（在工作树里，可直接开），以及上一轮（提交 `2415164`）的两趟。上一轮那两份原始输出**不在版本库里**（`sql/obj/` 被 gitignore 挡住），本轮重跑时已被覆盖，所以它们的读数只以区间形式存在于 `git show 2415164:sql/docs/fastsql-vs-sqlsugar.md` 的表格里——这是一条**降级了的可核性**，本文照实写在这里。本轮新增的格子（§2.6 换数据、§2.7 第二页、§2.12 事务、§2.13 留痕存活）只有两趟，格子里明写"本轮两趟"。个别形状在量具里出现在两节（`UseParameter` 的 2000×6 在 `3b` 与 `3d` 各跑一次），那种格子本文写出实际趟数。

重跑（已验：从该目录 `dotnet build` 成功）：

```powershell
cd E:\Resource\Fast\sql\obj\sqlsugar-compare
dotnet build -v q --nologo          # 0 警告 0 错误
$env:FASTSQL_DRIVERS_CONFIG = "E:\Resource\Fast\sql\FastSqlDrivers.json"
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new()   # 不清这行，控制台见证会是 GBK
bin\Debug\net10.0\fscompare.exe | Out-Null; Copy-Item run.txt run-A.txt
bin\Debug\net10.0\fscompare.exe | Out-Null; Copy-Item run.txt run-B.txt   # 两趟：本轮区间由这两份钉；上一轮的读数按上一段说的办法取
```

连接串的取法和集成层判据同一条：`FASTSQL_TEST_CONNECTION` 优先，其次 `sql/fastsql-test.connection.local.txt`（本地文件，gitignored）。量具全程不打印口令。

**段序是硬的**：`9`（留痕存活探针）与 `10`/`10b`（事务×装载）必须排在所有量墙钟的格子之后。这不是排版偏好——本轮第一版把事务那几段放在中间，跑出来的 `run.txt` 里**后面每一格对面的留痕全成了空白**（`OnLogExecuting 响 0 次`、`(无文本)`），因为事务内装载抛异常会把 `Ado.IsEnableLogEvent` 按灭且不恢复（§2.13 就是这一笔翻出来的证据）。这次翻车留着，因为它同时是结论和排雷图。

**计时纪律**（五处容易做假的地方，都堵上了）：

1. 每格取**三趟最小值**；
2. 计时段**只包住活本身**，`TRUNCATE`、建连接、建表都在计时段外——这条不是洁癖：早前一版把 `TRUNCATE` 算进去了，量出 FastSql L1 207 ms 对 SqlSugar 38 ms 的 5 倍差距，重排后的真实差距是四趟区间 30.66–36.81 对 28.97–96.03（§2.1）；
3. 表里的"句"指**一次命令文本**（一次 `ExecuteNonQuery`）。SqlSugar 的分块会把多段 `INSERT` 用 `;` 串进同一颗命令文本里，所以"1 句"**不等于**"1 条 INSERT"，本文在下面会写清到底几段。
4. **本节绝大多数毫秒数是四趟完整跑的区间**（来历见上一段），不是单趟。这一条也是被数据教的：同一格跨趟能从 38 ms 跳到 537 ms（§2.1 的 SqlSugar 内联那一档），单趟报出来的任何一位小数都会假装精度比实际有的高。区间下界用于排名次，上界用于说"抖多大"；**两副区间重叠的格子，本文不再排名次**（本轮据此收回两处写死的名次：§2.8 那句"对面稳定快约 10%"、§2.5 中间两档的排序；本轮新增的格子从一开始就按这条办——§2.2 的配对一比一、§2.12 的墙钟都不报名次）。
5. **§2.12 那一档的形制与别处不同**：它把 `begin → 写 → rollback` 整段计时，而别处只计"写"，所以那一档只跟同形制的那一档比，不拿来跟 §2.1 论高低。

## 2. 实测：十三格原始读数

### 2.1 批量插入 2000 行 × 7 列

| 走法 | 墙钟（四趟） | 留痕读数 |
| --- | --- | --- |
| FastSql L1 装载通道 | **30.66 – 36.81 ms** | `CopyChannel ∶ 2000 行 ∶ 1 次递交 ∶ 库里 2000` |
| FastSql L3 多值语句 | 1262.35 – 1525.92 ms | `MultiValue ∶ 7 块 ∶ 每块 315 行 ∶ 语句 7 句 ∶ 参数 12000 颗 ∶ SQL 文本累计 93263 字` |
| SqlSugar `Fastest<T>().BulkCopy` | **28.97 – 96.03 ms** | `OnLogExecuting 响 1 次 ∶ 参数 0 颗 ∶ SQL 文本累计 79 字 ∶ INSERT 0 段` |
| SqlSugar `Insertable(list)` | 38.19 – **537.38 ms** | `OnLogExecuting 响 1 次 ∶ 参数 0 颗 ∶ SQL 文本累计 328580 字 ∶ INSERT 10 段` |
| 裸 `SqlBulkCopy`（地板，§2.11） | 19.91 – 23.16 ms（只算复制） | 库里 2000 |

（"每句 315 行 × 2000 行"这一副 FastSql 的活在量具里出现在两处（本节的 `1` 与 `3d`），每处各四趟，合起来八副读数，跨度 1 215.39 – 1 525.92 ms（比 25%）。两处各写自己那四趟的区间，不混成一副。）

SqlSugar 批量那一档写出去的形状（原文截断）：

```sql
INSERT [cmp_sugar]  ([CustomerName],[CustomerId],[Amount],[State],[CreatedAt],[Note])
   SELECT N'客户1' AS [CustomerName],N'7' AS [CustomerId],1.25 AS [Amount],N'1' AS [State],'2026-10-05 01:02:03.000' AS [CreatedAt],N'cmp' AS [Note]
   UNION ALL
   SELECT N'客户2' ...
```

五个读数要合起来读：

- **装载通道这一档两边打平，而且这一格四趟区间整个重叠**：FastSql 30.66–36.81，SqlSugar 28.97–96.03，下界差 1.7 ms。地板（§2.11）在 19.91–23.16，所以两家各自都有约 8–15 ms 的"把自己那层套上去"的开销，这一层两家同量级。
- **这一格第一次给到了"段数"而不是只有"句数"**：留痕里那句 `INSERT 10 段` 是在 `OnLogExecuting` 里数 `INSERT ` 关键字的颗数量出来的（先前版本此处写"按 `SqlServerInsertBuilder.cs:34-42` 推得 10 段"，那一档现在坐实了：2000 行 ÷ 每段 200 行 = 10 段，与推算逐字相同）。
- **SqlSugar 的批量默认根本没走参数**：参数 0 颗、文本 32.8 万字，值全部内联成字面量，**一次往返**送出去。这条读数直接打折一句旧说法，见 §5.1；而它的**红利条件**是本轮新量出来的，见 §2.6。
- **长尾只出现在 SqlSugar 那两档**：`Insertable(list)` 四趟是 53.48 / 537.38 / 38.19 / 43.57 ms（更早四趟另给过 43.80 / 46.26 / 50.05 / 49.94），`Fastest.BulkCopy` 是 31.50 / 96.03 / 28.97 / 44.75。同一份二进制、同一副表、同样是三趟取最小，一趟之内不会这样，跨趟就会——本文**没有解释这一笔**，只把量程报出来，并据此把名次钉在"下界"上（见 §6 第一条）。FastSql 那两档在四趟里的跨度分别是 20%（L1）与 21%（L3），比对面那两档小一个量级（内联那一档的跨度是 14 倍）。
- **文本量是另一笔账**：同一批活 SqlSugar 写出 328,580 字，FastSql L3 写出 93,263 字加 12,000 颗参数。内联换到的是"不付参数的钱"（§2.5 量到了这一笔有多大），付出的是"服务端要为 33 万字的语句本文做编译"——这一笔先前只挂在"没量"那一栏，本轮量了，结果是它会改名次，见 §2.6。

### 2.2 逐条插入 500 行

| 走法 | 墙钟（四趟） | 更早四趟给过的读数 |
| --- | --- | --- |
| FastSql 逐条（`SingleRow`） | 282.31 – 380.44 ms | 302.07 / 307.27 / 335.22 / 365.24 |
| SqlSugar `Insertable` 单条循环 | 296.62 – 342.85 ms | 327.99 / 337.18 / 349.44 / 358.90 |

**持平，而且这一格的"持平"是量出来的、不是抹平的**：本轮两趟是同一趟里两边各跑一次，所以配对是量具记下的——282.31 对 339.19 本家快 17%，348.85 对 340.22 对面快 3%，**一比一**。上一轮那两趟只留下了区间和一副明确配对（380.44 对 296.62，对面快 22%），另一副本文不配对、不猜。再往前四趟（302.07 / 307.27 / 335.22 / 365.24 对 327.99 / 337.18 / 349.44 / 358.90）只能按大小顺序假定配对，那一半本家快 4–9% 三副、对面快 2% 一副——**这个"按序配对"是推的，不是量的**，本文只当参考。回到能主张的那一半：两副区间 282.31–380.44 与 296.62–342.85 大面积重叠。这一格两边都走参数化单条语句，量的其实是"一次往返多少钱"（约 0.6–0.8 ms/条），谁的内核都省不掉。

### 2.3 宽表 30 列 × 500 行

| 走法 | 墙钟（四趟） | 读数 |
| --- | --- | --- |
| FastSql `Auto`（不调 `WithBulk`） | **27.24 – 47.15 ms** | `Auto→CopyChannel` ∶ 1 次往返 ∶ 库里 500 ∶ `Reason` 原文是"500 行走 SqlServer 的装载通道（§12.1 的 L1）：不发 SQL 文本、不走参数，一次递交。通道内部怎么分批是驱动的事，对上层是一次递交一块；改写类钩子（`CudBuilding` / `ParameterBinding`）在这一档不适用" |
| FastSql L1 装载通道 | **30.84 – 37.79 ms** | `CopyChannel ∶ 500 行 ∶ 1 次递交 ∶ 库里 500` |
| FastSql L3 多值 | 1544.04 – 1949.06 ms | 8 块 ∶ 每块 65 行 ∶ 每块参数 1885 颗 ∶ 库里 500 |
| SqlSugar `Fastest.BulkCopy` | **30.51 – 35.99 ms** | 响 1 次 ∶ 参数 0 颗 ∶ 文本 83 字 ∶ INSERT 0 段 ∶ 库里 500 |
| SqlSugar `Insertable(list)` | 37.87 – 39.59 ms | 1 句（内含 **5 段** INSERT，数出来的）∶ 每段 100 行 ∶ 参数 0 颗 ∶ 文本累计 242,442 字 |

**先前版本在这一格写下"全篇差距最大：24.8 倍"，同一格重跑两轮之后的结论是那句写歪了。** 三处要改：

- **同档对同档，宽表上两家都是装载通道，四趟区间整个重叠**：FastSql 30.84–37.79 对 SqlSugar 30.51–35.99，下界差 0.3 ms。**宽表不让 FastSql 的 L1 掉速**（§2.1 那格 2000 行 × 7 列它是 30.66–36.81 ms，行数差四倍而墙钟同档）——这一条先前只留在"见 §2.1，本文未在宽表上重跑"那一格里，现在补上了。
- **24.8 倍比的不是同一件事。** 那一趟是"FastSql 被点名只许走 L3"对"SqlSugar 走它的默认（内联）"。把 `Auto` 那一行放进来（27–47 ms），FastSql 面对同一副宽表的默认行为根本不付这笔钱。
- **L3 慢的真正原因不在"宽表"，在"每命令参数颗数"**，与表宽窄无关——§2.5 用一趟扫把这条钉死了。顺带把先前一条**推算**升成实测：`1 句内含 5 段` 是在 `OnLogExecuting` 里数 `INSERT ` 关键字数出来的，不再按 `SqlServerInsertBuilder.cs:34-42` 推（30 列 ⇒ 列数 >20 ⇒ 每段 100 行 ⇒ 5 段，算与数逐字一致，本轮两趟都是 5 段）。

（SqlSugar 内联那一档跨三轮的读数是 76.56 / 37.87 / 38.28 / 38.79 / 39.59 ms——除最早那一趟之外都在 38–40 ms。本文把这格按 **38–77 ms** 看待，名次钉在下界；那一趟 76.56 没被解释。）

### 2.4 SqlSugar 的参数化那一条口：`UseParameter()`

先前版本的 §3.1 与 §5.1 说"对面也有参数上限意识，只是默认路径用不上"——那句**只有源码没有数**。上一轮把它跑了，本轮又跑了两遍（`UseParameter` 的窄表那副活在量具里出现在 `3b` 与 `3d` 两处，加上一轮的两趟一共六趟）：

| 走法 | 墙钟 | 读数 |
| --- | --- | --- |
| `Insertable(2000 行).UseParameter()`（6 列实体） | 434.22 – 537.19 ms（**六趟**） | **20 句 ∶ 每句 100 行 ∶ 每句 600 颗 ∶ 总参数 12000 颗 ∶ 文本累计 159,700 字 ∶ INSERT 20 段** |
| 同上，宽表（30 列实体） | 1260.94 – 1579.77 ms（四趟） | **10 句 ∶ 每句 50 行 ∶ 每句 1450 颗 ∶ 总参数 14,500 颗 ∶ 文本累计 124,010 字** |

写出去的是真参数化多值，参数名每句重新编号（原文截断）：

```sql
INSERT INTO [cmp_sugar]  ([CustomerName],[CustomerId],[Amount],[State],[CreatedAt],[Note])
VALUES(@CustomerName0,@CustomerId1,@Amount2,@State3,@CreatedAt4,@Note5),  (@CustomerName6,@CustomerId7, ...)
```

源码那一档与实测逐字对上（`ParameterInsertable.cs:111,129-132,171-187`）：起点是**写死的 100 行**，然后按 `pageSize * count > 2100` 连压 50⇒20⇒10，而那颗 `count` 是**实体列数（含自增列）**——所以 6 列实体（含 `Id` 共 7 列）是 7×100=700 ≤ 2100、不压，量到每句 100 行；宽表（含 `Id` 共 30 列）30×100=3000 > 2100、压到 50，实发每行 29 颗 ⇒ 1450。**每一句都压在 2100 以下：这一点先前只读到判据、没量到落地，现在量到了。**

但它与 FastSql 的分别也一并清楚了：**2100 对它是一道墙，不是一个目标**。100 行 × 21 列 = 2100 刚好不压，22 列才压到 50 行——它从不问"这一句多少行花多少钱"，只问"撞不撞墙"。而 FastSql 的 §12.3 公式问的也只是预算（`ParameterBudget * 9 / 10 / cols`），所以两家在这件事上是**同一种问法**，只是默认答案不同：2100 的九成 ⇒ 贴着曲线贵的那一端，100 行 ⇒ 贴着便宜的那一端。谁都没把"每命令颗数的超线性代价"放进目标函数（§2.5、§5.4）。

### 2.5 参数化的钱花在哪个变量上：同一批 2000 行，只改每语句行数

**再上一版在这里留了一条推断，上一轮把它换成了量出来的因果，而那条推断是错的。** 先前写的是："把 §2.1 与 §2.3 除以参数颗数得 0.146 与 0.126 ms/颗 ⇒ 耗时几乎只跟着**总**参数颗数走，不跟着行数走"。固定总颗数、只扫每语句行数，同一批 2000 行 × 6 列 = **12,000 颗参数一颗不变**（下表是四趟：上一轮两趟 + 本轮两趟）：

| 每语句行数（同 2000 行、同 12000 颗） | FastSql L3 墙钟（四趟） | 句数 | 每句参数 |
| --- | --- | --- | --- |
| 315 行（默认：预算 ÷ 列数给到的最大块） | 1215.39 – 1404.67 ms | 7 | 1890 |
| 100 行 | 308.33 – 480.78 ms | 20 | 600 |
| 50 行 | 204.32 – 311.95 ms | 40 | 300 |
| 20 行 | **150.53 – 223.74 ms** | 100 | 120 |

**总颗数一样，墙钟下界差 8.1 倍，方向还反着**（往返从 7 次涨到 100 次，反而快）。所以自变量不是总颗数——旧推断作废。四趟区间下来要补一句老实话：**中间两档不再分明**，100 行（308.33–480.78）与 50 行（204.32–311.95）在 308–312 交叠，50 行与 20 行（150.53–223.74）在 204–224 交叠；只有两端（1 890 颗与 120 颗）隔开一个量级，"颗数越少越快"这条本文只在**下界序列**上主张，中间名次不再报。三颗对照把剩下的候选各自否掉：

| 对照 | 墙钟（四趟） | 否掉了什么 |
| --- | --- | --- |
| **裸 ADO.NET 手拼**同样的参数化多值（不经 FastSql），每语句 315 / 100 / 20 行 | 1472.95 – 1706.45 / 328.16 – 425.60 / 145.54 – 161.03 ms | 曲线**不在 FastSql 这一层**：手写 ADO 同样中招（还略慢）。FastSql 的文本层在这一格没有可收的税 |
| 裸 ADO 同样 315 行/句，但**值内联、0 颗参数**（本文还更长） | **17.91 – 37.76 ms** | 行数不是自变量，语句本文长度也不是：同样行数、更长本文、零颗参数 ⇒ 同一格参数化那一档（1 473–1 706 ms）的 **1/45 到 1/82**（同界相除） |
| SqlSugar 的 `PageSize` 旋钮切到每段 100 / 50 行（内联，0 颗参数） | 52.63 – 56.14 / 67.01 – 73.33 ms | 与上一行同向：段多几倍也不贵，因为每命令颗数是 0 |

于是这一格的说法是：**代价跟着"每命令参数颗数"超线性走**。把上表折成每句耗时：120 颗 1.51–2.24 ms、300 颗 5.11–7.80 ms、600 颗 15.42–24.04 ms、1890 颗 173.63–200.67 ms——颗数涨 15.75 倍，每句耗时涨约 115 倍（取下界）到 89 倍（取上界；两端各自同界相除），两点拟合指数约 1.7，**只用来报形状，不当定律**。机制本文没有量：驱动的序列化还是服务端的参数解析，两可，见 §6。

四条直接从这一格推出的名次结论（第四条是本轮新加的，它把上面那条"内联便宜"限住了）：

1. §2.3 那"24.8 倍"里属于**块大小策略**的那一半与内核无关：同形状（每句 100 行 / 600 颗）比，**FastSql 308.33–480.78 ms 对 SqlSugar `UseParameter` 434.22–537.19 ms**，FastSql 下界快约 29%（但两副区间在 434–481 交叠，这一格的名次同样只钉在下界上）。
2. 剩下那一半属于**内联**这一档策略，FastSql 今天没有对应物：同一批 2000 行，本家最便宜的参数化形状（每句 20 行）150.53 ms，对面默认内联 38.19 ms ⇒ **3.9 倍**；本家每句 100 行 308.33 对对面同段数（每段 100 行）52.63 ⇒ **5.9 倍**；本家默认形状 1 215.39 对 38.19 ⇒ **32 倍**。宽表那一格更直白：1 544.04 对 37.87 ⇒ **41 倍**（§2.1、§2.3）。**这一条的成立条件是"每趟跑同一批数据"，条件一摘就换人，见 §2.6。**
3. **本家有一句话的调优空间**：`WithBulk(BulkMethod.MultiValue, rowsPerRoundTrip: 20)` 就把 1 215 ms 降到 151–224 ms，而那颗旋钮今天就在公开面上（`src/FastSql.Abstractions/Write/IInsert.cs:44-51`，`只许往下压、不许往上抬`）。§12.3 的公式把 2100 当**上限**用是对的，但它同时就是默认块大小，而量出来"贴着上限的块"正好落在曲线最贵的那一段。这一条本文只**报告**，不改规格也不改代码，见 §5.4。
4. 本家每一句贴墙（1 890 颗）的形状，换数据之后仍然贴墙（§2.6 里它只涨一成），而对面内联那一档换数据涨 3–4.5 倍 ⇒ **这一格与 §2.6 合起来才是名次**，单看任何一副都会把赢的那家认错。

### 2.6 同一批形状，每趟换一批全新值：把"计划能不能复用"这一笔摘出来

**这一节是本轮新开的，它改写了 §2.5 第 2 条的成立条件。** 前面所有格子都跑在同一批数据上：三趟计时用同一副值，于是内联那一档每趟写出去的是**逐字相同的一大坨本文**——服务端第一次编译过之后，后面两趟复用的是什么，本文先前没有区分过。量具的做法是把值整体平移（每趟 `shift += 1000`，`Cell` 的 prep 里递增 ⇒ 三趟是三批互不相交的数据），形状、行数、颗数一颗都不动，只让"值"变。参数化那一族的语句本文因此照旧逐字相同，而内联那一族的本文每趟都是新的（长度只差千分之几）。本轮两趟：

| 同一副活 | 每趟同数据（四趟） | 每趟换数据（本轮两趟） | 换数据带来的差 |
| --- | --- | --- | --- |
| SqlSugar 内联 ∶ 每段 100 行 | 52.63 – 56.14 ms | **168.32 – 250.35 ms** | 慢 **3.2 – 4.5 倍**（同界相除） |
| 裸 ADO ∶ 内联 ∶ 每句 315 行（0 颗参数） | 17.91 – 37.76 ms | **355.82 – 357.83 ms** | 慢 **9.5 – 20 倍** |
| FastSql L3 ∶ 每句 315 行（1890 颗/句） | 1215.39 – 1404.67 ms | 1388.55 – 1513.95 ms | 涨一成到两成，量级不变 |
| FastSql L3 ∶ 每句 20 行（120 颗/句） | 150.53 – 223.74 ms | 165.10 – 226.04 ms | 区间重叠 ⇒ 没涨 |
| SqlSugar `UseParameter`（600 颗/句） | 434.22 – 537.19 ms | 431.58 – 453.02 ms | 没涨，甚至略低 |
| 裸 ADO ∶ 参数化 ∶ 每句 315 行 | 1472.95 – 1706.45 ms | 1324.82 – 1384.43 ms | 换数据反而更快，本文不解释 |

三件事当场清楚：

- **内联的便宜是"同一批数据"租来的。** 值一变，对面默认那条口从 53 ms 档掉到 168–250 ms 档，手拼那条更从 18–38 ms 掉到 356 ms；而参数化那一族（两家加手拼三副）换数据几乎不动。这与 §2.5 的因果链是同一条：语句本文里带字面量 ⇒ 每颗新本文都要服务端编译；带参数 ⇒ 本文恒定、只换值。
- **名次随之改写**：同一批 2000 行 × 6 列，换数据之后本家最便宜的形状（每句 20 行）下界 **165.10 ms**，对面内联（每段 100 行）下界 **168.32 ms**——**本家反超 2%**，而两副区间（165–226 对 168–250）几乎完全重叠。所以"内联这一档 FastSql 拿不到"那句，在同数据条件下成立（3.9–41 倍，§2.5 第 2 条），在换数据条件下只剩"对面默认比本家默认便宜"（168 对 1 215，本家的默认块仍然贴墙，见 §5.4），不再是"便宜一个数量级"。
- **本家默认那一条仍然被自己的块大小坑**：它换不换数据都在 1 215–1 514 ms，而 `rowsPerRoundTrip: 20` 那一条 151–226 ms。这一格反过来给 §5.4 加了一句：**贴墙的块不只贵在内联对不过，它连"换数据"这一关都不需要对面帮忙就自己输了 8.4 倍**（1 388.55 对 165.10）。

口径要钉死的一处：这一节**没有**直接读服务端的计划缓存（没查 `sys.dm_exec_cached_plans`，也没抓 TDS），"编译 vs 复用"是从"只有内联那一族变动"这一条对照推出来的——本文把它写成**与源码形状一致的读数**，不是机制的证明（§6 第二条还挂着这一笔）。还有两点没试：只试了"每趟全换"，没试"每两趟换一次"这种中间档；每趟全换也让索引/page 分配的状态变了，本文无法把这一笔从编译那一笔里剥干净。

### 2.7 分页（表上 200 行，取 20 行；首页与第二页各一档）

| 走法 | 语句数 | 读数 |
| --- | --- | --- |
| FastSql `ToPagedAsync(1,20)`（`Auto`） | **1** | Items 20 ∶ Total **null** ∶ HasNext True |
| FastSql `ToPagedAsync(CountStar)` | 2 | Total 200 |
| SqlSugar `ToPageList(1,20,ref total)` | **2** | Items 20 ∶ Total 200 |
| SqlSugar `ToPageList(1,20)` 不要总数 | 1 | Items 20 |
| **FastSql `ToPagedAsync(2,20)`（`Auto`）** | **2** | Items 20 ∶ Total 200 ∶ HasNext True ∶ 写出去的两句在下面 |
| **SqlSugar `ToPageList(2,20,ref total)`** | 2 | Items 20 ∶ Total 200 |
| **SqlSugar `ToPageList(2,20)` 不要总数** | 1 | Items 20 ∶ 那一句是窗口函数外壳 |

这一格量的不是快慢而是**默认替你做什么**。FastSql 默认那一档在**首页**只发一条语句（`QueryEngine.cs:1225` 的 `size + 1`，多取一行探 `HasNext`），代价是 `Total` 为空；到**第二页起 `Auto` 自己换成 `CountStar`**（`QueryEngine.cs:1264-1271` 的 `Resolve`：`page == 1 ? SkipTotal : CountStar`），也就是两条——**这条换档先前只报源码，本轮实测到了**：第二页确实发两句，`Total` 也从 null 变成 200。要总数必须显式换 `CountMethod`（`src/FastSql.Abstractions/Query/IQuery.cs:19` 的 `PagedOptions` 默认 `CountMethod.Auto`）；`CountOverWindow`（单往返带总数）与 `SeparateTransaction`（同连接同事务两条）**今天直接抛异常**，不是静默降级。SqlSugar 的 `ref total` 是"要总数就固定两条语句"——`QueryableExecuteSql.cs:509` 那一档写得很白：先 `this.Clone().Count()` 再 `this.Clone().ToDataTablePage(...)`，两次往返、两次 `Clone()`。两边各有对面拿不到的东西：SqlSugar 拿不到"不写 `ref total` 之外还能选单往返带总数"，FastSql 拿不到默认给的准确总数。

本轮抓到两家第二页写出去的东西，形状差得比想象中远：

```sql
-- FastSql ∶ ToPagedAsync(2,20) 的两条语句
SELECT [t0].[Id], [t0].[CustomerName], … , [t0].[Note] FROM [cmp_fast] AS [t0]
  ORDER BY (SELECT NULL) OFFSET @p0 ROWS FETCH NEXT @p1 ROWS ONLY
SELECT COUNT(*) FROM [cmp_fast] AS [t0]

-- SqlSugar ∶ ToPageList(2,20) 不要总数的那一条
SELECT * FROM (SELECT [Id],[CustomerName],…,[Note],ROW_NUMBER() OVER( ORDER BY GetDate() ) AS RowIndex
               FROM [cmp_sugar] ) T WHERE RowIndex BETWEEN 21 AND 40
```

三读数合起来：

- **本家在 T-SQL 上从不发 `TOP`，先前本文那句写错了**：第二页真发的是 `OFFSET…FETCH`（`SqlServerDialect.cs:39-53` 的 `EmitPaging`，缺省偏移落成常量 `0` 而不是省略子句），而首页那一档**同形**——本家没有 TOP 发射器（`SqlServerDialect.cs` 全文搜不到 `TOP`，方言册既有金标 `tests/FastSql.Tests.Dialects/SqlServerPagingTests.cs:49` 钉的就是 `OFFSET 0 ROWS FETCH NEXT @p0 ROWS ONLY`）。先前版本在 §2.6 与 §3.4 两处写下的"单条 `TOP n+1`"是本文读源码读歪的，本轮翻掉，见 §5.6。首页那一句的**原文**本轮没抓到（`fastSamples` 在第二页之前清空了），所以这一句的根据是方言册 + 第二页的实测文本，不是首页的见证。
- **没有 `ORDER BY` 时两家都补了一颗，补的是两种东西**：本家补 `ORDER BY (SELECT NULL)`（`DialectBase.cs:461-470`，`PagingRequiresOrdering` 在 `SqlServerDialect.cs:31` 为 true ⇒ 语法要求它在，策略是"不替你排序，只让句子合法"）；对面补 `ORDER BY GetDate()`（`SqlServerQueryBuilder.cs:60` 写死）——那是**每行都不同的表达式**，等于替调用方造了一个不稳定的序。两者都不保证翻页不漏不重，但本家的写法不额外引入跨行变化，对面的会（本文仍未实测并发写下的漏重，见 §6）。
- **计数那句也不同形**：本家是 `SELECT COUNT(*)`，对面是 `SELECT COUNT(1)`（`QueryBuilder.cs:634-641` 的模板，化简成直查表；本轮那句原样抓到了）。这一笔只是形状，没有代价差可量。

SqlSugar 不计数那一档也是单条。**首页这一格持平；第二页起两家都是两条，也持平——但两家在第二页上的"合法化排序"不是同一颗，这一格从"持平"变成了"持平且各注不同的东西"。**

### 2.8 读 200 行 × 7 列并物化

FastSql `ToListAsync` **1.54 – 1.73 ms** ∶ SqlSugar `ToList` **1.36 – 1.59 ms**（四趟）。上一版写的是"对面稳定快约 0.15 ms（10%）"，**那句被本轮的四趟区间作废了**：本轮两趟是 1.54 对 1.36、1.62 对 1.59，第二趟只差 0.03 ms，两副区间在 1.54–1.59 交叠。四趟下界相除对面仍快约 12%，但本文不再写"稳定快 10%"——按 §1 第 4 条的规矩，**区间重叠的格子不排名次**。这一格现在的口径是：同一量级，对面在下界上略快，差距小于本格的抖动。

### 2.9 一次批量调用里留痕响几次

| 走法 | 读数 |
| --- | --- |
| FastSql L1 装载通道 | `Executed` 响 1 次（这一档不产 SQL 文本，交回的是 `BulkReport`） |
| FastSql L3 多值 | `Executed` 响 2 次（两块） |
| SqlSugar `Insertable(list)` 500 行 | `OnLogExecuting` 响 **1 次**（内含多段 INSERT） |
| SqlSugar `Fastest.BulkCopy` 500 行 | `OnLogExecuting` 响 1 次，文本是 `Begin BulkCopy name:[cmp_sugar] ,count: 500,current time: …` |

SqlSugar 那条"伪 SQL"是设计如此，不是日志坏了：`FastestProvider.cs:548` 在装载前发一句 `Begin {title} name:… ,count: N` 给 `OnLogExecuting`，紧接着 `:545` 把 `Ado.IsEnableLogEvent` **按灭**（先在本机字段 `:544` 存一份旧值），装载结束再在 `:530` 恢复；收尾那一句走 `OnLogExecuted`（`:533`）。所以装载期间真实的驱动侧动作在它的 SQL 日志流里是**看不见**的。而 `:530` 那一次恢复**不在 `finally` 里**——装载一抛，开关就永久留在按灭态，本轮把这一笔量出来了，见 §2.13。FastSql 走的是另一条路：L1 那一档根本不产 SQL，产的是结构化的 `BulkReport`（`src/FastSql.Abstractions/Providers/Bulk.cs:46-54`：`Requested` 与 `Method` 分开记"要哪一层"和"实际走了哪一层"，再加 `RowCount`、`RoundTrips`、`ChunkSize`、`Degraded`、`Reason` 五颗）。§2.1、§2.3 那几行读数里的"7 块 ∶ 每块 315 行"来自与它同源的 `InsertPlan`（`Chunks`/`RowsPerStatement`/`ParameterBudget`），量具把两张单一起打了。

要说清的一件事：`OnLogExecuting` 这一条通道在 SqlSugar 里**同时装 SQL 和装载动作的叙述文本**，接钩子做审计的人会在同一支流里看到两种东西；FastSql 的 `Aop.Executed` 只装真实 SQL，装载另走 `BulkReport`。这是形状之差，不是对错之差——直到 §2.13 那一格，它变成了形状之差**加**一条可用性之差。

### 2.10 值里带单引号的一行

| 面 | FastSql | SqlSugar |
| --- | --- | --- |
| 单条（参数化） | 正确落库，表还在 | 正确落库，表还在；写出去的是 `INSERT INTO [cmp_sugar] (…) VALUES (@CustomerName,@CustomerId,…)` |
| 批量（内联字面量） | —（走参数，见 §2.1） | 正确落库，表还在；写出去的是 `… SELECT N'O''Brien 客户' … N'''; DROP TABLE cmp_fast; --' AS [Note] …` |

两家的转义都成立，`'; DROP TABLE …'` 这类值都原样存下来了，五张对照表跑完都还在（量具最后按自己的规矩把五张删掉）。SqlSugar 的批量内联靠的是 `InsertBuilder.cs:263` 的 `value.ToString().ToSqlFilter()` 加 `N'…'` 前缀，加上 `GetDbColumn` 那一串分岔（`InsertBuilder.cs:291`）——它不是"随手拼字符串"，本文不打算把它写成 SQL 注入洞。**但它确实把值的正确性压在转义函数一个人身上**，而 FastSql 的批量把这件事压在参数绑定上。这是取舍，两笔代价不同：一面是"转义函数出错就是全库的事"，一面是"每命令颗数多了就慢"（§2.5 那条超线性曲线就是这一面的账单）。

顺带一条实测到的形状细节：SqlSugar 的内联把**整型也写成 `N'7'`**——`InsertBuilder.cs:218` 的 `FormatValue` 只列了 DateTime/byte[]/enum/bool/string/DateTimeOffset/float/decimal，其余全落 `:279` 那条兜底分支 `N + "'" + value.ToString() + "'"`。列上写的是 `N'7' AS [CustomerId]`，靠服务端隐式转换吃进去，同时把 `nvarchar` 塞进整型列的比较面。本文只报读数，不替它算代价。（本轮两趟那一句的字数都是 603，逐字相同。）

### 2.11 装载通道的地板

裸 `SqlBulkCopy`（自己造 DataTable，不经任何一家）：**19.91 – 23.16 ms**（把造 DataTable 的时间摘出去）／ 22.71 – 26.78 ms（含造），四趟。更早那版本记的是 25.14／25.47 ms，落在同一量级里。

这一格存在的意义只有一个：给 §2.1、§2.3 那几个 30+ ms 提供一个参照。**两家离地板都不远（7–15 ms），所以装载通道这一档谁也没有把驱动的钱花掉第二遍。**

### 2.12 同一批活放进事务里，加上"身份列 vs 约束检查"那颗互斥

事务这一档先前只在 §3.3 读码、§6 挂着"没跑"，本轮补了。**形制与别处不同**（§1 第 5 条）：计时段是 `begin → 写 → rollback` 整段，所以这一档只跟本档自己比，不跟 §2.1 论高低。`回滚后库里` 那一列是事务真生效的证据，不是装饰。本轮两趟：

| 走法 | 墙钟（本轮两趟） | 读数 |
| --- | --- | --- |
| FastSql L1 装载通道 ∶ 事务内 | 29.86 – 32.62 ms | `CopyChannel ∶ 2000 行 ∶ 回滚后库里 0` |
| FastSql L3 多值 ∶ 事务内 | 1178.00 – 1343.60 ms | 7 块 ∶ 回滚后库里 0 |
| SqlSugar `Fastest.BulkCopy` ∶ 事务内 | 30.81 – 42.20 ms | 回滚后库里 0 |
| SqlSugar 内联 ∶ 事务内 | 39.43 – 47.37 ms | 1 句 ∶ 回滚后库里 0 |

**事务那一层两家都没有可见开销**：FastSql L1 事务内 29.86–32.62 与它事务外的四趟 30.66–36.81 同档（还略低，本文不解释，只报读数）；对面装载事务内 30.81–42.20 对它事务外的 28.97–96.03 同档。两边的四趟/两趟区间都重叠，名次不报。

然后是本轮真正要钉的一颗开关。第五张表 `cmp_chk` 与窄表逐字相同，只多一颗 `CHECK (Amount >= 0)`，往里塞一行 `Amount = -5`：

| 探针 | 结果 |
| --- | --- |
| SqlSugar ∶ 事务外 ∶ `OffIdentity()` 开 | 没报约束错 ∶ **违约行 1 颗进了库**（约束没查） |
| SqlSugar ∶ 事务内 ∶ `OffIdentity()` 关 | **被拒** ∶ `INSERT 语句与 CHECK 约束"CK_cmp_chk_Amount"冲突` ∶ 库里 0 |
| SqlSugar ∶ 事务内 ∶ `OffIdentity()` 开 | 没报约束错 ∶ **违约行 1 颗进了库**（事务内读到 1 颗，回滚后 0） |
| FastSql ∶ 事务外 ∶ L1 默认开关 | **被拒** ∶ 同一条 CHECK 冲突 ∶ 库里 0 |
| FastSql ∶ 事务内 ∶ L1 默认开关 | **被拒** ∶ 同一条 CHECK 冲突 ∶ 库里 0 |

三件事：

- **§3.2 那条"身份列与约束检查二选一"从读码结论升成实测。** 源码上 `SqlServerFastBuilder.cs:52-54` 那一支在事务内且 `OffIdentity` 开着时只传 `KeepIdentity`，于是 `:59` 的 `CheckConstraints` 没了——本轮第二、三行就是这一支的两半：不开 `OffIdentity` 时事务里查约束（违约被拒），开了就**不查**（违约行照样进库，还进了事务）。
- **对面事务外根本不查约束**（第一行），这是 `SqlBulkCopy` 的默认语义（`CheckConstraints` 不在默认组合里），不是它的 bug；但它与 `OffIdentity` 叠出来的效果是**"带身份列的装载"在事务里也不查约束**，这跟"事务里就该被约束保护"的直觉相反。
- **本家的事务内外都被拒**：L1 恒传 `CheckConstraints | FireTriggers`，两颗位从驱动自己的枚举读（`SqlServerBulkCopy.cs:152-156` 的理由注释、`:208-217` 的 `Flags`），与是否在事务里无关，也没有一颗口能让你关掉它。这就是先前真库见证里"L1 与逐条等价"的行为来源，本轮第一次从约束面上量到。

一句边界：这一档只用了 `CHECK` 一种约束，外键、唯一索引、默认值、计算列没试；`OffIdentity` 关 + 事务外的组合没测（那一支会因身份列而整体失败，与约束无关）；对面那条老的 `Insertable(...).ExecuteBulkCopy` 路径没测（§3.2 说它完全没有 KeepIdentity）。

### 2.13 走过装载之后，对面的 SQL 留痕还活着吗

这一格是本轮翻车翻出来的：量具第一版把事务段放在中间，跑完之后**后面每一格对面的留痕全是空白**（`OnLogExecuting 响 0 次`、文本 `(无文本)`），一开始看着像对面"装载之后一直不留痕"，其实是一次异常把开关留在了按灭态。于是本轮把它做成一节，每颗探针先把 `IsEnableLogEvent` 拨回 true 再走一步活，然后用一颗普通查询当探针数它响几次——**这样每一步的死活只属于那一步**。本轮两趟，两份输出的这一节逐字相同：

| 走过这一步之后 | `IsEnableLogEvent` | 探针 |
| --- | --- | --- |
| 普通 `Insertable`（10 行） | True | 响 1 次 |
| `Ado.BeginTran + RollbackTran`（不装载） | True | 响 1 次 |
| 普通 `Insertable` **抛异常**（违约行撞 CHECK，不走装载） | True | 响 1 次 |
| `Fastest.BulkCopy` 成功 ∶ 事务外 | True | 响 1 次 |
| `Fastest.BulkCopy` 成功 ∶ 事务内（随后回滚） | True | 响 1 次 |
| `Fastest.BulkCopy` ∶ 事务外 ∶ 违约行（§2.12 已量：约束不查 ⇒ 这一支不抛） | True | 响 1 次 |
| `Fastest.BulkCopy ∶ OffIdentity` 开 ∶ 事务外 / 事务内（值合规） | True | 各响 1 次 |
| `Fastest.BulkCopy` 2000 行 ∶ 事务内（与 §2.12 同形） | True | 响 1 次 |
| **`Fastest.BulkCopy` ∶ 事务内 ∶ 违约行（抛）** | **False** | **响 0 次** |
| 再来一次**成功**装载（接在上一行之后，不拨回） | **False** | **响 0 次** |
| 手动 `IsEnableLogEvent = true` 之后 | True | 响 1 次 |

**能读出来的只有这一条组合**：让日志停摆的不是装载、不是事务、也不是异常，而是**"事务内的装载抛异常"**这一支——它跳过 `FastestProvider.cs:530` 的恢复（`End` 不在 `finally` 里，调用链 `:497-506` 直排），把开关留在 `Begin`（`:545`）按灭的那一态；而且**它不会自己回来**：紧接着再来一次正常装载也不恢复（上一行倒数第二格），只有手动赋值能救。普通 `Insertable` 抛异常不影响它，纯事务开合也不影响它，两行都在这里验过。

对本家的意义写在两处：① 审计/留痕类调用方如果按"日志流有没有内容"判断连接健康，对面这一支会给出假绿（流断了，但连接照用）；② FastSql 的留痕没有这种全局开关可被掐——`Aop.Executed` 与 `BulkReport` 都是逐次调用交回的对象（§2.9），异常路径上不恢复任何状态。这是形状之差带来的可用性差别，不是对面配错了什么：它的 `IsEnableLogEvent` 本来就是公开可写的开关（`Interface/IAdo.cs:29`）。

一句边界：本轮只测了 `Fastest.BulkCopy` 一条装载口在事务内抛异常的形状（窄表 + `cmp_chk` 违约行），`BulkMerge`/`BulkUpdate` 那两支同样调 `Begin`/`End`、本文**没测**；`isLog` 那颗快照字段在多次嵌套装载下的行为也没测。

## 3. 读码对照：六面

下面每一格都是"两边各自在源码里怎么做的"，配 `文件:行`。FastSql 的坐标对工作树现状负责；SqlSugar 的坐标对 §0 那份 211 快照负责。

### 3.1 批量写入的分块

| 面 | FastSql | SqlSugar |
| --- | --- | --- |
| 分块依据（默认那一档） | 参数上限（每块行数 = 上限 ÷ 列数，压在 2100 以下） | **固定行数**：默认 200，列数 >20 ⇒ 100，>30 ⇒ 50 |
| 出处 | `InsertEngine` 的批量决策段（§12.1 阶梯） | `SqlServerInsertBuilder.cs:34-42`（与基类 `InsertBuilder.cs:181-189` 同一套数） |
| 有没有第二条写法 | 阶梯今天只有三档可选加一档自动：`BulkMethod { Auto, CopyChannel, MultiValue, SingleRow }`（`src/FastSql.Abstractions/Providers/Bulk.cs:14,17,20,23`），**L2 数组绑定与 L4 临时表在这一家没有实现**（`SqlServerBulk.cs:8-9` 明写），所以枚举里根本没有那两个成员 | 有，而且是**显式 opt-in**：`Insertable(list).UseParameter()`（口在 `InsertableProvider.cs:519`）换到参数化那一族，那颗口里**才**有 2100 的处理——`ParameterInsertable.cs:171-187`：`pageSize * count > 2100` 连压三档 50⇒20⇒10，起点 `pageSize` 写死 100（`:129-132`）。**本轮实测过这一条**：每句 100 行 / 600 颗，宽表每句 50 行 / 1450 颗，两句都压在 2100 以下（§2.4） |
| 值的写法 | 参数绑定 | 默认那一条**内联字面量**，例外才回落到参数（`InsertBuilder.cs:291` 起：服务器时间、坏小数、`SqlParameterDbType` 转换器） |
| 多段的拼法 | 一块一句，**分次往返** | 每段独立 `INSERT`，段间 `;\r\n`（`SqlServerInsertBuilder.cs:65`），**全串进一颗命令文本、一次往返**；SqlServer 末尾还追一句 `select SCOPE_IDENTITY();`（`:70`） |
| 要回主键时 | 与阶梯无关，走 returning 那一族 | `IsNoPage && IsReturnPkList` 时把 `pageSize` 抬成全量、**不再分块**（`:43-46`）——2000 行要回 PK 就是一颗 2000 段的大文本 |
| 身份列 | L1 需显式 `KeepIdentity`，列类型不过闸门就整批回落 | `OffIdentity`：整句前后包 `SET IDENTITY_INSERT <表> ON/OFF`（`:74-78`）；`.PageSize(n)` 另有一档，默认 1000 且**外层自开事务**（`InsertablePage.cs:27,29-52`） |

一句话概括这一面：**SqlSugar 的 L3 是"一次往返送一大坨本文"，FastSql 的 L3 是"几次往返送参数"。** 而 §2.5 那趟扫把这一面真正的账单指出来了：**贵的不是"列多"、也不是"往返多"，是"一颗命令里塞了几颗参数"**。两家的默认块在这一点上恰好相反：FastSql 永远贴墙（每命令颗数 = 预算 × 0.9 = 1890，与表宽无关），SqlSugar 的参数化那一档从 100 行起步、只在超过 21 列时才降档（6 列实体 ⇒ 每命令 600 颗，离墙远），所以**窄表上它比 FastSql 便宜、宽表上两家一样贴墙**（§2.4、§2.5）。至于它的默认那一档：内联字面量，每命令颗数恒为 0，这条曲线它根本不碰（§2.1）——**但它碰另一条：本文每趟都新，服务端每趟都得编译**，§2.6 量到这一笔是 3–20 倍，而同样的换数据对参数化那一族几乎不收钱。所以这一面的完整说法是：**两家各有各的曲线，FastSql 的在每命令颗数上，SqlSugar 默认那条在"值变不变"上**；只在"每趟同一批数据"的台面上比，才只见前一条不见后一条。

### 3.2 装载通道

| 面 | FastSql | SqlSugar |
| --- | --- | --- |
| 走的是哪颗实现 | 反射解析驱动里的 `SqlBulkCopy` 类型（`SqlServerBulkCopy.cs:161`，先试 `Microsoft.Data.SqlClient` 再试 `System.Data.SqlClient`） | **两条路**：`Fastest<T>().BulkCopy` 走 `SqlServerFastBuilder.ExecuteBulkCopyAsync`（`SqlServerFastBuilder.cs:19-35`，builder 由类型名字符串反射装配，`Private.cs:62-65`）；老的 `Insertable(...).ExecuteBulkCopy` 走 `SqlServerBlukCopy.cs`。两条都**编译期直接** `new SqlBulkCopy(...)`（`using Microsoft.Data.SqlClient` 在 `SqlServerBlukCopy.cs:4`） |
| 为什么形状不同 | provider 层零编译期驱动引用（§3 的分层规矩），所以反射 + 缓存是代价 | 主库工程直接引驱动（`SqlSugar.csproj:32`，5.2.1）；NoDrive 只是同一颗 dll 换包装（§0），所以它同样带编译期引用——实测可用就是这一条的旁证 |
| 列对齐 / 表形状从哪来 | 自己带 `ColumnMappings` 按名字映射（`SqlServerBulkCopy.cs:64` 那条注释写明了不给映射时按序号对齐的风险） | **向库里要一份 schema**：实体列表那一档是 `Where(it => false).Select("*").ToDataTable()` 拿空表壳，并按 CLR 类型缓存（`Private.cs:73-85`）；DataTable 入参那一档每次 `select top 0 * from <表>`（`FastestProvider.cs:507-520` → `SqlServerBlukCopy.cs:82`） |
| 事务内 | 由 `BulkConnection` 把连接与事务一起交给通道；本轮实测：事务内 29.86–32.62 ms 与事务外同档，且**约束照查**（§2.12） | `Ado.Transaction != null` 时 `SqlBulkCopyOptions.CheckConstraints`（`SqlServerBlukCopy.cs:112`、`SqlServerFastBuilder.cs:59`）；无事务时默认构造（`:108`、`:47`）。本轮实测：事务内装载 30.81–42.20 ms 与事务外同档（§2.12） |
| 身份列 | 显式 `KeepIdentity`，且列类型不过闸门就整批回落 | `Fastest` 认 `OffIdentity()`：开了就传 `SqlBulkCopyOptions.KeepIdentity`（`SqlServerFastBuilder.cs:41-43`，事务内 `:52-54`）；老那条 `ExecuteBulkCopy` 路径**完全没有** KeepIdentity（`SqlServerBlukCopy.cs:103-120`） |
| 可配的开关 | `CheckConstraints \| FireTriggers` 两颗都从**驱动自己的枚举**上读（`SqlServerBulk.cs:31` 的 `Describe`，读枚举那一段在 `SqlServerBulkCopy.cs:210`），事务内外恒传（§2.12 两行都被 CHECK 拒了） | 没有可配面。而且有一条**源码上的互斥，本轮实测坐实**：事务内开 `OffIdentity()` 那一支只传 `SqlBulkCopyOptions.KeepIdentity`（`SqlServerFastBuilder.cs:54`），同一支里 `CheckConstraints`（`:59`）就没了——身份列与约束检查二选一。数在 §2.12：同一批违约行，`OffIdentity` 关 ⇒ 被拒，`OffIdentity` 开 ⇒ 进库（事务里读到 1 颗，回滚后 0） |
| 超时 | 驱动的 `BulkCopyTimeout`，取不到就用请求里的 `TimeoutSeconds` | `copy.BulkCopyTimeout = Context.Ado.CommandTimeOut`（`SqlServerBlukCopy.cs:118`） |
| 结果 | 结构化 `BulkReport` | `int`（且是 `DbColumnInfoList.Count`，即"送了几行"，不是"库里落了几行"，`SqlServerBlukCopy.cs:40`） |
| 早退 | 空批不进通道 | `DbColumnInfoList` 空 ⇒ 直接返回 0（`:21`） |
| sync-over-async | 两条口各自到底 | 同步 `BulkCopy(List<T>)` 是 `BulkCopyAsync(...).ConfigureAwait(true).GetAwaiter().GetResult()`（`FastestProvider.cs:59`）；`ExecuteBulkCopyAsync` 在 DataTable 那一支里**同步**调 `WriteToServer()`（`SqlServerBlukCopy.cs:49`） |
| 分块 | 由阶梯一次性决定，`BulkReport` 里交回 | `Fastest.Size` 决定，`Size > 0` 时按 Size 切成多次复制（`FastestProvider.cs:63-74`） |
| 附加行为 | 无 | `DateTime.MinValue` 夹到 `1753-01-01`（`SqlServerBlukCopy.cs:133`） |

这一面的实质差别是**那一次"要 schema"的往返放在哪**：SqlSugar 实体列表那一档把它缓存住了（每个 CLR 类型一次），FastSql 自己带映射表所以一次都不发。这也决定了 §2.1 与 §2.3 那两档装载通道的读数里**不含**取壳查询——三趟取最小把首趟的一次性开销挑掉了，而 FastSql 的首次反射装载同样被挑掉，两边同待遇（见 §1 计时纪律）。宽表上这一档两轮都跑过（§2.3）：FastSql 30.84–37.79 ms 对 SqlSugar 30.51–35.99 ms，四趟区间重叠，**表宽对两家的装载通道都不是事**。

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

事务与装载的交叉本轮补跑了（§2.12）：两家的整批装载放进事务再回滚，`回滚后库里` 都是 0（本家走 `orm.BeginTransaction()` + `tx.Rollback()`，对面走 `Ado.BeginTran()` + `RollbackTran()`），墙钟与事务外同档。这一格只证了"装载真进了事务"，没证嵌套、没证 `RequiresNew`，也没证高并发下的锁形态。

### 3.4 分页

| 面 | FastSql | SqlSugar |
| --- | --- | --- |
| 限行写法 | 一条 `OFFSET @p0 ROWS FETCH NEXT @p1 ROWS ONLY`：首页 `size + 1` 探 `HasNext`（`QueryEngine.cs:1225`），第二页起 `Auto` 换成 `CountStar`、两条语句（`QueryEngine.cs:1264-1271` 的 `Resolve`，**本轮实测到第二页真发两句**，§2.7）。**本家在 T-SQL 上没有 `TOP` 那一形**（`SqlServerDialect.cs:39-53`，方言册金标 `SqlServerPagingTests.cs:49`），先前本文写的"单条 TOP n+1"是读歪了，见 §5.6 | **三档**：只有 `Take` ⇒ `TOP n`；`Take==1` 且首页 ⇒ 也走 `TOP`；一旦带 `Skip`（第 2 页起，**以及 `ToPageList(1,20)`——它的 Skip 是 0 而不是 null**）⇒ `ROW_NUMBER() OVER(…) AS RowIndex` 外壳再 `BETWEEN`（`SqlServerQueryBuilder.cs:60-77`，外壳模板 `QueryBuilder.cs:162-168`）。本轮抓到第二页原文：`… WHERE RowIndex BETWEEN 21 AND 40`（§2.7） |
| 没有 ORDER BY 时 | 由方言决定，不替你补一个**真的序**：语法要求 ORDER BY 时补 `ORDER BY (SELECT NULL)` 占位（`DialectBase.cs:461-470`，`PagingRequiresOrdering` 在本家为 true，`SqlServerDialect.cs:31`）。本轮实测到它真发在第二页那句里（§2.7）。翻页仍不保证不漏不重——本文没实测，只报文本 | **注入 `ORDER BY GetDate()`**（`SqlServerQueryBuilder.cs:60` 一行写死）：窗口函数要有排序，它给了个**每行都不同的表达式**，本轮两趟抓到的原文都是这一颗（§2.7）。翻页在并发写压下不保证不漏不重，本文**没实测**这一条，只报源码写法与实测文本 |
| OFFSET/FETCH 作为单独一档 | 本家在 T-SQL 上**默认就发这一形**（首页与第二页都是，§2.7），所以没有"另开一口"这回事 | 有专门一套口 `ToOffsetPage`／`ToOffsetDataTablePage`（`QueryableHelper.cs:1740-1744` 拼 ` OFFSET … ROWS FETCH NEXT …`，调用点 `QueryableExecuteSql.cs:477,491,497`），**不是 `ToPageList` 走的路**；本轮走的是 `ToPageList`，那套口没测 |
| 默认条数 | 由 `PagedOptions` 选（`IQuery.cs:19`） | 由调用形态选（写 `ref total` 就要两条） |
| 计数 | 显式换 `CountMethod` 才发第二句；`StripOrderingForCount` 默认去掉排序。本轮抓到第二页那两句的原文：`SELECT … ORDER BY (SELECT NULL) OFFSET @p0 ROWS FETCH NEXT @p1 ROWS ONLY` 与 `SELECT COUNT(*) FROM [cmp_fast] AS [t0]`（§2.7） | `Clone().Count()` 再 `Clone().ToDataTablePage()`（`QueryableExecuteSql.cs:509-515`）；计数模板是 `SELECT COUNT(1) FROM ({0}) CountTable`（`QueryBuilder.cs:634-641`），本文实测那句被化简成了直查表——本轮两趟抓到的原文都是 `SELECT COUNT(1) FROM [cmp_sugar]` |

两家在这一面**能力对等，默认相反**：FastSql 默认省钱（首页一条语句、无总数），SqlSugar 默认给全（两条语句、有总数）。形状上各有一笔本文量不到的账：FastSql 首页只多取一行数据（`FETCH NEXT size+1`），SqlSugar 第 2 页起要把整行集过了窗口函数再剥壳——本轮两副原文都在 §2.7 摆着，孰轻孰重本文不判（没量）。

### 3.5 留痕 / AOP

| 面 | FastSql | SqlSugar |
| --- | --- | --- |
| 事前钩子 | `Aop.Executing` 声明在 `src/FastSql.Abstractions/Aop/FastSqlAop.cs:13`，**`src/` 下派发点 0 处**（本文用 grep 复核过）——挂在规格 §10.1 的名单上，是个空挂 | `OnLogExecuting` 是 `Action<string, SugarParameter[]>`（`ConnectionConfig.cs:91`）：返回 void，**不能取消、不能回退**，但拿到的参数数组是引用，改值得到的是改后的执行。每语句一次（`AdoProvider.cs:1556-1593` 的 `ExecuteBefore`→`LogEventStarting`，调用点散在 `:487,528,562,600,638,682,721`） |
| 事前改写 | 没有这一档（上一条既然没派发，就什么都做不了） | 另有专口 `OnExecutingChangeSql`（`ConnectionConfig.cs:93`，`Func<SQL, 参数, KeyValuePair<SQL, 参数>>`；接线点 `AdoProvider.cs:82`）——**能换 SQL 也能换参数** |
| 事后钩子 | `Aop.Executed` 只装真实 SQL（`ExecutedEvent` 带 `DialectSql` + 耗时） | `OnLogExecuted`（`ConnectionConfig.cs:92`）与装载收尾共用一条流（`FastestProvider.cs:533`） |
| 装载期能否看见 | 不产 SQL，产 `BulkReport` | **看不见**：装载期间日志被按灭（`FastestProvider.cs:545`），前后各留一句伪 SQL（`:548`、`:533`） |
| 装载抛异常之后还能不能看见 | 没有可被掐的开关：留痕是逐次调用交回的对象（`Aop.Executed` / `BulkReport`），异常路径不改任何全局状态 | **不能，且不会自己回来**：`End` 里那句恢复（`FastestProvider.cs:530`）不在 `finally` 上，调用链 `_BulkCopy` 是 `Begin → ExecuteBulkCopyAsync → End` 直排（`:497-506`），异常一抛开关永久留在 false。本轮十颗探针逐一步验过，只有"事务内的装载抛异常"这一支会死，成功装载（事务内外）、普通 `Insertable` 抛异常、纯事务开合都不死；再走一次成功装载也不恢复，只有手动赋值能救（§2.13） |
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

建表这一面本文**没做实测**（§0 与 §1 明写五张对照表全由裸 ADO 建，就是为了不把建表聪明度混进写入对照；第五张 `cmp_chk` 只服务 §2.12 那几颗约束探针）。所以只报形状，不排名次。

## 4. 名次：分四档说

区间重叠的格子一律放进"持平"，这是 §1 第 4 条的规矩。本轮没有格子从别档搬进持平（持平那五行里只有"装载通道"和"逐条写入"上一轮就在，本轮继续重叠），本轮动的是**名次句本身**：§2.8 那句"对面稳定快约 10%"被四趟区间作废、§2.5 中间两档（每句 100 行与 50 行）的排序因重叠而收回、新增的 §2.12 墙钟与 §2.6 的反超都只钉下界不写"更快"。另有一处是加条件而不是挪档：**§2.6 把"内联对面拿不走"这句写死的名次限定成了"只在可复用的数据上"**（见 §5.5）。

| 档 | 内容 |
| --- | --- |
| **FastSql 明显在前** | 事务面：保存点存在且两条路（`AdoExecutor.cs:309-314`）+ 显式传播 `Requires`/`RequiresNew` + 失败外抛，对面**全树检索 `SAVEPOINT` 命中 0**、嵌套静默（`AdoProvider.cs:265-270`）、`UseTran` 吞异常（`:343-366`）、`using` 那一档 `Dispose` 无条件回滚（`SqlSguarTransaction.cs:48-51`）；装载的决策可交回（`BulkReport` vs 一个 `int`）；**装载开关彼此独立，而且这一条本轮从数上坐实了**（本家 `CheckConstraints`/`FireTriggers` 恒传、事务内外都被约束拦，对面事务内一旦开 `OffIdentity` 就**不查约束、违约行进库**，§2.12 + `SqlServerFastBuilder.cs:52-59`）；留痕不可被掐断（对面"事务内装载抛异常"会把 `IsEnableLogEvent` 永久留在按灭态、后续成功装载也不恢复，§2.13）；约定优于配置的实体映射（注解更少，§3.6）；AOT 口径（provider 零编译期驱动引用，见 §3.2 的代价与理由）；**同形状的参数化多值**（每句 100 行 / 600 颗：308.33–480.78 ms 对 434.22–537.19 ms，下界快 29%，§2.5 第 1 条——**区间重叠，所以"快约四分之一"这句只在下界上成立，先前把它写成不打折的优势是过誉**）；**换数据之下的最省形状**（每句 20 行 165.10 ms 对面内联 168.32 ms，§2.6，同样只钉下界） |
| **持平** | 装载通道墙钟（7 列表 30.66–36.81 对 28.97–96.03；30 列表 30.84–37.79 对 30.51–35.99，§2.1、§2.3——两副区间都重叠，宽表不掉速这条两轮都量到）；逐条写入（282.31–380.44 对 296.62–342.85，两副区间重叠；本轮两趟本家与对面各胜一副，§2.2）；事务内装载（本家 L1 29.86–32.62 对对面 30.81–42.20，且两家 `回滚后库里` 都是 0，§2.12——注意形制：这一档含 begin/rollback）；转义正确性；分页能力面（首页 1 条 vs 2 条，第二页两家都 2 条，且第二页的换档本轮实测到了，§2.7）；读侧物化（1.54–1.73 对 1.36–1.59，区间重叠 ⇒ 只报"同一量级、对面下界略快"，先前那句"稳定快 10%"作废，§2.8） |
| **SqlSugar 明显在前** | **内联字面量那一档策略——但只在"每趟同一批数据"的台面上**：同一批 2000 行，本家最便宜的参数化形状 150.53 ms 对它 38.19 ms（3.9 倍），本家默认形状 1 215.39 ms 对它（32 倍），宽表那一格 41 倍（§2.1、§2.3、§2.5 第 2 条），而 FastSql 今天没有对应物；**可一旦每趟换一批全新值，这一档就从对面手里搬走**：对面内联 168.32–250.35 ms，本家每句 20 行 165.10–226.04 ms，下界本家反超（§2.6）。所以这一行今天只能读成"**对面在可复用的数据上占优**"，读不成"对面的批量更便宜"；把多段塞进一次往返（同一条限制）；**事前改写 SQL 的口**（`OnExecutingChangeSql`，对面 §3.5 那格；FastSql 的 `Executing` 是空挂）；公开面数量与生态（导航属性、主从分离、缓存、分表、`DynamicBuilder`、批量 upsert，见 §6） |
| **各走各路** | 分页默认（省钱 vs 给全）、以及第二页上各补的那颗排序（`(SELECT NULL)` 占位 vs `GetDate()` 表达式，§2.7）；留痕流形状（SQL 一条流 + 报告另一条 vs 一条流两种内容，对面缓存命中时根本不留痕、装载抛异常时留痕整条停摆）；批量正确性的落点（转义函数 vs 每命令颗数）；**块大小的两种问法**（本家：2100 当上限并且贴墙 ⇒ 每命令恒 1890 颗；对面参数化那一档：从 100 行起步、只在 >21 列时降档 ⇒ 窄表离墙远、宽表同样贴墙，§2.4、§3.1）；**两条不同的曲线**（本家贵在第①颗参数进第几颗命令里，对面贵在第②趟数据是不是新的，§2.5 + §2.6——同一台实例上两条都在，只是各自只在自己的默认路径上碰其中一条） |

**一句不许漂的话**（本轮又改过一次，因为数据又改了它的方向）：FastSql 今天在装载通道之外没有争议的速度优势只剩**同形状参数化的下界**（快 29%）与**换数据之下的最省形状**（反超 2%），两处都只钉得住下界；它的默认 L3 块贴着自己那颗预算上限，而 §2.5 量出来**那正好是曲线上最贵的一段**——同一批活改成每句 20 行是 151–224 ms，换数据那一档也是 165–226 ms（`rowsPerRoundTrip` 一句话的事）。所以先前那格"参数化多值 24.8 倍差距"要读成**策略差**而不是**内核差**：内核那一半本家赢，块大小那一半本家输在自己的默认值上。而对面原本被本文判成"拿不走的东西"的那一档——**内联**——本轮也被限定住了：它的红利来自服务端复用同一颗计划，值一变就收走（§2.6）。这一条不构成"追上就行"的安慰：它意味着 §12.3 那颗公式少了一项（每命令颗数的代价），也意味着 L1 之外的写路径还需要一次专门的调优裁决——见 §5.4 与 §6。

## 5. 被这次对照翻掉、坐实或新钉住的旧说法

### 5.1 打折：规格里那句"SqlSugar 固定行数分块 ⇒ 受参数上限约束"

规格 `docs/fastsql-orm-design.md:1510` 那句话原文是：**"多值语句的参数个数 = 行数 × 列数。500 行 × 30 列 = 15000 参数 —— 在 DM/SQL Server 上直接爆。FreeSql / SqlSugar 都用固定行数分块，换宽表就出事。"** 前一半（我们自己的算法）没错，后一半这次对照下来要拆成两半说：

- **翻掉的是默认那一条。** 分块规则确实是固定行数（`SqlServerInsertBuilder.cs:34-42`），但默认路径把值全部内联（`:61` 走 `FormatValue`），那颗命令文本里**一颗参数都没有**（§2.1 的"参数 0 颗"），2100 这一关它进不去。所以正确陈述是：**SqlSugar 的默认批量不受参数上限约束，受的是"本文长度 + 服务端解析 + 计划缓存"的约束**——这三样里前两项上一轮量过（§2.5 那颗手拼对照：内联、每句 315 行、本文比参数化那一档还长，同数据 17.91–37.76 ms 跑完同一批活，比参数化那一档便宜一个数量级），第三项**本轮量出来了，而且它是带条件的**：值一变，那颗手拼对照涨到 355.82–357.83 ms、对面自己的内联涨到 168.32–250.35 ms（§2.6）。所以那句纠正要写成：**默认路径不受参数上限约束，受的是"每趟的本文新不新"约束**；本文长度本身几乎不收钱（同数据下最长的那副反而最快），收钱的是复用不上计划。
- **坐实的是它另外那一条口，而且是从数上坐实的（窄表六趟、宽表四趟，§2.4）。** `Insertable(list).UseParameter()`（口在 `InsertableProvider.cs:519`）换到参数化实现，那里**确实**按 2100 分块：`ParameterInsertable.cs:129-132` 起点写死 100 行，`:171-187` 拿 `pageSize * count > 2100` 连压三档 50⇒20⇒10。实测两副表各给出一句不超过 2100 的形状（6 列实体 ⇒ 每句 100 行 / 600 颗；30 列 ⇒ 每句 50 行 / 1450 颗，§2.4）。所以"对面也有参数上限意识"这句不算冤枉它，只是**默认路径用不上**，并且它的问法是"撞不撞墙"而不是"多少行划算"。

规格这一轮**不动**（本轮没拿到记账例外），此处只把纠正落在本文，等下一轮再并回规格。

### 5.2 坐实：`Aop.Executing` 是空挂

用法文档 §2.5 那张钩子表把 `Executing`（`FastSqlAop.cs:13`）与 `Executed`（`:14`）**并列为"观察"类**，而同节的"没有的"那段只点了 `QueryBuilding`、`CommandCreating` 两颗桩。这次实测把它**补上第三颗**：量具挂在 `orm.Aop.Executing` 上的语句计数器对每一次调用都报 0，换成 `Executed` 才有数；再按 grep 复核，`src/FastSql.Core` 下 `.Executing` **0 处引用**。声明在场、派发点没有——这是一颗待办，不是口径问题。对面同一位置（`OnLogExecuting`）是有派发点的（`AdoProvider.cs` 里七处调用），所以这一格是真差距，不是形状之差。

（一句可核性上的实话：本轮补格之后，量具里那颗 `Executing` 计数器已经摘掉了，现在 `Program.cs` 只剩 `orm.Aop.Executed += …` 那一行。重跑 §1 复现不出这条，要复现得临时再挂一次。）

### 5.3 报告：顺手撞到的两处本家账目对不上

同一次核对里撞到两处文档/注释与工作树对不上的地方。本文只**报告**，不改那两份，也不改 `src/` 里的注释：

1. `TableRemap` / `Register<T>` / `RegisterTable<T>` 在规格里有名，`src/` 下 **0 处命中**（本次用 grep 复核，连 `RegisterTable` 与 `T Register<` 两种写法一起找）；
2. **本家自己的注释与实测对不上**：`src/FastSql.Providers.SqlServer/SqlServerBulk.cs:9` 写"500 行 × 30 列在 2100 上限下是 5 块"，而 §2.3 量到的是 **8 块、每块 65 行**（29 颗参数 × 65 = 1885，压在 2100 下；要 5 块就得每块 100 行 = 2900 颗，超预算）。按那颗 `ParameterBudget => 2100`（`SqlServerBulk.cs:29`）反推，注释里的"5 块"算错。

顺带一条**被本次核对否掉的旧旗标**：先前记过"用法文档 §3.2 引的 `XuguProvider.cs:148` / `Gbase8sProvider.cs:162` 行号已漂"，这次逐行读过去——那两行正是 `public IBulkStrategy? Bulk => null;`，与文中"这三家的 `Bulk` 是 null"逐字对上。**行号没漂，那条旗标作废。**

### 5.4 新钉住：本家自己的默认块大小落在曲线上最贵的那一段

这一条不是旧说法，是上一轮新量出来、本轮又加了一重的、指向本家的一条账，写在这里以免被读成"对面的胜利"：

`docs/fastsql-orm-design.md` §12.3 那颗分块公式的输入只有两项——列数与参数预算（`ParameterBudget * 9 / 10 / cols`，落在 `src/FastSql.Core/Write/StatementEmitter.cs:27`），也就是**"别撞 2100"。它没有"每命令多少颗划算"这一项**，而 §2.5 量出来这一项是存在的、而且是超线性的：同一批 2000 行 × 6 列、总 12,000 颗参数，每句 1 890 颗要 1 215.39–1 404.67 ms，每句 120 颗只要 150.53–223.74 ms。本家默认给的是前者（贴墙）。

本轮这一条被加强了两次，两笔都记在这里：① **换数据之后它仍然贵**（1 388.55–1 513.95 ms，对同形状最省档的 165.10–226.04 ms ⇒ **8.4 倍**，§2.6）——先前那句"内联把它比下去了"还给对面一半分量，这一笔是纯本家自己的账，对面不在场；② 四趟区间之下中间两档（每句 100 行与 50 行）重叠，所以**"往下压到多少行才划算"本文报不出一个精确档位**，只能报"两端差 8 倍、贴墙那一端在最贵处"。

**本文不改代码，也不改规格**，三个理由：① 这不是缺陷——阶梯每一档都按请求交付、`rowsPerRoundTrip` 也确实能压（`IInsert.cs:44-51`），要动的是**默认值裁决**；② 那颗公式在规格里是钉了行的，改它属于记账那一轮，本轮没有例外；③ 一项超线性代价要在 16 家 provider 上取一个通用默认，得先有跨家的量法（本文只证了 T-SQL 这一家、一台实例）。要接的话，形状大概是三选一：给 `MultiValue` 加一颗"每命令颗数"的目标值；或只在 `BulkReport.Reason` 里把块大小的来路写清、把裁决留给调用方；或什么都不改，在用法文档写一句"大批量时显式给 `rowsPerRoundTrip`"。**本文只把账挂在这里，不代作这个决定。**

### 5.5 限定：先前写下的"对面真正拿不走的东西是内联"

上一版在 §4 那句"不许漂的话"里写下过 **"对面真正拿不走的东西是内联这一档"**（当时的数是 6 倍、23 倍、43 倍）。本轮 §2.6 给这句加了条件，条件本身要记下来，因为它是本文自己写歪过的一次：

那几副倍数的台面上，三趟计时用同一批值 ⇒ 内联那一档每趟写出去的是**逐字相同**的本文，服务端复用的是什么本文，本文先前没区分过。值一变（每趟 `shift += 1000`，形状与颗数一颗不动），对面内联从 52.63–56.14 ms 掉到 168.32–250.35 ms，手拼那颗从 17.91–37.76 掉到 355.82–357.83，而参数化那一族（两家 + 手拼）不动。**所以那句要改成：内联这一档对面确实没有对应物，但它的红利是"数据可复用"租来的；真实业务里每批值都不同的装载，本家最省的形状在上下界上都能追平甚至反超（165.10 对 168.32，§2.6）。** 反过来，那句也被本轮钉得更实了：**本家默认那块贴墙的形状在两种台面下都输**，这一笔不需要对面在场。

一句边界：这一节没有直接读计划缓存（没进 `sys.dm_exec_cached_plans`），"复用/编译"是从"只有内联那一族随值变动"这条对照推出来的（§2.6 末段、§6）。

### 5.6 翻掉：本文自己那句"FastSql 首页单条 `TOP n+1`"

本文上一版在它自己的 §2.6（分页那一格，本轮挪到 §2.7）与 §3.4 两处都写过本家首页发的是"单条 `TOP n+1`"。本轮读到了发射器，那句是**本文读歪的**：本家在 T-SQL 上**根本不产 `TOP`**——`SqlServerDialect.cs` 全文没有 `TOP`，分页只有一条路 `EmitPaging`（`:39-53`：`EmitPagingOrdering` + `OFFSET … ROWS` + `FETCH NEXT … ROWS ONLY`，缺省偏移落成常量 `0` 而不是省略子句，注释就写着"省略 OFFSET 会得到语法错"），既有金标 `tests/FastSql.Tests.Dialects/SqlServerPagingTests.cs:49` 钉的正是 `OFFSET 0 ROWS FETCH NEXT @p0 ROWS ONLY`。

所以正确的说法是：**首页一条语句没有错（本轮实测仍是 1 句、`Total` 为空、`HasNext` 为 True），但它的形状与第二页同形——`OFFSET @p0 ROWS FETCH NEXT @p1 ROWS ONLY`，`@p0` 是 0，`@p1` 是 `size + 1`。** 那句"多取一行探 HasNext"（`QueryEngine.cs:1225`）本身没错，错的是本文给它配的语法名。这一笔要翻的原因是它会误导读者去方言里找一颗不存在的 TOP 分支；可核性上也得说清：**首页那一句的原文本轮没抓到**（量具的样本袋在第二页之前清空了），这一句的根据是发射器代码 + 既有金标 + 第二页的实测文本三者一致，不是首页见证（§2.7 第一条已注）。

## 6. 这次没量的，别当成比过了

- **跨趟长尾没解释**：SqlSugar 内联那一档四趟是 53.48 / 537.38 / 38.19 / 43.57 ms（§2.1），`Fastest.BulkCopy` 是 31.50 / 96.03 / 28.97 / 44.75。本文只把名次钉在区间下界，**没有诊断这一笔**（服务端忙、计划缓存被换掉、网络抖动三可）。凡本文用"稳定"形容的档位，都是四趟跨度在 21% 以内的那些。
- **参数代价的曲线只取了四个点**（每命令 120 / 300 / 600 / 1890 颗），而且只在 6 列表上扫；指数约 1.7 是两点拟合，**不是定律**，也没在别的列宽下验证。宽表那一副只量了默认档（每块 65 行 / 1 885 颗）与 L1，没在 30 列上扫过行数。
- **曲线归谁没分离干净**：§2.5 那颗裸 ADO 对照只证明"不在 FastSql 这一层"。驱动侧的参数序列化与服务端侧的参数解析各占多少，本文没量（要分开得抓 TDS 或比 `SET STATISTICS`）。手拼那一格还有一处不严格：参数没设类型与长度，与 ORM 的绑定不完全同形。
- **"换数据"那一节量的是后果，不是机制**（§2.6）：本文没有进 `sys.dm_exec_cached_plans`／`sys.dm_exec_query_stats` 看命中次数，也没抓 TDS 包，"编译 vs 复用"是从"只有内联那一族随值变动"推出来的。另外三点没剥干净：① 只试了"每趟全换"，没有"每两趟换一次"这种中间档，所以曲线在部分复用下长什么样本文不知道；② 换数据同时改变了写入的值本身（数字位数、字符串长度、页与索引的分配状态），这一笔与编译那一笔混在一起，本文无法定价；③ 参数化那一族的本文确实逐字不变，但它是否真命中了缓存同样是推的。裸 ADO 参数化那一档换数据之后还**更快**（1 324 对 1 473），本文不解释。
- **内联那一档本文是手拼的，不是从对面嘴里拿的**：`§2.5` 那颗 17.91–37.76 ms（同数据）与 355.82–357.83 ms（换数据）的对照出自量具自己的裸 ADO 实现，对面真实实现另有它的转义与列类型分岔（§2.10）。所以"本家该不该做内联"这一问，本文只给了量级，**没给可行性**——安全责任在转义函数身上这件事，见 §2.10 那笔取舍。
- **装载的两笔一次性开销都被计时纪律挑掉了**：SqlSugar 首趟的"向库里要空表壳"与 FastSql 首次的反射装载。要量稳态之外的那一笔，得改成"取第一趟"重跑。
- **事务与装载只跑了最窄的一格**（§2.12）：`begin → 整批装载 → rollback` 的墙钟与"回滚后库里 0"，加一颗 `CHECK` 约束、三颗对面开关组合、两颗本家默认。没跑的还有：嵌套事务里的装载、`RequiresNew` 下的装载、`Fastest.Size` 分批与事务的叠加（对面那一支每批自开事务，`FastestProvider.cs:63-74`）、对面老的 `ExecuteBulkCopy` 路径、唯一索引/外键/默认值/计算列、并发写下锁与阻塞的形态，以及"违约行进库"那一支在真业务表上会带来什么（本文只证明它能进，没有评价该不该）。
- **`ORDER BY GetDate()` 的翻页稳定性仍没实测**（§3.4 只报源码 + §2.7 报实测文本）。要证它得构造并发写入下的连续翻页比对。已经补上的是另一半：**Auto 从第二页起换成 `CountStar` 本轮实测到了**（两句、`Total` 200，§2.7）——但只测了第 2 页、表上 200 行、这一家；第 3 页之后与更深页的换档没测，`CountOverWindow`/`SeparateTransaction` 仍是抛异常。
- **完全没碰的面**：导航属性、主从分离、缓存层（§3.5 只说明它会绕过留痕）、分表、`DynamicBuilder`、`BulkMerge`（源码里有：`FastestProvider.cs:266-304` 是临时表 + 装载 + `MERGE` 三步的实现，同步口 `:207-211` 依旧是 `GetAwaiter().GetResult()`）、`BulkUpdate`（它与 `BulkCopy` 共用 `Begin`/`End` 那对日志开关，§2.13 只测了 `BulkCopy` 一支）、`SetColumns` 批量更新、批量删除、`DbFirst`、`CodeFirst` 的建表结果。
- **只有 SQL Server 一家**：FastSql 有 16 颗叶子 provider，本文只证了 T-SQL 这一家，而 SqlSugar 的批量写法是**每家一套 override**（如 `Realization/PostgreSQL/SqlBuilder/PostgreSQLInsertBuilder.cs:31-33` 的 `SqlTemplateBatchUnion` 是 `" VALUES "`，即 PG 那一家的批量是参数化还是内联，本文没读也没量）。§5.4 那条默认值的账同样只对这一家成立。
- **一台实例、一组数据形状、每格两到四趟、每趟三取最小**：换实例、换列类型（`varchar(max)`、`datetime2`、`sql_variant`）、换批量大小都可能换结论。§2.6 那一节尤其薄——它是**两趟**，不是四趟，而且它的整个论点是"跨趟的相对变化"，不是某一格的绝对值。
- **211 / 215 / 218 的差异**：见 §0，本文不猜；快照没有 `.git`，连"对应上游哪个 commit"都无法确定。§2.12/§2.13 那两组读码坐标（`SqlServerFastBuilder.cs:52-59`、`FastestProvider.cs:497-506,523-536,544-545`）都对 211 那份源码负责，而数是 218 跑出来的——本轮它们**逐条对上了**（互斥的行为、恢复不在 finally 的行为，实测与源码同一形状），但"218 是否改了这两处"本文仍然无法从源码确定。

## 7. 这份文档怎么被维护

- 想更新数字：跑 §1 的量具**连跑两趟**（`run-A.txt` / `run-B.txt`），把每格的两趟读数与文档里现有的区间**取并**写成新范围，并**在格子里改趟数标注**（本轮就有两格从"四趟"变"六趟"、三格从"两趟"变"四趟"），**保留每格的"读数"原文**（那才是可核的东西，毫秒数只是它的摘要）。只跑一趟就更新数字，等于把抖动作成精度。
- **原始输出的可核性是会丢的**：`sql/obj/` 不进版本库，所以 `run-A.txt`/`run-B.txt` 只活到下一次重跑。本轮上一版的两趟原文就已经被覆盖了（读数只以区间形式留在 `git show 2415164` 那份文档里）。要长期留住某一副见证，得把它抄进文档正文（本轮 §2.6、§2.7、§2.12、§2.13 的四段原文与十行探针表就是这么存的），或者挪出 `obj/`——本文选择前者，因为这份文档不是台账。
- **段序不能重排**：§9/§10/`10b` 那三段（对应本文 §2.12、§2.13）必须在所有计时格之后。理由本轮付过学费：事务内装载抛异常会掐断对面的日志开关，放在前面会把后面每一格的留痕读数打成空白（§1 的"段序是硬的"那一段）。
- 想更新名次：§4 的四档只允许由 §2 的格子推动，而**名次一律钉在区间下界**，区间宽度另写一句，**两副区间重叠就不排名次、挪进"持平"**（本轮这么处理的是 §2.5 中间两档、§2.8、新增的 §2.12；§2.2 上一轮就在持平里，本轮只把它的配对写成"一比一"并把更早四趟的按序配对标注成推的）；§3 的读码格只允许由 `文件:行` 推动，被实测追上的那几处要写明"本轮从读码结论升成实测"（§2.7 第二页换档、§2.12 那颗互斥）。
- 一处旧说法被翻掉或被坐实：按 §5 的格式记在本文里（§5.1 打折、§5.2 坐实、§5.3 报告、§5.4 新账、§5.5 限定、§5.6 翻掉本文自己），等一次记账轮一起并回规格，不要在这里顺手改那两份。本文自己写下过的推断也在这一族里挨改——§2.5 那句"0.146 与 0.126 ms/颗"（上一版被数据否掉）与 §4 那句"对面真正拿不走的东西是内联"（本版被自己加了条件）都留着，因为**被否掉的推断比站得住的推断更值得记**。
