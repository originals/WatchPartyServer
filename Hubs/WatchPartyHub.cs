using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using WatchPartyServer.Models;
using WatchPartyServer.Services;
using WatchPartyServer.Validation;

namespace WatchPartyServer.Hubs;

public partial class WatchPartyHub : Hub
{
    private const string LobbyGroup = "Lobby";
    private const string AdminGroup = "Admins";
    private const string ServerVersion = "2.0.0";

    private static readonly DateTime ServerStartTime = DateTime.UtcNow;
    private static readonly string? AdminKey = Environment.GetEnvironmentVariable("WATCHPARTY_ADMIN_KEY");
    private static readonly ConcurrentDictionary<string, DateTime> LastPlaybackUpdate = new();
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> PendingPlaybackBroadcasts = new();
    private static readonly ConcurrentDictionary<string, DateTime> LastMemberTimeReport = new();
    private static readonly ConcurrentDictionary<string, DateTime> LastMemberStateReport = new();
    private static readonly ConcurrentDictionary<string, bool> AuthenticatedAdmins = new();
    private static readonly ConcurrentDictionary<string, (int Count, DateTime LastAttempt)> FailedAuthAttempts = new();

    private readonly IRoomStateManager _stateManager;
    private readonly IAdminStateManager _adminManager;
    private readonly ILogService _logService;
    private readonly ILogger<WatchPartyHub> _logger;

    public WatchPartyHub(
        IRoomStateManager stateManager, 
        IAdminStateManager adminManager, 
        ILogService logService,
        ILogger<WatchPartyHub> logger)
    {
        _stateManager = stateManager;
        _adminManager = adminManager;
        _logService = logService;
        _logger = logger;
    }

    private string ShortConnectionId => Context.ConnectionId[..8];

    private void LogEvent(string level, string message, string? roomId = null)
    {
        _logService.Log(level, message, ShortConnectionId, roomId);
    }

    #region Connection Lifecycle

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("[CONNECT] {ConnectionId}", ShortConnectionId);
        LogEvent("INFO", "Connected");
        _adminManager.TrackConnection(Context.ConnectionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, LobbyGroup);
        await base.OnConnectedAsync();
        await BroadcastStatsToAdminsAsync();
        await BroadcastConnectionsToAdminsAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("[DISCONNECT] {ConnectionId} {Reason}", 
            ShortConnectionId, exception?.Message ?? "clean");
        LogEvent("INFO", $"Disconnected: {exception?.Message ?? "clean"}");

