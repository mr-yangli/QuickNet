using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FastSql.Generators;

/// <summary>§10.2 解析链的编译期实现（§12.7 A 的 G1 级）。触发条件是实体带 <c>[Table]</c>——
/// 自研属性或 DataAnnotations 的那个都算，因为「标注过」正是 §12.7 那张三级表里 G1 与 G2 的分界线。
/// 没标注、有基类、属性不可公开写的实体不产出代码，运行期由 <c>GeneratedEntitySchemaRegistry</c>
/// 逐条退回 G2（并按 <c>FastSql003</c> 说明为什么没走 G1）。</summary>
[Generator(LanguageNames.CSharp)]
public sealed class EntityContractGenerator : IIncrementalGenerator
{
    const string FastSqlTable = "FastSql.Metadata.TableAttribute";
    const string DataSchemaTable = "System.ComponentModel.DataAnnotations.Schema.TableAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var fastsql = context.SyntaxProvider.ForAttributeWithMetadataName(
            FastSqlTable,
            static (node, _) => node is TypeDeclarationSyntax { AttributeLists.Count: > 0 },
            static (capture, _) => EntityFactsBuilder.Build(capture, viaFastSqlTable: true));

        var dataAnnotations = context.SyntaxProvider.ForAttributeWithMetadataName(
            DataSchemaTable,
            static (node, _) => node is TypeDeclarationSyntax { AttributeLists.Count: > 0 },
            static (capture, _) => EntityFactsBuilder.Build(capture, viaFastSqlTable: false));

        var outcomes = fastsql.Collect().Combine(dataAnnotations.Collect())
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
                spc.AddSource("FastSqlGeneratedMetadata.g.cs", EntityFactsWriter.RenderLookup(models));
        });
    }
}

/// <summary>诊断。<c>messageFormat</c> 一律是 <c>{0}</c>：话术由 <see cref="EntityFactsBuilder"/> 现拼，
/// 因为要与反射侧 <c>MetadataConflictException</c> 的消息逐字对齐——两处各写一份模板就谈不上「一致」。</summary>
static class FastSqlDiagnostics
{
    public static readonly DiagnosticDescriptor MetadataConflict = Define(
        "FastSql001", "同一层元数据来源互斥", DiagnosticSeverity.Error);

    public static readonly DiagnosticDescriptor IndexMemberUnmapped = Define(
        "FastSql002", "索引引用了未映射的属性", DiagnosticSeverity.Error);

    /// <summary>与 <c>FastSql002</c> 同一形状：<see cref="EntityFactsBuilder"/> 现拼的消息与反射侧
    /// <c>EntityContractFactory.MapToColumns</c> 那句逐字相同，只是主语从索引换成引用导航。</summary>
    public static readonly DiagnosticDescriptor ForeignKeyMemberUnmapped = Define(
        "FastSql004", "引用导航的外键指向了未映射的属性", DiagnosticSeverity.Error);

    /// <summary>不是错：G2 接得住，只是这一条路径上没有「编译期零反射」的待遇（§12.7）。</summary>
    public static readonly DiagnosticDescriptor NotGenerated = Define(
        "FastSql003", "实体不走 G1，退回反射解析", DiagnosticSeverity.Info);

    static DiagnosticDescriptor Define(string id, string title, DiagnosticSeverity severity)
        => new(id, title, "{0}", "FastSql.Metadata", severity, isEnabledByDefault: true);
}
