namespace WatchPartyServer.Models;

public record RemoveConnectionResult
{
    public string? RoomId { get; init; }
    public bool RoomDeleted { get; init; }
    public string? NewHostConnectionId { get; init; }
}
