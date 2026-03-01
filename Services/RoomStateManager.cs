using System.Collections.Concurrent;
using WatchPartyServer.Models;

namespace WatchPartyServer.Services;

public class RoomStateManager : IRoomStateManager, IAdminStateManager
{
    private readonly ConcurrentDictionary<string, RoomState> _rooms = new();
    private readonly ConcurrentDictionary<string, string> _connectionToRoom = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _roomConnections = new();
    private readonly ConcurrentDictionary<string, string> _connectionToUsername = new();
    private readonly HashSet<string> _globalBlacklist = new();
    private readonly object _blacklistLock = new();

    public RoomState CreateRoom(string roomName, string hostUsername, string hostConnectionId, bool isPrivate, string? password, SharedLocation? sharedLocation)
    {
        var roomId = Guid.NewGuid().ToString("N")[..8];
        var room = new RoomState
        {
            RoomId = roomId,
            RoomName = roomName,
            HostUsername = hostUsername,
            HostConnectionId = hostConnectionId,
            IsPrivate = isPrivate,
            Password = isPrivate ? (password ?? string.Empty) : string.Empty,
            SharedLocation = sharedLocation,
            CreatedAt = DateTime.UtcNow
        };
        room.Members.Add(hostUsername);
        room.MemberTimes[hostUsername] = 0;
        room.MemberStates[hostUsername] = MemberState.Idle;

        _rooms[roomId] = room;
        _connectionToRoom[hostConnectionId] = roomId;
        _connectionToUsername[hostConnectionId] = hostUsername;
        _roomConnections[roomId] = new HashSet<string> { hostConnectionId };

        return room;
    }

    public bool IsGloballyBlacklisted(string username)
    {
        lock (_blacklistLock)
        {
            return _globalBlacklist.Contains(username.ToLowerInvariant());
        }
    }

    public bool AddMember(string roomId, string connectionId, string username, string? password)
    {
        if (string.IsNullOrWhiteSpace(roomId) || string.IsNullOrWhiteSpace(username))
            return false;

        if (!_rooms.TryGetValue(roomId, out var room)) return false;

        if (_connectionToRoom.TryGetValue(connectionId, out var oldRoomId) && oldRoomId != roomId)
        {
            RemoveConnection(connectionId);
        }

        lock (room)
        {
            if (room.BannedUsers.Contains(username.ToLowerInvariant()))
                return false;

            if (room.IsPrivate && room.Password != (password ?? string.Empty))
                return false;

            if (!room.Members.Contains(username))
            {
                room.Members.Add(username);
                room.MemberTimes[username] = room.CurrentTime;

                if (room.IsWaitingForReady)
                    room.MemberStates[username] = MemberState.Loading;
                else if (!string.IsNullOrEmpty(room.CurrentVideoId))
                    room.MemberStates[username] = MemberState.Loading;
                else
                    room.MemberStates[username] = MemberState.Idle;
            }
        }

        _connectionToUsername[connectionId] = username;
        _connectionToRoom[connectionId] = roomId;

        _roomConnections.AddOrUpdate(
            roomId,
            _ => new HashSet<string> { connectionId },
            (_, set) =>
            {
                lock (set) { set.Add(connectionId); }
                return set;
            });

        return true;
    }

    public string? GetConnectionRoom(string connectionId)
    {
        return _connectionToRoom.TryGetValue(connectionId, out var roomId) ? roomId : null;
    }

    public List<RoomInfo> GetRoomList()
    {
        var result = new List<RoomInfo>();
        foreach (var kvp in _rooms)
        {
            var room = kvp.Value;
            lock (room)
            {
                result.Add(RoomMapper.ToRoomInfo(room));
            }
        }
        return result;
    }

    public RoomInfo? GetRoomInfo(string roomId)
    {
        if (!_rooms.TryGetValue(roomId, out var room)) return null;
        lock (room)
        {
            return RoomMapper.ToRoomInfo(room);
        }
    }

