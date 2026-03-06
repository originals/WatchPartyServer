using System.Collections.Concurrent;
using WatchPartyServer.Models;

namespace WatchPartyServer.Services;

public interface ILogService
{
    event Action<LogEntry>? OnLogAdded;
    void Log(string level, string message, string? connectionId = null, string? roomId = null);
    List<LogEntry> GetLogs(int count = 100, string? levelFilter = null, string? roomIdFilter = null);
    void Clear();
}

public class LogService : ILogService
{
    private const int MaxLogEntries = 1000;
    private readonly ConcurrentQueue<LogEntry> _logs = new();
    private readonly object _trimLock = new();

    public event Action<LogEntry>? OnLogAdded;

    public void Log(string level, string message, string? connectionId = null, string? roomId = null)
    {
        var entry = new LogEntry
        {
            Timestamp = DateTime.UtcNow,
            Level = level,
            Message = message,
            ConnectionId = connectionId,
            RoomId = roomId
        };

        _logs.Enqueue(entry);
        TrimIfNeeded();
        OnLogAdded?.Invoke(entry);
    }

    public List<LogEntry> GetLogs(int count = 100, string? levelFilter = null, string? roomIdFilter = null)
    {
        var logs = _logs.ToArray().AsEnumerable();

        if (!string.IsNullOrEmpty(levelFilter))
            logs = logs.Where(l => l.Level.Equals(levelFilter, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(roomIdFilter))
            logs = logs.Where(l => l.RoomId == roomIdFilter);

        return logs
            .OrderByDescending(l => l.Timestamp)
            .Take(count)
            .ToList();
    }

    public void Clear()
    {
        _logs.Clear();
    }

    private void TrimIfNeeded()
    {
        if (_logs.Count <= MaxLogEntries) return;

        lock (_trimLock)
        {
            while (_logs.Count > MaxLogEntries)
                _logs.TryDequeue(out _);
        }
    }
}
