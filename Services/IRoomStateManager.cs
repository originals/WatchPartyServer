using WatchPartyServer.Models;

namespace WatchPartyServer.Services;

public interface IRoomStateManager
{
    RoomState CreateRoom(string roomName, string hostUsername, string hostConnectionId, bool isPrivate, string? password, SharedLocation? sharedLocation);
    bool AddMember(string roomId, string connectionId, string username, string? password);
    string? GetConnectionRoom(string connectionId);
    List<RoomInfo> GetRoomList();
    RoomInfo? GetRoomInfo(string roomId);
    StateSnapshot? GetRoomSnapshot(string roomId);
    SyncPayload? GetCurrentPlaybackState(string roomId);
    bool IsHost(string roomId, string connectionId);
    bool IsMember(string roomId, string connectionId);
    bool IsConnectionBlacklisted(string connectionId);
    RemoveConnectionResult RemoveConnection(string connectionId);
    Dictionary<string, double>? UpdateMemberTime(string roomId, string connectionId, double currentTime);
    MemberStateUpdateResult? UpdateMemberState(string roomId, string connectionId, MemberState state);
    DeleteRoomResult? DeleteRoom(string roomId, string connectionId);
    SyncPayload? UpdatePlayback(string roomId, SyncPayload payload);
    SyncPayload? SetPlayState(string roomId, bool isPlaying);
    bool UpdateRoom(string roomId, string connectionId, string roomName, string description);
    EnqueueResult? EnqueueVideo(string roomId, QueueItem item);
    bool UpdateMaxQueuePerUser(string roomId, string connectionId, int maxQueuePerUser);
    SkipResult? SkipToNext(string roomId);
    List<QueueItem>? RemoveFromQueue(string roomId, int index);
    List<QueueItem>? ReorderQueue(string roomId, int fromIndex, int toIndex);
    BanMemberResult? BanMember(string roomId, string hostConnectionId, string username);
    bool IsGloballyBlacklisted(string username);
}
