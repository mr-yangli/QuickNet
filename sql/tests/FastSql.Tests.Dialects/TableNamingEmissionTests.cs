using System.Reflection;
using System.Text;
using FastSql.Metadata;
using FastSql.Sql;
using FastSql.TestInfra;
using Xunit;

namespace FastSql.Tests.Dialects;

/// <summary>规格 §16.4② 那颗"加一条架构测试钉住即可"的测试：表名在<em>整个仓库里是一条值的通路</em>，
/// 不许在任何地方以字符串形式从契约直接拼进 SQL。分表把这个要求从"整洁"升级成了"正确性"——
/// 一处绕过换算的拼接发出的是<code>INSERT INTO FastSqlShardLog_{year}</code>，库里既没有这张表、
/// 也不报错，只在某张别的表上少了行。
///
/// <para><b>两半各钉一件事</b>。第一半是<em>行为</em>：十六家方言族逐家量一遍，模板进发射口必须当场抛，
/// 而换算好的名字家户都落得下同一条后缀（钉的是"没有一家把兜底覆写掉了"——<c>EmitTableName</c> 是
/// virtual，任何一家重写它而忘了那一句守卫，只有逐家跑一遍才看得见）。第二半是<em>形状</em>：反射枚举
/// Abstractions 里所有收 <see cref="TableInfo"/> 的接口成员，闭集就是那九颗，多一颗就要人来认账
/// （钉的是"别新开一条命名的路"，那是这一档唯一防得住"将来"的形式）。</para>
///
/// <para><b>为什么不是文件扫描</b>：扫 <c>src/</c> 找"哪里把 <c>table.Name</c> 拼进了 writer"要一份豁免清单
/// —— DDL 那六家 <c>*CodeFirst</c>、<c>SqlServerKeys.IdentityInsertToggle</c>（今天不可达）、模型打印器、
/// 异常消息、执行标签各占一行。清单一旦写下就替豁免的那几处说话，而它<em>恰恰量不到</em>新加的第六处。
/// 行为那一半不需要清单：模板漏进任何一句可执行文本，后缀断言自己会红。</para>
///
/// <para><b>两次拆探针：各咬得住一半，但两册都会红，区别在红的是哪 16 颗</b>。把 <c>DialectBase.EmitTableName</c>
/// 那句守卫注释掉 → 本册 <c>34 / 16</c>，红的全是 <see cref="Every_family_refuses_a_template_at_the_naming_hook"/>
/// （逐家一条，后缀那颗与两条 Fact 一条不红），而换算链路那一册 <c>63 / 1</c> —— 唯一陪红的
/// <c>A_template_reaching_the_emitter_is_refused_there</c> 就是这条守卫在别处的第二次断言
/// （<c>obj/logs/v38-10-probe-1a.log</c> ∶ <c>obj/logs/v38-10-probe-1b.log</c>）。把桶号宽度从 <c>D2</c> 改成
/// <c>D0</c> → 本册仍是 <c>34 / 16</c> 而红的换成 <see cref="Nothing_a_family_emits_ever_carries_a_placeholder"/>
/// （后缀那一半，逐家一条），换算那一册 <c>63 / 4</c>，四颗全是桶名形状
/// （<c>obj/logs/v38-10-probe-2.log</c>，一场里两册各一段汇总）。两次都数得出 16，可指认的那一半不同 ——
/// 这就是"两半各有各的牙口"能成立的准确形状：不是"只有一册红"，是同一册里两颗 Fact 各自只被自己的那种改动咬得住。</para></summary>
public sealed class TableNamingEmissionTests
{
    static readonly DateTime January = new(2026, 1, 15, 10, 30, 0);
    static readonly DateTime February = new(2026, 2, 15, 10, 30, 0);

