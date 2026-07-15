using System;
using System.Collections.Generic;

public interface IChannelRepository
{
    event Action OnNewUntaggedItem;
    event Action<long, string> OnChannelTagAssigned;
    event Action<string, string> OnDomainTagAssigned;
    event Action<int, string> OnDirectMessageTagAssigned;
    event Action<int> OnPreviewRequested;
    void RequestPreview(int messageId);
    bool IsPaused { get; set; }
    event Action OnForceScanRequested;
    void RequestForceScan();
    string GetTagForChannel(long channelId);
    void SaveTag(long channelId, string tag);
    void RemoveTag(long channelId);
    Dictionary<long, string> GetMappings();

    // Обновлено для сохранения ID сообщения
    void AddAvailableChannel(long id, string title, string url, int triggerMsgId);
    Dictionary<long, (string Title, string Url, int TriggerMsgId)> GetAvailableChannels();

    string GetTagForDomain(string domain);
    void SaveDomainTag(string domain, string tag);
    void RemoveDomainTag(string domain);
    void AddAvailableDomain(string domain, int triggerMsgId);
    Dictionary<string, int> GetAvailableDomains();
    Dictionary<string, string> GetDomainMappings();

    void AddPendingDirectMessage(int messageId, string previewText);
    Dictionary<int, string> GetPendingDirectMessages();
    void SaveDirectMessageTag(int messageId, string tag);
    void RemovePendingDirectMessage(int messageId);

    void SetActiveMenu(long chatId, int messageId);
    (long ChatId, int MessageId)? GetActiveMenu();
    long GetAdminChatId();
}