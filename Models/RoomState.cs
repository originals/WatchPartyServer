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
    public bool IsWaitingForReady { get; set; }
    public string PendingVideoId { get; set; } = string.Empty;
    public List<QueueItem> Queue { get; set; } = new();
    public List<string> Members { get; set; } = new();
    public HashSet<string> BannedUsers { get; set; } = new();
    public Dictionary<string, double> MemberTimes { get; set; } = new();
    public Dictionary<string, MemberState> MemberStates { get; set; } = new();
    public DateTime CreatedAt { get; set; }
}
