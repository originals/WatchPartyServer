using WatchPartyServer.Models;

namespace WatchPartyServer.Services;

internal static class RoomMapper
{
    public static RoomInfo ToRoomInfo(RoomState room)
    {
        return new RoomInfo
        {
            RoomId = room.RoomId,
            RoomName = room.RoomName,
            Description = room.Description,
            HostUsername = room.HostUsername,
            IsPrivate = room.IsPrivate,
            SharedLocation = room.SharedLocation,
            MemberCount = room.Members.Count,
            CreatedAt = room.CreatedAt
        };
    }

    public static StateSnapshot ToSnapshot(RoomState room)
    {
        return new StateSnapshot
        {
            RoomId = room.RoomId,
            RoomName = room.RoomName,
            Description = room.Description,
            HostUsername = room.HostUsername,
            SharedLocation = room.SharedLocation,
            CurrentVideoId = room.CurrentVideoId,
            CurrentTime = room.GetCalculatedTime(),
            IsPlaying = room.IsPlaying,
            SequenceNumber = room.SequenceNumber,
            Queue = room.Queue.ToList(),
            Members = room.Members.ToList(),
            MemberTimes = new Dictionary<string, double>(room.MemberTimes),
            MemberStates = new Dictionary<string, MemberState>(room.MemberStates),
            MaxQueuePerUser = room.MaxQueuePerUser
        };
    }

    public static DetailedRoomInfo ToDetailedInfo(RoomState room, int connectionCount)
    {
        return new DetailedRoomInfo
        {
            RoomId = room.RoomId,
            RoomName = room.RoomName,
            HostUsername = room.HostUsername,
            HostConnectionId = room.HostConnectionId,
            IsPrivate = room.IsPrivate,
            MemberCount = room.Members.Count,
            ConnectionCount = connectionCount,
            Members = room.Members.ToList(),
            BannedUsers = room.BannedUsers.ToList(),
            QueueLength = room.Queue.Count,
            CurrentVideoId = room.CurrentVideoId,
            IsPlaying = room.IsPlaying,
            CreatedAt = room.CreatedAt
        };
    }
}
