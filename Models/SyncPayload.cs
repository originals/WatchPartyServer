namespace WatchPartyServer.Models;

public record SyncPayload
{
    public double Timestamp { get; init; }
    public bool IsPlaying { get; init; }
    public long SequenceNumber { get; init; }
}
