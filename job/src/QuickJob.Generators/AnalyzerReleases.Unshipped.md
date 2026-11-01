### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
QJ0001 | QuickJob.Registration | Error | [QuickJob] 方法必须 static —— 运行期调度器只认静态 job 体
QJ0002 | QuickJob.Registration | Error | handler 形状不在支持族里（对应 JobHandlerBinder 支持的九种无参形状与两种 RequestType 形状）
QJ0003 | QuickJob.Registration | Error | Cron 与 Every 互斥，对应启动期 Register 的同一句校验
QJ0004 | QuickJob.Registration | Error | private 方法或者泛型容器里的方法，生成的登记代码够不着
QJ0005 | QuickJob.Registration | Error | 重复的 job 名，运行期 PendingRegistrations.Add 抛同一句话
QJ0006 | QuickJob.Registration | Error | 时长文本解析不了，对应 DurationText.Parse 的启动期失败
QJ0007 | QuickJob.Registration | Error | Cron 表达式结构不对（字段数/字符档/降序区间），最终语义以运行期 CronSchedule.Parse 为准
QJ0011 | QuickJob.Registration | Error | Every 必须是正时长，对应 interval 定义需要正 Every 的启动期校验
