using WatchPartyServer.Models;

namespace WatchPartyServer.Services;

public interface IAdminStateManager
{
    ServerStats GetServerStats(DateTime serverStartTime);
    List<ClientConnectionInfo> GetAllConnections();
    List<DetailedRoomInfo> GetDetailedRoomList();
    List<RoomMemberStates> GetAllRoomMemberStates();
    DeleteRoomResult? AdminDeleteRoom(string roomId);
    string? GetConnectionUsername(string connectionId);
    List<string> GetGlobalBlacklist();
    bool AddToGlobalBlacklist(string username);
    bool RemoveFromGlobalBlacklist(string username);
    void TrackConnection(string connectionId);
    void UntrackConnection(string connectionId);
    int GetLobbyConnectionCount();
}
