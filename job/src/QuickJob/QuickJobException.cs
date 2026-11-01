namespace QuickJob;

/// <summary>
/// The one exception type QuickJob throws on its own surface: bad cron strings, bad registration
/// shapes, lifecycle misuse. Handler exceptions are never masqueraded as this type — they surface
/// via the run's failure path (events / execution log / retries).
/// </summary>
public class QuickJobException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public QuickJobException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public QuickJobException(string message, Exception inner) : base(message, inner) { }
}
