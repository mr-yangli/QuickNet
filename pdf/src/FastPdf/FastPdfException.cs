namespace FastPdf;

/// <summary>Raised when a PDF export cannot be produced (typically: no embeddable CJK font on this machine).</summary>
public class FastPdfException : Exception
{
    public FastPdfException(string message) : base(message) { }
    public FastPdfException(string message, Exception inner) : base(message, inner) { }
}
