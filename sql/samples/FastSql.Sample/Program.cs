using FastSql;
using FastSql.Sample;
using FastSql.Schema;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

// ── 0. 准备：删掉旧库文件，保证每次运行都是干净的 ────────────────────────
var dbFile = "fastsql-sample.db";
if (File.Exists(dbFile)) File.Delete(dbFile);
var connectionString = $"Data Source={dbFile}";

// ── 1. DI 注册 ──────────────────────────────────────────────────────────
//
// 简单模式：连接串启动期确定
//
var services = new ServiceCollection();

services.AddFastSqlOrm(DbKind.Sqlite, connectionString, builder =>
{
    builder.SchemaSync = SchemaSyncPolicy.Execute; // 开发环境自动建表
    builder.ForceSchemaSync = true;                // 示例跳过环境检查
});

// 注册 DbContext（Scoped）
services.AddScoped<AppDbContext>();

using var sp = services.BuildServiceProvider();

// ── 2. 建库 + 同步表结构 ────────────────────────────────────────────────
//
// SQLite 打开连接即建文件，但 SqlServer/MySql 等需要先 EnsureDatabaseExistsAsync。
// SchemaSync = Execute 时 SyncStructureAsync 内部会先调 EnsureDatabaseExistsAsync，
// 所以这一行同时完成"建库 + 建表"两件事。
//
using var scope = sp.CreateScope();
var orm = scope.ServiceProvider.GetRequiredService<FastSqlOrm>();

// 注册表是惰性的：实体第一次被 Select/Set 触及时才进 RegisteredTypes。
// SyncStructure 按 RegisteredTypes 建表，所以要先"碰一下"每个实体类型。
_ = orm.Select<Customer>();
_ = orm.Select<Order>();
_ = orm.Select<OrderItem>();

var syncResult = await orm.SyncStructureAsync();
Console.WriteLine($"表结构同步完成：新建 {syncResult.CreatedTables.Count} 张表");
foreach (var t in syncResult.CreatedTables)
    Console.WriteLine($"  - {t}");

Console.WriteLine();

// ── 3. 通过 DbContext 做增删改查（跟踪路径） ────────────────────────────
Console.WriteLine("═══ 3. DbContext 跟踪路径 ═══");
Console.WriteLine();

var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

// ── 3.1 新增 ──
var customer = new Customer { Name = "张三", City = "上海" };
db.Add(customer);
await db.SaveChangesAsync();
Console.WriteLine($"新增客户：Id={customer.Id}, Name={customer.Name}");

// ── 3.2 查询 ──
var found = await db.Customers
    .Where(c => c.Name == "张三")
    .FirstOrDefaultAsync();
Console.WriteLine($"查询客户：Id={found?.Id}, Name={found?.Name}, City={found?.City}");

// ── 3.3 修改 ──
found!.City = "北京";
await db.SaveChangesAsync(); // AutoDetectChanges 自动侦测到 City 变了

var updated = await db.Customers.Where(c => c.Id == found.Id).FirstOrDefaultAsync();
Console.WriteLine($"修改客户：Id={updated?.Id}, City={updated?.City}");

// ── 3.4 删除 ──
db.Remove(updated!);
await db.SaveChangesAsync();

var count = await db.Customers.Where(c => c.Name == "张三").ToListAsync();
Console.WriteLine($"删除客户后查询：找到 {count.Count} 条");

Console.WriteLine();

// ── 4. 主从明细增删改查（Order + OrderItem） ─────────────────────────────
Console.WriteLine("═══ 4. 主从明细（DbContext） ═══");
Console.WriteLine();

// 先建一个客户
var cust = new Customer { Name = "李四", City = "广州" };
db.Add(cust);
await db.SaveChangesAsync();
Console.WriteLine($"客户：Id={cust.Id}");

// ── 4.1 新增主表 + 明细（一起保存） ──
//
// SaveChanges 按外键边自动排写入先后：先 INSERT Order，拿到自增 Id，
// 再 INSERT OrderItem（此时 OrderId 已回填）。
//
var order = new Order
{
    CustomerId = cust.Id,
    Amount = 299.50m,
    CreatedAt = DateTime.Now,
    Note = "第一笔订单",
};
db.Add(order);

