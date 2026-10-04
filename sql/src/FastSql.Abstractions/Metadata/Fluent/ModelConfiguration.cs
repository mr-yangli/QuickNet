namespace FastSql.Metadata.Fluent;

/// <summary>同层冲突的记录器：同一个事实被写两次就在配置期抛，不等启动解析
/// ——§13.11 要求断言的是异常，不是"取后写的那条"。实体级与成员级共用这一条尺子。</summary>
sealed class FactBag(string owner)
{
    HashSet<string>? _facts;

    public void Record(string fact, object? value)
    {
        _facts ??= [];

        if (!_facts.Add(fact))
            throw new MetadataConflictException(owner, [$"Fluent 规则 {fact} 已被设置，本次又设置为 {value}", "同层重复配置按启动期失败处理"]);
    }
}

/// <summary>Fluent 配置（§10.2 优先级 1）。只作为解析期的输入，解析完就冻结进 <c>EntityContract</c>，
/// 三个消费点（DDL / 绑定 / 校验）不再回读这里。</summary>
public sealed class FluentMember
{
    readonly FactBag _facts;

    public FluentMember(string memberName)
    {
        MemberName = memberName;
        _facts = new FactBag(memberName);
    }

    public string MemberName { get; }

    public string? ColumnName { get; private set; }

    public int? MaxLength { get; private set; }

    public int? Precision { get; private set; }

    public int? Scale { get; private set; }

    public bool? Required { get; private set; }

    public bool? IsKey { get; private set; }

    public bool? IsIdentity { get; private set; }

    public bool? IsIgnored { get; private set; }

    /// <summary>并发列（§8.2 的乐观锁，只比较不书写）。与 <c>[ConcurrencyToken]</c> 同一格，优先级链上 Fluent 在上。</summary>
    public bool? IsConcurrencyToken { get; private set; }

    public string? StoreTypeName { get; private set; }

    public string? Comment { get; private set; }

    internal bool IsEmpty => ColumnName is null && MaxLength is null && Precision is null && Scale is null
                             && Required is null && IsKey is null && IsIdentity is null && IsIgnored is null
                             && IsConcurrencyToken is null && StoreTypeName is null && Comment is null;

    public sealed class PropertyBuilder(FluentMember member)
    {
        public PropertyBuilder HasColumnName(string name)
        {
            member._facts.Record(nameof(HasColumnName), name);
            member.ColumnName = name;
            return this;
        }

        public PropertyBuilder HasMaxLength(int length)
        {
            member._facts.Record(nameof(HasMaxLength), length);
            member.MaxLength = length;
            return this;
        }

        public PropertyBuilder HasPrecision(int precision, int scale)
        {
            member._facts.Record(nameof(HasPrecision), precision + "," + scale);
            member.Precision = precision;
            member.Scale = scale;
            return this;
        }

        public PropertyBuilder HasDbType(string storeTypeName)
        {
            member._facts.Record(nameof(HasDbType), storeTypeName);
            member.StoreTypeName = storeTypeName;
            return this;
        }

        public PropertyBuilder HasComment(string comment)
        {
            member._facts.Record(nameof(HasComment), comment);
            member.Comment = comment;
            return this;
        }

        public PropertyBuilder IsRequired(bool required = true)
        {
            member._facts.Record(nameof(IsRequired), required);
            member.Required = required;
            return this;
        }

        public PropertyBuilder IsKey(bool isKey = true)
        {
            member._facts.Record(nameof(IsKey), isKey);
            member.IsKey = isKey;
            return this;
        }

        public PropertyBuilder IsIdentity(bool isIdentity = true)
        {
            member._facts.Record(nameof(IsIdentity), isIdentity);
            member.IsIdentity = isIdentity;
            return this;
        }

        public PropertyBuilder IsIgnore()
        {
            member._facts.Record(nameof(IsIgnore), true);
            member.IsIgnored = true;
            return this;
        }

