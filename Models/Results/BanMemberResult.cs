namespace WatchPartyServer.Models;

public record BanMemberResult
{
    public string? BannedConnectionId { get; init; }
    public string Username { get; init; } = string.Empty;
}
