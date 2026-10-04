using FastSql.Metadata;
using FastSql.Providers;
using FastSql.Sql;

namespace FastSql.Providers.MySqlBase;

/// <summary>MySQL 血统的 DDL（§9.2、§10.4，第四家）。与另四族同一纪律：P0 只产出文本与黄金样本比对，
/// <b>不执行</b>，而 DDL 与运行时查询共用同一个 <see cref="IdentifierPolicy.MySql"/>。
///
/// <para>五处与三族不同，其中三处是"换个写法"之外的东西：
/// <list type="bullet">
/// <item><b>模式就是数据库，而默认那一个是连接带来的</b>：所以 <c>Schema</c> 为 null 时与 Oracle 血统同一处理
/// —— <em>什么都不写</em>，而不是补一个假名字（这一家没有 <c>dbo</c>/<c>public</c> 那种人人可进的公共模式）。
/// 但同一件事在两家的含义不同：那一族写出来的是"当前用户的模式"，这里写出来的是 <c>DATABASE()</c>，
/// 而它是连接串上 <c>Database=…</c> 那一颗决定的 —— <b>建表落在哪个库里由驱动参数决定而不是由 provider 决定</b>，
/// 这是 §14 那颗"连接串参数改变方言假设"缺口的第二个实例（第一个是金仓的 Oracle 兼容模式）。</item>
/// <item><b>自增是列上的一个子句而不是一个类型</b>：<c>AUTO_INCREMENT</c> 跟在形状之后，
/// 与 <c>IDENTITY</c> 那一档同位，所以 <see cref="IdentityClause"/> 是这一族给叶子（TiDB 的
/// <c>AUTO_ID_CACHE</c>、MariaDB 的差异）留的覆写点，形状与 <c>PostgreSqlCodeFirst</c> 那颗同名虚口一致 ——
/// 那一颗今天也没有覆写者，这条纪律因此是"三族同形"而不是"有覆写才留"。</item>
/// <item><b>改列是一句整列重写</b>：<c>MODIFY COLUMN</c> 吃的是"这一列的完整定义"，
/// 所以型、可空性、<c>AUTO_INCREMENT</c> 三格<em>必须</em>一起写 —— 漏哪一格就等于顺手改了它。
/// 这与 T-SQL 的 <c>ALTER COLUMN</c>（会顺带重置可空性）、Oracle 血统的 <c>MODIFY</c>（一句带两半）
/// 是同一个事故的三种来路，而这一家把第三格（自增）也放进了雷区：<c>MODIFY COLUMN id INT</c> 会把
/// 那一列的 <c>AUTO_INCREMENT</c> 摘掉，而它是主键时库直接报 <c>1075</c>。
/// 五家里"改精度"这一档只需要<b>一条</b>语句的是这四家（T-SQL 的 <c>ALTER COLUMN</c>、Oracle 血统的
/// <c>MODIFY</c>、这一家的 <c>MODIFY COLUMN</c>，以及 v0.31 第五家那句 <c>ALTER COLUMN … &lt;型&gt; NOT NULL</c>；
/// 上一版这里写作"这一家与 Oracle 血统两家"，交账时按
/// <c>SqlServerCodeFirst.EmitAlterColumnPrecision</c> 重数推翻 —— 它那一句也是单条，只是可空性要在同一条里重述）；
/// 要两条的是金仓那一家（<c>TYPE</c> 不碰 <c>NOT NULL</c>），所以这一家这里没有那条 <c> ; </c> 串联。</item>
/// <item><b>注释没有独立语句，它是列定义的一部分</b>：这一家没有 <c>COMMENT ON COLUMN</c>，
/// 也没有 T-SQL 那种扩展属性存储过程。所以 <see cref="TryEmitColumnComment"/> 发的是
/// <c>ALTER TABLE … MODIFY COLUMN &lt;整列定义&gt; COMMENT '…'</c> —— 一句能加上注释，
/// 而<em>代价</em>要说清：它顺手重写了那一列的型与约束（所以必须从契约重发全部三格，见上一条），
/// 而反过来 <see cref="EmitAlterColumnPrecision"/> 若不带上契约里的注释就会把注释抹掉。
/// 两条共用一个 <c>Definition(column, includeComment)</c> 出口正是为了让这两半不会各自漏一格。
/// 库里已有而契约没有的注释会在改精度时丢失，这一档记 §14。</item>
/// <item><b>全文索引在这里是真语法</b>：<c>CREATE FULLTEXT INDEX</c> 在另两族要抛（PG 系与 Oracle 血统各有各的另一套东西），
/// 这一家它是原生子句。所以这是五家里第一次有人<em>把它建成一句真全文索引</em>的一格（T-SQL 那一族也不抛，
/// 但它发的是被降格的普通索引，见 <see cref="EmitCreateIndex"/> 那条注释）—— 但它也不是"建好就能查中文"：
/// 默认解析器按空格与标点切词，中文要 <c>WITH PARSER ngram</c>（8.0 起自带但要显式选），
/// 而索引类型在 <c>IndexContract</c> 里没有那一格。所以这里发一颗不带 parser 的 FULLTEXT，
/// 并把"中文全文索引今天建成的是英文分词"这一档记进 §10.4 与 §14 ——
/// 它比"抛"更该写清，因为这一家<em>不报错</em>。</item>
/// </list></para>
///
/// <para>两处这一族特有的取舍写在 <see cref="TableOptions"/> 与 <see cref="EmitCreateIndex"/> 上：
/// 建表带 <c>ENGINE = InnoDB</c> 与 <c>DEFAULT CHARSET = utf8mb4</c>，前者是"这个 ORM 承诺事务"的兑现方式
/// （默认引擎在有些部署里不是它），后者是 §9.3 那句"默认必须映射到宽的一侧"在 DDL 上的同一判断 ——
/// 不写的话 <c>VARCHAR(200)</c> 装 200 个汉字还是 <c>?</c>，取决于库的默认字符集而不是取决于契约。</para>
///
/// <para>一处今天<em>不判</em>的东西，与三族同一形状但代价不同：索引列的字节上限（旧的 767 / 新的 3072）
/// 在 <c>utf8mb4</c> 下换算成字符是 191 / 768，而 <c>CodeFirst</c> 只看 <c>ColumnContract.MaxLength</c>。
/// 长字符串列上建索引今天会红在库而不是红在这里 —— 报错了才知道要加前缀长度，而前缀长度在契约里没有那一格（§14）。</para></summary>
public class MySqlCodeFirst(MySqlTypes types) : ICodeFirstDialect
{
    public IdentifierPolicy Identifiers => IdentifierPolicy.MySql;

