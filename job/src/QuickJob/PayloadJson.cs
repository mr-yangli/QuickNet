namespace QuickJob;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// JSON plumbing for payloads and definition contracts (both stored as plain JSON text). Payload
/// bytes stay UTF-8 JSON in <see cref="JobRunContext.Payload"/>; the typed-request path only adds
/// a System.Text.Json deserialise on top. No BinaryData here — priceless: it left the default
/// .NET 10 reference surface, and QuickJob doesn't take packages.
/// </summary>
public static class PayloadJson
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web)
    {
        // 存档形态里枚举保真排版：字符串形态在 store 里才好读
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Serialises a JSON-serialisable payload into the canonical UTF-8 JSON bytes.</summary>
    public static byte[] Serialize(object? value)
        => value is null ? [] : JsonSerializer.SerializeToUtf8Bytes(value, WebOptions);

    /// <summary>Deserialises a payload previously written by <see cref="Serialize"/> (or any raw JSON text).</summary>
    public static TPayload? Deserialize<TPayload>(byte[]? payload)
        => payload is not { Length: > 0 } ? default : JsonSerializer.Deserialize<TPayload>(payload, WebOptions);

    /// <summary>Canonical store text for a payload (null when none). JSON text IS the store format.</summary>
    public static string? ToStoreText(byte[]? payload)
        => payload is not { Length: > 0 } ? null : System.Text.Encoding.UTF8.GetString(payload);

    /// <summary>Store text back to the runtime byte view.</summary>
    public static byte[]? FromStoreText(string? text)
        => string.IsNullOrEmpty(text) ? null : System.Text.Encoding.UTF8.GetBytes(text);
}

/// <summary>Definition (de)serialisation for the store seam's <see cref="StoredDefinition.Contract"/>.</summary>
internal static class DefinitionSerialization
{
    private static readonly object Gate = new();
    private static JsonSerializerOptions? _writeOptions;
    private static JsonSerializerOptions? _readOptions;

    /// <summary>Serialises a definition (schedule + options; handler stays in code).</summary>
    public static string Write(JobDefinition def)
    {
        lock (Gate)
        {
            _writeOptions ??= new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new JsonStringEnumConverter() },
                WriteIndented = false,
            };
            return JsonSerializer.Serialize(def, _writeOptions);
        }
    }

    /// <summary>Reads a contract back; null when it does not decode (store pollution shouldn't brick the boot).</summary>
    public static JobDefinition? TryLoad(string? contract)
    {
        if (string.IsNullOrWhiteSpace(contract)) return null;
        try
        {
            lock (Gate)
            {
                _readOptions ??= new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    Converters = { new JsonStringEnumConverter() },
                    PropertyNameCaseInsensitive = true,
                };
                var loaded = JsonSerializer.Deserialize<JobDefinition>(contract, _readOptions);
                return loaded?.Options is null ? null : loaded; // 缺 options 的旧档当作读不得
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
