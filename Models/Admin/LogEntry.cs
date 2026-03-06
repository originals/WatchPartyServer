namespace WatchPartyServer.Models;

public record LogEntry
{
    public DateTime Timestamp { get; init; }
    public string Level { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? ConnectionId { get; init; }
    public string? RoomId { get; init; }
}