    public StateSnapshot? GetRoomSnapshot(string roomId)
    {
        if (!_rooms.TryGetValue(roomId, out var room)) return null;
        lock (room)
        {
            return RoomMapper.ToSnapshot(room);
        }
    }

    public bool IsHost(string roomId, string connectionId)
    {
        return _rooms.TryGetValue(roomId, out var room)
               && room.HostConnectionId == connectionId;
    }

    public bool IsMember(string roomId, string connectionId)
    {
        return _connectionToRoom.TryGetValue(connectionId, out var memberRoomId)
               && memberRoomId == roomId;
    }

    public RemoveConnectionResult RemoveConnection(string connectionId)
    {
        if (!_connectionToRoom.TryRemove(connectionId, out var roomId))
            return new RemoveConnectionResult();

        _connectionToUsername.TryRemove(connectionId, out var username);

        if (!_roomConnections.TryGetValue(roomId, out var connections))
        {
            _rooms.TryRemove(roomId, out _);
            return new RemoveConnectionResult { RoomId = roomId, RoomDeleted = true };
        }

        string? newHost = null;
        bool roomDeleted = false;
        bool wasWaitingForReady = false;

        lock (connections)
        {
            connections.Remove(connectionId);

            if (connections.Count == 0)
            {
                _rooms.TryRemove(roomId, out _);
                _roomConnections.TryRemove(roomId, out _);
                roomDeleted = true;
            }
            else if (_rooms.TryGetValue(roomId, out var room))
            {
                lock (room)
                {
                    wasWaitingForReady = room.IsWaitingForReady;

                    if (room.HostConnectionId == connectionId)
                    {
                        newHost = connections.First();
                        room.HostConnectionId = newHost;
                        if (_connectionToUsername.TryGetValue(newHost, out var newHostUsername))
                            room.HostUsername = newHostUsername;
                    }

                    if (username != null)
                    {
                        room.Members.Remove(username);
                        room.MemberTimes.Remove(username);
                        room.MemberStates.Remove(username);
                    }
                }
            }
        }

        return new RemoveConnectionResult
        {
            RoomId = roomId,
            RoomDeleted = roomDeleted,
            NewHostConnectionId = newHost,
            WasWaitingForReady = wasWaitingForReady
        };
    }

    public Dictionary<string, double>? UpdateMemberTime(string roomId, string connectionId, double currentTime)
    {
        if (!_rooms.TryGetValue(roomId, out var room)) return null;
        if (!_connectionToUsername.TryGetValue(connectionId, out var username)) return null;

        lock (room)
        {
            room.MemberTimes[username] = currentTime;
            if (room.HostConnectionId == connectionId)
                room.CurrentTime = currentTime;

            return new Dictionary<string, double>(room.MemberTimes);
        }
    }

    public Dictionary<string, MemberState>? UpdateMemberState(string roomId, string connectionId, MemberState state)
    {
        if (!_rooms.TryGetValue(roomId, out var room)) return null;
        if (!_connectionToUsername.TryGetValue(connectionId, out var username)) return null;

        lock (room)
        {
            room.MemberStates[username] = state;
            return new Dictionary<string, MemberState>(room.MemberStates);
        }
    }

    public bool AreAllMembersReady(string roomId)
    {
        if (!_rooms.TryGetValue(roomId, out var room)) return false;

        lock (room)
        {
            if (!room.IsWaitingForReady) return false;

            foreach (var member in room.Members)
            {
                if (!room.MemberStates.TryGetValue(member, out var state))
                    return false;
                if (state != MemberState.Ready)
                    return false;
            }
            return true;
        }
    }

    public void StartWaitingForReady(string roomId, string videoId)
    {
        if (!_rooms.TryGetValue(roomId, out var room)) return;

        lock (room)
        {
            room.IsWaitingForReady = true;
            room.PendingVideoId = videoId;
            room.IsPlaying = false;
            foreach (var member in room.Members)
            {
                room.MemberStates[member] = MemberState.Loading;
            }
        }
    }

