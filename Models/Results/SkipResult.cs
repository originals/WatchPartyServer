namespace WatchPartyServer.Models;

public record SkipResult
{
    public string? VideoId { get; init; }
    public List<QueueItem> Queue { get; init; } = new();
    public bool IsPlaying { get; init; }
    public long SequenceNumber { get; init; }
}
