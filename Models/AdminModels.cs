namespace WatchPartyServer.Models;

public record ServerStats
{
    public int TotalRooms { get; init; }
    public int TotalConnections { get; init; }
    public int PublicRooms { get; init; }
    public int PrivateRooms { get; init; }
    public DateTime ServerStartTime { get; init; }
    public TimeSpan Uptime { get; init; }
}

public record ClientConnectionInfo
{
    public string ConnectionId { get; init; } = string.Empty;
    public string ShortId { get; init; } = string.Empty;
    public string? Username { get; init; }
    public string? RoomId { get; init; }
}

public record DetailedRoomInfo
{
    public string RoomId { get; init; } = string.Empty;
    public string RoomName { get; init; } = string.Empty;
    public string HostUsername { get; init; } = string.Empty;
    public string HostConnectionId { get; init; } = string.Empty;
    public bool IsPrivate { get; init; }
    public int MemberCount { get; init; }
    public int ConnectionCount { get; init; }
    public List<string> Members { get; init; } = new();
    public List<string> BannedUsers { get; init; } = new();
    public int QueueLength { get; init; }
    public string CurrentVideoId { get; init; } = string.Empty;
    public bool IsPlaying { get; init; }
    public DateTime CreatedAt { get; init; }
}