var item1 = new OrderItem { OrderId = order.Id, Sku = "SKU-001", Quantity = 2, Price = 99.75m };
var item2 = new OrderItem { OrderId = order.Id, Sku = "SKU-002", Quantity = 1, Price = 100.00m };
db.Add(item1);
db.Add(item2);

await db.SaveChangesAsync();
Console.WriteLine($"订单：Id={order.Id}, Amount={order.Amount}");
Console.WriteLine($"  明细1：Id={item1.Id}, OrderId={item1.OrderId}, Sku={item1.Sku}");
Console.WriteLine($"  明细2：Id={item2.Id}, OrderId={item2.OrderId}, Sku={item2.Sku}");

// ── 4.2 查询主表 + 按外键查明细 ──
var orders = await db.Orders
    .Where(o => o.CustomerId == cust.Id)
    .ToListAsync();

foreach (var o in orders)
{
    var items = await db.OrderItems
        .Where(i => i.OrderId == o.Id)
        .ToListAsync();
    Console.WriteLine($"订单 {o.Id}：{items.Count} 行明细");
    foreach (var i in items)
        Console.WriteLine($"  - {i.Sku} × {i.Quantity} @ {i.Price}");
}

// ── 4.3 修改明细 ──
item1.Quantity = 5;
db.Update(item1); // 标为全列脏
await db.SaveChangesAsync();

var refreshed = await db.OrderItems.Where(i => i.Id == item1.Id).FirstOrDefaultAsync();
Console.WriteLine($"修改明细后：Sku={refreshed?.Sku}, Quantity={refreshed?.Quantity}");

// ── 4.4 删除明细 ──
db.Remove(item2);
await db.SaveChangesAsync();

var remaining = await db.OrderItems.Where(i => i.OrderId == order.Id).ToListAsync();
Console.WriteLine($"删除明细后：剩余 {remaining.Count} 行");

// ── 4.5 删除主表（先删明细再删主表，或一起交给 SaveChanges 排先后） ──
db.Remove(order);
db.Remove(remaining[0]); // 删最后一行明细
await db.SaveChangesAsync();

var orderCount = await db.Orders.Where(o => o.CustomerId == cust.Id).ToListAsync();
var itemCount = await db.OrderItems.Where(i => i.OrderId == order.Id).ToListAsync();
Console.WriteLine($"删除主从后：订单 {orderCount.Count} 条，明细 {itemCount.Count} 条");

Console.WriteLine();

// ── 5. 通过原始 ORM 做增删改查（非跟踪路径） ────────────────────────────
Console.WriteLine("═══ 5. 原始 ORM 非跟踪路径 ═══");
Console.WriteLine();

// ── 5.1 插入 ──
var newCust = new Customer { Name = "王五", City = "深圳" };
var identity = await orm.Insert(newCust).ExecuteIdentityAsync();
newCust.Id = (int)identity;
Console.WriteLine($"Insert 插入：Id={newCust.Id}, Name={newCust.Name}");

// ── 5.2 查询 ──
var queried = await orm.Select<Customer>()
    .Where(c => c.City == "深圳")
    .ToListAsync();
Console.WriteLine($"Select 查询：找到 {queried.Count} 条，Name={queried[0].Name}");

// ── 5.3 更新 ──
var affrows = await orm.Update<Customer>()
    .Set(c => c.City, "杭州")
    .Where(c => c.Id == newCust.Id)
    .ExecuteAffrowsAsync();
Console.WriteLine($"Update 链：影响 {affrows} 行");

var afterUpdate = await orm.Select<Customer>().Where(c => c.Id == newCust.Id).FirstOrDefaultAsync();
Console.WriteLine($"更新后：City={afterUpdate?.City}");

// ── 5.4 删除 ──
var delRows = await orm.Delete<Customer>()
    .Where(c => c.Id == newCust.Id)
    .ExecuteAffrowsAsync();
Console.WriteLine($"Delete 链：影响 {delRows} 行");

Console.WriteLine();

// ── 6. 事务 ─────────────────────────────────────────────────────────────
Console.WriteLine("═══ 6. 事务 ═══");
Console.WriteLine();

// 回调式事务：body 内抛异常自动回滚
var result = await orm.ExecuteInTransactionAsync(async ct =>
{
    var c = new Customer { Name = "赵六", City = "成都" };
    await orm.Insert(c).ExecuteAffrowsAsync(ct);
    Console.WriteLine($"  事务内插入：Name={c.Name}");
});
Console.WriteLine($"事务结果：Success={result.Success}");

