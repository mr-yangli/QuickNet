using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using FastSql.Metadata;
using FastSql.Providers;
using FastSql.Schema;
using FastSql.Query;
using FastSql.Query.Model;
using FastSql.Sql;
using FastSql.Sql.Ast;
using System.Linq.Expressions;

namespace FastSql.Translation;

/// <summary>lambda → AST 表达式。链式与 LINQ 共用这一个访问器（§5.5 硬约束 2：LINQ 侧不许有第二套翻译路径）。
/// 翻译不了的形态一律抛 <see cref="UntranslatableExpressionException"/> 并给改写建议，绝不静默客户端求值（§5.8）。</summary>
/// <remarks>闭包常量求值需要反射与委托编译，因此整个归约器标 <c>RUC/RDC</c>：AOT 场景要么换成表达式树解释器
/// （EF Core 为此自带一个），要么走 <c>FastSql.Generators</c> 预翻译的查询。这条边界只有<b>一处</b>，
/// 由调用方（查询提供者）显式承担。</remarks>
[RequiresUnreferencedCode("闭包常量求值按 MemberInfo 读字段/属性。")]
[RequiresDynamicCode("闭包常量求值的退路是 LambdaExpression.Compile()。")]
public sealed class ExpressionTranslator(IDbProvider provider, IEntitySchemaRegistry schemas, Func<Type, int, TableRef> newTableRef)
{
    public IDbProvider Provider { get; } = provider;
    readonly IEntitySchemaRegistry _schemas = schemas;
    readonly Func<Type, int, TableRef> _newTableRef = newTableRef;
    int _subqueryIndex = 1000;

    /// <summary>谓词位：bool 列要补成 <c>[flag] = 1</c>，SQL Server 不接受裸 bit 列作条件。</summary>
    public SqlExpression Predicate(LambdaExpression lambda, Scope scope)
        => Booleanized(lambda.Body, Visit(lambda.Body, scope));

    public SqlExpression Visit(Expression node, Scope scope, ColumnContract? hint = null)
    {
        switch (node)
        {
            case BinaryExpression binary:
                return Binary(binary, scope, hint);

            case UnaryExpression unary:
                return Unary(unary, scope, hint);

            case MemberExpression member:
                return Member(member, scope, hint);

            case MethodCallExpression call:
                return Call(call, scope, hint);

            case ConditionalExpression conditional:
                return new CaseExpression(null,
                [
                    new WhenClause(Booleanized(conditional.Test, Visit(conditional.Test, scope)), Visit(conditional.IfTrue, scope, hint)),
                ], Visit(conditional.IfFalse, scope, hint));

            case ConstantExpression constant:
                return Value(constant.Value, hint);

            case ParameterExpression parameter:
                throw new UntranslatableExpressionException(scope.NameOf(parameter),
                    "整行不能当值用：成员位置只能放列（如 new { o.Id, c.Name }），要整行就只选一张表（Select((o, c) => o)）");

            case InvocationExpression invocation:
                throw new UntranslatableExpressionException(invocation.ToString(), "去掉显式委托调用，把表达式直接内联");

            default:
                return Reduced(node, scope, hint);
        }
    }

    SqlExpression Reduced(Expression node, Scope scope, ColumnContract? hint)
    {
        // 不引用任何查询参数的子表达式 = 客户端可求值常量（闭包变量、静态字段、预计算算术）。
        if (ReferencesScope(node, scope))
            throw new UntranslatableExpressionException(node.ToString(),
                "该形态当前无映射。对照 §5.7 覆盖矩阵；确需本地计算就先 ToList()/ToListAsync() 落到内存再算", node.NodeType.ToString());

        return Value(Evaluate(node), hint);
    }

    // ── 叶子 ──────────────────────────────────────────────────────────

    static SqlExpression Value(object? value, ColumnContract? hint)
    {
        if (value is null)
            return new ParameterValue(null, hint);

        var type = value.GetType();

        // 枚举按底层整型落库（§9.3）：不这样做的库会按名字绑参，读回来就是一串问号。
        if (type.IsEnum)
            value = Convert.ChangeType(value, Enum.GetUnderlyingType(type));

        return new ParameterValue(value, hint);
    }

    public static bool ReferencesScope(Expression node, Scope scope)
        => node switch
        {
            ParameterExpression p => scope.Contains(p),
            LambdaExpression lambda => ReferencesScope(lambda.Body, scope),
            BinaryExpression b => ReferencesScope(b.Left, scope) || ReferencesScope(b.Right, scope),

            // 缺这一臂时，"引用了行的成员初始化"被判成不引用参数，走进 Evaluate 的编译退路，
            // 吐出来的是 InvalidOperationException: variable 'o' of type 'X' referenced from scope ''
            // —— 一句指不了路的内部错误。补上之后同一颗表达式拿到的是"该形态当前无映射"（实测两形：
            // <c>new S { A = o.A } == default</c> 与 <c>new S { A = o.A }.ToString() != "x"</c>）。
            // 另注：net10 上 <see cref="MemberInitExpression"/> 已经不继承 UnaryExpression
            // （基类是 Expression，属性叫 <c>NewExpression</c> 而不是 <c>New</c>），靠下面那条
            // UnaryExpression 兜不到 —— 实测过。两类仍未覆盖、症状还是那句内部错误（§14 已记）：
            // 集合初始化 <c>new C { Items = { o.A } }</c>（这一支不走 OfType&lt;MemberAssignment&gt;），
            // 以及 IN 的集合那一侧（<c>new[]{ o.X }.Contains(y)</c>，它直接 Evaluate，不过分类器）。
            MemberInitExpression init
                => init.Bindings.OfType<MemberAssignment>().Any(b => ReferencesScope(b.Expression, scope))
                   || ReferencesScope(init.NewExpression, scope),

            UnaryExpression u => ReferencesScope(u.Operand, scope),
            MemberExpression m => m.Expression is not null && ReferencesScope(m.Expression, scope),
            MethodCallExpression c => (c.Object is not null && ReferencesScope(c.Object, scope)) || c.Arguments.Any(a => ReferencesScope(a, scope)),
            ConditionalExpression c => ReferencesScope(c.Test, scope) || ReferencesScope(c.IfTrue, scope) || ReferencesScope(c.IfFalse, scope),
            NewExpression n => n.Arguments.Any(a => ReferencesScope(a, scope)),
            NewArrayExpression a => a.Expressions.Any(e => ReferencesScope(e, scope)),
            InvocationExpression i => ReferencesScope(i.Expression, scope),
            TypeBinaryExpression t => t.Expression is not null && ReferencesScope(t.Expression, scope),
            _ => false,
        };

    /// <summary>求值「不引用任何查询参数」的子表达式。先走常数/字段/属性这条无代码生成的快路，
    /// 只有真正需要执行用户算术时才付 <c>Compile()</c> 的成本。</summary>
    public static object? Evaluate(Expression node) => node switch
    {
        ConstantExpression c => c.Value,

        MemberExpression { Member: FieldInfo field } m => field.GetValue(m.Expression is null ? null : Evaluate(m.Expression)),

        MemberExpression { Member: PropertyInfo property } m => property.GetValue(m.Expression is null ? null : Evaluate(m.Expression)),

        UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u => Coerce(Evaluate(u.Operand), u.Type),

        // default(T)、用户算术、方法调用等一律交给编译退路：形态再多也不必在此枚举一遍。
        _ => Expression.Lambda(node).Compile().DynamicInvoke(),
    };

    static object? Coerce(object? value, Type target)
    {
        if (value is null || target.IsInstanceOfType(value))
            return value;

        var underlying = Nullable.GetUnderlyingType(target) ?? target;

