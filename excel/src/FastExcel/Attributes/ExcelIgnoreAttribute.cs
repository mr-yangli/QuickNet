namespace FastExcel;

/// <summary>Excludes a property or field from import and export.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class ExcelIgnoreAttribute : Attribute
{
}
