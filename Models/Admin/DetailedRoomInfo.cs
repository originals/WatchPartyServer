namespace WatchPartyServer.Models;

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