    /// <summary>自增写法：列定义里的一个子句，位置在型与可空性<b>之间</b>（这一家两种顺序都认，
    /// 取文档示例那一形）。带覆写点的理由见类注释第二条。</summary>
    protected virtual string IdentityClause => " AUTO_INCREMENT";

    /// <summary>建表尾部的表选项。<b>不是版式而是语义</b>：<c>ENGINE</c> 决定这张表有没有事务（§5 的整个
    /// <c>SaveChanges</c> 依赖它），<c>CHARSET</c> 决定 <c>VARCHAR(n)</c> 里的 <c>n</c> 能不能装下中文
    /// —— 而后者与 §9.3 的 <c>NVARCHAR</c> 默认是同一条判断的两半：一边管 CLR→列，一边管列→库。
    /// 这里刻意<em>不</em>写 <c>COLLATE</c>：排序规则是现场策略（它与 <c>_ci</c>/<c>_ai</c> 决定守卫的比较结果，
    /// 见 <c>MySqlChangeGuard</c> 最后那段），猜一颗比留给现场更糟。</summary>
    const string TableOptions = " ENGINE = InnoDB DEFAULT CHARSET = utf8mb4";

    public void EmitCreateTable(SqlWriter writer, EntityContract contract)
    {
        writer.Append("CREATE TABLE ");
        EmitTable(writer, contract.Table);
        writer.Append(" (");

        var parts = new List<string>(contract.Columns.Length + 1);

        foreach (var column in contract.Columns)
            parts.Add(Definition(column));

        if (contract.PrimaryKey.Length > 0)
        {
            var key = string.Join(", ", contract.PrimaryKey.Select(c => Identifiers.Quote(c.ColumnName)));

            parts.Add($"CONSTRAINT {Identifiers.Quote("PK_" + contract.Table.Name)} PRIMARY KEY ({key})");
        }

        writer.Append(string.Join(", ", parts)).Append(')').Append(TableOptions);

        foreach (var index in contract.Indexes)
        {
            writer.Append(" ; ");
            EmitCreateIndex(writer, contract.Table, index);
        }
    }