        LastPlaybackUpdate.TryRemove(Context.ConnectionId, out _);
        LastMemberTimeReport.TryRemove(Context.ConnectionId, out _);
        LastMemberStateReport.TryRemove(Context.ConnectionId, out _);
        AuthenticatedAdmins.TryRemove(Context.ConnectionId, out _);
        FailedAuthAttempts.TryRemove(Context.ConnectionId, out _);
        _adminManager.UntrackConnection(Context.ConnectionId);

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
        await BroadcastConnectionsToAdminsAsync();
        await base.OnDisconnectedAsync(exception);
    }

    #endregion

    #region Public API

    public string GetVersion() => ServerVersion;

    public long Ping() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public List<RoomInfo> GetRooms() => _stateManager.GetRoomList();

    #endregion

    #region Room Operations

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
        LogEvent("INFO", $"Created room '{roomName}' (private={isPrivate})", room.RoomId);

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
            LogEvent("WARN", $"Failed to join room", roomId);
            throw new HubException("Failed to join room. Check the room ID and password.");
        }

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, LobbyGroup);
        await Groups.AddToGroupAsync(Context.ConnectionId, roomId);

        _logger.LogInformation("[JOIN_ROOM] {Username} joined {RoomId}", username, roomId);
        LogEvent("INFO", $"Joined room as '{username}'", roomId);

        var state = _stateManager.GetRoomSnapshot(roomId)!;
        await Clients.Caller.SendAsync("ReceiveFullState", state);
        await Clients.OthersInGroup(roomId).SendAsync("ReceiveFullState", state);
        await BroadcastRoomListToLobbyAsync();
        await BroadcastAllAdminDataAsync();

        return _stateManager.GetRoomInfo(roomId)!;
    }

    public async Task LeaveRoom(string roomId)
    {
        LogEvent("INFO", "Left room", roomId);
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
        LogEvent("INFO", $"Deleted room, {result.ConnectionIds.Count} members ejected", roomId);
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

    #endregion

    #region Playback Sync

    private static readonly TimeSpan PlaybackUpdateMinInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan PlaybackBroadcastDebounceInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan MemberTimeMinInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MemberStateMinInterval = TimeSpan.FromMilliseconds(100);

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
        await BroadcastPlaybackStateToAdminsAsync(roomId, syncPayload);
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
            await BroadcastPlaybackStateToAdminsAsync(roomId, syncPayload);
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
        await BroadcastMemberTimesToAdminsAsync(roomId, memberTimes);
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
        await BroadcastMemberStatesToAdminsAsync(roomId, result.MemberStates);
    }

    public async Task RequestState(string roomId)
    {
        var state = _stateManager.GetRoomSnapshot(roomId);
        if (state != null)
            await Clients.Caller.SendAsync("ReceiveFullState", state);
    }

    #endregion

    #region Queue Operations

    public async Task EnqueueVideo(string roomId, QueueItem item)
    {
        if (!_stateManager.IsMember(roomId, Context.ConnectionId)) return;
        if (string.IsNullOrWhiteSpace(item.VideoId))
            throw new HubException("Video ID is required.");

        var result = _stateManager.EnqueueVideo(roomId, item);
        if (result == null)
            throw new HubException("Queue limit reached. Wait for a video to be played before adding more.");

        await Clients.Group(roomId).SendAsync("ReceiveQueueUpdate", result.Queue);
        await BroadcastQueueUpdateToAdminsAsync(roomId, result.Queue);
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
        await BroadcastQueueUpdateToAdminsAsync(roomId, result.Queue);
    }

    public async Task RemoveFromQueue(string roomId, int index)
    {
        if (!_stateManager.IsHost(roomId, Context.ConnectionId)) return;

        var queue = _stateManager.RemoveFromQueue(roomId, index);
        if (queue == null) return;

        await Clients.Group(roomId).SendAsync("ReceiveQueueUpdate", queue);
        await BroadcastQueueUpdateToAdminsAsync(roomId, queue);
    }

    public async Task ReorderQueue(string roomId, int fromIndex, int toIndex)
    {
        if (!_stateManager.IsHost(roomId, Context.ConnectionId)) return;

        var queue = _stateManager.ReorderQueue(roomId, fromIndex, toIndex);
        if (queue == null) return;

        await Clients.Group(roomId).SendAsync("ReceiveQueueUpdate", queue);
        await BroadcastQueueUpdateToAdminsAsync(roomId, queue);
    }

    #endregion

    #region Broadcast Helpers

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

    private async Task BroadcastToAdminsAsync(string method, object data)
    {
        await Clients.Group(AdminGroup).SendAsync(method, data);
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

    private async Task BroadcastMemberStatesToAdminsAsync(string roomId, Dictionary<string, MemberState> memberStates)
    {
        var snapshot = _stateManager.GetRoomSnapshot(roomId);
        if (snapshot == null) return;
        await BroadcastToAdminsAsync("AdminReceiveRoomMemberStates", new { 
            RoomId = roomId, 
            Members = snapshot.Members,
            MemberStates = memberStates 
        });
    }

    private async Task BroadcastMemberTimesToAdminsAsync(string roomId, Dictionary<string, double> memberTimes)
    {
        var snapshot = _stateManager.GetRoomSnapshot(roomId);
        if (snapshot == null) return;
        await BroadcastToAdminsAsync("AdminReceiveRoomMemberTimes", new { 
            RoomId = roomId, 
            Members = snapshot.Members,
            MemberTimes = memberTimes 
        });
    }

    private async Task BroadcastQueueUpdateToAdminsAsync(string roomId, List<QueueItem> queue)
    {
        await BroadcastToAdminsAsync("AdminReceiveQueueUpdate", new { RoomId = roomId, Queue = queue });
    }

    private async Task BroadcastPlaybackStateToAdminsAsync(string roomId, SyncPayload syncPayload)
    {
        await BroadcastToAdminsAsync("AdminReceivePlaybackState", new { 
            RoomId = roomId, 
            Timestamp = syncPayload.Timestamp,
            IsPlaying = syncPayload.IsPlaying,
            SequenceNumber = syncPayload.SequenceNumber
        });
    }

    private async Task BroadcastLogToAdminsAsync(LogEntry entry)
    {
        await BroadcastToAdminsAsync("AdminReceiveLogEntry", entry);
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

    public async Task<bool> AdminAuthenticate(string adminKey)
    {
        if (string.IsNullOrEmpty(AdminKey))
        {
            _logger.LogError("[ADMIN_AUTH_DISABLED] Admin key not configured");
            throw new HubException("Admin access is not configured on this server.");
        }

        if (!string.Equals(adminKey, AdminKey, StringComparison.Ordinal))
        {
            _logger.LogWarning("[ADMIN_AUTH_FAILED] {ConnectionId}", ShortConnectionId);
            LogEvent("WARN", "Admin authentication failed");
            AuthenticatedAdmins.TryRemove(Context.ConnectionId, out _);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, AdminGroup);
            return false;
        }

        AuthenticatedAdmins[Context.ConnectionId] = true;
        await Groups.AddToGroupAsync(Context.ConnectionId, AdminGroup);
        _logger.LogInformation("[ADMIN_AUTH] {ConnectionId} authenticated as admin", ShortConnectionId);
        LogEvent("INFO", "Admin authenticated");

        await Clients.Caller.SendAsync("AdminReceiveStats", _adminManager.GetServerStats(ServerStartTime));
        await Clients.Caller.SendAsync("AdminReceiveConnections", _adminManager.GetAllConnections());
        await Clients.Caller.SendAsync("AdminReceiveDetailedRooms", _adminManager.GetDetailedRoomList());
        await Clients.Caller.SendAsync("AdminReceiveBlacklist", _adminManager.GetGlobalBlacklist());
        await Clients.Caller.SendAsync("AdminReceiveMemberStates", _adminManager.GetAllRoomMemberStates());
        await Clients.Caller.SendAsync("AdminReceiveLogs", _logService.GetLogs(100));

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

    public async Task AdminRequestStats()
    {
        RequireAdmin();
        await Clients.Caller.SendAsync("AdminReceiveStats", _adminManager.GetServerStats(ServerStartTime));
    }

    public async Task AdminRequestConnections()
    {
        RequireAdmin();
        await Clients.Caller.SendAsync("AdminReceiveConnections", _adminManager.GetAllConnections());
    }

    public async Task AdminRequestDetailedRooms()
    {
        RequireAdmin();
        await Clients.Caller.SendAsync("AdminReceiveDetailedRooms", _adminManager.GetDetailedRoomList());
    }

    public async Task AdminRequestAllMemberStates()
    {
        RequireAdmin();
        await Clients.Caller.SendAsync("AdminReceiveMemberStates", _adminManager.GetAllRoomMemberStates());
    }

    public async Task AdminRequestLogs(int count = 100, string? levelFilter = null, string? roomIdFilter = null)
    {
        RequireAdmin();
        await Clients.Caller.SendAsync("AdminReceiveLogs", _logService.GetLogs(count, levelFilter, roomIdFilter));
    }

    public async Task AdminClearLogs()
    {
        RequireAdmin();
        _logService.Clear();
        LogEvent("INFO", "Admin cleared logs");
        await Clients.Caller.SendAsync("AdminLogsCleared");
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
        LogEvent("WARN", $"Admin force deleted room, {result.ConnectionIds.Count} members ejected", roomId);

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
        LogEvent("WARN", $"Admin kicked user '{username ?? "unknown"}'");

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
        LogEvent("INFO", $"Admin broadcast: {message}");
        await Clients.All.SendAsync("AdminMessage", message);
    }

    public async Task AdminRequestBlacklist()
    {
        RequireAdmin();
        await Clients.Caller.SendAsync("AdminReceiveBlacklist", _adminManager.GetGlobalBlacklist());
    }

    public async Task<bool> AdminAddToBlacklist(string username)
    {
        RequireAdmin();
        if (string.IsNullOrWhiteSpace(username))
            return false;

        var result = _adminManager.AddToGlobalBlacklist(username);
        _logger.LogInformation("[ADMIN_BLACKLIST_ADD] {Username} added={Result}", username, result);
        LogEvent("WARN", $"Admin added '{username}' to blacklist");

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
        LogEvent("INFO", $"Admin removed '{username}' from blacklist");

        if (result)
            await BroadcastBlacklistToAdminsAsync();

        return result;
    }

    public async Task AdminRequestRoomState(string roomId)
    {
        RequireAdmin();
        var snapshot = _stateManager.GetRoomSnapshot(roomId);
        await Clients.Caller.SendAsync("AdminReceiveRoomState", roomId, snapshot);
    }

    #endregion
}
