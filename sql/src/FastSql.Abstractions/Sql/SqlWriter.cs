using System.Data;
using System.Text;
using FastSql.Metadata;

namespace FastSql.Sql;

/// <summary><see cref="Hint"/> 不是给驱动用的（驱动只认 Name/Value/DbType/Size），它是<b>参数自带的出处</b>：
/// <see cref="Values.IValueBinder.CreateParameter"/> 本来就收了列契约再决定 DbType，扔掉之后
/// 「这一颗参数属于哪一列」就只能靠"参数是按 AST 顺序生成的"这种隐含约定重建 —— 一旦有方言在
/// <c>Emit</c> 里多插一颗参数（分页 hint、并发谓词），按位置配对的 <c>ParameterBinding</c> 钩子就会
/// 把值改到隔壁列上。那是 §10.2 说的"三个月后才现形"的错，所以出处必须跟着参数走。</summary>
/// <remarks><see cref="DriverName"/> 是 v0.34 第二笔立的<b>载体</b>（§14 三条省法的第 ① 条）：
/// 默认 null 说的是"provider 没有表态"，此时 <c>AdoExecutor.Bind</c> 按方言那颗
/// <see cref="ISqlDialect.DriverAcceptsPrefixedParameterNames"/> 决定摘不摘前缀；provider 若给了非 null，
/// 那就是点名交给驱动的名字，执行器一个字都不再动。<b>今天五家 binder 全发 null、消费点只有执行器一处</b> ——
/// 这颗是留口的形状，不是一颗有产出的开关，与 <see cref="Hint"/> 早期"先随参数走、后有人读"同一段路。</remarks>
public sealed record AdoParameter(string Name, object? Value, DbType DbType, int Size, ColumnContract? Hint = null, string? DriverName = null);

/// <summary>
/// 方言的书写面。所有 provider 只通过这个类型落地文本，因此 <c>Emit</c> 里不可能
/// 出现字符串拼接风格之外的第二套写法（§12.7 B）。
/// 这是 ref struct：把它改成 class 会让每个方言签名破功，改动前先看 §14 的 P0 交付项。
/// </summary>
public ref struct SqlWriter
{
    const string EscapedQuote = "''";

    readonly StringBuilder _builder;
    readonly List<AdoParameter> _parameters;
    readonly char _parameterPrefix;

    public SqlWriter(StringBuilder builder, List<AdoParameter> parameters, char parameterPrefix)
    {
        _builder = builder;
        _parameters = parameters;
        _parameterPrefix = parameterPrefix;
    }

    public readonly int Length => _builder.Length;

    public IReadOnlyList<AdoParameter> Parameters => _parameters;

    /// <summary>派生一个临时书写器：文本各自一份，参数表与编号共享（编号本来就长在这张表上，
    /// 见 <see cref="NextParameterName"/>），所以两侧不可能生成同名参数。</summary>
    public SqlWriter Fork() => new(new StringBuilder(), _parameters, _parameterPrefix);

    public SqlWriter Append(string text)
    {
        _builder.Append(text);
        return this;
    }

    public SqlWriter Append(ReadOnlySpan<char> text)
    {
        _builder.Append(text);
        return this;
    }

    public SqlWriter Append(char ch)
    {
        _builder.Append(ch);
        return this;
    }

    public SqlWriter Append(int value)
    {
        _builder.Append(value);
        return this;
    }

    public SqlWriter AppendSpace()
    {
        _builder.Append(' ');
        return this;
    }

    /// <summary>标识符的唯一落地口径：判据、转义与大小写折叠都在 <see cref="IdentifierPolicy"/> 那一份，
    /// 这里只多给了它一个缓冲区，<b>不让 <c>[t0].[Code1]</c> 先作为一个中间 string 存在</b>。
    /// 发射层每一列都要过一次这条路，那一次拼接就是 §12.8 第 1 行"每列约 304 B"里的大头 ——
    /// 也是 <c>NeedsQuote</c> 那一判为什么不许顺手跑一趟大写映射的理由。</summary>
    public SqlWriter AppendIdentifier(IdentifierPolicy identifiers, string identifier)
    {
        if (identifiers.NeedsQuote(identifier))
            identifiers.AppendQuoted(_builder, identifier);
        else
            identifiers.AppendPlain(_builder, identifier);

        return this;
    }

    /// <summary>字面量字符串。SQL Server 在 cp1252 排序规则下会把非 ASCII 静默变成 <c>?</c>，
    /// 所以 <paramref name="unicode"/> 必须由 <see cref="Metadata.ColumnContract.Unicode" /> 决定，不能默认 false。</summary>
    public SqlWriter AppendStringLiteral(string value, bool unicode)
    {
        if (unicode)
            _builder.Append('N');

        _builder.Append('\'');

        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\'')
                continue;

            _builder.Append(value, start, i - start + 1);
            _builder.Append('\'');
            start = i + 1;
        }

        _builder.Append(value, start, value.Length - start);
        _builder.Append('\'');
        return this;
    }

    /// <summary>参数的<b>编号就是它在 <see cref="Parameters"/> 里的下标</b>，而不是书写器自持的一个计数器。
    /// 这不是省事：类型是 ref struct，方言的各 <c>Emit*</c> 方法之间<b>按值传递</b>它，字段上的自增在离开
    /// 被调方时就随副本一起消失 —— 于是 <c>OFFSET @p0 … FETCH NEXT @p0</c> 同名，而参数集合里只有一条。
    /// 文本与参数表是引用类型所以活得下来，只有号码会分岔，因此号码必须长在那张共享表上。
    /// 名字只在参数真正进表的那一刻算被占用，连续调用两次会得到同一个名字。</summary>
    public string NextParameterName() => _parameterPrefix + "p" + _parameters.Count;

    public void AddParameter(string name, object? value, DbType dbType = DbType.Object, int size = 0)
        => _parameters.Add(new AdoParameter(name, value, dbType, size));

    public void AddParameter(object? value, DbType dbType = DbType.Object, int size = 0)
        => AddParameter(NextParameterName(), value, dbType, size);

    /// <summary>登记参数并落地它的占位符名。方言里所有参数化都必须走这一条，
    /// 否则会出现「文本里有 @p3、参数集合里没有」这类只能靠执行期报错发现的问题。</summary>
    public SqlWriter AppendParameter(AdoParameter parameter)
    {
        _parameters.Add(parameter);
        _builder.Append(parameter.Name);
        return this;
    }

    /// <summary>登记参数而<em>不</em>落地它的名字：位置形占位符（<c>?</c>）那一家文本里没有名字可落，
    /// 而那颗名字仍然要存在 —— 它是内核侧的编号、是 <see cref="AdoParameter.Hint"/> 的载体，也是
    /// <c>ParameterBinding</c> 钩子按列对上号的唯一依据（本文件抬头那句「出处必须跟着参数走」）。
    /// <b>与 <see cref="AppendParameter"/> 同一张表、同一个编号纪律</b>：调用方仍要先问
    /// <see cref="NextParameterName"/>，否则两次登记会同名。
    /// 名字与文本<em>脱钩</em>之后，"绑定按先后配"这件事第一次变成了方言层的责任，
    /// 所以 <c>DialectBase</c> 在发射函数模板时会核对实参次序（那里写了后果）。</summary>
    public SqlWriter RegisterParameter(AdoParameter parameter)
    {
        _parameters.Add(parameter);
        return this;
    }

    public readonly string ToSqlString() => _builder.ToString();

    public override readonly string ToString() => _builder.ToString();
}
