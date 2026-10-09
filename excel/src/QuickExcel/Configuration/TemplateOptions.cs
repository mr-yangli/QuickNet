namespace QuickExcel;

/// <summary>
/// Controls <see cref="Excel.SaveAsByTemplate(string, string, object, TemplateOptions?)"/> rendering.
/// </summary>
public sealed class TemplateOptions
{
    /// <summary>Sheet inside the template to render. Null = first sheet.</summary>
    public string? SheetName { get; set; }

    /// <summary>
    /// Format used when a DateTime value replaces a placeholder as text. Default "yyyy-MM-dd HH:mm:ss".
    /// Set to "serial" to write the OADate number instead and keep the template cell's date style.
    /// </summary>
    public string DateTimeFormat { get; set; } = "yyyy-MM-dd HH:mm:ss";

    /// <summary>Format used for DateOnly values rendered as text. Default "yyyy-MM-dd".</summary>
    public string DateOnlyFormat { get; set; } = "yyyy-MM-dd";

    public TemplateOptions() { }

    public TemplateOptions Clone() => (TemplateOptions)MemberwiseClone();
}
