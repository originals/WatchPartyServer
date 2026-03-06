namespace WatchPartyServer.Models;

public record DeleteRoomResult
{
    public List<string> ConnectionIds { get; init; } = new();
}

public record EnqueueResult
{
    public List<QueueItem> Queue { get; init; } = new();
}

public record RemoveConnectionResult
{
    public string? RoomId { get; init; }
    public bool RoomDeleted { get; init; }
    public string? NewHostConnectionId { get; init; }
}

public record SkipResult
{
    public string? VideoId { get; init; }
    public List<QueueItem> Queue { get; init; } = new();
    public bool IsPlaying { get; init; }
    public long SequenceNumber { get; init; }
}

public record BanMemberResult
{
    public string? BannedConnectionId { get; init; }
    public string Username { get; init; } = string.Empty;
}

public record MemberStateUpdateResult
{
    public Dictionary<string, MemberState> MemberStates { get; init; } = new();
    public bool StateChanged { get; init; }
}
