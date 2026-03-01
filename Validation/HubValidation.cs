using Microsoft.AspNetCore.SignalR;

namespace WatchPartyServer.Validation;

public static class HubValidation
{
    private const int MaxRoomNameLength = 50;
    private const int MaxDescriptionLength = 200;
    private const int MaxUsernameLength = 32;

    public static void ValidateRoomName(string? roomName)
    {
        if (string.IsNullOrWhiteSpace(roomName))
            throw new HubException("Room name is required.");
        if (roomName.Length > MaxRoomNameLength)
            throw new HubException($"Room name must be {MaxRoomNameLength} characters or less.");
    }

    public static void ValidateUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new HubException("Username is required.");
        if (username.Length > MaxUsernameLength)
            throw new HubException($"Username must be {MaxUsernameLength} characters or less.");
    }

    public static void ValidateRoomId(string? roomId)
    {
        if (string.IsNullOrWhiteSpace(roomId))
            throw new HubException("Room ID is required.");
    }

    public static void ValidateDescription(string? description)
    {
        if (description != null && description.Length > MaxDescriptionLength)
            throw new HubException($"Description must be {MaxDescriptionLength} characters or less.");
    }
}
