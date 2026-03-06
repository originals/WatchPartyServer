using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using WatchPartyServer.Hubs;
using WatchPartyServer.Models;

namespace WatchPartyServer.Services;

public class StateBroadcastService : BackgroundService
{
    private static readonly TimeSpan BroadcastInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FullStateBroadcastInterval = TimeSpan.FromSeconds(10);

    private readonly IRoomStateManager _stateManager;
    private readonly IHubContext<WatchPartyHub> _hubContext;
    private readonly ILogger<StateBroadcastService> _logger;
    private readonly ConcurrentDictionary<string, BroadcastState> _lastBroadcastState = new();

    private class BroadcastState
    {
        public long SequenceNumber { get; set; }
        public int MemberCount { get; set; }
        public string CurrentVideoId { get; set; } = string.Empty;
        public DateTime LastFullBroadcast { get; set; }
    }

    public StateBroadcastService(
        IRoomStateManager stateManager,
        IHubContext<WatchPartyHub> hubContext,
        ILogger<StateBroadcastService> logger)
    {
        _stateManager = stateManager;
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("State broadcast service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await BroadcastAllRoomStatesAsync(stoppingToken);
                await Task.Delay(BroadcastInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting room states");
                await Task.Delay(BroadcastInterval, stoppingToken);
            }
        }

        _logger.LogInformation("State broadcast service stopped");
    }

    private async Task BroadcastAllRoomStatesAsync(CancellationToken cancellationToken)
    {
        var rooms = _stateManager.GetRoomList();
        if (rooms.Count == 0) return;

        var activeRoomIds = rooms.Select(r => r.RoomId).ToHashSet();
        foreach (var roomId in _lastBroadcastState.Keys.ToList())
        {
            if (!activeRoomIds.Contains(roomId))
                _lastBroadcastState.TryRemove(roomId, out _);
        }

        var tasks = rooms
            .Select(room => BroadcastRoomStateAsync(room.RoomId, cancellationToken))
            .ToArray();

        await Task.WhenAll(tasks);
    }

    private async Task BroadcastRoomStateAsync(string roomId, CancellationToken cancellationToken)
    {
        var state = _stateManager.GetRoomSnapshot(roomId);
        if (state == null) return;

        var now = DateTime.UtcNow;
        var lastState = _lastBroadcastState.GetOrAdd(roomId, _ => new BroadcastState());

        bool needsFullBroadcast = NeedsFullBroadcast(state, lastState, now);

        if (needsFullBroadcast)
        {
            await _hubContext.Clients.Group(roomId).SendAsync("ReceiveFullState", state, cancellationToken);
            lastState.LastFullBroadcast = now;
        }
        else if (state.IsPlaying && !string.IsNullOrEmpty(state.CurrentVideoId))
        {
            var syncPayload = new SyncPayload
            {
                Timestamp = state.CurrentTime,
                IsPlaying = state.IsPlaying,
                SequenceNumber = state.SequenceNumber
            };
            await _hubContext.Clients.Group(roomId).SendAsync("ReceiveSyncUpdate", syncPayload, cancellationToken);
        }

        lastState.SequenceNumber = state.SequenceNumber;
        lastState.MemberCount = state.Members.Count;
        lastState.CurrentVideoId = state.CurrentVideoId;
    }

    private bool NeedsFullBroadcast(StateSnapshot state, BroadcastState lastState, DateTime now)
    {
        if ((now - lastState.LastFullBroadcast) >= FullStateBroadcastInterval)
            return true;

        if (state.Members.Count != lastState.MemberCount)
            return true;

        if (state.CurrentVideoId != lastState.CurrentVideoId)
            return true;

        return false;
    }
}
