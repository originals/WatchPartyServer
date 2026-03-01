namespace WatchPartyServer.Models;

public record RoomInfo
{
    public string RoomId { get; init; } = string.Empty;
    public string RoomName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string HostUsername { get; init; } = string.Empty;
    public bool IsPrivate { get; init; }
    public SharedLocation? SharedLocation { get; init; }
    public int MemberCount { get; init; }
    public DateTime CreatedAt { get; init; }
}
