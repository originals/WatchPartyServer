using System.Text.Json;

namespace WatchPartyServer.Services;

public interface IBanListPersistenceService
{
    HashSet<string> Load();
    Task SaveAsync(IEnumerable<string> bannedUsers);
}

public class BanListPersistenceService : IBanListPersistenceService
{
    private const string DataFolder = "data";
    private const string FileName = "banlist.json";
    private readonly string _filePath;
    private readonly ILogger<BanListPersistenceService> _logger;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public BanListPersistenceService(ILogger<BanListPersistenceService> logger)
    {
        _logger = logger;
        _filePath = Path.Combine(DataFolder, FileName);
        EnsureDataFolderExists();
    }

    private void EnsureDataFolderExists()
    {
        if (!Directory.Exists(DataFolder))
        {
            Directory.CreateDirectory(DataFolder);
            _logger.LogInformation("Created data folder at {Path}", Path.GetFullPath(DataFolder));
        }
    }

    public HashSet<string> Load()
    {
        if (!File.Exists(_filePath))
        {
            _logger.LogInformation("No existing ban list found at {Path}", _filePath);
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var list = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            _logger.LogInformation("Loaded {Count} banned users from {Path}", list.Count, _filePath);
            return new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load ban list from {Path}", _filePath);
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public async Task SaveAsync(IEnumerable<string> bannedUsers)
    {
        await _saveLock.WaitAsync();
        try
        {
            var list = bannedUsers.ToList();
            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_filePath, json);
            _logger.LogDebug("Saved {Count} banned users to {Path}", list.Count, _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save ban list to {Path}", _filePath);
        }
        finally
        {
            _saveLock.Release();
        }
    }
}