        return underlying.IsEnum ? Enum.ToObject(underlying, value) : Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture);
    }

    // ── 成员 ──────────────────────────────────────────────────────────

    SqlExpression Member(MemberExpression member, Scope scope, ColumnContract? hint)
    {
        // Nullable<T> 的三个读法先截掉，否则会被当成不存在的列。
        if (member.Expression is not null && member.Expression.Type.IsGenericType
            && member.Expression.Type.GetGenericTypeDefinition() == typeof(Nullable<>)
            && member.Member.Name is "HasValue" or "Value" or "GetValueOrDefault")
        {
            var target = Visit(member.Expression, scope, hint);

            return member.Member.Name == "HasValue"
                ? new SqlUnaryExpression(UnaryOperator.IsNotNull, target)
                : target;
        }

        // 分组形参（<c>g</c>）不是表位，它的成员读法在列契约之外：单列键是 <c>g.Key</c> 那一颗表达式，
        // 复合键是 <c>g.Key.&lt;成员&gt;</c>。判在位置绑定与 Part 之前 —— 走后两条路都会把 `g` 当表位问，
        // 得到一句「该形参没有对应的 FROM 表」，而那是一个把正确写法报成错误的消息。
        if (RootedAtGrouping(member, scope, out var grouping, out var path))
            return GroupingKey(grouping, path, member);

        if (member.Expression is ParameterExpression parameter)
        {
            var table = scope.TableOf(parameter);
            var contract = table.Contract ?? throw new FastSqlException($"表位 {table.Alias} 没有实体契约，无法解析 {member.Member.Name}");

            if (contract.ByMember.TryGetValue(member.Member.Name, out var column))
                return new ColumnExpression(table, column);

            throw Navigation(member, $"{table.Alias}.{member.Member.Name}");
        }

        if (member.Expression is null || !ReferencesScope(member.Expression, scope))
            return Value(Evaluate(member), hint);

        // 引用了表位、却又不是列的成员读法（<c>o.CreatedAt.Year</c>、<c>o.CustomerName.Length</c>）
        // 只能是 CLR 的派生形状。认不出来必须抛：把 <c>o.CreatedAt.Kind</c> 静默翻译成一个裸列，
        // 产出的是"语法正确、结果错"的 SQL —— 那正是 §5.8 禁止的那一类。
        return Part(member, scope, hint);
    }

    /// <summary>这一串成员读法是不是长在一个分组形参上（<c>g</c>），并把它的路径按外→内收成
    /// <c>["Key"]</c> / <c>["Key","City"]</c>。中间只要出现非成员节点（方法调用、转换…）就不算，
    /// 交给原有那两条路给出它们各自的报错。</summary>
    static bool RootedAtGrouping(MemberExpression member, Scope scope, out GroupingShape grouping, out string[] path)
    {
        var names = new List<string>();
        Expression at = member;

        while (at is MemberExpression chain)
        {
            names.Add(chain.Member.Name);
            at = chain.Expression!;
        }

        grouping = default;

        if (at is not ParameterExpression parameter || !scope.TryGroupingOf(parameter, out grouping))
        {
            path = [];

            return false;
        }

        names.Reverse();
        path = [.. names];

        return true;
    }

    /// <summary><c>g.Key</c> 与 <c>g.Key.&lt;成员&gt;</c> 的落点（§5.3）。除了这两条形就没有别的：
    /// 组里的行在分组之后不再是一列列可选的值，而这正是 <c>IGrouping</c> 那个类型只有 <c>Key</c> 一个属性的原因。</summary>
    static SqlExpression GroupingKey(GroupingShape grouping, IReadOnlyList<string> path, Expression node)
    {
        if (path[0] != "Key")
            throw new UntranslatableExpressionException(node.ToString(),
                $"分组后的形参上可读的成员只有 Key（这里读的是 {path[0]}）：组内的行要聚合成值（g.Count()、g.Sum(o => o.列) 等），" +
                "非分组键的整行列在分组之后不是一颗可选的值");

        if (grouping.Members is null)
        {
            if (path.Count == 1)
                return grouping.KeyOrThrow(node.ToString());

            throw new UntranslatableExpressionException(node.ToString(),
                "单列分组键没有成员：g.Key 已经是那一颗表达式。要按几列分组请写成 GroupBy(o => new { o.A, o.B })，再读 g.Key.A");
        }

        // 复合键：g.Key 整体不是值（下面那句 KeyOrThrow 的说法），g.Key.A 才是。
        if (path.Count == 1)
            return grouping.KeyOrThrow(node.ToString());

        if (path.Count != 2)
            throw new UntranslatableExpressionException(node.ToString(),
                "复合分组键只到一层（g.Key.<成员>）：再往下的成员读法属于 CLR 形状，不在分组键上");

        return grouping.Members.TryGetValue(path[1], out var key)
            ? key
            : throw new UntranslatableExpressionException(node.ToString(),
                $"分组键里没有成员 {path[1]}（在册的成员：{string.Join("、", grouping.Members.Keys)}）");
    }

    /// <summary>列的 CLR 形状是闭合的：值类型（含枚举、<c>Guid</c>、<c>decimal</c>、日期）加上
    /// <c>string</c> 与 <c>byte[]</c>。除此之外的类型当宿主用，只能是导航或未映射成员。
    /// 别拿 <c>StoreTypeOf</c> 来问"这是不是列" —— 它对认不出的类型是直接抛"没有类型映射"的，
    /// 而这里要的是判断本身，不是那条报错。</summary>
    static bool IsStorable(Type owner) => owner.IsValueType || owner == typeof(string) || owner == typeof(byte[]);

    /// <summary>把宿主写成 <c>t0.Customer</c> 这种能直接对照 FROM 的路径：报错要指到表位，
    /// 而不是复述一遍用户写的形参名（形参名不参与任何解析，§5.1）。</summary>
    static string OwnerPath(Expression owner, Scope scope)
        => owner switch
        {
            UnaryExpression { NodeType: ExpressionType.Convert } widen => OwnerPath(widen.Operand, scope),
            MemberExpression chain => OwnerPath(chain.Expression!, scope) + "." + chain.Member.Name,
            ParameterExpression parameter => scope.TableOf(parameter).Alias,
            _ => owner.ToString(),
        };

    static UntranslatableExpressionException Navigation(Expression node, string path)
        => new(node.ToString(), typeof(IEnumerable).IsAssignableFrom(node.Type) && node.Type != typeof(string)
            ? $"{path} 是集合导航而不是列：批量加载（IncludeMany，两次查询 + 内存缝合）见 §5.7；要现在拿到明细行请显式 join 到明细表，注意那会让主表行重复。引用导航那一档（to-one）在投影/谓词里已经会自动联表（§5.2），集合导航没有这一档，因为一条 LEFT JOIN 就把每行主表复制成明细行数那么多行 —— 页码、行数与聚合会一起改语义。"
            : $"{path} 不是列 —— 引用导航整个当值用没有对应物（要读它的列请再往下写一格，如 {path}.某一列，那条 LEFT JOIN 库自己补，§5.2；判那条边空不空则要比较它那一侧的主键列）；未映射的计算成员（表达式体属性、[Computed] 之外的那些）则是根本取不到列。");

    /// <summary>列上的 CLR 派生读法 → 符号函数（§5.7 覆盖矩阵）。
    /// 键是<b>属性名</b>，与 <see cref="SymbolicKey"/> 那张"方法名"表是两套形状：表达式树里属性访问
    /// 是 <see cref="MemberExpression"/>，只有 <c>get_XXX</c> 以方法形态出现时才走那边。</summary>
    SqlExpression Part(MemberExpression member, Scope scope, ColumnContract? hint)
    {
        var owner = member.Expression!;
        var kind = Nullable.GetUnderlyingType(owner.Type) ?? owner.Type;

        // 穿过引用导航读成员（<c>o.Customer.City</c>）：宿主不是列，所以这既不是列读法，也不是下面那张
        // 派生读法表里的一条。契约上有那条 to-one 边就把宿主补成一张表位（<c>LEFT JOIN</c> 由
        // <see cref="NavigationJoinScout"/> 欠着，算子落地时进 FROM，§5.2），读的那一格落在这张新表上 ——
        // 调用方既不写 <c>Include</c> 也不写 join。认不出才抛：把 <c>o.CreatedAt.Kind</c> 静默翻译成一个裸列，
        // 产出的是"语法正确、结果错"的 SQL —— 那正是 §5.8 禁止的那一类。
        if (!IsStorable(kind))
        {
            var target = scope.NavigationTable(owner);

            if (target?.Contract is { } targetContract && targetContract.ByMember.TryGetValue(member.Member.Name, out var column))
                return new ColumnExpression(target, column);

            // 宿主联上了而这一格仍认不出（导航的导航整个当值、集合导航、未映射成员）时报的是<em>整条读法</em>：
            // 只报宿主会把"缺的是 .Manager 那一格"说成"Customer 不是列"，而 Customer 这一形刚刚才落得成。
            throw target is null
                ? Navigation(owner, OwnerPath(owner, scope))
                : Navigation(member, OwnerPath(member, scope));
        }

        var temporal = kind == typeof(DateTime) || kind == typeof(DateTimeOffset) || kind == typeof(DateOnly);

        var symbol = member.Member.Name switch
        {
            nameof(string.Length) when kind == typeof(string) => Fn.Length,
            "Year" when temporal => Fn.DateYear,
            "Month" when temporal => Fn.DateMonth,
            "Day" when temporal => Fn.DateDay,
            "Hour" when temporal => Fn.DateHour,
            "Minute" when temporal => Fn.DateMinute,
            "Second" when temporal => Fn.DateSecond,
            "Date" when temporal => Fn.ToDate,
            _ => null,
        };

        return symbol is null
            ? throw new UntranslatableExpressionException(member.ToString(),
                  $"{kind.Name}.{member.Member.Name} 没有 SQL 对应形态：可翻译成函数的只有 §5.7 矩阵里那些读法，" +
                  "确需本地计算就把这一段先 ToList()/ToListAsync() 落到内存再读这一档")
            : Template(symbol, [Visit(owner, scope, hint)]);
    }

    // ── 二元 ──────────────────────────────────────────────────────────

    SqlExpression Binary(BinaryExpression node, Scope scope, ColumnContract? hint)
    {
        if (node.NodeType == ExpressionType.Coalesce)
            return Template(Fn.Coalesce, [Visit(node.Left, scope, hint), Visit(node.Right, scope, hint)]);

        if (node.NodeType is ExpressionType.AndAlso or ExpressionType.OrElse)
        {
            var op = node.NodeType == ExpressionType.AndAlso ? BinaryOperator.And : BinaryOperator.Or;
            return Fold(op,
                Booleanized(node.Left, Visit(node.Left, scope)),
                Booleanized(node.Right, Visit(node.Right, scope)));
        }

        // 先按「直连列」定尺子，再递归：这样 StringLength / Unicode 能进到参数绑定（§10.3 的单一真相）。
        var leftColumn = DirectColumn(node.Left, scope);
        var rightColumn = DirectColumn(node.Right, scope);
        var lhs = Visit(node.Left, scope, leftColumn ?? hint);
        var rhs = Visit(node.Right, scope, rightColumn ?? hint);

        if (node.NodeType is ExpressionType.Equal or ExpressionType.NotEqual && rhs is ParameterValue { Value: null })
            return new SqlUnaryExpression(node.NodeType == ExpressionType.Equal ? UnaryOperator.IsNull : UnaryOperator.IsNotNull, lhs);

        if (node.NodeType == ExpressionType.Equal && rhs is ParameterValue { Value: bool flag })
            return new SqlUnaryExpression(flag ? UnaryOperator.IsTrue : UnaryOperator.IsFalse, lhs);

        // C# 的 "a + b" 把 null 当空串，而 T-SQL/Oracle 的 + 会把整个表达式变成 NULL：
        // 不折成 CONCAT，== "xy" 这类查询在库里会悄悄少行（§5.8 的「绝不静默生成错 SQL」）。
        if (node.NodeType == ExpressionType.Add && node.Type == typeof(string))
            return Template(Fn.Concat, [lhs, rhs]);

        return new SqlBinaryExpression(Operator(node.NodeType), lhs, rhs);
    }

    /// <summary>CLR 侧是 bool、SQL 侧却不是一个"谓词形状"时要补 <c>= 1</c>（T-SQL 不接受裸 bit 列作条件）。
    /// 谓词形状是一张闭合表：比较/逻辑/LIKE、<c>IS NULL</c>/<c>NOT</c>、<c>EXISTS</c>、<c>IN</c>，
    /// 以及方言自己给的恒真/恒假片段（空集合那条 <c>1 = 0</c>）。漏掉一种就得到
    /// <c>[Id] IN (…) = 1</c> 这种在库里直接语法错的文本 —— 它只在真连库时才暴露，所以形状表要写全。</summary>
    static bool IsPredicate(SqlExpression ast) => ast is SqlBinaryExpression or SqlUnaryExpression
                                                  or ExistsExpression or InExpression or InlineLiteral;

    SqlExpression Booleanized(Expression linqNode, SqlExpression ast)
        => linqNode.Type == typeof(bool) && !IsPredicate(ast)
            ? new SqlUnaryExpression(UnaryOperator.IsTrue, ast)
            : ast;

    ColumnContract? DirectColumn(Expression node, Scope scope)
        => node is MemberExpression { Expression: ParameterExpression p } member && scope.Contains(p)
            ? scope.TableOf(p).Contract?.ByMember.GetValueOrDefault(member.Member.Name)
            : null;

    static BinaryOperator Operator(ExpressionType type) => type switch
    {
        ExpressionType.Add => BinaryOperator.Add,
        ExpressionType.Subtract => BinaryOperator.Subtract,
        ExpressionType.Multiply => BinaryOperator.Multiply,
        ExpressionType.Divide => BinaryOperator.Divide,
        ExpressionType.Modulo => BinaryOperator.Modulo,
        ExpressionType.Equal => BinaryOperator.Equal,
        ExpressionType.NotEqual => BinaryOperator.NotEqual,
        ExpressionType.LessThan => BinaryOperator.LessThan,
        ExpressionType.LessThanOrEqual => BinaryOperator.LessThanOrEqual,
        ExpressionType.GreaterThan => BinaryOperator.GreaterThan,
        ExpressionType.GreaterThanOrEqual => BinaryOperator.GreaterThanOrEqual,
        ExpressionType.And => BinaryOperator.BitwiseAnd,
        ExpressionType.Or => BinaryOperator.BitwiseOr,
        _ => throw new UntranslatableExpressionException(type.ToString(), "该运算符没有 SQL 对应"),
    };

    static SqlExpression Fold(BinaryOperator op, SqlExpression left, SqlExpression right)
    {
        // 保持 AND/OR 扁平右倾：否则 `a && b && c` 在链式（两次 Append）与 LINQ（一棵树）下
        // 会长出两棵不同的树，§13.2 的等价性测试立刻红。
        if (left is SqlBinaryExpression { Operator: var same } existing && same == op)
            return existing with { Right = Fold(op, existing.Right, right) };

        return new SqlBinaryExpression(op, left, right);
    }

    SqlExpression Unary(UnaryExpression node, Scope scope, ColumnContract? hint)
    {
        switch (node.NodeType)
        {
            case ExpressionType.Not:
                return new SqlUnaryExpression(UnaryOperator.Not, Booleanized(node.Operand, Visit(node.Operand, scope, hint)));

            case ExpressionType.Negate or ExpressionType.NegateChecked:
                return new SqlUnaryExpression(UnaryOperator.Negate, Visit(node.Operand, scope, hint));

            case ExpressionType.Convert or ExpressionType.ConvertChecked:
            {
                var operand = Visit(node.Operand, scope, hint);

                // enum ↔ int、T ↔ T?、装箱成 object 只是 CLR 的表示差异，不进 SQL。
                // 装箱这条最常从 string.Concat(object, object) 里冒出来：C# 把 a + "/" + i 编成它，
                // 而 CONCAT 本来就会把数字转成文本 —— 补一个 CAST 既没有目标类型可填，也是多余的。
                if (node.Type == typeof(object) || RepresentationOnly(node.Operand.Type, node.Type))
                    return operand;

                var storeType = Provider.StoreTypeOf(Nullable.GetUnderlyingType(node.Type) ?? node.Type);
                return storeType is null ? operand : new CastExpression(operand, storeType);
            }

            case ExpressionType.TypeAs:
                throw new UntranslatableExpressionException(node.ToString(), "as 转换依赖鉴别器投影（P1）");

            default:
                throw new UntranslatableExpressionException(node.NodeType.ToString(), null);
        }
    }

    static bool IsSameColumnar(Type from, Type to)
        => (Nullable.GetUnderlyingType(from) ?? from) == (Nullable.GetUnderlyingType(to) ?? to);

    /// <summary>这一颗 Convert 只是 CLR 的表示差异、不改变落进库的值，因此不发射 SQL 转换。
    /// 判据一律看穿一层 <see cref="Nullable"/>，且不收紧原有豁免只放宽：
    /// <c>enum == enum?</c> 与 <c>int 列 == short? 量</c> 这类比较，C# 长出的是提升到 <c>Int32?</c>/<c>Int64</c>
    /// 的转换，两侧各裹一层 Nullable 就让 <c>IsEnum</c> 读成 false、<c>IsSameColumnar</c> 比出不同型，
    /// 于是落到 <c>StoreTypeOf</c> —— 那里取到的是 DDL 建表型名（MySQL 给 "INT"），
    /// 发出去成 <c>CAST(@p0 AS INT)</c>，而 MySQL 的 CAST 不认 INT，整条句子语法错。
    /// 整型之间只豁免加宽（16→32、32→64）：整数加宽不改变值，两侧进库是同一颗比较，
    /// 且免掉列侧的 CAST 才守得住索引；收窄会真丢值，留着 CAST 让它响。
    /// <c>char</c> 不进整型表：它的列是 CHAR(1) 存文本，拿数值去比字符是库级静默降级，比报错更坏。</summary>
    static bool RepresentationOnly(Type from, Type to)
    {
        var source = Nullable.GetUnderlyingType(from) ?? from;
        var target = Nullable.GetUnderlyingType(to) ?? to;

        if (source.IsEnum || target.IsEnum || IsSameColumnar(from, to))
            return true;

        var width = IntegerWidth(source);
        return width > 0 && width < IntegerWidth(target) && source != typeof(ulong);
    }

    /// <summary>整型的位宽；不在表上的（含 <c>char</c>、decimal、引用型）回 0，表示这一族不讨论加宽。</summary>
    static int IntegerWidth(Type type)
    {
        if (type == typeof(sbyte) || type == typeof(byte)) return 8;
        if (type == typeof(short) || type == typeof(ushort)) return 16;
        if (type == typeof(int) || type == typeof(uint)) return 32;
        if (type == typeof(long) || type == typeof(ulong)) return 64;
        return 0;
    }

    // ── 方法调用 ──────────────────────────────────────────────────────

    SqlExpression Call(MethodCallExpression node, Scope scope, ColumnContract? hint)
    {
        if (TryIQuerySubquery(node, scope, hint, out var iqueryResult))
            return iqueryResult;

        var declaring = node.Method.DeclaringType;

        // 分组链上的 Enumerable 方法先判掉（§5.3）：`g.Count()` 落 COUNT(*)、`g.Sum(o => o.列)` 落 SUM(那一列)，
        // 其余那几颗（Any/First/Distinct…）在 GROUP BY 语义下没有对应物，要给出的是这个理由，而不是
        // 下面那句"为 Enumerable.X 注册映射"——那里指错了地方：缺的不是方言模板。
        // 判在集合导航那两条<em>之前</em>：二者在树上是同一个 Enumerable 扩展方法，区别只在第一个实参
        // 是"表位形参的导航成员"还是"分组形参本身"，分错支会得到一条相关子查询套进 GROUP BY 句里。
        if (declaring == typeof(Enumerable) && IsOverGrouping(node, scope, out _))
            return GroupAggregate(node, scope);

        switch (node.Method.Name)
        {
            // 实测形状（.NET 10）：int[] 的 ids.Contains(x) 编成 2 元的 MemoryExtensions.Contains，
            // 枚举数组同写法编成 3 元、末尾多一个值为 null 的 IEqualityComparer<T> 实参。
            // 所以判别条件不能钉在实参数量上，只能钉在"这两个类"上，再对多出来的那一元单独放行。
            case "Contains" when node.Arguments.Count is 2 or 3 && (declaring == typeof(Enumerable) || declaring == typeof(MemoryExtensions)):
            {
                // 比较器实参只有 null 这一种可以当没看见 —— 它在两侧都等价于 EqualityComparer<T>.Default。
                // 真给了比较器还照默认语义发射，等于把用户显式指定的相等规则偷偷换掉（§5.8）。
                if (node.Arguments.Count == 3 && node.Arguments[2] is not ConstantExpression { Value: null })
                    throw new UntranslatableExpressionException(node.ToString(),
                        "IN 的元素相等规则要下推到库里才保得住语义，本版本只接受默认比较器（编译期传下来的 null）。" +
                        "请去掉自定义比较器，或先物化到内存再过滤。");

                return InFromCollection(AsEnumerable(Evaluate(SpanSource(node.Arguments[0])), node), node.Arguments[1], scope, hint);
            }

            case "Contains" when node.Object is { Type: not null } enumerable && enumerable.Type != typeof(string)
                                 && typeof(IEnumerable).IsAssignableFrom(enumerable.Type)
                                 && !ReferencesScope(enumerable, scope):
                return InFromCollection(AsEnumerable(Evaluate(node.Object), node), node.Arguments[0], scope, hint);

            case "StartsWith" or "EndsWith" or "Contains" when declaring == typeof(string):
                return StringLike(node, scope);

            case "op_Equality" when node.Arguments.Count == 2:
                return Binary(Expression.MakeBinary(ExpressionType.Equal, node.Arguments[0], node.Arguments[1]), scope, hint);

            case "Equals" when node.Arguments.Count == 1 && declaring == typeof(string):
                return new SqlBinaryExpression(BinaryOperator.Equal, Visit(node.Object!, scope, hint), Visit(node.Arguments[0], scope, hint));

            case "IsNullOrEmpty" when declaring == typeof(string) && node.Arguments.Count == 1:
            {
                var target = Visit(node.Arguments[0], scope, hint);
                var empty = new ParameterValue(string.Empty, DirectColumn(node.Arguments[0], scope));

                return new SqlBinaryExpression(BinaryOperator.Or,
                    new SqlUnaryExpression(UnaryOperator.IsNull, target),
                    new SqlBinaryExpression(BinaryOperator.Equal, target, empty));
            }

            case "IsNullOrWhiteSpace" when declaring == typeof(string) && node.Arguments.Count == 1:
                return Template(Fn.Blank, [Visit(node.Arguments[0], scope, hint)]);

            case "Any" or "Count" when declaring == typeof(Enumerable):
                if (TryNavigationSubquery(node, scope, out var subqueryResult))
                    return subqueryResult;
                throw new UntranslatableExpressionException(node.ToString(),
                    "集合导航上的子查询聚合只支持直接导航属性（如 o.Items.Any(...)），当前形态无法识别为导航子查询");

            case "Sum" or "Max" or "Min" or "Average" when declaring == typeof(Enumerable):
                throw new UntranslatableExpressionException(node.ToString(), "集合导航上的子查询聚合（Sum/Max/Min/Average）在 P1（关联子查询 / APPLY）");
        }

        var arguments = node.Object is null
            ? node.Arguments.Select(a => Visit(a, scope, hint)).ToArray()
            : [Visit(node.Object, scope, hint), .. node.Arguments.Select(a => Visit(a, scope, hint))];

        var key = SymbolicKey(node);

        if (key == Fn.Concat)
            return FoldConcatenation(arguments);

        if (key is not null)
            return Template(key, arguments);

        if (Provider.Expressions.TryGetFunction(MethodKey(node.Method), out var registered))
            return new FunctionExpression(registered.Template, [.. arguments]);

        throw new UntranslatableExpressionException(node.ToString(),
            $"为 {declaring?.Name}.{node.Method.Name} 注册映射：provider.Expressions.Register(\"{MethodKey(node.Method)}\", new FunctionMapping(\"模板\"))；不想注册就先 ToList()/ToListAsync() 落到内存再调这个方法");
    }

    static IEnumerable AsEnumerable(object? value, Expression node)
        => value as IEnumerable ?? throw new UntranslatableExpressionException(node.ToString(), "IN 的集合必须是可求值的集合常量");

    /// <summary>剥掉数组 → <c>Span&lt;T&gt;</c>/<c>ReadOnlySpan&lt;T&gt;</c> 的隐式转换。
    /// .NET 10 起 <c>ids.Contains(x)</c>（<c>ids</c> 是数组）绑到 <c>MemoryExtensions.Contains(ReadOnlySpan&lt;T&gt;, T)</c>
    /// 而不是 <c>Enumerable.Contains</c>，树上因此多一层 <c>op_Implicit</c>；span 不是 <c>IEnumerable</c>，
    /// 也不可能被常量求值认出来，而数组→span 是无损的，所以回到原数组才是这一版的正确读法。</summary>
    static Expression SpanSource(Expression node)
        => node is MethodCallExpression { Method.Name: "op_Implicit", Method.DeclaringType: { IsGenericType: true } span, Arguments.Count: 1 } cast
           && IsSpan(span.GetGenericTypeDefinition())
            ? cast.Arguments[0]
            : node;

    // typeof(X) 不是常量，写不成 `is typeof(Span<>) or typeof(ReadOnlySpan<>)`。
    static bool IsSpan(Type definition) => definition == typeof(Span<>) || definition == typeof(ReadOnlySpan<>);

    SqlExpression InFromCollection(IEnumerable items, Expression targetNode, Scope scope, ColumnContract? hint)
    {
        var target = Visit(targetNode, scope, hint);
        var column = (target as ColumnExpression)?.Column ?? hint;
        var values = new List<SqlExpression>();

        foreach (var item in items)
            values.Add(Value(item, column));

        if (values.Count == 0)
            // 空集合特化成恒假（§5.7 的 Contains 条目），恒假的写法由方言决定。
            return new InlineLiteral(Provider.Dialect.EmptySetPredicate);

        return new InExpression(target, [.. values]);
    }

    // ── 分组链上的聚合（§5.3） ────────────────────────────────────────

    /// <summary>这一颗 Enumerable 扩展方法是不是打在<em>分组形参</em>上（<c>g.Count()</c> / <c>g.Sum(o =&gt; …)</c>）。
    /// 判据只有一个：第一个实参是 <see cref="Scope"/> 里那个分组形参本身。
    /// <b>不判方法的重载</b>：<c>Enumerable.Sum</c> 有十来颗重载（含 <c>Func&lt;T,int&gt;</c> 与 <c>Func&lt;T,int?&gt;</c> 两族），
    /// 认重载名要反射比 <c>MethodInfo</c>，而这一格要的只是"宿主是不是那一组"。</summary>
    static bool IsOverGrouping(MethodCallExpression node, Scope scope, out GroupingShape grouping)
    {
        grouping = default;

        if (node.Object is not null || node.Arguments.Count == 0)
            return false;

        return node.Arguments[0] is ParameterExpression parameter && scope.TryGroupingOf(parameter, out grouping);
    }

    /// <summary><c>g.Count()</c> → <c>COUNT(*)</c>，<c>g.Sum(o =&gt; o.Amount)</c> → <c>SUM([t0].[Amount])</c>。
    /// 发射用的模板与聚合出口那一条（<see cref="Query.QueryCompiler.Aggregate"/>）取的是同一张注册表，
    /// 所以方言<em>不需要为分组新登记任何东西</em>；区别只在参数：那里的聚合是整句的选列，这里的聚合
    /// 是一颗投影槽位或 <c>HAVING</c> 的一侧，组由 <c>GROUP BY</c> 划，不再有第二层 FROM。</summary>
    SqlExpression GroupAggregate(MethodCallExpression node, Scope scope)
    {
        IsOverGrouping(node, scope, out var grouping);

        var countLike = node.Method.Name is "Count" or "LongCount";

        if (!countLike && node.Method.Name is not ("Sum" or "Max" or "Min" or "Average"))
            throw new UntranslatableExpressionException(node.ToString(),
                "分组链上可用的聚合只有 g.Count()、g.Sum(o => o.列)、g.Max/Min/Average(o => o.列)；" +
                "其余形状（Any/First/Distinct/带条件的计数…）在 GROUP BY 句里没有对应物，本版本未接入");

        if (countLike)
        {
            if (node.Arguments.Count != 1)
                throw new UntranslatableExpressionException(node.ToString(),
                    "带条件的计数（g.Count(o => …)）要下推成 COUNT(CASE WHEN … THEN 1 END)，本版本未接入");

            // LongCount 与 Count 发同一句 COUNT(*)：库里那一列本来就是整数，
            // 读回 int 还是 long 由投影槽位的声明类型定（§5.7 那套按声明类型判的形状）。
            return Template(Fn.Count, [new StarExpression(null)]);
        }

        if (node.Arguments.Count != 2 || StripQuote(node.Arguments[1]) is not LambdaExpression selector)
            throw new UntranslatableExpressionException(node.ToString(),
                $"g.{node.Method.Name}(…) 需要一个列选择器，例如 g.{node.Method.Name}(o => o.Amount)");

        if (selector.Parameters.Count != 1)
            throw new UntranslatableExpressionException(node.ToString(),
                "分组聚合的选择器只有一个形参（组里的一行）");

        var symbol = node.Method.Name switch
        {
            "Sum" => Fn.Sum,
            "Max" => Fn.Max,
            "Min" => Fn.Min,
            _ => Fn.Average,
        };

        // 组里的行就是那张表的行，所以形参绑回同一个表位：这一格不建子查询、不给别名，
        // 与集合导航那条路（TryNavigationSubquery 里另起一个表位 + 关联条件）是两种形状。
        var argument = Visit(selector.Body, scope.Derive(grouping.Table, selector.Parameters[0]));

        return Template(symbol, [argument]);
    }

    // ── 关联子查询（集合导航上的 Any / Count） ────────────────────────

    /// <summary>把 <c>o.Items.Any(...)</c> / <c>o.Items.Count(...)</c> 翻译成关联子查询。
    /// 检测模式：<c>Arguments[0]</c> 是 <c>parameter.Navigation</c>，且该成员不是列而是集合导航。
    /// 关联条件由子端的外键列指向父端的主键列拼出（与 <c>IncludeMany</c> 的 FK 发现逻辑同一套约定）。</summary>
    bool TryNavigationSubquery(MethodCallExpression node, Scope scope, out SqlExpression result)
    {
        result = null!;

        var source = StripConvert(node.Arguments[0]);

        if (source is not MemberExpression { Expression: ParameterExpression parentParam } memberAccess)
            return false;

        if (!scope.Contains(parentParam))
            return false;

        var parentTable = scope.TableOf(parentParam);
        var parentContract = parentTable.Contract;

        if (parentContract is null)
            return false;

        var navName = memberAccess.Member.Name;

        if (parentContract.ByMember.ContainsKey(navName))
            return false;

        var childType = CollectionElementType(memberAccess.Type);

        if (childType is null)
            return false;

        EntityContract childContract;

        try { childContract = _schemas.Contract(childType); }
        catch { return false; }

        IReadOnlyList<string> fkColumnNames;

        try { fkColumnNames = DiscoverFkColumnsForSubquery(parentContract, childContract, navName); }
        catch { return false; }

        var parentKeys = parentContract.PrimaryKey;

        if (parentKeys.Length == 0 || fkColumnNames.Count != parentKeys.Length)
            return false;

        var childTable = _newTableRef(childType, _subqueryIndex++);

        SqlExpression? correlation = null;

        for (var i = 0; i < parentKeys.Length; i++)
        {
            var fkColumn = childContract.ByColumn[fkColumnNames[i]];
            var equal = new SqlBinaryExpression(BinaryOperator.Equal,
                new ColumnExpression(childTable, fkColumn),
                new ColumnExpression(parentTable, parentKeys[i]));
            correlation = correlation is null ? equal : new SqlBinaryExpression(BinaryOperator.And, correlation, equal);
        }

        SqlExpression? filter = null;

        if (node.Arguments.Count == 2 && node.Arguments[1] is LambdaExpression predicateLambda)
        {
            var subScope = scope.DeriveInner(childTable, predicateLambda.Parameters[0]);
            var visited = Visit(predicateLambda.Body, subScope);
            filter = Booleanized(predicateLambda.Body, visited);
        }

        SqlExpression? where = correlation;

        if (filter is not null)
            where = where is null ? filter : new SqlBinaryExpression(BinaryOperator.And, where, filter);

        if (node.Method.Name == "Any")
        {
            result = new ExistsExpression(new SelectStatement
            {
                Columns = [new Projection(new InlineLiteral("1"), null)],
                From = new SourceTable(childTable),
                Where = where,
            });
        }
        else
        {
            result = new ScalarSubqueryExpression(new SelectStatement
            {
                Columns = [new Projection(new FunctionExpression("COUNT(*)", []), null)],
                From = new SourceTable(childTable),
                Where = where,
            });
        }

        return true;
    }

    // ── 非导航子查询（IQuery 闭包捕获） ──────────────────────────────

    /// <summary>检测闭包捕获的 <see cref="FastSqlQuery{T}"/> 上的终端方法调用，翻译成子查询 AST。
    /// 形态：<c>subQuery.Count()</c> → 标量子查询；<c>subQuery.Contains(x)</c> → IN 子查询；
    /// <c>subQuery.Sum/Max/Min/Average(selector)</c> → 聚合标量子查询；
    /// <c>Enumerable.Contains(subQuery, x)</c> → IN 子查询（扩展方法后备）。
    /// 检测在 <c>Call</c> 最前面跑，早于所有现有的方法分派——否则 <c>Contains</c> 会走集合常量路径、
    /// <c>Count</c> 会落到"未注册映射"的报错。</summary>
    bool TryIQuerySubquery(MethodCallExpression node, Scope scope, ColumnContract? hint, [NotNullWhen(true)] out SqlExpression? result)
    {
        result = null;

        if (node.Object is null || ReferencesScope(node.Object, scope))
            return false;

        var engine = ExtractQuery(node.Object);

        if (engine is null)
            return false;

        switch (node.Method.Name)
        {
            case "Count" when node.Arguments.Count == 0:
                result = HandleIQueryCount(engine);
                return true;

            case "Sum" or "Max" or "Min" or "Average" when node.Arguments.Count == 1 && StripQuote(node.Arguments[0]) is LambdaExpression lambda:
                result = HandleIQueryAggregate(engine, node.Method.Name, lambda);
                return true;

            case "Contains" when node.Arguments.Count == 1:
                result = HandleIQueryContains(engine, node.Arguments[0], scope, hint);
                return true;
        }

        if (node.Method.Name == "Contains" && node.Arguments.Count is 2 or 3
            && (node.Method.DeclaringType == typeof(Enumerable) || node.Method.DeclaringType == typeof(MemoryExtensions)))
        {
            var sourceEngine = ExtractQuery(SpanSource(node.Arguments[0]));

            if (sourceEngine is not null)
            {
                if (node.Arguments.Count == 3 && node.Arguments[2] is not ConstantExpression { Value: null })
                    throw new UntranslatableExpressionException(node.ToString(),
                        "IN 子查询的元素相等规则要下推到库里才保得住语义，本版本只接受默认比较器。请去掉自定义比较器。");

                result = HandleIQueryContains(sourceEngine, node.Arguments[1], scope, hint);
                return true;
            }
        }

        return false;
    }

    /// <summary>从表达式节点提取运行期的 <see cref="QueryEngine"/>。
    /// 闭包捕获的 <c>FastSqlQuery&lt;T&gt;</c> 在表达式树里是一串 <c>MemberExpression</c>（闭包字段访问链），
    /// <see cref="Evaluate"/> 能把它求回运行期对象；然后走 <see cref="IEngineCarrier"/> 这一型判断拿引擎。
    /// 只接受"简单变量引用"——闭包字段访问链。如果查询上再链了 Where/Select 等方法调用，
    /// lambda 里可能引用外层参数（关联子查询），<see cref="Evaluate"/> 的 Compile 退路拿不到外层参数会炸，
    /// 所以这一律拒绝；用户需要先把子查询存进变量再调终端方法。</summary>
    static QueryEngine? ExtractQuery(Expression expr)
    {
        // 先挡掉方法调用链（那串只能靠 Compile 求值，引用外层参数时会炸），再做类型判断。
        if (!IsSimpleClosureReference(expr))
            return null;

        // 原先这一处是 type.GetProperty("Engine", …)?.GetValue(obj)：每次编译一句关联子查询都反射一次。
        // 换成内部接口后，"链式对象带引擎"这件事写在类型上，而不是写在一串按名字找成员的运行期约定里。
        return Evaluate(expr) is IEngineCarrier carrier ? carrier.Engine : null;
    }

    /// <summary>判表达式是否"简单闭包变量引用"——一串 <see cref="MemberExpression"/> 字段访问，
    /// 不含方法调用。方法调用链（如 <c>query.Where(...)</c>）的 lambda 里可能引用外层参数，
    /// <see cref="Evaluate"/> 的 Compile 退路拿不到外层参数会炸。</summary>
    static bool IsSimpleClosureReference(Expression expr) => expr switch
    {
        ConstantExpression => true,
        MemberExpression m => m.Expression is null || IsSimpleClosureReference(m.Expression),
        UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u
            => IsSimpleClosureReference(u.Operand),
        _ => false,
    };

    SqlExpression HandleIQueryCount(QueryEngine engine)
    {
        var effective = engine.Effective();
        var aggregateModel = engine.AggregateModel(effective, AggregateKind.Count, null);
        var ast = RemapSubqueryAliases(engine.Compile(aggregateModel));
        return new ScalarSubqueryExpression(ast);
    }

    SqlExpression HandleIQueryAggregate(QueryEngine engine, string methodName, LambdaExpression selector)
    {
        var kind = methodName switch
        {
            "Sum" => AggregateKind.Sum,
            "Max" => AggregateKind.Max,
            "Min" => AggregateKind.Min,
            "Average" => AggregateKind.Average,
            _ => throw new UntranslatableExpressionException(methodName, null),
        };

        var effective = engine.Effective();
        var aggregateModel = engine.AggregateModel(effective, kind, selector);
        var ast = RemapSubqueryAliases(engine.Compile(aggregateModel));
        return new ScalarSubqueryExpression(ast);
    }

    SqlExpression HandleIQueryContains(QueryEngine engine, Expression targetNode, Scope scope, ColumnContract? hint)
    {
        var target = Visit(targetNode, scope, hint);
        var effective = engine.Effective();
        var ast = RemapSubqueryAliases(engine.Compile(effective));
        return new InExpression(target, Subquery: ast);
    }

    /// <summary>子查询的 <see cref="SelectStatement"/> 里的 <see cref="TableRef"/> 来自子查询自己的
    /// <c>RootEngine</c>，别名总是 <c>t0</c>、<c>t1</c>……嵌进外层 WHERE 后和外层的别名撞车——
    /// <c>SUM(t0.Amount)</c> 指的到底是子查询的表还是外层的表？SQL 引擎按最近绑定选，结果就指错了。
    /// 这一手把子查询 AST 里所有 <see cref="TableRef"/> 按 <see cref="_subqueryIndex"/> 重发一套不冲突的别名，
    /// 与导航子查询（<c>TryNavigationAny</c> 里 <c>_newTableRef(childType, _subqueryIndex++)</c>）同一套策略。</summary>
    SelectStatement RemapSubqueryAliases(SelectStatement stmt)
    {
        var map = new Dictionary<TableRef, TableRef>();
        CollectTableRefs(stmt, map);
        foreach (var kv in map.Keys)
        {
            var idx = _subqueryIndex++;
            map[kv] = new TableRef(idx, FastSqlOrm.AliasAt(idx), kv.Table, kv.Contract);
        }
        return RemapStatement(stmt, map);
    }

    static void CollectTableRefs(SelectStatement stmt, Dictionary<TableRef, TableRef> map)
    {
        if (stmt.From is not null) CollectTableRefs(stmt.From, map);
        foreach (var j in stmt.Joins) CollectTableRefs(j.Source, map);
        foreach (var c in stmt.Columns) CollectTableRefs(c.Node, map);
        if (stmt.Where is not null) CollectTableRefs(stmt.Where, map);
        foreach (var g in stmt.GroupBy) CollectTableRefs(g, map);
        if (stmt.Having is not null) CollectTableRefs(stmt.Having, map);
        foreach (var o in stmt.OrderBy) CollectTableRefs(o.Node, map);
        if (stmt.Offset is not null) CollectTableRefs(stmt.Offset, map);
        if (stmt.Limit is not null) CollectTableRefs(stmt.Limit, map);
    }

    static void CollectTableRefs(TableSource src, Dictionary<TableRef, TableRef> map)
    {
        map.TryAdd(src.Ref, src.Ref);
        switch (src)
        {
            case SourceSubquery sub: CollectTableRefs(sub.Query, map); break;
            // 派生表内层引用的是它自己那份 Root，而它与外层的 Ref 是同一个对象 —— 不下钻就得不到改名，
            // 于是内层的 [t0] 与外层改出来的 [t6] 指向两套别名，嵌进外层 WHERE 的那一句会指错列。
            case SourceDerived derived: CollectTableRefs(derived.Query, map); break;
            // 分表那一份是 N 个内层，每一个的 Root 都是它自己那张分片表位（别名同一个），下钻要逐个下。
            case SourceUnion union: foreach (var branch in union.Branches) CollectTableRefs(branch, map); break;
        }
    }

    static void CollectTableRefs(SqlExpression expr, Dictionary<TableRef, TableRef> map)
    {
        switch (expr)
        {
            case ColumnExpression c: map.TryAdd(c.Table, c.Table); break;
            case StarExpression s when s.Table is not null: map.TryAdd(s.Table, s.Table); break;
            case SqlBinaryExpression b: CollectTableRefs(b.Left, map); CollectTableRefs(b.Right, map); break;
            case SqlUnaryExpression u: CollectTableRefs(u.Operand, map); break;
            case FunctionExpression f: foreach (var a in f.Arguments) CollectTableRefs(a, map); break;
            case CastExpression ca: CollectTableRefs(ca.Operand, map); break;
            case InExpression ie:
                CollectTableRefs(ie.Target, map);
                if (ie.Values is not null) foreach (var v in ie.Values) CollectTableRefs(v, map);
                if (ie.Subquery is not null) CollectTableRefs(ie.Subquery, map);
                break;
            case ExistsExpression ee: CollectTableRefs(ee.Query, map); break;
            case ScalarSubqueryExpression se: CollectTableRefs(se.Query, map); break;
            case CaseExpression ce:
                if (ce.Operand is not null) CollectTableRefs(ce.Operand, map);
                foreach (var w in ce.Whens) { CollectTableRefs(w.When, map); CollectTableRefs(w.Then, map); }
                if (ce.Else is not null) CollectTableRefs(ce.Else, map);
                break;
        }
    }

    static SelectStatement RemapStatement(SelectStatement stmt, Dictionary<TableRef, TableRef> map)
    {
        return stmt with
        {
            From = stmt.From is not null ? RemapTableSource(stmt.From, map) : null,
            Joins = stmt.Joins.Select(j => j with
            {
                Source = RemapTableSource(j.Source, map),
                On = j.On is not null ? RemapExpression(j.On, map) : null,
            }).ToArray(),
            Columns = stmt.Columns.Select(c => c with { Node = RemapExpression(c.Node, map) }).ToArray(),
            Where = stmt.Where is not null ? RemapExpression(stmt.Where, map) : null,
            GroupBy = stmt.GroupBy.Select(g => RemapExpression(g, map)).ToArray(),
            Having = stmt.Having is not null ? RemapExpression(stmt.Having, map) : null,
            OrderBy = stmt.OrderBy.Select(o => o with { Node = RemapExpression(o.Node, map) }).ToArray(),
            Offset = stmt.Offset is not null ? RemapExpression(stmt.Offset, map) : null,
            Limit = stmt.Limit is not null ? RemapExpression(stmt.Limit, map) : null,
        };
    }

    static TableSource RemapTableSource(TableSource src, Dictionary<TableRef, TableRef> map)
    {
        var newRef = map.TryGetValue(src.Ref, out var nr) ? nr : src.Ref;
        return src switch
        {
            SourceTable => new SourceTable(newRef),
            SourceSubquery sub => new SourceSubquery(newRef, RemapStatement(sub.Query, map)),
            SourceDerived derived => new SourceDerived(newRef, RemapStatement(derived.Query, map)),
            SourceUnion union => new SourceUnion(newRef, [.. union.Branches.Select(b => RemapStatement(b, map))]),
            // 原生文本这一形没有可改名的列引用（那一段一颗不动），但<em>别名要跟着改</em>：外层那句的
            // WHERE 里的 [t1000].[Amount] 已经被改名，而 FROM (…) 挂的名字若还停在 [t0]，
            // 得到的是一句"无法绑定多部分标识符"。
            SourceRawText raw => new SourceRawText(newRef, raw.Sql, raw.Parameters),
            _ => src,
        };
    }

    static SqlExpression RemapExpression(SqlExpression expr, Dictionary<TableRef, TableRef> map)
    {
        switch (expr)
        {
            case ColumnExpression c:
                var t = map.TryGetValue(c.Table, out var nt) ? nt : c.Table;
                return ReferenceEquals(t, c.Table) ? expr : new ColumnExpression(t, c.Column);
            case StarExpression s when s.Table is not null:
                var st = map.TryGetValue(s.Table, out var snt) ? snt : s.Table;
                return ReferenceEquals(st, s.Table) ? expr : new StarExpression(st);
            case SqlBinaryExpression b:
                var bl = RemapExpression(b.Left, map);
                var br = RemapExpression(b.Right, map);
                return ReferenceEquals(bl, b.Left) && ReferenceEquals(br, b.Right) ? expr : new SqlBinaryExpression(b.Operator, bl, br);
            case SqlUnaryExpression u:
                var uo = RemapExpression(u.Operand, map);
                return ReferenceEquals(uo, u.Operand) ? expr : new SqlUnaryExpression(u.Operator, uo);
            case FunctionExpression f:
                var fa = f.Arguments.Select(a => RemapExpression(a, map)).ToArray();
                return fa.SequenceEqual(f.Arguments) ? expr : new FunctionExpression(f.Template, fa);
            case CastExpression ca:
                var co = RemapExpression(ca.Operand, map);
                return ReferenceEquals(co, ca.Operand) ? expr : new CastExpression(co, ca.StoreType);
            case InExpression ie:
                var it = RemapExpression(ie.Target, map);
                var iv = ie.Values?.Select(v => RemapExpression(v, map)).ToArray();
                var isq = ie.Subquery is not null ? RemapStatement(ie.Subquery, map) : null;
                return ReferenceEquals(it, ie.Target) && (iv is null || iv.SequenceEqual(ie.Values!)) && ReferenceEquals(isq, ie.Subquery)
                    ? expr : new InExpression(it, iv, ie.Negated, isq);
            case ExistsExpression ee:
                var eq = RemapStatement(ee.Query, map);
                return ReferenceEquals(eq, ee.Query) ? expr : new ExistsExpression(eq, ee.Negated);
            case ScalarSubqueryExpression se:
                var sq = RemapStatement(se.Query, map);
                return ReferenceEquals(sq, se.Query) ? expr : new ScalarSubqueryExpression(sq);
            case CaseExpression ce:
                var cOp = ce.Operand is not null ? RemapExpression(ce.Operand, map) : null;
                var cWhens = ce.Whens.Select(w => new WhenClause(RemapExpression(w.When, map), RemapExpression(w.Then, map))).ToArray();
                var cElse = ce.Else is not null ? RemapExpression(ce.Else, map) : null;
                return new CaseExpression(cOp, cWhens, cElse);
            default:
                return expr;
        }
    }

    static Expression StripConvert(Expression node) => node switch
    {
        UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u => StripConvert(u.Operand),
        _ => node,
    };

    /// <summary>剥掉 <c>Expression&lt;T&gt;</c> 参数在表达式树里的 <c>Quote</c> 包裹。
    /// 方法形参是 <c>Expression&lt;Func&lt;T,TResult&gt;&gt;</c> 时，调用方的 lambda 在树上多一层 Quote。</summary>
    static Expression StripQuote(Expression node) => node is UnaryExpression { NodeType: ExpressionType.Quote } q ? StripQuote(q.Operand) : node;

    [RequiresUnreferencedCode("按运行期 Type 反射判集合元素型。")]
    [RequiresDynamicCode("按运行期 Type 反射判集合元素型。")]
    static Type? CollectionElementType(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            return type.GetGenericArguments()[0];

        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return iface.GetGenericArguments()[0];
        }

        return null;
    }

    /// <summary>子查询版的 FK 发现：与 <c>QueryEngine</c>/<c>DbSet</c> 里那两份逻辑同三档（点名那一档走
    /// <see cref="ContractResolver.ResolveDeclaredFkColumns"/>，同一根边不许两个答案），
    /// 但只返回列名清单、不抛带上下文的异常（调用方按 <c>return false</c> 处理）。</summary>
    static IReadOnlyList<string> DiscoverFkColumnsForSubquery(EntityContract parentContract, EntityContract childContract, string memberName)
    {
        var parentKeys = parentContract.PrimaryKey;

        foreach (var rel in childContract.Relationships)
        {
            if (rel.TargetType == parentContract.ClrType)
                return rel.ColumnNames;
        }

        if (ContractResolver.ResolveDeclaredFkColumns(parentContract, childContract, memberName) is { } declaredColumns)
            return declaredColumns;

        var parentTypeName = parentContract.ClrType.Name;

        if (parentKeys.Length == 1)
        {
            foreach (var conventionName in new[] { $"{parentTypeName}Id", $"{parentTypeName}ID" })
            {
                if (childContract.ByColumn.ContainsKey(conventionName))
                    return [conventionName];
            }

            var byColumnName = $"{parentTypeName}{parentKeys[0].ColumnName}";

            if (childContract.ByColumn.ContainsKey(byColumnName))
                return [byColumnName];
        }

        var compositeNames = new List<string>(parentKeys.Length);

        foreach (var pk in parentKeys)
        {
            var name = $"{parentTypeName}{pk.ColumnName}";

            if (!childContract.ByColumn.ContainsKey(name))
                throw new FastSqlException($"子实体 {childContract.ClrType.Name} 上没有指向 {parentContract.ClrType.Name} 的外键列");

            compositeNames.Add(name);
        }

        return compositeNames;
    }

    /// <summary>CLR 方法 → 符号函数键。返回 null 表示没有形态规则，交给 provider 的注册表按方法全名兜。</summary>
    string? SymbolicKey(MethodCallExpression node)
    {
        var declaring = node.Method.DeclaringType;
        var name = node.Method.Name;

        if (declaring == typeof(string))
            return name switch
            {
                "get_Length" => Fn.Length,
                "ToUpper" or "ToUpperInvariant" => Fn.Upper,
                "ToLower" or "ToLowerInvariant" => Fn.Lower,
                "Trim" when node.Arguments.Count == 0 => Fn.Trim,
                "TrimStart" when node.Arguments.Count == 0 => Fn.TrimStart,
                "TrimEnd" when node.Arguments.Count == 0 => Fn.TrimEnd,
                "Substring" => Fn.Substring,
                "Replace" => Fn.Replace,
                "IndexOf" => Fn.IndexOf,
                "Concat" => Fn.Concat,
                "ToString" when node.Arguments.Count == 0 => Fn.CastText,
                _ => null,
            };

        if (declaring == typeof(Math))
            return name switch
            {
                "Round" => Fn.Round,
                "Abs" => Fn.Abs,
                "Ceiling" => Fn.Ceiling,
                "Floor" => Fn.Floor,
                "Power" => Fn.Power,
                "Sqrt" => Fn.Sqrt,
                "Max" => Fn.Greatest,
                "Min" => Fn.Least,
                _ => null,
            };

        if (declaring is { } t && (t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(DateOnly)))
            return name switch
            {
                "get_Year" => Fn.DateYear,
                "get_Month" => Fn.DateMonth,
                "get_Day" => Fn.DateDay,
                "get_Hour" => Fn.DateHour,
                "get_Minute" => Fn.DateMinute,
                "get_Second" => Fn.DateSecond,
                "get_Date" => Fn.ToDate,
                "AddYears" => Fn.AddYears,
                "AddMonths" => Fn.AddMonths,
                "AddDays" => Fn.AddDays,
                "AddHours" => Fn.AddHours,
                "AddMinutes" => Fn.AddMinutes,
                "AddSeconds" => Fn.AddSeconds,
                "get_Now" => Fn.Now,
                "get_UtcNow" => Fn.UtcNow,
                _ => null,
            };

        if (declaring == typeof(decimal))
            return name switch
            {
                "get_Value" => null,
                _ => null,
            };

        return null;
    }

    static string MethodKey(MethodInfo method) => (method.DeclaringType?.FullName ?? "global") + "." + method.Name;

    /// <summary><c>string.Concat(a,b,c)</c> 折成两两嵌套的 CONCAT：模板是按元数登记的，
    /// 让 N 参直接进 <c>CONCAT({0}, {1})</c> 会被 string.Format 静默丢掉第三个实参。</summary>
    SqlExpression FoldConcatenation(IReadOnlyList<SqlExpression> arguments)
    {
        if (arguments.Count == 0)
            return new ParameterValue(string.Empty, null);

        var result = arguments[0];

        for (var i = 1; i < arguments.Count; i++)
            result = Template(Fn.Concat, [result, arguments[i]]);

        return result;
    }

    SqlExpression Template(string symbol, IReadOnlyList<SqlExpression> arguments)
    {
        // 键带元数：同一符号在 1 参与 2 参下模板不同（Math.Round），少了这一步 string.Format 会静默丢掉多余实参。
        var key = Fn.Key(symbol, arguments.Count);

        return Provider.Expressions.TryGetFunction(key, out var mapping)
            ? new FunctionExpression(mapping.Template, arguments)
            : throw new UntranslatableExpressionException(key, $"{Provider.Kind} 未登记函数 {key}（可用 provider.Expressions.Register 覆写）");
    }

    /// <summary><paramref name="pattern"/> 就是最终模式串：通配符有意义，本方法不再转义它。
    /// 用户给的字面量要先进 <see cref="EscapeLike"/> 再补通配符，反了会把通配符一起转义掉。</summary>
    public SqlExpression Like(SqlExpression target, string pattern, ColumnContract? hint)
        => new SqlBinaryExpression(BinaryOperator.Like, target, new ParameterValue(pattern, hint));

    /// <summary>转义用户输入里被当成字面量的通配符，否则 <c>"50%"</c> 会匹配到 <c>"50abc"</c>。
    /// 转义符自身要先替换，不然第二步的 <c>%</c> 会被当成刚写下的通配符转义。</summary>
    string EscapeLike(string literal)
    {
        var escape = Provider.Expressions.LikeEscapeCharacter.ToString();

        return literal.Replace(escape, escape + escape, StringComparison.Ordinal)
                      .Replace("%", escape + "%", StringComparison.Ordinal)
                      .Replace("_", escape + "_", StringComparison.Ordinal);
    }

    /// <summary>StartsWith / EndsWith / Contains(string) 的 LIKE 形态（§5.7 的字符串条目）。
    /// <b>先转义字面量、后补通配符</b>：两个动作对调，通配符就被 <see cref="EscapeLike"/> 一起吞了，
    /// <c>Contains</c> 会静默退化成全等匹配（少行而不是报错，正是 §5.8 不许的那种错）。</summary>
    SqlExpression StringLike(MethodCallExpression node, Scope scope)
    {
        var target = Visit(node.Object!, scope, null);
        var pattern = Evaluate(node.Arguments[0]) as string
                      ?? throw new UntranslatableExpressionException(node.ToString(), "LIKE 的模式串必须是可求值的字符串");
        var hint = (target as ColumnExpression)?.Column;
        var literal = EscapeLike(pattern);

        return node.Method.Name switch
        {
            "StartsWith" => Like(target, literal + "%", hint),
            "EndsWith" => Like(target, "%" + literal, hint),
            "Contains" => Like(target, "%" + literal + "%", hint),
            _ => throw new UntranslatableExpressionException(node.Method.Name, null),
        };
    }
}

static class DbProviderExtensions
{
    /// <summary>只为 CAST 目标服务：拿一个干净的探针契约问 provider 要类型名。</summary>
    public static string? StoreTypeOf(this IDbProvider provider, Type clrType)
    {
        var probe = new ColumnContract
        {
            MemberName = "cast",
            ClrType = clrType,
            ColumnName = string.Empty,
            LengthSemantics = LengthSemantics.Char,
            Unicode = true,
            IsNullable = false,
            IsPrimary = false,
            IsIdentity = false,
            MaxChars = clrType == typeof(string) ? 4000 : null,
            MaxBytes = null,
            Precision = null,
            Scale = null,
            Origin = new OriginTrace(MetadataLayer.ProviderFallback, "CAST 目标类型"),
        };

        return provider.Dialect.GetStoreType(provider.Types.ApplyStorageDefaults(probe));
    }
}
