using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConnectionManager.Core.Ipc;

/// Enum values as their camelCase names only. The built-in converter also takes 3, "3" and "on, off",
/// and matches names ignoring case; a pipe client gets none of that.
public sealed class StrictEnum<T> : JsonConverter<T> where T : struct, Enum
{
    private static readonly Dictionary<T, string> Names =
        Enum.GetValues<T>().ToDictionary(v => v, v => JsonNamingPolicy.CamelCase.ConvertName(v.ToString()));

    private static readonly Dictionary<string, T> Values = Names.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && Values.TryGetValue(reader.GetString()!, out var value)
            ? value
            : throw new JsonException($"Expected a {typeof(T).Name} name.");

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Names.TryGetValue(value, out var name) ? name : throw new JsonException($"{typeof(T).Name} {value} has no name."));
}