    public void EmitAddColumn(SqlWriter writer, TableInfo table, ColumnContract column)
    {
        writer.Append("ALTER TABLE ");
        EmitTable(writer, table);
        writer.Append(" ADD COLUMN ").Append(Definition(column));
    }

    /// <summary>一句 <c>MODIFY COLUMN</c> 把整列定义重写（见类注释第三条）。
    /// <c>COLUMN</c> 这一颗关键字可省，但写出来与 <c>ADD COLUMN</c> 同形，五家对读时少一处"这一家为什么省了"。</summary>
    public void EmitAlterColumnPrecision(SqlWriter writer, TableInfo table, ColumnContract column)
    {
        writer.Append("ALTER TABLE ");
        EmitTable(writer, table);
        writer.Append(" MODIFY COLUMN ").Append(Definition(column, includeComment: true));
    }

    /// <summary>这一家唯一的注释写法：<c>MODIFY COLUMN</c> 带上 <c>COMMENT</c>（见类注释第四条）。
    /// 返回 true 而不是走 Core 那条"跳过并 WARN"的退路，理由是这件事在这家确实做得到，
    /// 而代价（重写整列）写在上面而不是藏在返回值里。</summary>
    public bool TryEmitColumnComment(SqlWriter writer, TableInfo table, ColumnContract column)
    {
        writer.Append("ALTER TABLE ");
        EmitTable(writer, table);
        writer.Append(" MODIFY COLUMN ").Append(Definition(column, includeComment: true));

        return true;
    }

    public void EmitCreateIndex(SqlWriter writer, TableInfo table, IndexContract index)
    {
        writer.Append(index.Kind switch
        {
            IndexKind.Unique => "CREATE UNIQUE INDEX ",

            // 五家里唯一<em>升格</em>的一格（类注释第五条）。说"三族都抛"是错的，交账时按代码重数过：
            // PG 系与 Oracle 血统两族抛，T-SQL 那一族既不抛也不升格 —— 它落回 <c>_ =></c> 那一臂发成普通
            // <c>CREATE INDEX</c>，是一处"看起来建成了"的形状。v0.31 的第五家抛，而且它是三份"抛"里
            // 唯一一份有厂商文档正面依据的（文档列的索引种类是普通、UNIQUE、BITMAP 三种，没有全文那一档）。今天没有任何公共入口能造出这颗 <c>Kind</c>
            // （<c>IndexAttribute</c> 只带 Unique/Clustered，<c>FluentIndex</c> 只带 Unique，解析链交不出
            // <c>FullText</c>，provider 也就收不到），所以它记 §14 而不是本版缺陷。
            // 它同时是唯一一颗"ASC/DESC 在这里对全文索引没有意义"的：
            // 那一类索引按相关度排序，方向不是它的参数，所以这里不写方向而不是写一个被忽略的方向。
            IndexKind.FullText => "CREATE FULLTEXT INDEX ",
            _ => "CREATE INDEX ",
        });

        writer.Append(Identifiers.Quote(index.Name)).Append(" ON ");
        EmitTable(writer, table);
        writer.Append(" (");

        for (var i = 0; i < index.ColumnNames.Length; i++)
        {
            if (i > 0)
                writer.Append(',');

            writer.Append(' ').Append(Identifiers.Quote(index.ColumnNames[i]));

            if (index.Kind == IndexKind.FullText)
                continue;

            // 方向在这里是"8.0 起才真生效"的一颗：5.7 解析它但忽略它，于是 DESC 索引建出来是升序的，
            // 而"倒着取前 N 行"的查询会退化成一趟排序。今天不判版本（SPI 里没有那一格，§14 那一行），
            // 也不因此不写：写了在 8.0 上是对的，没写的版本上它才是静默的。
            writer.Append(index.Descending ? " DESC" : " ASC");
        }

        writer.Append(" )");
    }

