// Ported from firestaerter3/Jellyfin-Xtream-Library (GPL v3)
// Handles Xtream Codes API JSON quirks: booleans as "0"/"1" strings,
// Base64-encoded titles/descriptions, single objects returned where arrays expected, etc.

using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using XtreamBridge.Models;

namespace XtreamBridge.Converters;

/// <summary>Decodes Base64-encoded strings (Xtream encodes EPG title/desc in Base64).</summary>
public class Base64Converter : JsonConverter
{
    public override bool CanConvert(Type objectType) => objectType == typeof(string);

    public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        if (reader.Value is null) return string.Empty;
        var raw = (string)reader.Value!;
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(raw));
        }
        catch
        {
            return raw; // not Base64 — return as-is
        }
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is null) { writer.WriteNull(); return; }
        writer.WriteValue(Convert.ToBase64String(Encoding.UTF8.GetBytes((string)value)));
    }
}

/// <summary>
/// Converts "0"/"1" strings (or plain booleans/integers) to bool.
/// Many Xtream Codes providers return boolean fields as "0" or "1" strings.
/// </summary>
public class StringBoolConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) => objectType == typeof(bool);

    public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        return reader.TokenType switch
        {
            JsonToken.String  => "1".Equals((string?)reader.Value, StringComparison.Ordinal),
            JsonToken.Integer => Convert.ToInt64(reader.Value) == 1,
            JsonToken.Boolean => (bool)reader.Value!,
            _                 => false,
        };
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is null) { writer.WriteNull(); return; }
        writer.WriteValue((bool)value ? "1" : "0");
    }
}

/// <summary>
/// Accepts either a single object T or an array [T] and always returns ICollection&lt;T&gt;.
/// Some Xtream endpoints return a string instead of an array for single-element lists.
/// </summary>
public class SingularToListConverter<T> : JsonConverter
{
    public override bool CanConvert(Type objectType) => objectType == typeof(ICollection<T>);

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        return reader.TokenType switch
        {
            JsonToken.StartObject => new List<T> { serializer.Deserialize<T>(reader)! },
            JsonToken.StartArray  => serializer.Deserialize<List<T>>(reader) ?? new List<T>(),
            JsonToken.String when typeof(T) == typeof(string) =>
                new List<T> { (T)(object)(string)reader.Value! },
            JsonToken.Null => new List<T>(),
            _ => new List<T>(),
        };
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) =>
        serializer.Serialize(writer, value);
}

/// <summary>
/// Wraps a potentially-array response in a null when it's not an object.
/// Used for optional info objects that some providers return as [] instead of {}.
/// </summary>
public class OnlyObjectConverter<T> : JsonConverter where T : class
{
    public override bool CanConvert(Type objectType) => objectType == typeof(T);

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        var token = JToken.Load(reader);
        return token.Type == JTokenType.Object ? token.ToObject<T>(serializer) : null;
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) =>
        serializer.Serialize(writer, value);
}

/// <summary>
/// Deserialises the episodes object {"1":[...], "2":[...]} → Dictionary&lt;int, ICollection&lt;XtreamEpisode&gt;&gt;.
/// Handles providers that return a plain array or a single episode object for edge cases.
/// </summary>
public class EpisodeDictionaryConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) =>
        objectType == typeof(Dictionary<int, ICollection<XtreamEpisode>>);

    public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        var result = new Dictionary<int, ICollection<XtreamEpisode>>();
        if (reader.TokenType != JsonToken.StartObject)
        {
            if (reader.TokenType == JsonToken.StartArray) JToken.Load(reader); // discard
            return result;
        }

        var obj = JObject.Load(reader);
        foreach (var prop in obj.Properties())
        {
            if (!int.TryParse(prop.Name, out var seasonNum)) continue;
            result[seasonNum] = prop.Value.Type switch
            {
                JTokenType.Array  => prop.Value.ToObject<List<XtreamEpisode>>(serializer) ?? new(),
                JTokenType.Object => new List<XtreamEpisode>
                    { prop.Value.ToObject<XtreamEpisode>(serializer)! },
                _ => new List<XtreamEpisode>(),
            };
        }
        return result;
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) =>
        serializer.Serialize(writer, value);
}

/// <summary>Parses Unix timestamp strings or nulls into DateTime?.</summary>
public class UnixDateTimeNullableConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) =>
        objectType == typeof(DateTime?) || objectType == typeof(DateTime);

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null) return null;
        if (reader.TokenType == JsonToken.Integer)
        {
            var ts = Convert.ToInt64(reader.Value);
            return ts > 0 ? DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime : (DateTime?)null;
        }
        if (reader.TokenType == JsonToken.String)
        {
            var s = (string)reader.Value!;
            if (long.TryParse(s, out var ts2))
                return ts2 > 0 ? DateTimeOffset.FromUnixTimeSeconds(ts2).UtcDateTime : (DateTime?)null;
            if (DateTime.TryParse(s, out var dt)) return dt;
        }
        return null;
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) =>
        serializer.Serialize(writer, value);
}