    public void FinishWaitingForReady(string roomId)
    {
        if (!_rooms.TryGetValue(roomId, out var room)) return;

        lock (room)
        {
            room.IsWaitingForReady = false;
            room.IsPlaying = true;
            room.CurrentTime = 0;
        }
    }

    public bool IsWaitingForReady(string roomId)
    {
        if (!_rooms.TryGetValue(roomId, out var room)) return false;
        return room.IsWaitingForReady;
    }

    public DeleteRoomResult? DeleteRoom(string roomId, string connectionId)
    {
        if (!_rooms.TryGetValue(roomId, out var room)) return null;
        if (room.HostConnectionId != connectionId) return null;

        _rooms.TryRemove(roomId, out _);
        return CleanupRoomConnections(roomId);
    }

    private DeleteRoomResult CleanupRoomConnections(string roomId)
    {
        var connectionIds = new List<string>();
        if (_roomConnections.TryRemove(roomId, out var connections))
        {
            lock (connections)
            {
                connectionIds.AddRange(connections);
                foreach (var connId in connections)
                {
                    _connectionToRoom.TryRemove(connId, out _);
                    _connectionToUsername.TryRemove(connId, out _);
                }
            }
        }
        return new DeleteRoomResult { ConnectionIds = connectionIds };
    }

    public void UpdatePlayback(string roomId, SyncPayload payload)
    {
        if (!_rooms.TryGetValue(roomId, out var room)) return;
        lock (room)
        {
            room.CurrentTime = payload.Timestamp;
            room.IsPlaying = payload.IsPlaying;
        }
    }

    public bool UpdateRoom(string roomId, string connectionId, string roomName, string description)
    {
        if (!_rooms.TryGetValue(roomId, out var room)) return false;
        if (room.HostConnectionId != connectionId) return false;

        lock (room)
        {
            room.RoomName = roomName;
            room.Description = description;
        }
        return true;
    }

    public EnqueueResult? EnqueueVideo(string roomId, QueueItem item)
    {
        if (!_rooms.TryGetValue(roomId, out var room)) return null;
        lock (room)
        {
            room.Queue.Add(item);
            return new EnqueueResult { Queue = room.Queue.ToList() };
        }
    }

    public SkipResult? SkipToNext(string roomId)
    {
        if (!_rooms.TryGetValue(roomId, out var room)) return null;
        lock (room)
        {
            if (room.Queue.Count == 0)
                return new SkipResult { Queue = new List<QueueItem>() };

            var next = room.Queue[0];
            room.Queue.RemoveAt(0);
            room.CurrentVideoId = next.VideoId;
            room.CurrentTime = 0;
            room.IsPlaying = true;

            return new SkipResult
            {
                VideoId = next.VideoId,
                Queue = room.Queue.ToList(),
                IsPlaying = true
            };
        }
    }

    public List<QueueItem>? RemoveFromQueue(string roomId, int index)
    {
        if (!_rooms.TryGetValue(roomId, out var room)) return null;
        lock (room)
        {
            if (index < 0 || index >= room.Queue.Count) return null;
            room.Queue.RemoveAt(index);
            return room.Queue.ToList();
        }
    }

    public List<QueueItem>? ReorderQueue(string roomId, int fromIndex, int toIndex)
    {
        if (!_rooms.TryGetValue(roomId, out var room)) return null;
        lock (room)
        {
            if (fromIndex < 0 || fromIndex >= room.Queue.Count) return null;
            if (toIndex < 0 || toIndex >= room.Queue.Count) return null;
            if (fromIndex == toIndex) return room.Queue.ToList();

            var item = room.Queue[fromIndex];
            room.Queue.RemoveAt(fromIndex);
            room.Queue.Insert(toIndex, item);
            return room.Queue.ToList();
        }
    }

