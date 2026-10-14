using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace QuickSql.Generators;

/// <summary>§4 一行注册的登记面生成器。与 <see cref="EntityContractGenerator"/>（G1）不共享触发链：
/// 那条走 <c>ForAttributeWithMetadataName</c>，只看见**当前编译单元的源码**；而 provider 是引用过来的
/// 元数据，源码路径看不到。所以这一颗订阅 <c>context.CompilationProvider</c>、在
/// <c>ReferencedAssemblySymbols</c> 上找 <c>[QuickSqlProvider]</c>，把结果 emit 成消费方汇编里的一段
/// <c>[ModuleInitializer]</c>——每一行都是 <c>static f =&gt; new XxxProvider(f)</c>，纯静态委托、零反射，
/// 因此 §12.7 那颗 AOT/trim 契约不破。</summary>
/// <remarks>三条边界：
/// ① provider 叶子自己不带这段生成（它不"被自己引用"），只有引用 provider 且挂了生成器的那一份
///   编译单元产 <c>GeneratedProviders</c>——这正好和"注册发生在组装根"的语义对齐。
/// ② 没引用 provider 或未挂生成器时零产出，<c>ProviderRegistry</c> 是空表；
///   <c>QuickSqlOrm.Create</c> 那一步抛的错里点名"未挂生成器或没引用 provider"。
/// ③ 同一颗 <c>DbKind</c> 在多份被引用程序集里出现时按 <c>(Kind, 全名)</c> 稳定排序、后写为准，
///   与运行期 <c>ConcurrentDictionary</c> 的覆盖语义一致。<b>本版不发诊断</b>：重复登记不算错，
///   而是"最后赢"，将来若真需要"两份 provider 抢同一颗 Kind"应响，再补 <c>QuickSql005</c>。</remarks>
[Generator(LanguageNames.CSharp)]
public sealed class ProviderRegistrationGenerator : IIncrementalGenerator
{
    const string QuickSqlProviderAttribute = "QuickSql.Providers.QuickSqlProviderAttribute";
    const string DbKindType = "QuickSql.DbKind";
    const string IDbProviderType = "QuickSql.Providers.IDbProvider";
    const string DbProviderFactoryType = "System.Data.Common.DbProviderFactory";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var registrations = context.CompilationProvider
            .Select(static (compilation, _) => Collect(compilation));

