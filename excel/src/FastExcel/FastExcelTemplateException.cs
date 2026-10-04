namespace FastExcel;

/// <summary>Thrown when a template cannot be rendered (unsupported syntax, ambiguous placeholders).</summary>
public sealed class FastExcelTemplateException : Exception
{
    public FastExcelTemplateException(string message) : base(message) { }
    public FastExcelTemplateException(string message, Exception inner) : base(message, inner) { }
}