    /// <summary><see cref="SplitRouting"/> 认的四颗占位符。判据用"这四颗在不在文本里"而不是"有没有花括号"：
    /// 后者会把 <c>WithSql</c> 那种原样透出的用户文本也算进来。</summary>
    static readonly string[] Tokens = ["{year}", "{month}", "{day}", "{shard}"];

    static FastSqlOrm Orm(string family) => family switch
    {
        "SqlServer" => SqlServer.Orm(),
        "MySql" => MySql.Orm(),
        "Oracle" => Oracle.Orm(),
        "PostgreSql" => PostgreSql.Orm(),
        "Kingbase" => Kingbase.Orm(),
        "OpenGauss" => OpenGauss.Orm(),
        "GaussDb" => GaussDb.Orm(),
        "Highgo" => Highgo.Orm(),
        "Dameng" => Dameng.Orm(),
        "ShenTong" => ShenTong.Orm(),
        "Xugu" => Xugu.Orm(),
        "Gbase8s" => Gbase8s.Orm(),
        "Sqlite" => Sqlite.Orm(),
        "TiDb" => TiDb.Orm(),
        "OceanBase" => OceanBase.Orm(),
        "Gbase8c" => Gbase8c.Orm(),
        _ => throw new InvalidOperationException($"没有这一家方言：{family}"),
    };

