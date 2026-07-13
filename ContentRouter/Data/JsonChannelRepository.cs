using System.Collections.Generic;
using System.IO;
using System.Text.Json;

public class JsonChannelRepository : IChannelRepository
{
    private const string DB_FILE = "channels_db.json";
    private Dictionary<long, string> _mappings = new();
    private readonly Dictionary<long, (string Title, string Url)> _availableChannels = new();
    private readonly object _lock = new();

    public JsonChannelRepository()
    {
        if (File.Exists(DB_FILE))
        {
            try
            {
                _mappings = JsonSerializer.Deserialize<Dictionary<long, string>>(File.ReadAllText(DB_FILE)) ?? new();
            }
            catch { _mappings = new(); }
        }
    }

    public string GetTagForChannel(long channelId)
    {
        lock (_lock) return _mappings.TryGetValue(channelId, out string tag) ? tag : null;
    }

    public void SaveTag(long channelId, string tag)
    {
        lock (_lock)
        {
            _mappings[channelId] = tag;
            File.WriteAllText(DB_FILE, JsonSerializer.Serialize(_mappings));
        }
    }

    public void RemoveTag(long channelId)
    {
        lock (_lock)
        {
            if (_mappings.Remove(channelId))
                File.WriteAllText(DB_FILE, JsonSerializer.Serialize(_mappings));
        }
    }

    public Dictionary<long, string> GetMappings()
    {
        lock (_lock) return new Dictionary<long, string>(_mappings);
    }

    public void AddAvailableChannel(long id, string title, string url)
    {
        lock (_lock) _availableChannels[id] = (title, url);
    }

    public Dictionary<long, (string Title, string Url)> GetAvailableChannels()
    {
        lock (_lock) return new Dictionary<long, (string Title, string Url)>(_availableChannels);
    }
}