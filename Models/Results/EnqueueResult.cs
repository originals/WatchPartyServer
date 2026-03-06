namespace WatchPartyServer.Models;

public record EnqueueResult
{
    public List<QueueItem> Queue { get; init; } = new();
}
