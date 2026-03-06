namespace WatchPartyServer.Models;

public record DeleteRoomResult
{
    public List<string> ConnectionIds { get; init; } = new();
}
