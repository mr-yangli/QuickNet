namespace QuickPdf;

/// <summary>Raised when a PDF export cannot be produced (typically: no embeddable CJK font on this machine).</summary>
public class QuickPdfException : Exception
{
    public QuickPdfException(string message) : base(message) { }
    public QuickPdfException(string message, Exception inner) : base(message, inner) { }
}
