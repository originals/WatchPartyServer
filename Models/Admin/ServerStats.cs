namespace WatchPartyServer.Models;

public record ServerStats
{
    public int TotalRooms { get; init; }
    public int TotalConnections { get; init; }
    public int LobbyConnections { get; init; }
    public int RoomConnections { get; init; }
    public int PublicRooms { get; init; }
    public int PrivateRooms { get; init; }
    public DateTime ServerStartTime { get; init; }
    public TimeSpan Uptime { get; init; }
}