// 手动事务：不调 Commit = 回滚
using (var tran = orm.BeginTransaction())
{
    await orm.Insert(new Customer { Name = "临时", City = "临时" }).ExecuteAffrowsAsync();
    // 不调 tran.Commit()，using 结束时自动回滚
}
var tempCount = await orm.Select<Customer>().Where(c => c.Name == "临时").ToListAsync();
Console.WriteLine($"回滚后查询[临时]：找到 {tempCount.Count} 条（应为 0）");

Console.WriteLine();

// ── 7. 子查询（IQuery 捕获 → 标量子查询 / IN 子查询） ─────────────────
Console.WriteLine("═══ 7. 子查询 ═══");
Console.WriteLine();

// ── 7.1 补种子数据 ──
var cA = new Customer { Name = "陈七", City = "上海" };
var cB = new Customer { Name = "周八", City = "北京" };
var cC = new Customer { Name = "吴九", City = "上海" };
await orm.Insert(cA).ExecuteIdentityAsync();
await orm.Insert(cB).ExecuteIdentityAsync();
await orm.Insert(cC).ExecuteIdentityAsync();

var ordersA = new[]
{
    new Order { CustomerId = cA.Id, Amount = 100m,  CreatedAt = DateTime.Now, Note = "A-1" },
    new Order { CustomerId = cA.Id, Amount = 200m,  CreatedAt = DateTime.Now, Note = "A-2" },
    new Order { CustomerId = cA.Id, Amount = 300m,  CreatedAt = DateTime.Now, Note = "A-3" },
};
var ordersB = new[]
{
    new Order { CustomerId = cB.Id, Amount = 500m,  CreatedAt = DateTime.Now, Note = "B-1" },
    new Order { CustomerId = cB.Id, Amount = 600m,  CreatedAt = DateTime.Now, Note = "B-2" },
};
// ExecuteIdentityAsync 的返回值就是那一行的自增 Id，它不回写实体（§5.1 同一姿势），
// 而下面两行要用它给明细挂外键，所以必须接住 —— 漏掉这一步，明细的 OrderId 全留 0。
foreach (var o in ordersA.Concat(ordersB))
    o.Id = (int)await orm.Insert(o).ExecuteIdentityAsync();

foreach (var o in ordersA)
    await orm.Insert(new OrderItem { OrderId = o.Id, Sku = "SKU-A", Quantity = 1, Price = o.Amount }).ExecuteAffrowsAsync();
foreach (var o in ordersB)
    await orm.Insert(new OrderItem { OrderId = o.Id, Sku = "SKU-B", Quantity = 2, Price = o.Amount / 2 }).ExecuteAffrowsAsync();
Console.WriteLine("种子数据：3 客户、5 订单、5 明细");

// ── 7.2 Count 子查询 ──
//
// 捕获一个 IQuery<Order>，在 Where 里调 .Count()，
// 翻译器把它编译成标量子查询 (SELECT COUNT(*) FROM Orders)。
// 注意：子查询要先存进变量再调终端方法，不能在链式调用里直接写。
//
var allOrders = orm.Select<Order>();
var totalOrderCount = await allOrders.CountAsync();
Console.WriteLine($"Count 直接调用：共 {totalOrderCount} 笔订单");

// 子查询用法：找出"订单总数 > 3"时列出的客户（非关联——子查询不引用外层 c）
var countSub = orm.Select<Order>();
var customersWhenManyOrders = await orm.Select<Customer>()
    .Where(c => countSub.Count() > 3)
    .ToListAsync();
Console.WriteLine($"Count 子查询（总订单 > 3 时列出所有客户）：{string.Join(", ", customersWhenManyOrders.Select(c => c.Name))}");

// ── 7.3 Contains 子查询（IN） ──
//
// 捕获一个 IQuery<int>（投影出 CustomerId），在 Where 里调 .Contains(c.Id)，
// 翻译器把它编译成 IN (SELECT CustomerId FROM Orders WHERE Amount >= 500)。
//
var highValueCustomerIds = orm.Select<Order>()
    .Where(o => o.Amount >= 500m)
    .Select(o => o.CustomerId);
var highValueCustomers = await orm.Select<Customer>()
    .Where(c => highValueCustomerIds.Contains(c.Id))
    .ToListAsync();
