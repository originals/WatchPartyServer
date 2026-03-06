using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using WatchPartyServer.Models;
using WatchPartyServer.Services;
using WatchPartyServer.Validation;

namespace WatchPartyServer.Hubs;

public class WatchPartyHub : Hub
{
    private const string LobbyGroup = "Lobby";
    private static readonly DateTime ServerStartTime = DateTime.UtcNow;
    private static readonly string? AdminKey = Environment.GetEnvironmentVariable("WATCHPARTY_ADMIN_KEY");

    private static readonly TimeSpan PlaybackUpdateMinInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan PlaybackBroadcastDebounceInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan MemberTimeMinInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MemberStateMinInterval = TimeSpan.FromMilliseconds(100);
    private static readonly ConcurrentDictionary<string, DateTime> LastPlaybackUpdate = new();
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> PendingPlaybackBroadcasts = new();
    private static readonly ConcurrentDictionary<string, DateTime> LastMemberTimeReport = new();
    private static readonly ConcurrentDictionary<string, DateTime> LastMemberStateReport = new();
    private static readonly ConcurrentDictionary<string, bool> AuthenticatedAdmins = new();
    private static readonly ConcurrentDictionary<string, (int Count, DateTime LastAttempt)> FailedAuthAttempts = new();

    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private readonly IRoomStateManager _stateManager;
    private readonly IAdminStateManager _adminManager;
    private readonly ILogger<WatchPartyHub> _logger;

    public WatchPartyHub(IRoomStateManager stateManager, IAdminStateManager adminManager, ILogger<WatchPartyHub> logger)
    {
        _stateManager = stateManager;
        _adminManager = adminManager;
        _logger = logger;
    }

    private const string ServerVersion = "2.0.0";

