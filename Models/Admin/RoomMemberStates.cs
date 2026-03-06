namespace WatchPartyServer.Models;

public record RoomMemberStates
{
    public string RoomId { get; init; } = string.Empty;
    public string RoomName { get; init; } = string.Empty;
    public List<string> Members { get; init; } = new();
    public Dictionary<string, MemberState> MemberStates { get; init; } = new();
    public Dictionary<string, double> MemberTimes { get; init; } = new();
}
