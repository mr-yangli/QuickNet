namespace FastExcel;

/// <summary>Names the worksheet a type is exported to (when no explicit sheet name is passed to the API).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Struct, AllowMultiple = false, Inherited = true)]
public sealed class ExcelSheetAttribute : Attribute
{
    public string Name { get; }

    public ExcelSheetAttribute(string name) => Name = name;
}
