namespace FastWord;

/// <summary>Sets the export column width (Excel character units) for a member.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class WordColumnWidthAttribute : Attribute
{
    public double Width { get; }

    public WordColumnWidthAttribute(double width) => Width = width;
}
