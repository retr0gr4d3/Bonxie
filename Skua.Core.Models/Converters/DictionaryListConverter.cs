using Newtonsoft.Json;

namespace Skua.Core.Models.Converters;

public class DictionaryListConverter<TKey, TVal> : JsonConverter
{
    public override bool CanConvert(Type objectType)
    {
        return objectType == typeof(List<TVal>);
    }

    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
    {
        // AS3 serialises an empty object as [] (or null) on some servers, so accept a list too
        return reader.TokenType switch
        {
            JsonToken.Null => new List<TVal>(),
            JsonToken.StartArray => serializer.Deserialize<List<TVal>>(reader)?.Where(v => v is not null).ToList() ?? new List<TVal>(),
            _ => serializer.Deserialize<Dictionary<TKey, TVal>>(reader)?.Values.ToList() ?? new List<TVal>()
        };
    }

    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
    {
        serializer.Serialize(writer, value);
    }
}