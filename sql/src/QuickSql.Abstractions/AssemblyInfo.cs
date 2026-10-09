using System.Runtime.CompilerServices;

/// <remarks>钩子事件的构造器是 <c>internal</c>（§10.1）：事件只能由发射层造，调用方伪造一个
/// <see cref="QuickSql.Aop.AuditValueEvent"/> 就能绕过 <c>WasSet</c> 的记录，回放也就不可信了。
/// 写路径是第一个真正的消费者，而它在 Core，所以在这里开一扇内部可见的口子 ——
/// 而不是把构造器改成 public 让"事件"变成一种用户可以自己 new 出来喂给钩子的东西。</remarks>
[assembly: InternalsVisibleTo("QuickSql.Core")]