    /// <summary>十六家 = <see cref="DbKind"/> 的成员数，一颗不多一颗不少：漏一家就是"这家没被量过"，
    /// 而台账上写的却是"这一档全族已接"。</summary>
    public static TheoryData<string> Families()
    {
        var data = new TheoryData<string>();

        foreach (var kind in Enum.GetNames<DbKind>())
            data.Add(kind);

        return data;
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void Every_family_refuses_a_template_at_the_naming_hook(string family)
    {
        var dialect = Orm(family).Provider.Dialect;

        foreach (var template in new[] { ShardTemplate(family), TenantTemplate(family) })
        {
            var raised = Checks.Raised<FastSqlException>(() =>
            {
                Emit(dialect, template);

                return null;
            });

            Assert.Contains("没换算成具体分片就被送去发射", raised.Message);

            // 消息给的是两条出路而不是一句"不支持"：读侧缺的是选片范围，写侧缺的是行上那颗分片键值。
            Assert.Contains("读侧要先给选片范围", raised.Message);
            Assert.Contains("写侧按行的分片键值算目标表", raised.Message);
        }
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void Nothing_a_family_emits_ever_carries_a_placeholder(string family)
    {
        var orm = Orm(family);

        foreach (var (label, sql, suffixes) in Surfaces(orm))
        {
            foreach (var token in Tokens)
                Assert.False(sql.Contains(token, StringComparison.Ordinal),
                    $"{family} 的 {label} 把分表模板原样发了出去（文本里还有 {token}）：{Environment.NewLine}{sql}");

            foreach (var suffix in suffixes)
                Assert.True(sql.Contains(suffix, StringComparison.OrdinalIgnoreCase),
                    $"{family} 的 {label} 里没有换算好的片名后缀 {suffix}：{Environment.NewLine}{sql}");
        }
    }

    /// <summary>一家方言能碰到的<em>全部</em>命名出口，各配它应当落下的片名后缀。
    /// 读、三种写、DDL 两条档各占一格 —— 这一格少一条，那一面就没有被量过。</summary>
    static (string Label, string Sql, string[] Suffixes)[] Surfaces(FastSqlOrm orm) =>
    [
        ("读·单片", orm.Select<ShardLog>().SplitTables(January, January).ToSql(), ["202601"]),
        ("读·两片", orm.Select<ShardLog>().SplitTables(January, February).ToSql(), ["202601", "202602"]),
        ("读·桶", orm.Select<TenantRow>().SplitTables(0, 1).ToSql(), ["_00", "_01"]),
        ("写·INSERT", orm.Insert(new ShardLog { Id = Guid.Empty, OccurredAt = January, Level = 1 }).Explain().Chunks[0].Sql.Text, ["202601"]),
        ("写·UPDATE", orm.Update<ShardLog>().SplitTable(January).Set(o => o.Level, 9).Where(o => o.Id == Guid.Empty).ToSql(), ["202601"]),
        ("写·DELETE", orm.Delete<ShardLog>().SplitTable(January).Where(o => o.Id == Guid.Empty).ToSql(), ["202601"]),
        ("DDL·时间档", orm.SplitTablesScript<ShardLog>(January, February), ["202601", "202602"]),
        ("DDL·哈希档", orm.SplitTablesScript<TenantRow>(0, 1), ["_00", "_01"]),
    ];

    /// <summary>上面那两份逐家用例的前提：清单确实就是 <see cref="DbKind"/> 全家。少一家不是"少跑一家"，
    /// 是"这一档在那一家身上从来没被量过"，而消息里那一句 <c>没有这一家方言</c> 只防得住拼错、防不住漏写一整条。</summary>
    [Fact]
    public void Every_db_kind_has_an_orm_and_every_orm_is_in_the_list()
    {
        var kinds = Enum.GetNames<DbKind>().Select(Orm).Select(static orm => orm.Provider.Kind.ToString()).ToArray();

        Assert.Equal(Enum.GetNames<DbKind>().Order(StringComparer.Ordinal).ToArray(), kinds.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(16, kinds.Length);
    }

    /// <summary>收 <see cref="TableInfo"/> 的接口成员是一个<em>闭集</em>，九颗各有角色：DML 侧只有
    /// <see cref="ISqlDialect.EmitTableName"/> 一颗，<c>WriteTargetAlias</c> 是同一条分岔的别名那半
    /// （Oracle 血统的自引用名就是表名，所以它也得收这颗值）；<c>ICodeFirstDialect</c> 那四颗只可能被喂换算好的片，
    /// 那一头由 <see cref="Nothing_a_family_emits_ever_carries_a_placeholder"/> 的 DDL 两格量；
    /// <c>IdentityInsertToggle</c> 本版本不可达（§12.4），它现在是名单上的一处<em>已知敞口</em>而不是漏网；
    /// 读系统表那两颗把名字放进绑定参数、不进文本。新开第十颗就是"又开了一条命名的路"，必须人来认账。</summary>
    [Fact]
    public void The_abstraction_surface_takes_a_table_in_exactly_the_named_members()
    {
        var found = typeof(ISqlDialect).Assembly.DefinedTypes
            .Where(static type => type.IsInterface)
            .SelectMany(static type => type.GetMethods()
                .Where(method => method.GetParameters().Any(parameter => parameter.ParameterType == typeof(TableInfo)))
                .Select(method => type.Name + "." + method.Name))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
        [
            "ICodeFirstDialect.EmitAddColumn",
            "ICodeFirstDialect.EmitAlterColumnPrecision",
            "ICodeFirstDialect.EmitCreateIndex",
            "ICodeFirstDialect.TryEmitColumnComment",
            "IKeyStrategy.IdentityInsertToggle",
            "ISchemaReader.ProbeLengthSemanticsAsync",
            "ISchemaReader.ReadTableAsync",
            "ISqlDialect.EmitTableName",
            "ISqlDialect.WriteTargetAlias",
        ], found);
    }

    static TableInfo ShardTemplate(string family) => Orm(family).Schemas.Contract<ShardLog>().Table;

    static TableInfo TenantTemplate(string family) => Orm(family).Schemas.Contract<TenantRow>().Table;

    /// <summary><see cref="SqlWriter"/> 是 <c>ref struct</c>，装不进 lambda 的捕获，所以那一句过这颗本地函数。</summary>
    static void Emit(ISqlDialect dialect, TableInfo table)
    {
        var writer = new SqlWriter(new StringBuilder(), [], dialect.ParameterPrefix);

        dialect.EmitTableName(writer, table);
    }
}
