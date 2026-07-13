using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Linq;

public class DatabaseModel
{
    public Dictionary<long, string> ChannelMappings { get; set; } = new();
    public Dictionary<string, string> DomainMappings { get; set; } = new();
    public long ActiveMenuChatId { get; set; } = 0;
    public int ActiveMenuMessageId { get; set; } = 0;
}

public class JsonChannelRepository : IChannelRepository
{
    private const string DB_FILE = "channels_db.json";
    private DatabaseModel _db = new();
    private readonly Dictionary<long, (string Title, string Url)> _availableChannels = new();
    private readonly HashSet<string> _availableDomains = new();
    private readonly object _lock = new();

    public JsonChannelRepository()
    {
        if (File.Exists(DB_FILE))
        {
            try { _db = JsonSerializer.Deserialize<DatabaseModel>(File.ReadAllText(DB_FILE)) ?? new DatabaseModel(); }
            catch { _db = new DatabaseModel(); }
        }
    }

    private void Save() => File.WriteAllText(DB_FILE, JsonSerializer.Serialize(_db));

    public string GetTagForChannel(long channelId) { lock (_lock) return _db.ChannelMappings.TryGetValue(channelId, out string tag) ? tag : null; }
    public void SaveTag(long channelId, string tag) { lock (_lock) { _db.ChannelMappings[channelId] = tag; Save(); } }
    public void RemoveTag(long channelId) { lock (_lock) { if (_db.ChannelMappings.Remove(channelId)) Save(); } }
    public Dictionary<long, string> GetMappings() { lock (_lock) return new Dictionary<long, string>(_db.ChannelMappings); }
    public void AddAvailableChannel(long id, string title, string url) { lock (_lock) _availableChannels[id] = (title, url); }
    public Dictionary<long, (string Title, string Url)> GetAvailableChannels() { lock (_lock) return new Dictionary<long, (string Title, string Url)>(_availableChannels); }

    public string GetTagForDomain(string domain) { lock (_lock) return _db.DomainMappings.TryGetValue(domain, out string tag) ? tag : null; }
    public void SaveDomainTag(string domain, string tag) { lock (_lock) { _db.DomainMappings[domain] = tag; Save(); } }
    public void RemoveDomainTag(string domain) { lock (_lock) { if (_db.DomainMappings.Remove(domain)) Save(); } }
    public void AddAvailableDomain(string domain) { lock (_lock) _availableDomains.Add(domain); }
    public HashSet<string> GetAvailableDomains() { lock (_lock) return new HashSet<string>(_availableDomains); }
    public Dictionary<string, string> GetDomainMappings() { lock (_lock) return new Dictionary<string, string>(_db.DomainMappings); }

    public void SetActiveMenu(long chatId, int messageId) { lock (_lock) { _db.ActiveMenuChatId = chatId; _db.ActiveMenuMessageId = messageId; Save(); } }
    public (long ChatId, int MessageId)? GetActiveMenu() { lock (_lock) return _db.ActiveMenuMessageId == 0 ? null : (_db.ActiveMenuChatId, _db.ActiveMenuMessageId); }
}