namespace WatchPartyServer.Models;

public record StateSnapshot
{
    public string RoomId { get; init; } = string.Empty;
    public string RoomName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string HostUsername { get; init; } = string.Empty;
    public SharedLocation? SharedLocation { get; init; }
    public string CurrentVideoId { get; init; } = string.Empty;
    public double CurrentTime { get; init; }
    public bool IsPlaying { get; init; }
    public bool IsWaitingForReady { get; init; }
    public List<QueueItem> Queue { get; init; } = new();
    public List<string> Members { get; init; } = new();
    public Dictionary<string, double> MemberTimes { get; init; } = new();
    public Dictionary<string, MemberState> MemberStates { get; init; } = new();
}
