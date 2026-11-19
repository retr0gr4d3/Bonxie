using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Skua.Core.Models.Items;

namespace Skua.Core.Models.Converters;

public class QuestRewardConverter : JsonConverter
{
    public override bool CanConvert(Type objectType)
    {
        return objectType == typeof(List<ItemBase>);
    }

    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
    {
        // { "itemsS": { id: item }, ... } on AQW; tolerate [] / null groups that some servers send for empty ones
        if (reader.TokenType != JsonToken.StartObject)
        {
            reader.Skip();
            return new List<ItemBase>();
        }

        List<ItemBase> rewards = new();
        foreach (JProperty group in JObject.Load(reader).Properties())
        {
            IEnumerable<JToken> items = group.Value switch
            {
                JObject byId => byId.Properties().Select(p => p.Value),
                JArray list => list,
                _ => Enumerable.Empty<JToken>()
            };
            foreach (JToken item in items)
            {
                if (item.Type == JTokenType.Object && item.ToObject<ItemBase>(serializer) is { } reward)
                    rewards.Add(reward);
            }
        }
        return rewards;
    }

    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
    {
        serializer.Serialize(writer, value);
    }
}