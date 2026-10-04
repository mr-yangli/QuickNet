using FastSql.Metadata;
using FastSql.Providers;
using FastSql.Sql;

namespace FastSql.Providers.Gbase8s;

/// <summary>GBase 8s（Informix 血统）的建表与改表（§9.2 的 ICodeFirstDialect，第六家）。
/// 三处与另五家分岔，每一处都是一句"本家的 DDL 形状与那五家不同"：
/// <list type="bullet">
/// <item><b>列定义里没有自增子句，因为自增在<em>型名</em>里</b>：另五家在宽度之后追一句
/// <c>IDENTITY(1,1)</c> / <c>AUTO_INCREMENT</c> / <c>GENERATED … AS IDENTITY</c> 之类，
/// 本家的 <c>SERIAL</c> / <c>BIGSERIAL</c> 就是<em>型名本身</em>（<c>Gbase8sTypes.GetStoreType</c> 第三条）。
/// 于是这一步落在 <see cref="SerialFor"/> 上而不是落在类型名后面，而它与另五家的分工<b>读起来是同一句</b>
/// （"型名归 <c>ITypeMap</c>、自增归 <c>ICodeFirstDialect</c>"）—— 不同的只是这里换的是<em>另一个型名</em>，
/// 那几家加的是一<em>句子句</em>。为什么不能写在 <c>GetStoreType</c> 里：共用层先定型后判自增，
/// 那一版的 <c>SERIAL</c> 分支永远走不到，而这件事是本次由 <c>golden/gbase8s/ddl-order.sql</c> 抓出来的
/// （第一次生成的样本里那一列写的是 <c>"Id" INTEGER NOT NULL</c>），改判整颗记 §13.5。
/// <b>只起点、无步长</b>那一格（<c>SERIAL(n)</c> 的 <c>n</c> 是起始值）今天在本文件发不出去，
/// 因为契约里没有"自增起点"那一格（缺口与 <c>Gbase8sTypes</c> 第三条并排记 §14）。</item>
/// <item><b>约束名写在 <c>PRIMARY KEY (…)</c> <em>之后</em></b>：<c>PRIMARY KEY (col) CONSTRAINT name</c>
/// 【文档·IBM】，与另五家那句 <c>CONSTRAINT name PRIMARY KEY (col)</c> 的词序<em>相反</em>。
/// 这一格是"看着只差引号字形"的那一类陷阱的反面：只差一个词序，而那一个词序在库里是语法错。
/// 六家共同的表级主键发射口在这里第一次需要<em>换序</em>而不是<em>换壳</em>，而 <c>src/</c> 里
/// 那份表级主键文本各族自写（§2 的禁令），所以本家改的是自己这一份、不动别人。</item>
/// <item><b><see cref="TryEmitColumnComment"/> 返回 false，而这是本仓库第一次</b>：这一族的语句清单里
/// <b>没有 <c>COMMENT ON</c> 那一条</b>【文档·IBM】。列注释在那一边是<em>目录表</em>（<c>syscomments</c>）
/// 的事而不是<em>语句</em>的事，而 SPI 里没有"往目录表写一行"的形状。返回 false 之后走 Core 那条
/// "跳过并 WARN"的退路 —— 于是本仓库第一次出现<b>一条已定义能力位在任何 provider 侧都没人声明</b>
/// 也不是：那颗位一直有五家声明，变化是<b>交集第一次掉一位</b>
/// （<c>CapabilityLedgers</c> 那条"每接一家交集就小一格"的反例链今天走到第八格，而它是<em>第一次真掉</em>：
/// 前七次都不掉，因为新来的家声明的位都是已出现过的位；这一次掉的不是"新家的能力少"，
/// 是<b>共用的发射路径第一次有一条走到一家就断</b>）。</item>
/// </list>
/// 另两处与"没有"有关，两处都<em>不是</em>能力缺失而是形状不同：
/// <b>模式</b>那一格，本家有 <c>CREATE SCHEMA</c> 而它的形是 <c>CREATE SCHEMA AUTHORIZATION user</c>
/// —— 那一格里"模式"在库里就是<em>所有者</em>而不是一棵独立的命名空间树【文档·IBM】，
/// 所以本家发不出别家那种"给应用建一个模式、把表放进去"的语句（本条初稿写作"这一家没有
/// <c>CREATE SCHEMA</c>"，被 IBM 手册那一页驳回，改判记 §13.5 —— 而改判之后结论没变、理由整条换了）。
/// <b>多语句</b>那一格，本文件与五家同形地在 <c>EmitCreateTable</c> 尾部用 <c> ; </c> 串索引，
/// 而本家能不能收一条带 <c>;</c> 的命令文本<em>没有读数</em>（与 <c>Gbase8sKeys</c> 类注释第二段那颗
/// 是同一问、同一档，两处一起留给第一台库）—— 六家都躲不掉 §9.5 那条脚本路径，本家是第六家。
///
/// <para><see cref="EmitColumnDefinition"/> 在 DDL 发射点<b>拒收 <c>rowversion</c></b>，与 <c>XuguCodeFirst</c>
/// 同一落点而<em>来路差两档</em>：那一家是"建得成、会自增、读不回"三格实测之后裁的（v0.32 那一批读数），
/// 本家是按<em>缺席读</em>【文档·IBM】—— 这一族的类型清单里没有那一型，所以发出去是一句库会拒的建表。
/// 两边都拒的判据同一条：<b>不要建出一列"看着能用"的废列</b>（§5.8）。差别要说死，否则下一版会以为
/// 本家也有那三格读数：这里没有"建得成"那一半，<c>GBase 8s</c> 收不收那一串词本次<em>未问</em>。</para></summary>
public sealed class Gbase8sCodeFirst(Gbase8sTypes types) : ICodeFirstDialect
{
    public IdentifierPolicy Identifiers => IdentifierPolicy.Gbase8s;

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

