namespace WatchPartyServer.Models;

public record ClientConnectionInfo
{
    public string ConnectionId { get; init; } = string.Empty;
    public string ShortId { get; init; } = string.Empty;
    public string? Username { get; init; }
    public string? RoomId { get; init; }
    public bool IsInLobby { get; init; }
    public DateTime ConnectedAt { get; init; }
}