    private string ShortConnectionId => Context.ConnectionId[..8];

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("[CONNECT] {ConnectionId}", ShortConnectionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, LobbyGroup);
        await base.OnConnectedAsync();
        await BroadcastStatsToAdminsAsync();
    }

    public string GetVersion() => ServerVersion;

    public long Ping() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public List<RoomInfo> GetRooms() => _stateManager.GetRoomList();

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
        await Clients.Caller.SendAsync("ReceiveFullState", state);
        await BroadcastRoomListToLobbyAsync();
        await BroadcastAllAdminDataAsync();

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
        await Clients.Caller.SendAsync("ReceiveFullState", state);
        await Clients.OthersInGroup(roomId).SendAsync("ReceiveFullState", state);
        await BroadcastRoomListToLobbyAsync();
        await BroadcastAllAdminDataAsync();

        return _stateManager.GetRoomInfo(roomId)!;
    }

    public async Task LeaveRoom(string roomId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId);
        var result = _stateManager.RemoveConnection(Context.ConnectionId);

        await Groups.AddToGroupAsync(Context.ConnectionId, LobbyGroup);

        if (result.RoomDeleted)
            CancelPendingBroadcast(roomId);
        else if (result.RoomId != null)
            await BroadcastRoomStateAsync(result.RoomId);

        await BroadcastRoomListToLobbyAsync();
        await BroadcastAllAdminDataAsync();
    }

    public async Task DeleteRoom(string roomId)
    {
        var result = _stateManager.DeleteRoom(roomId, Context.ConnectionId);
        if (result == null)
            throw new HubException("Cannot delete room.");

        _logger.LogInformation("[DELETE_ROOM] {RoomId} deleted, {Count} members ejected", roomId, result.ConnectionIds.Count);
        CancelPendingBroadcast(roomId);

        await Clients.Group(roomId).SendAsync("RoomDeleted");

        foreach (var connId in result.ConnectionIds)
        {
            await Groups.RemoveFromGroupAsync(connId, roomId);
            await Groups.AddToGroupAsync(connId, LobbyGroup);
        }

        await BroadcastRoomListToLobbyAsync();
        await BroadcastAllAdminDataAsync();
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
        await BroadcastDetailedRoomsToAdminsAsync();
    }

    public async Task SetPlayState(string roomId, bool isPlaying)
    {
        if (!_stateManager.IsHost(roomId, Context.ConnectionId))
            return;

        var syncPayload = _stateManager.SetPlayState(roomId, isPlaying);
        if (syncPayload == null) return;

        if (PendingPlaybackBroadcasts.TryGetValue(roomId, out var existingCts))
        {
            existingCts.Cancel();
            existingCts.Dispose();
            PendingPlaybackBroadcasts.TryRemove(roomId, out _);
        }

        await Clients.OthersInGroup(roomId).SendAsync("ReceivePlayStateUpdate", syncPayload);
    }

    public async Task UpdatePlayback(string roomId, SyncPayload payload)
    {
        if (!_stateManager.IsHost(roomId, Context.ConnectionId))
            return;
        if (payload.Timestamp < 0) return;

        var now = DateTime.UtcNow;
        var key = Context.ConnectionId;
        if (LastPlaybackUpdate.TryGetValue(key, out var lastUpdate) && now - lastUpdate < PlaybackUpdateMinInterval)
            return;
        LastPlaybackUpdate[key] = now;

        var previousState = _stateManager.GetCurrentPlaybackState(roomId);
        var playStateChanged = previousState != null && previousState.IsPlaying != payload.IsPlaying;

        var syncPayload = _stateManager.UpdatePlayback(roomId, payload);
        if (syncPayload == null) return;

        if (PendingPlaybackBroadcasts.TryGetValue(roomId, out var existingCts))
        {
            existingCts.Cancel();
            existingCts.Dispose();
            PendingPlaybackBroadcasts.TryRemove(roomId, out _);
        }

        if (playStateChanged)
        {
            await Clients.OthersInGroup(roomId).SendAsync("ReceiveSyncUpdate", syncPayload);
        }
        else
        {
            var cts = new CancellationTokenSource();
            PendingPlaybackBroadcasts[roomId] = cts;
            _ = DebouncedBroadcastPlaybackAsync(roomId, cts);
        }
    }

    private async Task DebouncedBroadcastPlaybackAsync(string roomId, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(PlaybackBroadcastDebounceInterval, cts.Token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (cts.Token.IsCancellationRequested)
            return;

        if (PendingPlaybackBroadcasts.TryRemove(roomId, out var removedCts))
            removedCts.Dispose();

        var latestPayload = _stateManager.GetCurrentPlaybackState(roomId);
        if (latestPayload != null)
            await Clients.OthersInGroup(roomId).SendAsync("ReceiveSyncUpdate", latestPayload);
    }

    public async Task ReportPlaybackTime(string roomId, double currentTime)
    {
        if (currentTime < 0) return;

        var memberTimes = _stateManager.UpdateMemberTime(roomId, Context.ConnectionId, currentTime);
        if (memberTimes == null) return;

        var now = DateTime.UtcNow;
        var key = Context.ConnectionId;
        if (LastMemberTimeReport.TryGetValue(key, out var lastReport) && now - lastReport < MemberTimeMinInterval)
            return;
        LastMemberTimeReport[key] = now;

        await Clients.Group(roomId).SendAsync("ReceiveMemberTimes", memberTimes);
    }

    public async Task ReportMemberState(string roomId, MemberState state)
    {
        var result = _stateManager.UpdateMemberState(roomId, Context.ConnectionId, state);
        if (result == null) return;

        if (!result.StateChanged)
        {
            var now = DateTime.UtcNow;
            var key = Context.ConnectionId;
            if (LastMemberStateReport.TryGetValue(key, out var lastReport) && now - lastReport < MemberStateMinInterval)
                return;
            LastMemberStateReport[key] = now;
        }
        else
        {
            _logger.LogInformation("[STATE_CHANGE] {ConnectionId} in {RoomId}: {State}", ShortConnectionId, roomId, state);
            LastMemberStateReport[Context.ConnectionId] = DateTime.UtcNow;
        }

        await Clients.Group(roomId).SendAsync("ReceiveMemberStates", result.MemberStates);
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
        if (string.IsNullOrWhiteSpace(item.VideoId))
            throw new HubException("Video ID is required.");

        var result = _stateManager.EnqueueVideo(roomId, item);
        if (result == null)
        {
            throw new HubException("Queue limit reached. Wait for a video to be played before adding more.");
        }

        await Clients.Group(roomId).SendAsync("ReceiveQueueUpdate", result.Queue);
    }

    public async Task UpdateMaxQueuePerUser(string roomId, int maxQueuePerUser)
    {
        if (!_stateManager.IsHost(roomId, Context.ConnectionId))
            throw new HubException("Only the host can change queue settings.");

        if (maxQueuePerUser < 0 || maxQueuePerUser > 100)
            throw new HubException("Max queue per user must be between 0 and 100.");

        if (!_stateManager.UpdateMaxQueuePerUser(roomId, Context.ConnectionId, maxQueuePerUser))
            throw new HubException("Failed to update queue settings.");

        await BroadcastRoomStateAsync(roomId);
    }

    public async Task PlayNextInQueue(string roomId)
    {
        if (!_stateManager.IsHost(roomId, Context.ConnectionId)) return;

        var result = _stateManager.SkipToNext(roomId);
        if (result == null) return;

        if (result.VideoId != null)
        {
            if (PendingPlaybackBroadcasts.TryGetValue(roomId, out var existingCts))
            {
                existingCts.Cancel();
                existingCts.Dispose();
                PendingPlaybackBroadcasts.TryRemove(roomId, out _);
            }

            await Clients.Group(roomId).SendAsync("ReceiveQueueUpdate", result.Queue);
            await Clients.Group(roomId).SendAsync("ForceLoadVideo", result.VideoId);
        }
        else
        {
            await Clients.Group(roomId).SendAsync("ReceiveQueueUpdate", result.Queue);
        }
    }

    public async Task RemoveFromQueue(string roomId, int index)
    {
        if (!_stateManager.IsHost(roomId, Context.ConnectionId)) return;

        var queue = _stateManager.RemoveFromQueue(roomId, index);
        if (queue == null) return;

        await Clients.Group(roomId).SendAsync("ReceiveQueueUpdate", queue);
    }

    public async Task ReorderQueue(string roomId, int fromIndex, int toIndex)
    {
        if (!_stateManager.IsHost(roomId, Context.ConnectionId)) return;

        var queue = _stateManager.ReorderQueue(roomId, fromIndex, toIndex);
        if (queue == null) return;

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
        await BroadcastAllAdminDataAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("[DISCONNECT] {ConnectionId} {Reason}", 
            ShortConnectionId, exception?.Message ?? "clean");

        LastPlaybackUpdate.TryRemove(Context.ConnectionId, out _);
        LastMemberTimeReport.TryRemove(Context.ConnectionId, out _);
        LastMemberStateReport.TryRemove(Context.ConnectionId, out _);
        AuthenticatedAdmins.TryRemove(Context.ConnectionId, out _);
        FailedAuthAttempts.TryRemove(Context.ConnectionId, out _);

        var result = _stateManager.RemoveConnection(Context.ConnectionId);

        if (result.RoomId != null)
        {
            if (result.RoomDeleted)
            {
                _logger.LogInformation("[ROOM_AUTO_DELETE] {RoomId} (last member left)", result.RoomId);
                CancelPendingBroadcast(result.RoomId);
            }
            else
            {
                if (result.NewHostConnectionId != null)
                    _logger.LogInformation("[HOST_TRANSFER] Room {RoomId}: new host assigned", result.RoomId);

                await BroadcastRoomStateAsync(result.RoomId);
            }

            await BroadcastRoomListToLobbyAsync();
        }

        await BroadcastStatsToAdminsAsync();
        await base.OnDisconnectedAsync(exception);
    }

    private static void CancelPendingBroadcast(string roomId)
    {
        if (PendingPlaybackBroadcasts.TryRemove(roomId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private async Task BroadcastRoomListToLobbyAsync()
    {
        var rooms = _stateManager.GetRoomList();
        await Clients.All.SendAsync("ReceiveRoomListUpdate", rooms);
    }

    private async Task BroadcastRoomStateAsync(string roomId)
    {
        var state = _stateManager.GetRoomSnapshot(roomId);
        if (state != null)
            await Clients.Group(roomId).SendAsync("ReceiveFullState", state);
    }

    #region Admin Broadcast Helpers

    private async Task BroadcastToAdminsAsync(string method, object data)
    {
        var adminConnectionIds = AuthenticatedAdmins.Keys.ToList();
        if (adminConnectionIds.Count == 0) return;
        await Clients.Clients(adminConnectionIds).SendAsync(method, data);
    }

    private async Task BroadcastStatsToAdminsAsync()
    {
        var stats = _adminManager.GetServerStats(ServerStartTime);
        await BroadcastToAdminsAsync("AdminReceiveStats", stats);
    }

    private async Task BroadcastConnectionsToAdminsAsync()
    {
        var connections = _adminManager.GetAllConnections();
        await BroadcastToAdminsAsync("AdminReceiveConnections", connections);
    }

    private async Task BroadcastDetailedRoomsToAdminsAsync()
    {
        var rooms = _adminManager.GetDetailedRoomList();
        await BroadcastToAdminsAsync("AdminReceiveDetailedRooms", rooms);
    }

    private async Task BroadcastAllRoomStatesToAdminsAsync()
    {
        var roomList = _stateManager.GetRoomList();
        var roomStates = new Dictionary<string, StateSnapshot>();
        foreach (var room in roomList)
        {
            var snapshot = _stateManager.GetRoomSnapshot(room.RoomId);
            if (snapshot != null)
                roomStates[room.RoomId] = snapshot;
        }
        await BroadcastToAdminsAsync("AdminReceiveAllRoomStates", roomStates);
    }

    private async Task BroadcastBlacklistToAdminsAsync()
    {
        var blacklist = _adminManager.GetGlobalBlacklist();
        await BroadcastToAdminsAsync("AdminReceiveBlacklist", blacklist);
    }

    private async Task BroadcastAllAdminDataAsync()
    {
        await BroadcastStatsToAdminsAsync();
        await BroadcastConnectionsToAdminsAsync();
        await BroadcastDetailedRoomsToAdminsAsync();
        await BroadcastAllRoomStatesToAdminsAsync();
    }

    #endregion

    #region Admin Operations

    private bool IsAdminAuthenticated => AuthenticatedAdmins.ContainsKey(Context.ConnectionId);

    private void RequireAdmin()
    {
        if (!IsAdminAuthenticated)
            throw new HubException("Admin authentication required.");
    }

    private static bool SecureCompare(string a, string b)
    {
        if (a == null || b == null) return false;
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }

    private bool IsLockedOut(string connectionId)
    {
        if (!FailedAuthAttempts.TryGetValue(connectionId, out var attempt))
            return false;

        if (attempt.Count >= MaxFailedAttempts && DateTime.UtcNow - attempt.LastAttempt < LockoutDuration)
            return true;

        if (DateTime.UtcNow - attempt.LastAttempt >= LockoutDuration)
            FailedAuthAttempts.TryRemove(connectionId, out _);

        return false;
    }

    private void RecordFailedAttempt(string connectionId)
    {
        FailedAuthAttempts.AddOrUpdate(
            connectionId,
            _ => (1, DateTime.UtcNow),
            (_, existing) => (existing.Count + 1, DateTime.UtcNow));
    }

    public async Task<bool> AdminAuthenticate(string adminKey)
    {
        if (string.IsNullOrEmpty(AdminKey))
        {
            _logger.LogError("[ADMIN_AUTH_DISABLED] Admin key not configured - admin access disabled");
            throw new HubException("Admin access is not configured on this server.");
        }

        if (IsLockedOut(Context.ConnectionId))
        {
            _logger.LogWarning("[ADMIN_AUTH_LOCKOUT] {ConnectionId} is locked out due to too many failed attempts", ShortConnectionId);
            throw new HubException("Too many failed attempts. Please try again later.");
        }

        if (!SecureCompare(adminKey, AdminKey))
        {
            RecordFailedAttempt(Context.ConnectionId);
            var attempts = FailedAuthAttempts.TryGetValue(Context.ConnectionId, out var a) ? a.Count : 1;
            _logger.LogWarning("[ADMIN_AUTH_FAILED] {ConnectionId} failed auth attempt {Attempt}/{Max}", 
                ShortConnectionId, attempts, MaxFailedAttempts);
            AuthenticatedAdmins.TryRemove(Context.ConnectionId, out _);
            return false;
        }

        FailedAuthAttempts.TryRemove(Context.ConnectionId, out _);
        AuthenticatedAdmins[Context.ConnectionId] = true;
        _logger.LogInformation("[ADMIN_AUTH] {ConnectionId} authenticated as admin", ShortConnectionId);

        await Clients.Caller.SendAsync("AdminReceiveStats", _adminManager.GetServerStats(ServerStartTime));
        await Clients.Caller.SendAsync("AdminReceiveConnections", _adminManager.GetAllConnections());
        await Clients.Caller.SendAsync("AdminReceiveDetailedRooms", _adminManager.GetDetailedRoomList());
        await Clients.Caller.SendAsync("AdminReceiveBlacklist", _adminManager.GetGlobalBlacklist());

        var roomList = _stateManager.GetRoomList();
        var roomStates = new Dictionary<string, StateSnapshot>();
        foreach (var room in roomList)
        {
            var snapshot = _stateManager.GetRoomSnapshot(room.RoomId);
            if (snapshot != null)
                roomStates[room.RoomId] = snapshot;
        }
        await Clients.Caller.SendAsync("AdminReceiveAllRoomStates", roomStates);

        return true;
    }

    public ServerStats AdminGetStats()
    {
        RequireAdmin();
        return _adminManager.GetServerStats(ServerStartTime);
    }

    public List<ClientConnectionInfo> AdminGetConnections()
    {
        RequireAdmin();
        return _adminManager.GetAllConnections();
    }

    public List<DetailedRoomInfo> AdminGetDetailedRooms()
    {
        RequireAdmin();
        return _adminManager.GetDetailedRoomList();
    }

    public async Task<bool> AdminForceDeleteRoom(string roomId)
    {
        RequireAdmin();
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
        await BroadcastAllAdminDataAsync();
        return true;
    }

    public async Task<bool> AdminKickUser(string connectionId)
    {
        RequireAdmin();
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
        await BroadcastAllAdminDataAsync();
        return true;
    }

    public async Task AdminBroadcastMessage(string message)
    {
        RequireAdmin();
        _logger.LogInformation("[ADMIN_BROADCAST] {Message}", message);
        await Clients.All.SendAsync("AdminMessage", message);
    }

    public List<string> AdminGetBlacklist()
    {
        RequireAdmin();
        return _adminManager.GetGlobalBlacklist();
    }

    public async Task<bool> AdminAddToBlacklist(string username)
    {
        RequireAdmin();
        if (string.IsNullOrWhiteSpace(username))
            return false;

        var result = _adminManager.AddToGlobalBlacklist(username);
        _logger.LogInformation("[ADMIN_BLACKLIST_ADD] {Username} added={Result}", username, result);

        if (result)
            await BroadcastBlacklistToAdminsAsync();

        return result;
    }

    public async Task<bool> AdminRemoveFromBlacklist(string username)
    {
        RequireAdmin();
        if (string.IsNullOrWhiteSpace(username))
            return false;

        var result = _adminManager.RemoveFromGlobalBlacklist(username);
        _logger.LogInformation("[ADMIN_BLACKLIST_REMOVE] {Username} removed={Result}", username, result);

        if (result)
            await BroadcastBlacklistToAdminsAsync();

        return result;
    }

    public StateSnapshot? AdminGetRoomState(string roomId)
    {
        RequireAdmin();
        return _stateManager.GetRoomSnapshot(roomId);
    }

    #endregion
}