Console.WriteLine($"Contains 子查询（有 ≥500 订单的客户）：{string.Join(", ", highValueCustomers.Select(c => c.Name))}");

// ── 7.4 Sum / Max / Min / Average 直接调用 ──
//
// 在 IQuery 上直接调聚合方法，翻译成 SELECT SUM/MAX/MIN/AVG(...) FROM ...。
//
var sumAmount = await allOrders.SumAsync(o => o.Amount);
var maxAmount = await allOrders.MaxAsync(o => o.Amount);
var minAmount = await allOrders.MinAsync(o => o.Amount);
var avgAmount = await allOrders.AverageAsync(o => o.Amount);
Console.WriteLine($"Sum：总订单金额 = {sumAmount}");
Console.WriteLine($"Max：最高单笔 = {maxAmount}");
Console.WriteLine($"Min：最低单笔 = {minAmount}");
Console.WriteLine($"Average：平均单笔 = {avgAmount}");

// ── 7.5 Sum / Max 子查询 ──
//
// 在 Where 里当标量子查询用：子查询独立于外层参数，翻译器编译成
// (SELECT SUM(Amount) FROM Orders) 嵌入外层 WHERE。
// 别名作用域已隔离——子查询的表走 t1000+ 序列，不会和外层 t0 撞名。
//
var totalSum = orm.Select<Order>();
var customersWhenBigTotal = await orm.Select<Customer>()
    .Where(c => totalSum.Sum(o => o.Amount) > 1000m)
    .ToListAsync();
Console.WriteLine($"Sum 子查询（总订单金额 > 1000 时列出客户）：{string.Join(", ", customersWhenBigTotal.Select(c => c.Name))}");

var maxSub = orm.Select<Order>();
var customersWhenHighMax = await orm.Select<Customer>()
    .Where(c => maxSub.Max(o => o.Amount) > 500m)
    .ToListAsync();
Console.WriteLine($"Max 子查询（最高单笔 > 500 时列出客户）：{string.Join(", ", customersWhenHighMax.Select(c => c.Name))}");

Console.WriteLine();

// ── 8. 分组查询（GROUP BY / HAVING） ───────────────────────────────────
Console.WriteLine("═══ 8. 分组查询 ═══");
Console.WriteLine();

// ── 8.1 单列分组 + 多个聚合 ──
//
// GroupBy 之后链上能选的只有 g.Key 与聚合：形参类型是 IGrouping<TKey, TValue>，
// 它除了 Key 就没有实体成员，所以"分组后偷偷选一列非键"这种句子在编译期就过不去（§5.3）。
// 聚合选择器里那个 o 是"组里的一行"，落回同一张表位 —— 发的是 SUM([t0].[Amount])，不是相关子查询。
//
var perCustomer = await orm.Select<Order>()
    .GroupBy(o => o.CustomerId)
    .Select(g => new
    {
        CustomerId = g.Key,
        Orders = g.Count(),
        Total = g.Sum(o => o.Amount),
        Largest = g.Max(o => o.Amount),
        Smallest = g.Min(o => o.Amount),
        Average = g.Average(o => o.Amount),
    })
    .ToListAsync();

Console.WriteLine("按客户分组（CustomerId / 笔数 / 合计 / 最大 / 最小 / 平均）：");
foreach (var row in perCustomer)
    Console.WriteLine($"  客户 {row.CustomerId}：{row.Orders} 笔，合计 {row.Total}，最大 {row.Largest}，最小 {row.Smallest}，平均 {row.Average:0.##}");

// 未选出的那一组也在库里占一行；要"每组只给一个标量"就整条投影成一列。
var orderCountPerCustomer = await orm.Select<Order>()
    .GroupBy(o => o.CustomerId)
    .Select(g => g.Count())
    .ToListAsync();
Console.WriteLine($"只投影聚合（g => g.Count()）：[{string.Join(", ", orderCountPerCustomer)}]");

// ── 8.2 HAVING：分组之后筛组 ──
//
// Having 的谓词落进 HAVING 而不是 WHERE —— 这件事由"它在 GroupBy 之后"决定。
// 分组之前的条件仍然写在 Where 里（那条链在 GroupBy 之前，见 §8.5）。
//
var regulars = await orm.Select<Order>()
    .GroupBy(o => o.CustomerId)
    .Having(g => g.Count() > 2)
    .Select(g => new { CustomerId = g.Key, Orders = g.Count(), Total = g.Sum(o => o.Amount) })
    .ToListAsync();

