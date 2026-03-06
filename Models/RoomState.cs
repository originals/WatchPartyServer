namespace WatchPartyServer.Models;

public class RoomState
{
    public string RoomId { get; set; } = string.Empty;
    public string RoomName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string HostConnectionId { get; set; } = string.Empty;
    public string HostUsername { get; set; } = string.Empty;
    public bool IsPrivate { get; set; }
    public string Password { get; set; } = string.Empty;
    public SharedLocation? SharedLocation { get; set; }
    public string CurrentVideoId { get; set; } = string.Empty;
    public double CurrentTime { get; set; }
    public bool IsPlaying { get; set; }
    public DateTime LastTimeUpdate { get; set; } = DateTime.UtcNow;
    public long SequenceNumber { get; set; }
    public List<QueueItem> Queue { get; set; } = new();
    public List<string> Members { get; set; } = new();
    public HashSet<string> BannedUsers { get; set; } = new();
    public Dictionary<string, double> MemberTimes { get; set; } = new();
    public Dictionary<string, MemberState> MemberStates { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public int MaxQueuePerUser { get; set; }

    public double GetCalculatedTime()
    {
        if (!IsPlaying || string.IsNullOrEmpty(CurrentVideoId))
            return CurrentTime;

        var elapsed = (DateTime.UtcNow - LastTimeUpdate).TotalSeconds;
        return CurrentTime + elapsed;
    }
}
