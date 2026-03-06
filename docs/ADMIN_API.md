# WatchPartyServer Admin API Documentation

## Overview

The WatchPartyServer provides a comprehensive admin API for monitoring and managing the server in real-time. Admins receive full state on authentication and continuous updates via SignalR.

## Authentication

### Environment Setup
Set the `WATCHPARTY_ADMIN_KEY` environment variable on the server:
```bash
WATCHPARTY_ADMIN_KEY=your-secure-admin-key
```

### Client Authentication
```csharp
bool success = await hubConnection.InvokeAsync<bool>("AdminAuthenticate", "your-admin-key");
```

On successful authentication:
- Connection is added to the `Admins` SignalR group
- Full state is sent immediately (stats, connections, rooms, blacklist, member states, logs, room states)

---

## Data Models

### ServerStats
```csharp
public record ServerStats
{
    public int TotalRooms { get; init; }
    public int TotalConnections { get; init; }
    public int LobbyConnections { get; init; }      // Users not in any room
    public int RoomConnections { get; init; }       // Users in rooms
    public int PublicRooms { get; init; }
    public int PrivateRooms { get; init; }
    public DateTime ServerStartTime { get; init; }
    public TimeSpan Uptime { get; init; }
}
```

### ClientConnectionInfo
```csharp
public record ClientConnectionInfo
{
    public string ConnectionId { get; init; }
    public string ShortId { get; init; }           // First 8 chars
    public string? Username { get; init; }
    public string? RoomId { get; init; }
    public bool IsInLobby { get; init; }
    public DateTime ConnectedAt { get; init; }
}
```

### DetailedRoomInfo
```csharp
public record DetailedRoomInfo
{
    public string RoomId { get; init; }
    public string RoomName { get; init; }
    public string HostUsername { get; init; }
    public string HostConnectionId { get; init; }
    public bool IsPrivate { get; init; }
    public int MemberCount { get; init; }
    public int ConnectionCount { get; init; }
    public List<string> Members { get; init; }
    public List<string> BannedUsers { get; init; }
    public int QueueLength { get; init; }
    public string CurrentVideoId { get; init; }
    public bool IsPlaying { get; init; }
    public DateTime CreatedAt { get; init; }
}
```

### RoomMemberStates
```csharp
public record RoomMemberStates
{
    public string RoomId { get; init; }
    public string RoomName { get; init; }
    public Dictionary<string, MemberState> MemberStates { get; init; }
    public Dictionary<string, double> MemberTimes { get; init; }
}
```

### LogEntry
```csharp
public record LogEntry
{
    public DateTime Timestamp { get; init; }
    public string Level { get; init; }             // INFO, WARN, ERROR
    public string Message { get; init; }
    public string? ConnectionId { get; init; }
    public string? RoomId { get; init; }
}
```

---

## Real-Time Events (Server → Admin)

Register these handlers before authenticating:

### State Updates
| Event | Payload | Triggered When |
|-------|---------|----------------|
| `AdminReceiveStats` | `ServerStats` | Connect/disconnect, room changes |
| `AdminReceiveConnections` | `List<ClientConnectionInfo>` | Connect/disconnect |
| `AdminReceiveDetailedRooms` | `List<DetailedRoomInfo>` | Room create/join/leave/delete/update |
| `AdminReceiveAllRoomStates` | `Dictionary<string, StateSnapshot>` | Room changes |
| `AdminReceiveBlacklist` | `List<string>` | Blacklist add/remove |
| `AdminReceiveMemberStates` | `List<RoomMemberStates>` | On authentication |

### Live Monitoring
| Event | Payload | Triggered When |
|-------|---------|----------------|
| `AdminReceiveRoomMemberStates` | `{ RoomId, MemberStates }` | Member state changes |
| `AdminReceiveRoomMemberTimes` | `{ RoomId, MemberTimes }` | Playback time updates |
| `AdminReceiveQueueUpdate` | `{ RoomId, Queue }` | Queue changes |
| `AdminReceiveLogEntry` | `LogEntry` | New log entry |

### Single Room State
| Event | Payload | Triggered When |
|-------|---------|----------------|
| `AdminReceiveRoomState` | `(roomId, StateSnapshot)` | `AdminRequestRoomState` called |

---

## Admin Methods (Admin → Server)

All methods require prior authentication.

### Data Requests
```csharp
// Request current stats
await hubConnection.InvokeAsync("AdminRequestStats");

// Request all connections
await hubConnection.InvokeAsync("AdminRequestConnections");

// Request detailed room list
await hubConnection.InvokeAsync("AdminRequestDetailedRooms");

// Request all member states
await hubConnection.InvokeAsync("AdminRequestAllMemberStates");

// Request specific room state
await hubConnection.InvokeAsync("AdminRequestRoomState", roomId);

// Request logs (count, levelFilter, roomIdFilter)
await hubConnection.InvokeAsync("AdminRequestLogs", 100, "WARN", null);

// Request blacklist
await hubConnection.InvokeAsync("AdminRequestBlacklist");
```

### Actions
```csharp
// Force delete any room (returns bool)
bool success = await hubConnection.InvokeAsync<bool>("AdminForceDeleteRoom", roomId);

// Kick any user (returns bool)
bool success = await hubConnection.InvokeAsync<bool>("AdminKickUser", connectionId);

// Broadcast message to all users
await hubConnection.InvokeAsync("AdminBroadcastMessage", "Server maintenance in 5 minutes");

// Clear logs
await hubConnection.InvokeAsync("AdminClearLogs");
```

### Blacklist Management
```csharp
// Add user to global blacklist (returns bool)
bool success = await hubConnection.InvokeAsync<bool>("AdminAddToBlacklist", username);

// Remove user from blacklist (returns bool)
bool success = await hubConnection.InvokeAsync<bool>("AdminRemoveFromBlacklist", username);
```

