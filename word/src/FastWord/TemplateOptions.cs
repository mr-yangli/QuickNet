namespace FastWord;

/// <summary>
/// Controls <see cref="Word.SaveAsByTemplate(string, string, object, TemplateOptions?)"/> rendering.
/// </summary>
public sealed class TemplateOptions
{
    /// <summary>Format used when a DateTime value replaces a text placeholder. Default "yyyy-MM-dd HH:mm:ss".</summary>
    public string DateTimeFormat { get; set; } = "yyyy-MM-dd HH:mm:ss";

    /// <summary>Format used for DateOnly values rendered as text. Default "yyyy-MM-dd".</summary>
    public string DateOnlyFormat { get; set; } = "yyyy-MM-dd";

    public TemplateOptions() { }

    public TemplateOptions Clone() => (TemplateOptions)MemberwiseClone();
}
