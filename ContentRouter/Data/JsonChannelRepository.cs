using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Linq;

public class ChannelCacheItem
{
    public string Title { get; set; }
    public string Url { get; set; }
    public int TriggerMsgId { get; set; }
}

public class DatabaseModel
{
    public bool IsPaused { get; set; } = false;
    public Dictionary<long, string> ChannelMappings { get; set; } = new();
    public Dictionary<string, string> DomainMappings { get; set; } = new();
    public Dictionary<long, ChannelCacheItem> ChannelCache { get; set; } = new();
    public Dictionary<string, int> AvailableDomains { get; set; } = new();
    public Dictionary<int, string> PendingDirectMessages { get; set; } = new();
    public long ActiveMenuChatId { get; set; } = 0;
    public int ActiveMenuMessageId { get; set; } = 0;
}

public class JsonChannelRepository : IChannelRepository
{
    public event Action OnNewUntaggedItem;
    public event Action<long, string> OnChannelTagAssigned;
    public event Action<string, string> OnDomainTagAssigned;
    public event Action<int, string> OnDirectMessageTagAssigned;
    public event Action<int> OnPreviewRequested;
    public event Action OnForceScanRequested;
    public void RequestForceScan() { OnForceScanRequested?.Invoke(); }
    public void RequestPreview(int messageId) { OnPreviewRequested?.Invoke(messageId); }
    private const string DB_FILE = "channels_db.json";
    private DatabaseModel _db = new();
    private readonly object _lock = new();

    public JsonChannelRepository()
    {
        if (File.Exists(DB_FILE))
        {
            try { _db = JsonSerializer.Deserialize<DatabaseModel>(File.ReadAllText(DB_FILE)) ?? new DatabaseModel(); }
            catch { _db = new DatabaseModel(); }
        }
        if (_db.ChannelCache == null) _db.ChannelCache = new Dictionary<long, ChannelCacheItem>();
        if (_db.PendingDirectMessages == null) _db.PendingDirectMessages = new Dictionary<int, string>();
        if (_db.AvailableDomains == null) _db.AvailableDomains = new Dictionary<string, int>();
        
        //_db.PendingDirectMessages.Clear();
        Save();
    }

    private void Save()
    {
        string json = JsonSerializer.Serialize(_db);
        int retries = 5;
        while (retries > 0)
        {
            try
            {
                File.WriteAllText(DB_FILE, json);
                break; // Успешно записали, выходим из цикла
            }
            catch (IOException)
            {
                retries--;
                if (retries == 0)
                {
                    Console.WriteLine("[БД-ОШИБКА] Не удалось сохранить файл после 5 попыток.");
                    break;
                }
                System.Threading.Thread.Sleep(100); // Ждем 100мс пока Windows отпустит файл
            }
        }
    }
    public bool IsPaused { get { lock (_lock) return _db.IsPaused; } set { lock (_lock) { _db.IsPaused = value; Save(); } } }

    public string GetTagForChannel(long channelId) { lock (_lock) return _db.ChannelMappings.TryGetValue(channelId, out string tag) ? tag : null; }
    public void SaveTag(long channelId, string tag) { lock (_lock) { _db.ChannelMappings[channelId] = tag; Save(); } OnChannelTagAssigned?.Invoke(channelId, tag); }
    public void RemoveTag(long channelId) { lock (_lock) { if (_db.ChannelMappings.Remove(channelId)) Save(); } }
    public Dictionary<long, string> GetMappings() { lock (_lock) return new Dictionary<long, string>(_db.ChannelMappings); }

    public void AddAvailableChannel(long id, string title, string url, int triggerMsgId)
    {
        bool notifyUI = false;
        lock (_lock)
        {
            if (!_db.ChannelCache.ContainsKey(id)) { _db.ChannelCache[id] = new ChannelCacheItem { Title = title, Url = url, TriggerMsgId = triggerMsgId }; if (!_db.ChannelMappings.ContainsKey(id)) notifyUI = true; Save(); }
            else if (_db.ChannelCache[id].Title != title) { _db.ChannelCache[id].Title = title; _db.ChannelCache[id].Url = url; Save(); }
        }
        if (notifyUI) OnNewUntaggedItem?.Invoke();
    }
    public Dictionary<long, (string Title, string Url, int TriggerMsgId)> GetAvailableChannels() { lock (_lock) return _db.ChannelCache.ToDictionary(k => k.Key, v => (v.Value.Title, v.Value.Url, v.Value.TriggerMsgId)); }

    public string GetTagForDomain(string domain) { lock (_lock) return _db.DomainMappings.TryGetValue(domain, out string tag) ? tag : null; }
    public void SaveDomainTag(string domain, string tag) { lock (_lock) { _db.DomainMappings[domain] = tag; Save(); } OnDomainTagAssigned?.Invoke(domain, tag); }
    public void RemoveDomainTag(string domain) { lock (_lock) { if (_db.DomainMappings.Remove(domain)) Save(); } }
    public void AddAvailableDomain(string domain, int triggerMsgId) { bool isNew = false; lock (_lock) { if (!_db.AvailableDomains.ContainsKey(domain)) { _db.AvailableDomains[domain] = triggerMsgId; isNew = true; Save(); } } if (isNew) OnNewUntaggedItem?.Invoke(); }
    public Dictionary<string, int> GetAvailableDomains() { lock (_lock) return new Dictionary<string, int>(_db.AvailableDomains); }
    public Dictionary<string, string> GetDomainMappings() { lock (_lock) return new Dictionary<string, string>(_db.DomainMappings); }

    public void AddPendingDirectMessage(int messageId, string previewText) { bool isNew = false; lock (_lock) { if (!_db.PendingDirectMessages.ContainsKey(messageId)) { _db.PendingDirectMessages[messageId] = previewText; isNew = true; Save(); } } if (isNew) OnNewUntaggedItem?.Invoke(); }
    public Dictionary<int, string> GetPendingDirectMessages() { lock (_lock) return new Dictionary<int, string>(_db.PendingDirectMessages); }
    public void SaveDirectMessageTag(int messageId, string tag) { lock (_lock) { _db.PendingDirectMessages.Remove(messageId); Save(); } OnDirectMessageTagAssigned?.Invoke(messageId, tag); }
    public void RemovePendingDirectMessage(int messageId) { lock (_lock) { if (_db.PendingDirectMessages.Remove(messageId)) Save(); } }

    public void SetActiveMenu(long chatId, int messageId) { lock (_lock) { _db.ActiveMenuChatId = chatId; _db.ActiveMenuMessageId = messageId; Save(); } }
    public (long ChatId, int MessageId)? GetActiveMenu() { lock (_lock) return _db.ActiveMenuMessageId == 0 ? null : (_db.ActiveMenuChatId, _db.ActiveMenuMessageId); }
    public long GetAdminChatId() { lock (_lock) return _db.ActiveMenuChatId; }
}