Console.WriteLine($"Having 只留 2 笔以上的客户：{string.Join(", ", regulars.Select(r => $"客户{r.CustomerId}({r.Orders}笔/{r.Total})"))}");

// ── 8.3 复合分组键 + DTO 投影 ──
//
// 键写成 new { … } 就是复合键：GROUP BY 里按声明序发几列，而 g.Key 整体不是一个值，
// 只能逐成员读 g.Key.OrderId / g.Key.Sku（成员名就是匿名类型上那一个）。
//
var skuTotals = await orm.Select<OrderItem>()
    .GroupBy(i => new { i.OrderId, i.Sku })
    .Select(g => new SkuTotal
    {
        OrderId = g.Key.OrderId,
        Sku = g.Key.Sku,
        Units = g.Sum(i => i.Quantity),
        Amount = g.Sum(i => i.Quantity * i.Price),
    })
    .ToListAsync();

Console.WriteLine("按 (订单, 货号) 复合键分组的 DTO 投影：");
foreach (var t in skuTotals)
    Console.WriteLine($"  订单 {t.OrderId} / {t.Sku}：{t.Units} 件，金额 {t.Amount}");

// ── 8.4 分组排序 + 取前 N 名 ──
//
// OrderBy 在 GroupBy 之后排的是"组"，所以键与聚合都能当排序依据。
// Take 的页与组同一单位：LIMIT 2 拿的是前两组。
//
var topTwo = orm.Select<Order>()
    .Where(o => o.Amount > 0m)
    .GroupBy(o => o.CustomerId)
    .OrderByDescending(g => g.Sum(o => o.Amount))
    .Take(2)
    .Select(g => new { CustomerId = g.Key, Total = g.Sum(o => o.Amount) });

Console.WriteLine("前两组（按合计金额降序）产出的 SQL：");
Console.WriteLine($"  {topTwo.ToLogSql()}");

foreach (var row in await topTwo.ToListAsync())
    Console.WriteLine($"  客户 {row.CustomerId}：合计 {row.Total}");

// ── 8.5 Explain()：分组键与 HAVING 都在模型文本里 ──
var grouped = orm.Select<Order>()
    .Where(o => o.Amount >= 200m)
    .GroupBy(o => o.CustomerId)
    .Having(g => g.Count() > 1)
    .Select(g => new { CustomerId = g.Key, Orders = g.Count(), Total = g.Sum(o => o.Amount) });

var plan = grouped.Explain();

Console.WriteLine("Explain() 模型文本里的分组那几行（摘录）：");
foreach (var line in plan.ModelText.Split('\n').Where(l => l.Contains("WHERE") || l.Contains("GROUP BY") || l.Contains("HAVING")))
    Console.WriteLine($"  {line.TrimEnd()}");

Console.WriteLine($"同一条查询产出的 SQL：  {grouped.ToLogSql()}");

var filteredGroups = await grouped.ToListAsync();

Console.WriteLine($"先 WHERE 再 GROUP BY 再 HAVING：{string.Join(", ", filteredGroups.Select(r => $"客户{r.CustomerId}({r.Orders}笔/{r.Total})"))}");

// 还没接住的三件事，都会当场报错而不是给一个看起来合理的数（不是静默降级）：
//   · 分组后的 Count / ToPagedAsync 的总数 —— 要 SELECT COUNT(*) FROM (SELECT … GROUP BY …)，派生表入口未接入；
//   · 联表之后的分组 —— GroupBy 今天开在单表链（IQuery<T>）上，两表/三表链没有这一颗口；
//   · 组内去重计数 g.CountDistinct(…) —— COUNT(DISTINCT x) 与 FILTER 各家不同，未接入。

Console.WriteLine();
Console.WriteLine("═══ 示例完成 ═══");

// ── 附注：多租户每库一串的注册姿势 ──────────────────────────────────────
//
// services.AddFastSqlOrm(DbKind.SqlServer, sp =>
// {
//     var tenant = sp.GetRequiredService<ICurrentTenant>();
//     return configuration.GetConnectionString(tenant.Name);
// }, builder =>
// {
//     builder.SchemaSync = SchemaSyncPolicy.Execute;
// });
//
// 每个请求的 Scoped FastSqlOrm 会调一次工厂，按当前租户拿到不同的连接串。
