namespace FastSql.Schema;

/// <summary>一次取数场次（一个 reader 的一趟读）的票据：列序起点 + 每列的直读档。
/// <b>它是读取委托的入参形状，不是每行的东西</b>（§16.21 那一格的落地形态：
/// 「把『这列究竟是什么类型』的探测从每值提升到每场次」）。
/// 每场次建一次 = 每列一次 <c>GetFieldType</c>；每行读一列 = 一次加法、一次数组读加一次比较，
/// 命中时换成原始访问器（实测那格：装箱 95.2 ns / 24 B，直读 89.0 ns / 0 B）。</summary>
/// <remarks><b>为什么只有起点、没有序号数组</b>：实体投影的选列就是契约列序、Include 的子节点是一段连续列
/// （<c>QueryCompiler.EntityColumns</c> 与 <c>IncludeNode.ColumnOffset</c>），两处都是"从某处起连续 n 列"，
/// 所以序号是 <c>Offset + i</c> 这一个加法而不是每场次一张 <c>int[]</c>。
/// 做成一个类型（而不是一颗 <c>byte[]</c> 参数）仍然是为了<em>同源</em>：档位是按 <c>Offset + i</c> 去问驱动的，
/// 把两样东西拆成两个参数，就会有"向量按 0..n-1 探、序号却带偏移"那种错位空间——
/// 现在这个加法只在一个地方发生，错位空间比序号数组那一版更小。
/// 做成 <c>readonly struct</c> 是同一处改动省下来的第二笔：票本身不落堆（每场次只剩那一颗 <c>byte[]</c>），
/// 而它的字段就 12 字节，按值传进委托比按引用传一个堆对象还便宜。
/// <b>不可变性</b>：<see cref="Ops"/> 交出去就是只读的，改一张票等于改所有共用它的行。
/// 场次之间不复用：换一个 reader 就重探一次（<c>Materializer.EntityRows</c> 按 reader 引用判），
/// 因为「驱动交回什么类型」是那只 reader 的性质，不是那个实体的性质。</remarks>
public readonly struct ReadPlan(int offset, byte[] ops)
{
    /// <summary>契约第 0 列在这趟读的 reader 上的序号。第 <c>i</c> 列的序号是 <c>Offset + i</c>，
    /// 探测与读取用的都是这一式（<c>ReadOps.Build</c> 与各家臂的调用点是同一颗起点）。</summary>
    public int Offset { get; } = offset;

    /// <summary>每列的直读档，取值是 <c>ReadOps</c> 那批常量；0（<c>ReadOps.Boxed</c>）= 这一列走装箱口。
    /// 这里的数字由运行期的 <c>ReadOps.Probe</c> 落，读它的是生成器模板与 G2 的表达式树，
    /// 两边都只经 <c>DirectRead</c> 的臂去解释，所以编号只有一处定义。</summary>
    public byte[] Ops { get; } = ops;
}
