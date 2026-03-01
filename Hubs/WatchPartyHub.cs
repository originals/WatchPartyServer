using Microsoft.AspNetCore.SignalR;
using WatchPartyServer.Models;
using WatchPartyServer.Services;
using WatchPartyServer.Validation;

namespace WatchPartyServer.Hubs;

public class WatchPartyHub : Hub
{
    private const string LobbyGroup = "Lobby";
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(10);
    private static readonly DateTime ServerStartTime = DateTime.UtcNow;
    private static readonly string AdminKey = Environment.GetEnvironmentVariable("WATCHPARTY_ADMIN_KEY") ?? "CinemaAdmin2024!";

    private readonly IRoomStateManager _stateManager;
    private readonly IAdminStateManager _adminManager;
    private readonly ILogger<WatchPartyHub> _logger;

    public WatchPartyHub(IRoomStateManager stateManager, IAdminStateManager adminManager, ILogger<WatchPartyHub> logger)
    {
        _stateManager = stateManager;
        _adminManager = adminManager;
        _logger = logger;
    }

    private string ShortConnectionId => Context.ConnectionId[..8];

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("[CONNECT] {ConnectionId}", ShortConnectionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, LobbyGroup);
        await base.OnConnectedAsync();
    }

    public List<RoomInfo> GetRooms()
    {
        var rooms = _stateManager.GetRoomList();
        _logger.LogInformation("[GET_ROOMS] {Count} rooms", rooms.Count);
        return rooms;
    }

    public async Task<RoomInfo> CreateRoom(string roomName, bool isPrivate, string? password, string username, SharedLocation? sharedLocation)
    {
        HubValidation.ValidateRoomName(roomName);
        HubValidation.ValidateUsername(username);

        if (_stateManager.IsGloballyBlacklisted(username))
            throw new HubException("You are not allowed to use watch party.");

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, LobbyGroup);

        var room = _stateManager.CreateRoom(roomName, username, Context.ConnectionId, isPrivate, password, sharedLocation);
        await Groups.AddToGroupAsync(Context.ConnectionId, room.RoomId);

        _logger.LogInformation("[CREATE_ROOM] {Username} created '{RoomName}' ({RoomId}) private={IsPrivate} hasLocation={HasLocation}", 
            username, roomName, room.RoomId, isPrivate, sharedLocation != null);

        var state = _stateManager.GetRoomSnapshot(room.RoomId)!;
        LogStateSnapshot(state);
        await Clients.Caller.SendAsync("ReceiveFullState", state);
        await BroadcastRoomListToLobbyAsync();

        return _stateManager.GetRoomInfo(room.RoomId)!;
    }

    public async Task<RoomInfo> JoinRoom(string roomId, string username, string? password)
    {
        HubValidation.ValidateRoomId(roomId);
        HubValidation.ValidateUsername(username);

        if (_stateManager.IsGloballyBlacklisted(username))
            throw new HubException("You are not allowed to use watch party.");

        var oldRoomId = _stateManager.GetConnectionRoom(Context.ConnectionId);
        if (oldRoomId != null && oldRoomId != roomId)
        {
            _logger.LogInformation("[JOIN_ROOM] {Username} switching from {OldRoom} to {NewRoom}", username, oldRoomId, roomId);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, oldRoomId);

            var oldState = _stateManager.GetRoomSnapshot(oldRoomId);
            if (oldState != null)
                await Clients.Group(oldRoomId).SendAsync("ReceiveFullState", oldState);
        }

        if (!_stateManager.AddMember(roomId, Context.ConnectionId, username, password))
        {
            _logger.LogWarning("[JOIN_FAILED] {Username} failed to join {RoomId}", username, roomId);
            throw new HubException("Failed to join room. Check the room ID and password.");
        }

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, LobbyGroup);
        await Groups.AddToGroupAsync(Context.ConnectionId, roomId);

        _logger.LogInformation("[JOIN_ROOM] {Username} joined {RoomId}", username, roomId);

        var state = _stateManager.GetRoomSnapshot(roomId)!;
        LogStateSnapshot(state);
        await Clients.Caller.SendAsync("ReceiveFullState", state);
        await Clients.OthersInGroup(roomId).SendAsync("ReceiveFullState", state);
        await BroadcastRoomListToLobbyAsync();

        return _stateManager.GetRoomInfo(roomId)!;
    }

    public async Task LeaveRoom(string roomId)
    {
        _logger.LogInformation("[LEAVE_ROOM] {ConnectionId} leaving {RoomId}", ShortConnectionId, roomId);

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId);
        var result = _stateManager.RemoveConnection(Context.ConnectionId);

        await Groups.AddToGroupAsync(Context.ConnectionId, LobbyGroup);

        if (!result.RoomDeleted && result.RoomId != null)
        {
            if (result.WasWaitingForReady && _stateManager.AreAllMembersReady(result.RoomId))
            {
                _logger.LogInformation("[READY_CHECK] Member left but remaining members are ready in {RoomId}", result.RoomId);
                await StartPlaybackAfterReadyAsync(result.RoomId);
            }

            await BroadcastRoomStateAsync(result.RoomId);
        }

        await BroadcastRoomListToLobbyAsync();
    }

    public async Task DeleteRoom(string roomId)
    {
        var result = _stateManager.DeleteRoom(roomId, Context.ConnectionId);
        if (result == null)
            throw new HubException("Cannot delete room.");

        _logger.LogInformation("[DELETE_ROOM] {RoomId} deleted, {Count} members ejected", roomId, result.ConnectionIds.Count);

        await Clients.Group(roomId).SendAsync("RoomDeleted");

        foreach (var connId in result.ConnectionIds)
        {
            await Groups.RemoveFromGroupAsync(connId, roomId);
            await Groups.AddToGroupAsync(connId, LobbyGroup);
        }

        await BroadcastRoomListToLobbyAsync();
    }

    public async Task UpdateRoom(string roomId, string roomName, string description)
    {
        HubValidation.ValidateRoomName(roomName);
        HubValidation.ValidateDescription(description);

        if (!_stateManager.UpdateRoom(roomId, Context.ConnectionId, roomName, description))
            throw new HubException("Cannot update room.");

        _logger.LogInformation("[UPDATE_ROOM] {RoomId} updated: name={RoomName}", roomId, roomName);

        await BroadcastRoomStateAsync(roomId);
        await BroadcastRoomListToLobbyAsync();
    }

    public async Task UpdatePlayback(string roomId, SyncPayload payload)
    {
        if (!_stateManager.IsHost(roomId, Context.ConnectionId)) return;

        _logger.LogInformation("[PLAYBACK] Room {RoomId}: {State} @ {Time:F1}s", 
            roomId, payload.IsPlaying ? "PLAY" : "PAUSE", payload.Timestamp);

        _stateManager.UpdatePlayback(roomId, payload);
        await Clients.Group(roomId).SendAsync("ReceiveSyncUpdate", payload);
    }

    public async Task ReportPlaybackTime(string roomId, double currentTime)
    {
        var memberTimes = _stateManager.UpdateMemberTime(roomId, Context.ConnectionId, currentTime);
        if (memberTimes != null)
            await Clients.Group(roomId).SendAsync("ReceiveMemberTimes", memberTimes);
    }

    public async Task ReportMemberState(string roomId, MemberState state)
    {
        var memberStates = _stateManager.UpdateMemberState(roomId, Context.ConnectionId, state);
        if (memberStates != null)
            await Clients.Group(roomId).SendAsync("ReceiveMemberStates", memberStates);

        if (state == MemberState.Ready && _stateManager.AreAllMembersReady(roomId))
        {
            _logger.LogInformation("[READY_CHECK] All members ready in room {RoomId}, starting playback", roomId);
            await StartPlaybackAfterReadyAsync(roomId);
        }
    }

    private async Task StartPlaybackAfterReadyAsync(string roomId)
    {
        _stateManager.FinishWaitingForReady(roomId);

        await Clients.Group(roomId).SendAsync("ReceiveSyncUpdate", new SyncPayload
        {
            Timestamp = 0,
            IsPlaying = true
        });
    }

    public async Task RequestState(string roomId)
    {
        var state = _stateManager.GetRoomSnapshot(roomId);
        if (state != null)
            await Clients.Caller.SendAsync("ReceiveFullState", state);
    }

    public async Task EnqueueVideo(string roomId, QueueItem item)
    {
        if (!_stateManager.IsMember(roomId, Context.ConnectionId)) return;

        var result = _stateManager.EnqueueVideo(roomId, item);
        if (result == null) return;

        _logger.LogInformation("[QUEUE_ADD] {VideoId} added by {AddedBy} in {RoomId}", item.VideoId, item.AddedBy, roomId);

        await Clients.Group(roomId).SendAsync("ReceiveQueueUpdate", result.Queue);
    }

    public async Task PlayNextInQueue(string roomId)
    {
        if (!_stateManager.IsHost(roomId, Context.ConnectionId)) return;

        var result = _stateManager.SkipToNext(roomId);
        if (result == null) return;

        if (result.VideoId != null)
        {
            _logger.LogInformation("[PLAY_NEXT] Room {RoomId}: loading {VideoId}, waiting for members", roomId, result.VideoId);

            _stateManager.StartWaitingForReady(roomId, result.VideoId);
            await Clients.Group(roomId).SendAsync("ForceLoadVideo", result.VideoId);
            await Clients.Group(roomId).SendAsync("ReceiveQueueUpdate", result.Queue);

            _ = StartReadyTimeoutAsync(roomId, result.VideoId);
        }
        else
        {
            _logger.LogInformation("[PLAY_NEXT] Room {RoomId}: queue empty", roomId);
            await Clients.Group(roomId).SendAsync("ReceiveQueueUpdate", result.Queue);
        }
    }

    public async Task RemoveFromQueue(string roomId, int index)
    {
        if (!_stateManager.IsHost(roomId, Context.ConnectionId)) return;

        var queue = _stateManager.RemoveFromQueue(roomId, index);
        if (queue == null) return;

        _logger.LogInformation("[QUEUE_REMOVE] Index {Index} removed from {RoomId}", index, roomId);
        await Clients.Group(roomId).SendAsync("ReceiveQueueUpdate", queue);
    }

    public async Task ReorderQueue(string roomId, int fromIndex, int toIndex)
    {
        if (!_stateManager.IsHost(roomId, Context.ConnectionId)) return;

        var queue = _stateManager.ReorderQueue(roomId, fromIndex, toIndex);
        if (queue == null) return;

        _logger.LogInformation("[QUEUE_REORDER] {FromIndex} -> {ToIndex} in {RoomId}", fromIndex, toIndex, roomId);
        await Clients.Group(roomId).SendAsync("ReceiveQueueUpdate", queue);
    }

    public async Task BanMember(string roomId, string username)
    {
        var result = _stateManager.BanMember(roomId, Context.ConnectionId, username);
        if (result == null)
            throw new HubException("Cannot ban member.");

        _logger.LogInformation("[BAN_MEMBER] {Username} banned from {RoomId}", username, roomId);

        if (result.BannedConnectionId != null)
        {
            await Clients.Client(result.BannedConnectionId).SendAsync("MemberBanned", username);
            await Groups.RemoveFromGroupAsync(result.BannedConnectionId, roomId);
            await Groups.AddToGroupAsync(result.BannedConnectionId, LobbyGroup);
        }

        await BroadcastRoomStateAsync(roomId);
        await BroadcastRoomListToLobbyAsync();
    }

    private async Task StartReadyTimeoutAsync(string roomId, string videoId)
    {
        await Task.Delay(ReadyTimeout);

        if (!_stateManager.IsWaitingForReady(roomId))
            return;

        _logger.LogWarning("[READY_TIMEOUT] Room {RoomId}: timeout waiting for members, forcing playback", roomId);
        await StartPlaybackAfterReadyAsync(roomId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("[DISCONNECT] {ConnectionId} {Reason}", 
            ShortConnectionId, exception?.Message ?? "clean");

        var result = _stateManager.RemoveConnection(Context.ConnectionId);

        if (result.RoomId != null)
        {
            if (result.RoomDeleted)
            {
                _logger.LogInformation("[ROOM_AUTO_DELETE] {RoomId} (last member left)", result.RoomId);
            }
            else
            {
                if (result.NewHostConnectionId != null)
                    _logger.LogInformation("[HOST_TRANSFER] Room {RoomId}: new host assigned", result.RoomId);

                if (result.WasWaitingForReady && _stateManager.AreAllMembersReady(result.RoomId))
                {
                    _logger.LogInformation("[READY_CHECK] Member left but remaining members are ready in {RoomId}", result.RoomId);
                    await StartPlaybackAfterReadyAsync(result.RoomId);
                }

                await BroadcastRoomStateAsync(result.RoomId);
            }

            await BroadcastRoomListToLobbyAsync();
        }

        await base.OnDisconnectedAsync(exception);
    }

    private async Task BroadcastRoomListToLobbyAsync()
    {
        var rooms = _stateManager.GetRoomList();
        _logger.LogInformation("[BROADCAST_ROOMS] Sending {Count} rooms to all clients", rooms.Count);
        await Clients.All.SendAsync("ReceiveRoomListUpdate", rooms);
    }

    private async Task BroadcastRoomStateAsync(string roomId)
    {
        var state = _stateManager.GetRoomSnapshot(roomId);
        if (state != null)
            await Clients.Group(roomId).SendAsync("ReceiveFullState", state);
    }

    private void LogStateSnapshot(StateSnapshot state)
    {
        _logger.LogDebug("[STATE] Members: {Members}, Times: {Times}, States: {States}",
            string.Join(",", state.Members),
            string.Join(",", state.MemberTimes.Select(x => $"{x.Key}:{x.Value}")),
            string.Join(",", state.MemberStates.Select(x => $"{x.Key}:{x.Value}")));
    }

    #region Admin Operations

    public bool AdminAuthenticate(string adminKey)
    {
        if (adminKey != AdminKey)
        {
            _logger.LogWarning("[ADMIN_AUTH_FAILED] {ConnectionId} attempted admin auth with invalid key", ShortConnectionId);
            return false;
        }

        _logger.LogInformation("[ADMIN_AUTH] {ConnectionId} authenticated as admin", ShortConnectionId);
        return true;
    }

    public ServerStats AdminGetStats()
    {
        var stats = _adminManager.GetServerStats(ServerStartTime);
        _logger.LogInformation("[ADMIN_STATS] Rooms={Rooms}, Connections={Connections}", stats.TotalRooms, stats.TotalConnections);
        return stats;
    }

    public List<ClientConnectionInfo> AdminGetConnections()
    {
        var connections = _adminManager.GetAllConnections();
        _logger.LogInformation("[ADMIN_CONNECTIONS] Returning {Count} connections", connections.Count);
        return connections;
    }

    public List<DetailedRoomInfo> AdminGetDetailedRooms()
    {
        var rooms = _adminManager.GetDetailedRoomList();
        _logger.LogInformation("[ADMIN_ROOMS] Returning {Count} detailed rooms", rooms.Count);
        return rooms;
    }

    public async Task<bool> AdminForceDeleteRoom(string roomId)
    {
        var result = _adminManager.AdminDeleteRoom(roomId);
        if (result == null)
        {
            _logger.LogWarning("[ADMIN_DELETE_FAILED] Room {RoomId} not found", roomId);
            return false;
        }

        _logger.LogInformation("[ADMIN_DELETE_ROOM] {RoomId} force deleted, {Count} members ejected", roomId, result.ConnectionIds.Count);

        await Clients.Group(roomId).SendAsync("RoomDeleted");

        foreach (var connId in result.ConnectionIds)
        {
            await Groups.RemoveFromGroupAsync(connId, roomId);
            await Groups.AddToGroupAsync(connId, LobbyGroup);
        }

        await BroadcastRoomListToLobbyAsync();
        return true;
    }

    public async Task<bool> AdminKickUser(string connectionId)
    {
        if (!_adminManager.AdminKickConnection(connectionId))
        {
            _logger.LogWarning("[ADMIN_KICK_FAILED] Connection {ConnectionId} not found", connectionId[..8]);
            return false;
        }

        var username = _adminManager.GetConnectionUsername(connectionId);
        _logger.LogInformation("[ADMIN_KICK] Kicking connection {ConnectionId} ({Username})", connectionId[..8], username ?? "unknown");

        await Clients.Client(connectionId).SendAsync("AdminKicked", "You have been kicked by an administrator.");

        var result = _stateManager.RemoveConnection(connectionId);
        if (result.RoomId != null && !result.RoomDeleted)
            await BroadcastRoomStateAsync(result.RoomId);

        await BroadcastRoomListToLobbyAsync();
        return true;
    }

    public async Task AdminBroadcastMessage(string message)
    {
        _logger.LogInformation("[ADMIN_BROADCAST] {Message}", message);
        await Clients.All.SendAsync("AdminMessage", message);
    }

    public List<string> AdminGetBlacklist()
    {
        var blacklist = _adminManager.GetGlobalBlacklist();
        _logger.LogInformation("[ADMIN_BLACKLIST] Returning {Count} blacklisted users", blacklist.Count);
        return blacklist;
    }

    public bool AdminAddToBlacklist(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return false;

        var result = _adminManager.AddToGlobalBlacklist(username);
        _logger.LogInformation("[ADMIN_BLACKLIST_ADD] {Username} added={Result}", username, result);
        return result;
    }

    public bool AdminRemoveFromBlacklist(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return false;

        var result = _adminManager.RemoveFromGlobalBlacklist(username);
        _logger.LogInformation("[ADMIN_BLACKLIST_REMOVE] {Username} removed={Result}", username, result);
        return result;
    }

    #endregion
}
