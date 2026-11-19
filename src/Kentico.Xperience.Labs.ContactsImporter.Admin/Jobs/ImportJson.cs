using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// System.Text.Json settings of the wire contract: camelCase properties and camelCase string enums
/// (job state uses its exact name, see <see cref="ImportJobState"/>).
/// </summary>
internal static class ImportJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    /// <summary>
    /// Deserializes UTF-8 JSON. Throws <see cref="JsonException"/> when the JSON is invalid, a required
    /// property is missing or null, or the document is <c>null</c>.
    /// </summary>
    public static T Deserialize<T>(ReadOnlySpan<byte> utf8Json) where T : class =>
        JsonSerializer.Deserialize<T>(utf8Json, Options)
            ?? throw new JsonException($"JSON document of type {typeof(T).Name} is null.");

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            NumberHandling = JsonNumberHandling.Strict,
            RespectNullableAnnotations = true,
        };

        // Converters in this collection win over [JsonConverter] on a type, so the job state converter
        // must come before the general camelCase enum converter.
        options.Converters.Add(new ExactNameEnumConverter<ImportJobState>());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);

        return options;
    }
}

/// <summary>
/// Serializes an enum by its exact member name; numbers are rejected.
/// </summary>
internal sealed class ExactNameEnumConverter<TEnum>() : JsonStringEnumConverter<TEnum>(namingPolicy: null, allowIntegerValues: false)
    where TEnum : struct, Enum;
