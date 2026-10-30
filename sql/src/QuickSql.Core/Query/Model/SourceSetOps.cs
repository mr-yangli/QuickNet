using QuickSql.Sql.Ast;

namespace QuickSql.Query.Model;

/// <summary>链式合并（<c>UnionAll</c>/<c>Union</c>，2026-10-30 落地）的 FROM 位形状：两条<b>同型的腿</b>
/// 用 <c>UNION ALL</c>（不去重）或 <c>UNION</c>（整句去重）串起来，整串占一个表位 —— 与分表读侧
/// （<see cref="SourceUnion"/>）同一种 SQL 形状，而内层是谁写的不一样：分表的腿由选片机器造
/// （同一个模型的 N 张物理片），这里的腿是<b>调用方各自链出来的两条完整查询</b>。
///
/// <para><b>腿是模型不是 AST</b>：编译口（<c>QueryCompiler.Compile</c>）走到 FROM 位时才把两条腿各自
/// 编译成 <c>SelectStatement</c>（与派生表那格 <see cref="Sql.SourceDerived"/> 的内层同一时机），
/// 所以腿里的谓词、过滤器、集群分派全部按腿自己的路走完一遍；形状守卫（同型整行、无序无页、
/// 无急切加载、不套娃）住在链式入口，不在这里 —— 这里只管编译。</para></summary>
public sealed record SourceSetOps(TableRef Ref, QueryModel First, QueryModel Second, bool All) : Sql.Ast.TableSource(Ref);
