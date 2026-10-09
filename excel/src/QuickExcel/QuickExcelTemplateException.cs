namespace QuickExcel;

/// <summary>Thrown when a template cannot be rendered (unsupported syntax, ambiguous placeholders).</summary>
public sealed class QuickExcelTemplateException : Exception
{
    public QuickExcelTemplateException(string message) : base(message) { }
    public QuickExcelTemplateException(string message, Exception inner) : base(message, inner) { }
}
