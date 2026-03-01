using WatchPartyServer.Models;

namespace WatchPartyServer.Services;

public interface IAdminStateManager
{
    ServerStats GetServerStats(DateTime serverStartTime);
    List<ClientConnectionInfo> GetAllConnections();
    List<DetailedRoomInfo> GetDetailedRoomList();
    DeleteRoomResult? AdminDeleteRoom(string roomId);
    bool AdminKickConnection(string connectionId);
    string? GetConnectionUsername(string connectionId);
    List<string> GetGlobalBlacklist();
    bool AddToGlobalBlacklist(string username);
    bool RemoveFromGlobalBlacklist(string username);
}
