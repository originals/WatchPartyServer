namespace WatchPartyServer.Models;

public record MemberStateUpdateResult
{
    public Dictionary<string, MemberState> MemberStates { get; init; } = new();
    public bool StateChanged { get; init; }
}
