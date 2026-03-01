using Newtonsoft.Json;

namespace WatchPartyServer.Models;

public class SharedLocation
{
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("x")]
    public float X { get; set; }

    [JsonProperty("y")]
    public float Y { get; set; }

    [JsonProperty("z")]
    public float Z { get; set; }

    [JsonProperty("yaw")]
    public float Yaw { get; set; }

    [JsonProperty("pitch")]
    public float Pitch { get; set; }

    [JsonProperty("mapId")]
    public int MapId { get; set; }

    [JsonProperty("screenWidth")]
    public float ScreenWidth { get; set; }
}
