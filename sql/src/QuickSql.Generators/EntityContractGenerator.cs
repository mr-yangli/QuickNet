using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace QuickSql.Generators;

/// <summary>§10.2 解析链的编译期实现（§12.7 A 的 G1 级）。触发条件是实体带 <c>[Table]</c>——
/// 自研属性或 DataAnnotations 的那个都算，因为「标注过」正是 §12.7 那张三级表里 G1 与 G2 的分界线。
/// 没标注、有基类、属性不可公开写的实体不产出代码，运行期由 <c>GeneratedEntitySchemaRegistry</c>
/// 逐条退回 G2（并按 <c>QuickSql003</c> 说明为什么没走 G1）。</summary>
[Generator(LanguageNames.CSharp)]
public sealed class EntityContractGenerator : IIncrementalGenerator
{
    const string QuickSqlTable = "QuickSql.Metadata.TableAttribute";
    const string DataSchemaTable = "System.ComponentModel.DataAnnotations.Schema.TableAttribute";

    /// <summary>把 C# 文档注释（<c>/// &lt;summary&gt;</c>）当成备注的最低优先档，默认<b>关</b>。
    /// 消费方开这一档要写两行：属性本身，加上 <c>&lt;CompilerVisibleProperty Include="QuickSqlXmlDocComments" /&gt;</c>
    /// ——SDK 只把 <c>@(CompilerVisibleProperty)</c> 里点名的属性折进 <c>build_property.</c> 那一份生成的编辑器配置，
    /// 光设属性生成器读不到（这不是本生成器的兜底，是编译器的口径）。键名公开是给用例引的：
    /// 抄一份字符串进测试，改这一处就成静默假绿。</summary>
    public const string XmlDocCommentsOption = "build_property.QuickSqlXmlDocComments";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // 选项走管道而不是实例字段：生成器那一颗实例在 IDE 里跨多次编译复用，存字段会让上一次的读数
        // 留在下一次编译上，而增量缓存按输入算、根本不会重算那一条——症状是"改了开关而产出不动"。
        var xmlDocComments = context.AnalyzerConfigOptionsProvider
            .Select(static (provider, _) => Read(provider.GlobalOptions, XmlDocCommentsOption));

        var quicksql = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                QuickSqlTable,
                static (node, _) => node is TypeDeclarationSyntax { AttributeLists.Count: > 0 },
                static (capture, _) => capture)
            .Combine(xmlDocComments)
            .Select(static (both, _) => EntityFactsBuilder.Build(both.Left, viaQuickSqlTable: true, both.Right));

        var dataAnnotations = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                DataSchemaTable,
                static (node, _) => node is TypeDeclarationSyntax { AttributeLists.Count: > 0 },
                static (capture, _) => capture)
            .Combine(xmlDocComments)
            .Select(static (both, _) => EntityFactsBuilder.Build(both.Left, viaQuickSqlTable: false, both.Right));

        var outcomes = quicksql.Collect().Combine(dataAnnotations.Collect())
                       .Select(static (both, _) => both.Left.Concat(both.Right).ToList());

        context.RegisterSourceOutput(outcomes, static (spc, batch) =>
        {
            var models = new List<EntityModel>();

            foreach (var outcome in batch)
            {
                foreach (var diagnostic in outcome.Diagnostics)
                    spc.ReportDiagnostic(diagnostic);

                if (outcome.Model is not { } model)
                    continue;

                models.Add(model);
                spc.AddSource(model.Name + ".g.cs", EntityFactsWriter.Render(model));
            }

            if (models.Count > 0)
                spc.AddSource("QuickSqlGeneratedMetadata.g.cs", EntityFactsWriter.RenderLookup(models));
        });
    }

    /// <summary>开关读数：<b>只有写着 <c>true</c> 才算开</b>。缺键、空串、写成 <c>1</c>/<c>yes</c> 一律当关——
    /// 这一档会把源码里的开发期注释搬进库的备注，误开的代价比误关大，所以它不接受"看起来像真"的写法。</summary>
    static bool Read(AnalyzerConfigOptions options, string key)
        => options.TryGetValue(key, out var value) && value is { Length: > 0 } && bool.TryParse(value, out var enabled) && enabled;
}

/// <summary>诊断。<c>messageFormat</c> 一律是 <c>{0}</c>：话术由 <see cref="EntityFactsBuilder"/> 现拼，
/// 因为要与反射侧 <c>MetadataConflictException</c> 的消息逐字对齐——两处各写一份模板就谈不上「一致」。</summary>
static class QuickSqlDiagnostics
{
    public static readonly DiagnosticDescriptor MetadataConflict = Define(
        "QuickSql001", "同一层元数据来源互斥", DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor IndexMemberUnmapped = Define(
        "QuickSql002", "索引引用了未映射的属性", DiagnosticSeverity.Error);

    /// <summary>与 <c>QuickSql002</c> 同一形状：<see cref="EntityFactsBuilder"/> 现拼的消息与反射侧
    /// <c>EntityContractFactory.MapToColumns</c> 那句逐字相同，只是主语从索引换成引用导航。</summary>
    public static readonly DiagnosticDescriptor ForeignKeyMemberUnmapped = Define(
        "QuickSql004", "引用导航的外键指向了未映射的属性", DiagnosticSeverity.Error);

    /// <summary>不是错：G2 接得住，只是这一条路径上没有「编译期零反射」的待遇（§12.7）。</summary>
    public static readonly DiagnosticDescriptor NotGenerated = Define(
        "QuickSql003", "实体不走 G1，退回反射解析", DiagnosticSeverity.Info);

    static DiagnosticDescriptor Define(string id, string title, DiagnosticSeverity severity)
        => new(id, title, "{0}", "QuickSql.Metadata", severity, isEnabledByDefault: true);
}
