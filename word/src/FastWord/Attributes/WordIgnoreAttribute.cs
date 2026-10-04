namespace FastWord;

/// <summary>Excludes a property or field from the exported table.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class WordIgnoreAttribute : Attribute
{
}
