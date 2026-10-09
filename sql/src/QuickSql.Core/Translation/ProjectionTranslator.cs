using QuickSql.Metadata;
using QuickSql.Query.Model;
using QuickSql.Sql.Ast;
using System.Linq.Expressions;
using System.Reflection;

namespace QuickSql.Translation;

/// <summary>Select 的 lambda → 投影计划。投影决定物化形状，所以它是模型的一部分，不是方言的事（§5.5）。</summary>
public sealed class ProjectionTranslator(ExpressionTranslator expressions)
{
    public ProjectionPlan Translate(LambdaExpression projector, Scope scope)
    {
        var body = Unwrap(projector.Body);

        // 整行：o => o，或 o => new Order { … } 之外的实体本身
        if (body is ParameterExpression parameter && scope.Contains(parameter))
            return new EntityProjection(scope.TableIndex(parameter));

        // 值类型不一定是"一颗值"：`record struct` 与用户自己的 struct 也是值类型，而它们是投影目标。
        // 所以先问"这一颗是不是被构造出来的形状"，是就让给下面那两扇门（成员初始化 / 构造器）。
        // BCL 那一族（DateTime / Guid / 元组 / …）仍然算值 —— 判据用"与 object 同程序集"而不是列名单，
        // 新增一个 BCL 值类型不用回来改这里（`new DateTime(o.X.Year, 1, 1)` 这类必须留在值投影侧）。
        if (!IsConstructedShape(body)
            && (body.Type.IsValueType || body.Type == typeof(string) || body.Type == typeof(decimal) || body.Type == typeof(Guid)))
            return new ValueProjection(expressions.Visit(body, scope), UnwrapType(body.Type));

        switch (body)
        {
            case MemberInitExpression init:
                return MemberInit(init, scope);

            case NewExpression @new when @new.Arguments.Count > 0:
                return Constructor(@new, scope);

            case NewExpression:
                throw new UntranslatableExpressionException(projector.ToString(), "无参构造的投影没有意义，请至少选一列");

            case MemberExpression single:
                return new ValueProjection(expressions.Visit(single, scope), UnwrapType(projector.Body.Type));

            default:
                // o => o.SomeNavigation 之类：能翻译就当值，翻不了就说清楚。
                return new ValueProjection(expressions.Visit(body, scope), UnwrapType(projector.Body.Type));
        }
    }

    ProjectionPlan MemberInit(MemberInitExpression init, Scope scope)
    {
        var slots = new List<ProjectionSlot>();

        foreach (var binding in init.Bindings)
        {
            if (binding is not MemberAssignment assignment)
                throw new UntranslatableExpressionException(init.ToString(), "投影里只支持成员赋值，不支持集合初始化（子集合加载是 IncludeMany 的职责，P1）");

            var member = assignment.Member;
            var value = Unwrap(assignment.Expression);

            if (value is LambdaExpression)
                throw new UntranslatableExpressionException(init.ToString(), "投影里不接受委托");

            var node = expressions.Visit(value, scope);
            slots.Add(new ProjectionSlot(member.Name, node, DeclaredType(member)));
        }

        return new ObjectProjection(init.Type, [.. slots], ViaConstructor: false);
    }

    ProjectionPlan Constructor(NewExpression @new, Scope scope)
    {
        var ctor = @new.Constructor ?? throw new UntranslatableExpressionException(@new.ToString(), "构造器投影必须有可解析的构造函数");
        var slots = new List<ProjectionSlot>();
        var parameters = ctor.GetParameters();

        if (@new.Arguments.Count != parameters.Length)
            throw new UntranslatableExpressionException(@new.ToString(), "构造器参数与实参数量不一致");

        for (var i = 0; i < parameters.Length; i++)
        {
            var argument = Unwrap(@new.Arguments[i]);
            slots.Add(new ProjectionSlot(parameters[i].Name ?? ("arg" + i), expressions.Visit(argument, scope), parameters[i].ParameterType));
        }

        return new ObjectProjection(@new.Type, [.. slots], ViaConstructor: true);
    }

    static Expression Unwrap(Expression node)
    {
        while (node is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.Quote } unary)
            node = unary.Operand;

        return node;
    }

    /// <summary>这一颗表达式是不是"把一个形状构造出来"（而不是取一个值）。只有带实参的构造与
    /// 成员初始化算，无参构造在下面另有拒绝理由。</summary>
    static bool IsConstructedShape(Expression body) => body switch
    {
        MemberInitExpression init => IsUserDefinedShape(init.Type),
        NewExpression { Arguments.Count: > 0 } @new => IsUserDefinedShape(@new.Type),
        _ => false,
    };

    /// <summary>用户自己造的形状 = 不在 <see cref="object"/> 那个程序集里的类型。</summary>
    static bool IsUserDefinedShape(Type type) => type.Assembly != typeof(object).Assembly;

    /// <summary>槽位类型 = <b>落地目标的声明类型</b>，不是源表达式的类型。值最终由
    /// <c>ValueConverter.To&lt;这个类型&gt;</c> 交回并赋给那个成员/参数，所以"NULL 交回什么"只能由声明类型定：
    /// <c>int</c> 交回 0、<c>int?</c> 交回 null，两个都是该类型自己的默认值（规则在 §5.7）。
    /// 用表达式类型则会把 <c>int?</c> 摊平成 <c>int</c>，于是同一条缺陷在两条装配分支上有两种脸色：
    /// 成员初始化静默给 0（<c>HasValue</c> 还是真），构造器那条直接抛 <c>ArgumentException</c>
    /// （<c>Expression.New</c> 不做隐式加宽）—— 后者意味着带可空值类型参数的 DTO 投影根本不可用（§14 那一格）。</summary>
    static Type DeclaredType(MemberInfo member) => member switch
    {
        PropertyInfo property => property.PropertyType,
        _ => ((FieldInfo) member).FieldType,
    };

    /// <summary>只用于<b>标量</b>投影的 <see cref="ValueProjection.ResultType"/>：那里它是一个形状提示
    /// （给 <c>Explain()</c> 打印），真正决定读取的是调用方的类型实参 —— 物化器对标量走
    /// <c>To&lt;T&gt;</c>，可空性本来就带过去了，所以这一侧不需要保留 <c>Nullable&lt;&gt;</c>。
    /// DTO 槽位不能照这一条办，见 <see cref="DeclaredType"/>。</summary>
    static Type UnwrapType(Type type) => Nullable.GetUnderlyingType(type) ?? type;
}