        /// <summary>声明成并发列（<c>[ConcurrencyToken]</c> 的 Fluent 等价物，优先级更高）。</summary>
        public PropertyBuilder IsConcurrencyToken(bool isConcurrencyToken = true)
        {
            member._facts.Record(nameof(IsConcurrencyToken), isConcurrencyToken);
            member.IsConcurrencyToken = isConcurrencyToken;
            return this;
        }
    }
}

public sealed class FluentEntity
{
    readonly Dictionary<string, FluentMember> _members = [];
    readonly FactBag _facts;

    internal FluentEntity(string owner) => _facts = new FactBag(owner);

    public string? TableName { get; private set; }

    public string? Schema { get; private set; }

    public string? Comment { get; private set; }

    public IReadOnlyDictionary<string, FluentMember> Members => _members;

    /// <summary>配置期只有 CLR 成员名；列名要等解析链跑完才有（同一层的列名覆写会影响它）。</summary>
    public IReadOnlyList<FluentIndex> Indexes => _indexes;

    readonly List<FluentIndex> _indexes = [];

    internal void SetTable(string name, string? schema)
    {
        _facts.Record(nameof(SetTable), name);
        TableName = name;
        Schema = schema;
    }

    internal void SetComment(string comment)
    {
        _facts.Record(nameof(SetComment), comment);
        Comment = comment;
    }

    internal FluentMember Member(string name)
    {
        if (!_members.TryGetValue(name, out var member))
            _members[name] = member = new FluentMember(name);

        return member;
    }

    internal void AddIndex(FluentIndex index)
    {
        _facts.Record(nameof(AddIndex) + ":" + index.Name, string.Join(",", index.MemberNames));
        _indexes.Add(index);
    }
}

/// <summary>Fluent 侧的索引声明：成员名 + 唯一性。<c>Name</c> 可空，留空由约定生成。</summary>
public sealed record FluentIndex(string? Name, IReadOnlyList<string> MemberNames, bool Unique, bool Descending = false);

/// <summary>一个 <c>OrmOptions</c> 里的全部 Fluent 配置。</summary>
public sealed class ModelConfiguration
{
    readonly Dictionary<Type, FluentEntity> _entities = [];

    public EntityBuilder<T> Entity<T>() where T : class
    {
        if (!_entities.TryGetValue(typeof(T), out var entity))
            _entities[typeof(T)] = entity = new FluentEntity(typeof(T).Name);

        return new EntityBuilder<T>(entity);
    }

    public FluentEntity? Find(Type clrType) => _entities.GetValueOrDefault(clrType);
}

public sealed class EntityBuilder<T> where T : class
{
    readonly FluentEntity _entity;

    internal EntityBuilder(FluentEntity entity) => _entity = entity;

    public EntityBuilder<T> ToTable(string name, string? schema = null)
    {
        _entity.SetTable(name, schema);
        return this;
    }

    public EntityBuilder<T> HasComment(string comment)
    {
        _entity.SetComment(comment);
        return this;
    }

    public EntityBuilder<T> HasIndex(params string[] memberNames)
        => HasIndex(null, unique: false, memberNames);

    public EntityBuilder<T> HasUniqueIndex(params string[] memberNames)
        => HasIndex(null, unique: true, memberNames);

    public EntityBuilder<T> HasIndex(string? name, bool unique, params string[] memberNames)
    {
        if (memberNames.Length == 0)
            throw new FastSqlException($"实体 {typeof(T).Name} 的索引 {name} 至少要有一个成员");

        _entity.AddIndex(new FluentIndex(name, memberNames, unique));
        return this;
    }

    public FluentMember.PropertyBuilder Property<TProp>(System.Linq.Expressions.Expression<Func<T, TProp>> accessor)
        => new(_entity.Member(MemberName(accessor)));

    static string MemberName(System.Linq.Expressions.LambdaExpression accessor)
        => accessor.Body is System.Linq.Expressions.MemberExpression m
            ? m.Member.Name
            : throw new FastSqlException($"Fluent 配置只接受属性访问表达式，收到：{accessor.Body}");
}