        context.RegisterSourceOutput(registrations, static (spc, list) =>
        {
            if (list.Count == 0) return;
            spc.AddSource("GeneratedProviders.g.cs", Render(list));
        });
    }

    static List<Registration> Collect(Compilation compilation)
    {
        // 少了其中任何一颗，产出跑不起来——最典型是消费者只引了 Core、没引 Abstractions（不可能）
        // 或者只引了 provider 而没引 QuickSql.Generators 作 Analyzer（这才是本文件真正的守门）。
        var attribute = compilation.GetTypeByMetadataName(QuickSqlProviderAttribute);
        var dbKind = compilation.GetTypeByMetadataName(DbKindType);
        var idbProvider = compilation.GetTypeByMetadataName(IDbProviderType);
        var factory = compilation.GetTypeByMetadataName(DbProviderFactoryType);
        if (attribute is null || dbKind is null || idbProvider is null || factory is null)
            return [];

        var seen = new Dictionary<int, Registration>();

        foreach (var asm in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            foreach (var type in Flatten(asm.GlobalNamespace))
            {
                if (type.IsAbstract || type.DeclaredAccessibility != Accessibility.Public)
                    continue;
                if (type.TypeKind != TypeKind.Class)
                    continue;

                var attr = FindAttribute(type, attribute);
                if (attr is null)
                    continue;
                if (!type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, idbProvider)))
                    continue;
                if (!HasFactoryCtor(type, factory))
                    continue;

                if (attr.ConstructorArguments.IsDefaultOrEmpty)
                    continue;
                if (attr.ConstructorArguments[0].Value is not int raw)
                    continue;

                var memberName = DbKindMemberName(dbKind, raw);
                if (memberName is null)
                    continue;

                // DriverKey 是每颗 leaf 上都存在的 `public const string DriverKey`；
                // 缺这一颗就不登记（QuickSqlOrm.Create 走内建表解析时找不到键，抛"该 Kind 无登记"更清晰）。
                var driverKey = DriverKeyOf(type);
                if (driverKey is null)
                    continue;

                var entry = new Registration(
                    raw,
                    memberName,
                    driverKey,
                    type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));

                // 同 Kind 撞车：按全名字典序保留"最后一个"。选这个而不是"报诊断"是因为本仓库今天
                // 每颗 Kind 只有一份 provider；等真出现多来源时再决定是覆盖还是响。
                if (!seen.TryGetValue(raw, out var existing) ||
                    string.CompareOrdinal(existing.ProviderFullName, entry.ProviderFullName) < 0)
                    seen[raw] = entry;
            }
        }

        var result = seen.Values.ToList();
        result.Sort(static (a, b) => a.KindOrdinal.CompareTo(b.KindOrdinal));
        return result;
    }

    static IEnumerable<INamedTypeSymbol> Flatten(INamespaceSymbol ns)
    {
        foreach (var member in ns.GetMembers())
        {
            switch (member)
            {
                case INamespaceSymbol child:
                    foreach (var nested in Flatten(child))
                        yield return nested;
                    break;
                case INamedTypeSymbol type:
                    foreach (var nested in Flatten(type))
                        yield return nested;
                    break;
            }
        }
    }

    static IEnumerable<INamedTypeSymbol> Flatten(INamedTypeSymbol type)
    {
        yield return type;
        foreach (var nested in type.GetTypeMembers())
            foreach (var deeper in Flatten(nested))
                yield return deeper;
    }

    static AttributeData? FindAttribute(INamedTypeSymbol type, INamedTypeSymbol attribute)
    {
        foreach (var data in type.GetAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(data.AttributeClass, attribute))
                return data;
        }
        return null;
    }

    static bool HasFactoryCtor(INamedTypeSymbol type, INamedTypeSymbol factory)
    {
        foreach (var ctor in type.InstanceConstructors)
        {
            if (ctor.DeclaredAccessibility != Accessibility.Public) continue;
            if (ctor.Parameters.Length != 1) continue;
            if (!SymbolEqualityComparer.Default.Equals(ctor.Parameters[0].Type, factory)) continue;
            return true;
        }
        return false;
    }

    static string? DbKindMemberName(INamedTypeSymbol dbKind, int raw)
    {
        foreach (var member in dbKind.GetMembers())
        {
            if (member is IFieldSymbol field &&
                field.IsStatic && field.Name != "value__" &&
                field.ConstantValue is int v && v == raw)
                return field.Name;
        }
        return null;
    }

    /// <summary>读那颗 <c>public const string DriverKey</c>。沿继承链向上找一次，找不到返回 null
    /// （那一颗就整颗跳过——一行注册对没键的 leaf 无意义）。</summary>
    static string? DriverKeyOf(INamedTypeSymbol type)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            foreach (var member in t.GetMembers("DriverKey"))
            {
                if (member is IFieldSymbol { IsConst: true, ConstantValue: string s } field)
                    return s;
            }
        }
        return null;
    }

    static string Render(List<Registration> list)
    {
        var b = new StringBuilder();
        b.AppendLine("// <auto-generated/>")
         .AppendLine("// 由 QuickSql.Generators.ProviderRegistrationGenerator 生成（§4 一行注册）。手改会被下一次生成覆盖。")
         .AppendLine("// 每一行都是 `static f => new XxxProvider(f)` 的静态委托，零反射、AOT/trim 安全。")
         .AppendLine("#nullable enable")
         .AppendLine("using System.Runtime.CompilerServices;")
         .AppendLine("using System.Data.Common;")
         .AppendLine()
         .AppendLine("namespace QuickSql.Generated;")
         .AppendLine()
         .AppendLine("/// <summary>装配根自动登记：把本编译单元能看见的每一颗 <c>[QuickSqlProvider]</c>")
         .AppendLine("/// 折进 <c>QuickSql.Providers.ProviderRegistry</c>。<c>QuickSql.QuickSqlOrm.Create</c> 从这里读表。</summary>")
         .AppendLine("static class GeneratedProviders")
         .AppendLine("{")
         .AppendLine("    [ModuleInitializer]")
         .AppendLine("    internal static void Register()")
         .AppendLine("    {");

        foreach (var r in list)
        {
            b.Append("        global::QuickSql.Providers.ProviderRegistry.Register(")
             .Append("global::QuickSql.DbKind.").Append(r.KindName).Append(", ")
             .Append(SymbolDisplay.FormatLiteral(r.DriverKey, quote: true)).Append(", ")
             .Append("static (DbProviderFactory f) => new ").Append(r.ProviderFullName).Append("(f));")
             .AppendLine();
        }

        b.AppendLine("    }")
         .AppendLine("}");
        return b.ToString();
    }

    sealed class Registration
    {
        public Registration(int kindOrdinal, string kindName, string driverKey, string providerFullName)
        {
            KindOrdinal = kindOrdinal;
            KindName = kindName;
            DriverKey = driverKey;
            ProviderFullName = providerFullName;
        }

        public int KindOrdinal { get; }
        public string KindName { get; }
        public string DriverKey { get; }
        public string ProviderFullName { get; }
    }
}