            // 词序与另五家相反，见类注释第二条：CONSTRAINT 跟在括号后面，不是走在 PRIMARY KEY 前面。
            parts.Add($"PRIMARY KEY ({key}) CONSTRAINT {Identifiers.Quote("PK_" + contract.Table.Name)}");
        }

        writer.Append(string.Join(", ", parts)).Append(')');

        foreach (var index in contract.Indexes)
        {
            writer.Append(" ; ");
            EmitCreateIndex(writer, contract.Table, index);
        }
    }

    /// <summary>带括号那一形【文档·IBM】：本家的 <c>ADD</c> 子句是一个<em>列定义清单</em>，
    /// 而另五家发的是"ADD COLUMN 一条定义"或"ADD 一条定义"。括号在这里不是装饰 ——
    /// 它是这一句能一次加多列的那个位置的容器，而共用层一次只给一列，所以本家发出的永远是
    /// <c>ADD (一个元素)</c> 那种形状。那一格形状上的多余要说出来，否则读代码的人会以为漏了什么。</summary>
    public void EmitAddColumn(SqlWriter writer, TableInfo table, ColumnContract column)
    {
        writer.Append("ALTER TABLE ");
        EmitTable(writer, table);
        writer.Append(" ADD (").Append(Definition(column)).Append(')');
    }

    /// <summary><c>MODIFY (列 类型)</c>【文档·IBM】—— 本家改列不叫 <c>ALTER COLUMN</c>、不叫
    /// <c>MODIFY COLUMN</c>，而是一颗 <c>MODIFY</c> 后面跟括号里的<em>完整列定义</em>。
    /// <b>"完整"这一格有代价</b>：另五家里有的一家 <c>ALTER COLUMN</c> 只改宽度而不动别的属性，
    /// 本家这一句把可空性与注释位置一起重写一遍，而契约里没写的格子会被库按这一句给的形状定下来
    /// —— 与虚谷那一家记过的同一类风险（<c>CapabilityLedgerTests</c> 的达梦/虚谷那一格并排）。
    /// 这一位是 <c>Alter_Column_Precision</c> 那颗在本家的见证，也是交集掉一位之后<em>仍然在场</em>的那两颗之一。</summary>
    public void EmitAlterColumnPrecision(SqlWriter writer, TableInfo table, ColumnContract column)
    {
        writer.Append("ALTER TABLE ");
        EmitTable(writer, table);
        writer.Append(" MODIFY (").Append(Definition(column)).Append(')');
    }

    /// <summary>不发任何东西，返回 false。理由与后果写在类注释第三条（这一族的语句清单里没有
    /// <c>COMMENT ON</c>，列注释是目录表的事）。<b>刻意不把 <paramref name="column"/> 的文本塞进建表语句</b>：
    /// 那一形在另五家是 <c>COMMENT '…'</c> 的列内子句，而本家没有 —— 造一个注释文本进 DDL 会得到
    /// 一句库拒的建表，比不写注释严重得多（§1.2 原则 4）。</summary>
    public bool TryEmitColumnComment(SqlWriter writer, TableInfo table, ColumnContract column) => false;

    public void EmitCreateIndex(SqlWriter writer, TableInfo table, IndexContract index)
    {
        if (index.Kind == IndexKind.FullText)
            throw new FastSqlException("GBase 8s 没有 CREATE FULLTEXT INDEX 这种写法：这一族的索引种类按手册是普通、EXCLUDE（排除索引）与聚簇那一族【文档·IBM】，"
                + "而全文检索在那一边是 Blend 扩展件、不是一条语句级子句 —— 本次没有可引的那一页，所以今天不发。"
                + "另注意这一家多出来的那一支（EXCLUDE）在契约的 IndexKind 里没有对应那颗，所以它今天也发不出去（§14，与虚谷那句 BITMAP 同一格）。");

        writer.Append(index.Kind == IndexKind.Unique ? "CREATE UNIQUE INDEX " : "CREATE INDEX ");
        writer.Append(Identifiers.Quote(index.Name)).Append(" ON ");
        EmitTable(writer, table);
        writer.Append(" (");

        for (var i = 0; i < index.ColumnNames.Length; i++)
        {
            if (i > 0)
                writer.Append(',');

            writer.Append(' ').Append(Identifiers.Quote(index.ColumnNames[i]));

            // 只写 DESC、不写 ASC【文档·IBM】：这一族的索引列限定词手册给的是 DESC，
            // 而另五家（虚谷、达梦那几家）发的是"ASC/DESC 都写一颗"。缺省那一支本来就是升序，
            // 所以少写不改变语义；而万一这一家压根不认 ASC 那颗关键字，多写的那一颗会让整句红。
            // 两案不对称，取少写的一边 —— 与 XuguDialect 类注释第三条"取错不起的那一边"同一条判据。
            if (index.Descending)
                writer.Append(" DESC");
        }

        writer.Append(" )");
    }

    string Definition(ColumnContract column)
    {
        var writer = new SqlWriter(new(), [], '?');

        EmitColumnDefinition(writer, column);

        return writer.ToString();
    }

    void EmitColumnDefinition(SqlWriter writer, ColumnContract column)
    {
        var stored = types.ApplyStorageDefaults(column);
        var storeType = types.GetStoreType(stored);

        if (string.Equals(storeType.Trim(), "rowversion", StringComparison.OrdinalIgnoreCase))
            throw new FastSqlException("GBase 8s 不建 rowversion 列：这一族的类型清单里没有那一型（按缺席读【文档·IBM】，"
                + "本次没有读到它的清单，更没有一台库能问）。它不是虚谷那一格（那一家是实测到「建得成、会自增、读不回」三格之后裁的），"
                + "所以这里的判据只有一条：发一句库会拒的建表不如不发。"
                + "这一家请改用应用给值的并发列：[ConcurrencyToken] 而不带 [Column(DbTypes = \"rowversion\")]，"
                + "比较式与写侧与另五家逐字同形（Gbase8sConcurrency），缺的只是「由库递增」那一格 —— 那一格等的是 SPI 与一台库，不是改名。");

        writer.Append(Identifiers.Quote(column.ColumnName)).Append(' ').Append(SerialFor(column, storeType));

        // 本家没有列尾自增子句：IsIdentity 已经在型名里（SERIAL / BIGSERIAL），类注释第一条。
        writer.Append(stored.NullableInStore ? " NULL" : " NOT NULL");
    }

    /// <summary>自增列把整数的型名换成 <c>SERIAL</c> / <c>BIGSERIAL</c>，其余一律原样穿过。
    /// <b>为什么不写在 <see cref="Gbase8sTypes.GetStoreType"/> 里</b>：共用层在
    /// <c>EntityContractFactory.Complete</c> 第 130 行定型、第 133 行才判自增，那一侧看到的
    /// <c>IsIdentity</c> 恒为 false，写在那里是一颗永远走不到的分支（本次由生成出来的建表样本抓到）。
    /// <b>换名的判据取"算出来的型名"而不是取"人有没有写过"</b>，因为契约里那两格已经分不开了
    /// （<c>StoreTypeName</c> 被定型那一步填上之后，echo 与人写的同名）——所以人手工在自增列上写
    /// <c>[Column(DbTypes = "INTEGER")]</c> 会被这里换成 <c>SERIAL</c>。那一格是有意的：
    /// <c>IsIdentity</c> 是这一列更近的事实，而写下的 <c>INTEGER</c> 在自增列上本来就不是一个成立的声明。
    /// 换成别的名字（<c>DECIMAL(20,0)</c>、<c>INT8</c>…）则原样穿过：这里只认自己算得出的那两颗。</summary>
    static string SerialFor(ColumnContract column, string storeType)
    {
        if (!column.IsIdentity)
            return storeType;

        return storeType.Trim() switch
        {
            "INTEGER" => "SERIAL",
            "BIGINT" => "BIGSERIAL",
            _ => storeType,
        };
    }

    void EmitTable(SqlWriter writer, TableInfo table)
    {
        if (table.Schema is { Length: > 0 } schema)
        {
            writer.Append(Identifiers.Quote(schema)).Append('.');
        }

        writer.Append(Identifiers.Quote(table.Name));
    }
}
