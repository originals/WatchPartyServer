namespace WatchPartyServer.Models;

public class SharedLocation
{
    public string Name { get; set; } = string.Empty;
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Yaw { get; set; }
    public float Pitch { get; set; }
    public int MapId { get; set; }
    public float ScreenWidth { get; set; }
}