    public BanMemberResult? BanMember(string roomId, string hostConnectionId, string username)
    {
        if (!_rooms.TryGetValue(roomId, out var room)) return null;
        if (room.HostConnectionId != hostConnectionId) return null;

        string usernameLower = username.ToLowerInvariant();
        string? bannedConnectionId = null;

        lock (room)
        {
            if (room.HostUsername.Equals(username, StringComparison.OrdinalIgnoreCase))
                return null;

            room.BannedUsers.Add(usernameLower);
            room.Members.Remove(username);
            room.MemberTimes.Remove(username);
            room.MemberStates.Remove(username);
        }

        foreach (var kvp in _connectionToUsername)
        {
            if (kvp.Value.Equals(username, StringComparison.OrdinalIgnoreCase)
                && _connectionToRoom.TryGetValue(kvp.Key, out var connRoomId)
                && connRoomId == roomId)
            {
                bannedConnectionId = kvp.Key;
                break;
            }
        }

        if (bannedConnectionId != null)
        {
            _connectionToRoom.TryRemove(bannedConnectionId, out _);
            _connectionToUsername.TryRemove(bannedConnectionId, out _);

            if (_roomConnections.TryGetValue(roomId, out var connections))
            {
                lock (connections)
                {
                    connections.Remove(bannedConnectionId);
                }
            }
        }

        return new BanMemberResult
        {
            BannedConnectionId = bannedConnectionId,
            Username = username
        };
    }

    #region Admin Operations

    public ServerStats GetServerStats(DateTime serverStartTime)
    {
        var publicRooms = 0;
        var privateRooms = 0;
        foreach (var room in _rooms.Values)
        {
            if (room.IsPrivate) privateRooms++;
            else publicRooms++;
        }

        return new ServerStats
        {
            TotalRooms = _rooms.Count,
            TotalConnections = _connectionToRoom.Count,
            PublicRooms = publicRooms,
            PrivateRooms = privateRooms,
            ServerStartTime = serverStartTime,
            Uptime = DateTime.UtcNow - serverStartTime
        };
    }

    public List<ClientConnectionInfo> GetAllConnections()
    {
        var result = new List<ClientConnectionInfo>();
        foreach (var connId in _connectionToRoom.Keys)
        {
            _connectionToUsername.TryGetValue(connId, out var username);
            _connectionToRoom.TryGetValue(connId, out var roomId);
            result.Add(new ClientConnectionInfo
            {
                ConnectionId = connId,
                ShortId = connId.Length >= 8 ? connId[..8] : connId,
                Username = username,
                RoomId = roomId
            });
        }
        return result;
    }

    public List<DetailedRoomInfo> GetDetailedRoomList()
    {
        var result = new List<DetailedRoomInfo>();
        foreach (var kvp in _rooms)
        {
            var room = kvp.Value;
            _roomConnections.TryGetValue(kvp.Key, out var connections);
            lock (room)
            {
                result.Add(RoomMapper.ToDetailedInfo(room, connections?.Count ?? 0));
            }
        }
        return result;
    }

    public DeleteRoomResult? AdminDeleteRoom(string roomId)
    {
        if (!_rooms.TryRemove(roomId, out _)) return null;
        return CleanupRoomConnections(roomId);
    }

    public bool AdminKickConnection(string connectionId)
    {
        return _connectionToRoom.ContainsKey(connectionId);
    }

    public string? GetConnectionUsername(string connectionId)
    {
        return _connectionToUsername.TryGetValue(connectionId, out var username) ? username : null;
    }

    public List<string> GetGlobalBlacklist()
    {
        lock (_blacklistLock)
        {
            return _globalBlacklist.ToList();
        }
    }

    public bool AddToGlobalBlacklist(string username)
    {
        lock (_blacklistLock)
        {
            return _globalBlacklist.Add(username.ToLowerInvariant());
        }
    }

    public bool RemoveFromGlobalBlacklist(string username)
    {
        lock (_blacklistLock)
        {
            return _globalBlacklist.Remove(username.ToLowerInvariant());
        }
    }

    #endregion
}