---

## Client Implementation Example

```csharp
public class AdminClient
{
    private HubConnection _connection;

    public ServerStats? Stats { get; private set; }
    public List<ClientConnectionInfo> Connections { get; } = new();
    public List<DetailedRoomInfo> Rooms { get; } = new();
    public List<string> Blacklist { get; } = new();
    public List<LogEntry> Logs { get; } = new();
    public Dictionary<string, StateSnapshot> RoomStates { get; } = new();

    public async Task ConnectAsync(string serverUrl, string adminKey)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(serverUrl)
            .WithAutomaticReconnect()
            .Build();

        RegisterHandlers();
        await _connection.StartAsync();

        bool authenticated = await _connection.InvokeAsync<bool>("AdminAuthenticate", adminKey);
        if (!authenticated)
            throw new Exception("Admin authentication failed");

        // Data is automatically sent upon successful authentication
        // No need to call refresh methods - handlers will receive data
    }

    private void RegisterHandlers()
    {
        // Stats - automatically updated on connect/disconnect and room changes
        _connection.On<ServerStats>("AdminReceiveStats", stats => 
        {
            Stats = stats;
            // Update UI: stats.TotalConnections, stats.LobbyConnections, stats.RoomConnections, etc.
        });

        // Connections - automatically updated on connect/disconnect
        _connection.On<List<ClientConnectionInfo>>("AdminReceiveConnections", conns =>
        {
            Connections.Clear();
            Connections.AddRange(conns);
            // Update UI with connection list
        });

        // Rooms - automatically updated on room changes
        _connection.On<List<DetailedRoomInfo>>("AdminReceiveDetailedRooms", rooms =>
        {
            Rooms.Clear();
            Rooms.AddRange(rooms);
            // Update UI with room list
        });

        // Blacklist - automatically updated on blacklist changes
        _connection.On<List<string>>("AdminReceiveBlacklist", list =>
        {
            Blacklist.Clear();
            Blacklist.AddRange(list);
            // Update UI with blacklist
        });

        // Room states - automatically updated on room changes
        _connection.On<Dictionary<string, StateSnapshot>>("AdminReceiveAllRoomStates", states =>
        {
            RoomStates.Clear();
            foreach (var kvp in states)
                RoomStates[kvp.Key] = kvp.Value;
            // Update UI with room states
        });

        // Logs - sent on authentication
        _connection.On<List<LogEntry>>("AdminReceiveLogs", logs =>
        {
            Logs.Clear();
            Logs.AddRange(logs);
        });

        // Real-time log streaming
        _connection.On<LogEntry>("AdminReceiveLogEntry", entry => Logs.Insert(0, entry));

        // Live room monitoring - real-time member state changes
        _connection.On("AdminReceiveRoomMemberStates", (object data) =>
        {
            // data contains: { RoomId: string, MemberStates: Dictionary<string, MemberState> }
            // Update UI with member states for specific room
        });

        // Live room monitoring - real-time playback position updates
        _connection.On("AdminReceiveRoomMemberTimes", (object data) =>
        {
            // data contains: { RoomId: string, MemberTimes: Dictionary<string, double> }
            // Update UI with playback positions for specific room
        });

        // Live room monitoring - real-time queue changes
        _connection.On("AdminReceiveQueueUpdate", (object data) =>
        {
            // data contains: { RoomId: string, Queue: List<QueueItem> }
            // Update UI with queue for specific room
        });

        // Single room state response
        _connection.On<string, StateSnapshot?>("AdminReceiveRoomState", (roomId, state) =>
        {
            // Response to AdminRequestRoomState - use for room inspector
        });
    }
}
```

---

## Event Flow Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│                     ADMIN AUTHENTICATION                         │
├─────────────────────────────────────────────────────────────────┤
│  Admin calls: AdminAuthenticate(key)                            │
│       ↓                                                          │
│  Server validates key                                            │
│       ↓                                                          │
│  Admin added to "Admins" SignalR group                          │
│       ↓                                                          │
│  Server sends initial state:                                     │
│    - AdminReceiveStats                                           │
│    - AdminReceiveConnections                                     │
│    - AdminReceiveDetailedRooms                                   │
│    - AdminReceiveBlacklist                                       │
│    - AdminReceiveMemberStates                                    │
│    - AdminReceiveLogs                                            │
│    - AdminReceiveAllRoomStates                                   │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│                     REAL-TIME UPDATES                            │
├─────────────────────────────────────────────────────────────────┤
│  User connects → AdminReceiveStats, AdminReceiveConnections     │
│  User disconnects → AdminReceiveStats, AdminReceiveConnections  │
│  Room created → BroadcastAllAdminData                           │
│  Room joined → BroadcastAllAdminData                            │
│  Room left → BroadcastAllAdminData                              │
│  Room deleted → BroadcastAllAdminData                           │
│  Member banned → BroadcastAllAdminData                          │
│  Queue changed → AdminReceiveQueueUpdate                        │
│  Member state → AdminReceiveRoomMemberStates                    │
│  Playback time → AdminReceiveRoomMemberTimes                    │
│  Blacklist changed → AdminReceiveBlacklist                      │
│  New log → AdminReceiveLogEntry                                  │
└─────────────────────────────────────────────────────────────────┘
```

---

## Security Notes

1. **Admin key** should be a strong, randomly generated string
2. **Never expose** the admin key in client-side code or logs
3. Admin authentication is **connection-scoped** - disconnection revokes access
4. Consider rate limiting admin actions in production
5. All admin actions are logged with `[ADMIN_*]` prefix

---

## Version

API Version: 2.0.0
