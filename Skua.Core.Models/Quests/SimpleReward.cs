using Newtonsoft.Json;
using Skua.Core.Models.Items;

namespace Skua.Core.Models.Quests;

public class SimpleReward : ItemBase
{
    /// <summary>
    /// The rate at which this reward drops.
    /// </summary>
    [JsonProperty("iRate")]
    public double Rate { get; set; }

    /// <summary>
    /// The type of the item as an integer ID.
    /// </summary>
    [JsonProperty("iType")]
    [JsonConverter(typeof(RewardTypeConverter))]
    public int Type { get; set; }
}

// AQW sends iType as a number; SwordShine sends a letter ("S" standard, "C" choice, "R" random).
// Letters map onto the numbers scripts check, notably 2 = choose-a-reward.
public class RewardTypeConverter : JsonConverter<int>
{
    public override int ReadJson(JsonReader reader, Type objectType, int existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        string? value = reader.Value?.ToString();
        if (int.TryParse(value, out int number))
            return number;
        return value?.ToUpperInvariant() switch
        {
            "S" => 1,
            "C" => 2,
            "R" => 3,
            _ => 0
        };
    }

    public override void WriteJson(JsonWriter writer, int value, JsonSerializer serializer) => writer.WriteValue(value);
}