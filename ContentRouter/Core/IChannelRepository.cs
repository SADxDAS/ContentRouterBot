using System.Collections.Generic;

public interface IChannelRepository
{
    string GetTagForChannel(long channelId);
    void SaveTag(long channelId, string tag);
    void RemoveTag(long channelId);
    Dictionary<long, string> GetMappings();
    void AddAvailableChannel(long id, string title, string url);
    Dictionary<long, (string Title, string Url)> GetAvailableChannels();

    string GetTagForDomain(string domain);
    void SaveDomainTag(string domain, string tag);
    void RemoveDomainTag(string domain);
    void AddAvailableDomain(string domain);
    HashSet<string> GetAvailableDomains();
    Dictionary<string, string> GetDomainMappings();

    // SingleMessageApp
    void SetActiveMenu(long chatId, int messageId);
    (long ChatId, int MessageId)? GetActiveMenu();
}