    string Definition(ColumnContract column, bool includeComment = false)
    {
        var writer = new SqlWriter(new(), [], '@');

        EmitColumnDefinition(writer, column, includeComment);

        return writer.ToString();
    }

    void EmitColumnDefinition(SqlWriter writer, ColumnContract column, bool includeComment)
    {
        var stored = types.ApplyStorageDefaults(column);

        writer.Append(Identifiers.Quote(column.ColumnName)).Append(' ').Append(types.GetStoreType(stored));

        if (column.IsIdentity)
            writer.Append(IdentityClause);

        // 与另四族同一形状：可空性读 NullableInStore，DDL 里的可空性不读 IsNullable（v0.24 全站那一条规则）。
        // 这一家今天也没有 true 的 StoreFillsValue —— 而它是五家里<em>最接近</em>有的那一个：
        // ON UPDATE CURRENT_TIMESTAMP 那一档要的就是它，两步接法与不先置位的原因在 MySqlConcurrency。
        writer.Append(stored.NullableInStore ? " NULL" : " NOT NULL");

        // 注释跟着定义走而不是跟着一句独立语句走（类注释第四条）。只有契约真写了才发：
        // 契约没写而库里有一处注释时，改精度那一句会把那一处注释抹掉 —— 这一档已知，记 §14。
        if (includeComment && column.Comment is { Length: > 0 } comment)
            writer.Append(" COMMENT ").AppendStringLiteral(comment, unicode: false);
    }

    /// <summary>模式为空就不写限定（见类注释第一条）：这一家裸名说的是"当前连接所选的数据库"，
    /// 与 Oracle 血统那一家同一条判据（不补一个假名字），而与 T-SQL/PG 两家的 <c>dbo</c>/<c>public</c> 分岔。
    /// 顺带记一处<em>按厂商目录形状</em>的形状差别（这一句不在本仓库证过，五家的目录视图今天没有一台真库跑过它）：
    /// 它的 <c>CONSTRAINT 名 PRIMARY KEY (…)</c> 里那颗名字据文档落在<em>索引</em>目录上
    /// （<c>information_schema.STATISTICS</c> 的 <c>INDEX_NAME = 'PRIMARY'</c>），而落地另三家在<em>约束</em>目录里
    /// 各有一行（第五家本次只读到列与类型那几页，约束那一页【无出处】，所以这句"三家"没有替它签字）。
    /// <b>如果这一句是真的，</b>同一条 DDL 在落地四家的元数据里就不是同一个东西，而 <c>doctor</c>
    /// 是读目录的不是读文本的 —— 归 §14 那份"跨库对账表"，并且它要在有真库的那一天第一个被重读。</summary>
    void EmitTable(SqlWriter writer, TableInfo table)
    {
        if (table.Schema is { Length: > 0 } schema)
        {
            writer.Append(Identifiers.Quote(schema)).Append('.');
        }

        writer.Append(Identifiers.Quote(table.Name));
    }
}
