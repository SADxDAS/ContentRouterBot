using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using TL;
using WTelegram;
using TlMessage = TL.Message;
using TlChannel = TL.Channel;
using TlPeerChannel = TL.PeerChannel;
using TlUpdatesBase = TL.UpdatesBase;

public class UserBotWorker : BackgroundService
{
    private readonly IChannelRepository _repository;
    private Client client;

    private readonly List<(List<TlMessage> Msgs, long? ChannelId, string Domain, int? DirectMsgId)> _pendingGroups = new();
    private InputPeer _sourcePeer;
    private bool _isSourceChannel;
    private Dictionary<long, ChatBase> _allChats = new();

    // ========================================================
    // ДИНАМИЧЕСКИЕ ПРАВИЛА ИЗ routing.json
    // ========================================================
    private Dictionary<string, (string Chat, int? TopicId)> _routingRules = new();

    public UserBotWorker(IChannelRepository repository)
    {
        _repository = repository;
        LoadRoutingRules();
    }

    private void LoadRoutingRules()
    {
        try
        {
            if (System.IO.File.Exists("routing.json"))
            {
                string json = System.IO.File.ReadAllText("routing.json");
                _routingRules = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, (string Chat, int? TopicId)>>(json);
                Console.WriteLine($"[СИСТЕМА] Загружено {_routingRules.Count} правил из routing.json");
            }
            else
            {
                Console.WriteLine("[ОШИБКА] Файл routing.json не найден!");
            }
        }
        catch (Exception ex) { Console.WriteLine($"[ОШИБКА JSON] {ex.Message}"); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        client = new Client(Config);
        await client.LoginUserIfNeeded();
        Console.WriteLine("[USERBOT-WORKER] Юзербот успешно подключен.");

        var dialogs = await client.Messages_GetAllDialogs();
        foreach (var chat in dialogs.chats) _allChats[chat.Key] = chat.Value;
        var allUsers = dialogs.users.Values.ToList();

        // Не вызываем AddAvailableChannel тут, потому что нам нужен MsgId для триггера.
        // Бот сам добавит новые каналы, когда наткнется на сообщение из них.

        string sourceChatName = Environment.GetEnvironmentVariable("SOURCE_CHAT_NAME") ?? "Свалка";

        var sourceChat = _allChats.Values.FirstOrDefault(c => c.Title == sourceChatName);
        if (sourceChat != null) { _sourcePeer = sourceChat; _isSourceChannel = sourceChat is TlChannel; Console.WriteLine($"[USERBOT-WORKER] Слушаю чат: {sourceChat.Title}"); }
        else
        {
            string cleanName = sourceChatName.Replace("@", "").ToLower();
            var sourceUser = allUsers.FirstOrDefault(u => (u.username != null && u.username.ToLower() == cleanName) || u.first_name == sourceChatName);
            if (sourceUser != null) { _sourcePeer = sourceUser; Console.WriteLine($"[USERBOT-WORKER] Слушаю бота: {sourceUser.username}"); }
            else Console.WriteLine($"\n[КРИТИЧЕСКАЯ ОШИБКА] Свалка '{sourceChatName}' не найдена!");
        }

        _repository.OnChannelTagAssigned += (channelId, tag) => { _ = Task.Run(async () => { List<(List<TlMessage> Msgs, long? ChannelId, string Domain, int? DirectMsgId)> toProcess; lock (_pendingGroups) { toProcess = _pendingGroups.Where(p => p.ChannelId == channelId).ToList(); foreach (var p in toProcess) _pendingGroups.Remove(p); } foreach (var p in toProcess) await ProcessMessageGroup(p.Msgs, _sourcePeer, _isSourceChannel); }); };
        _repository.OnDomainTagAssigned += (domain, tag) => { _ = Task.Run(async () => { List<(List<TlMessage> Msgs, long? ChannelId, string Domain, int? DirectMsgId)> toProcess; lock (_pendingGroups) { toProcess = _pendingGroups.Where(p => p.Domain == domain).ToList(); foreach (var p in toProcess) _pendingGroups.Remove(p); } foreach (var p in toProcess) await ProcessMessageGroup(p.Msgs, _sourcePeer, _isSourceChannel); }); };
        _repository.OnDirectMessageTagAssigned += (msgId, tag) => { _ = Task.Run(async () => { List<(List<TlMessage> Msgs, long? ChannelId, string Domain, int? DirectMsgId)> toProcess; lock (_pendingGroups) { toProcess = _pendingGroups.Where(p => p.DirectMsgId == msgId).ToList(); foreach (var p in toProcess) _pendingGroups.Remove(p); } foreach (var p in toProcess) await ProcessMessageGroup(p.Msgs, _sourcePeer, _isSourceChannel, tag); }); };

        if (_sourcePeer != null)
        {
            try
            {
                Console.WriteLine("[USERBOT-WORKER] Начинаю глубокое сканирование истории...");
                int offsetId = 0;
                while (true)
                {
                    if (_repository.IsPaused)
                    {
                        await Task.Delay(2000); // Ждем, пока снимут с паузы
                        continue;
                    }

                    var history = await client.Messages_GetHistory(_sourcePeer, limit: 100, add_offset: 0, min_id: 0, max_id: offsetId);
                    if (history == null || history.Messages.Length == 0) break;

                    var messages = history.Messages.OfType<TlMessage>().ToList();
                    var historyGroups = messages.GroupBy(m => m.grouped_id == 0 ? m.id : m.grouped_id).ToList();
                    foreach (var group in historyGroups)
                    {
                        if (group.Any())
                        {
                            try { await ProcessMessageGroup(group.ToList(), _sourcePeer, _isSourceChannel); }
                            catch (Exception ex) { Console.WriteLine($"[ОШИБКА ОБРАБОТКИ ПАКЕТА] {ex.Message}"); }
                        }
                    }
                    offsetId = messages.Min(m => m.id);
                    await Task.Delay(500);
                }
                Console.WriteLine("[USERBOT-WORKER] Вся история разобрана.");
            }
            catch (Exception ex) { Console.WriteLine($"[ОШИБКА ИСТОРИИ] {ex.Message}"); }
        }

        client.OnUpdates += async (TL.UpdatesBase updates) =>
        {
            if (_sourcePeer == null) return;
            try
            {
                Dictionary<long, ChatBase> newChats = new();
                if (updates is TL.Updates u) newChats = u.chats;
                else if (updates is TL.UpdatesCombined uc) newChats = uc.chats;
                foreach (var chat in newChats) _allChats[chat.Key] = chat.Value;

                var validMessages = new List<TlMessage>();
                foreach (var update in updates.UpdateList)
                {
                    TlMessage msg = null;
                    if (update is UpdateNewMessage unm) msg = unm.message as TlMessage;
                    else if (update is UpdateNewChannelMessage uncm) msg = uncm.message as TlMessage;

                    if (msg != null && msg.peer_id.ID == _sourcePeer.ID) validMessages.Add(msg);
                }

                var liveGroups = validMessages.GroupBy(m => m.grouped_id == 0 ? m.id : m.grouped_id).ToList();
                foreach (var group in liveGroups)
                {
                    if (group.Any())
                    {
                        try { await ProcessMessageGroup(group.ToList(), _sourcePeer, _isSourceChannel); }
                        catch (Exception ex) { Console.WriteLine($"[ОШИБКА В РЕАЛТАЙМЕ] {ex.Message}"); }
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"[ФАТАЛЬНАЯ ОШИБКА ОБНОВЛЕНИЙ] {ex.Message}"); }
        };

        while (!stoppingToken.IsCancellationRequested) await Task.Delay(1000, stoppingToken);
    }

    private async Task ProcessMessageGroup(List<TlMessage> msgs, InputPeer sourcePeer, bool isSourceChannel, string predefinedTag = null)
    {
        if (_repository.IsPaused) return; // ПРОВЕРКА НА ПАУЗУ

        msgs = msgs.Where(msg =>
        {
            string text = msg.message ?? "";
            if (text.StartsWith("/")) return false;
            if (text.Contains("Маршрутизатор (SingleApp)")) return false;
            var activeMenu = _repository.GetActiveMenu();
            if (activeMenu != null && msg.id == activeMenu.Value.MessageId) return false;
            return true;
        }).ToList();

        if (msgs.Count == 0) return;

        string content = string.Join(" ", msgs.Select(m => m.message?.Trim()).Where(s => !string.IsNullOrEmpty(s)));

        string targetTag = predefinedTag;
        bool isDomainRouting = false;

        if (targetTag == null)
        {
            bool isGameFile = false;
            foreach (var m in msgs)
            {
                if (m.media is MessageMediaDocument doc && doc.document is TL.Document d)
                {
                    foreach (var a in d.attributes)
                    {
                        if (a is DocumentAttributeFilename attrFileName)
                        {
                            string fn = attrFileName.file_name.ToLower();
                            if (fn.EndsWith(".apk") || fn.EndsWith(".rar") || fn.EndsWith(".zip") || fn.EndsWith(".exe") || fn.EndsWith(".7z"))
                            {
                                isGameFile = true;
                                break;
                            }
                        }
                    }
                }
                if (isGameFile) break;
            }

            if (isGameFile)
            {
                targetTag = "Hgame";
            }
            else
            {
                bool hasTelegraphGlobal = content.ToLower().Contains("telegra.ph") || msgs.Any(m =>
                {
                    if (m.entities != null && m.entities.Any(e => e is MessageEntityTextUrl tu && tu.url.ToLower().Contains("telegra.ph"))) return true;
                    if (m.media is MessageMediaWebPage mw && mw.webpage is WebPage wp && (wp.url?.ToLower().Contains("telegra.ph") == true || wp.site_name?.ToLower().Contains("telegraph") == true)) return true;
                    return false;
                });

                if (hasTelegraphGlobal)
                {
                    targetTag = "Hmanga";
                }
                else
                {
                    var fwdMsg = msgs.FirstOrDefault(m => m.fwd_from != null);
                    if (fwdMsg != null && fwdMsg.fwd_from.from_id is TlPeerChannel pc)
                    {
                        string mappedTag = _repository.GetTagForChannel(pc.channel_id);
                        if (mappedTag != null) targetTag = mappedTag;
                        else
                        {
                            var fwdChat = _allChats.ContainsKey(pc.channel_id) ? _allChats[pc.channel_id] : null;
                            if (fwdChat != null)
                            {
                                string url = fwdChat is TlChannel ch && !string.IsNullOrEmpty(ch.username) ? $"https://t.me/{ch.username}" : $"https://t.me/c/{fwdChat.ID}/1";
                                _repository.AddAvailableChannel(pc.channel_id, fwdChat.Title, url, msgs.First().id);
                            }

                            lock (_pendingGroups) { _pendingGroups.Add((msgs, pc.channel_id, null, null)); }
                            return;
                        }
                    }
                    else
                    {
                        string domain = null;
                        foreach (var m in msgs)
                        {
                            if (m.entities != null)
                            {
                                foreach (var e in m.entities)
                                {
                                    if (e is MessageEntityTextUrl tu) { try { domain = new Uri(tu.url).Host; break; } catch { } }
                                    else if (e is MessageEntityUrl u) { string urlStr = m.message.Substring(u.offset, u.length); if (!urlStr.StartsWith("http")) urlStr = "https://" + urlStr; try { domain = new Uri(urlStr).Host; break; } catch { } }
                                }
                            }
                            if (domain != null) break;
                        }

                        if (domain == null)
                        {
                            var match = Regex.Match(content, @"https?://(?:www\.)?([^/]+)", RegexOptions.IgnoreCase);
                            if (match.Success) domain = match.Groups[1].Value;
                        }

                        if (domain != null)
                        {
                            domain = domain.ToLower().Replace("www.", "");
                            if (domain != "t.me")
                            {
                                string mappedTag = _repository.GetTagForDomain(domain);
                                if (mappedTag != null)
                                {
                                    targetTag = mappedTag;
                                    isDomainRouting = true;
                                }
                                else
                                {
                                    _repository.AddAvailableDomain(domain, msgs.First().id);
                                    lock (_pendingGroups) { _pendingGroups.Add((msgs, null, domain, null)); }
                                    return;
                                }
                            }
                        }

                        if (targetTag == null)
                        {
                            bool hasMedia = msgs.Any(m => m.media != null && !(m.media is MessageMediaWebPage));
                            if (hasMedia)
                            {
                                int firstMsgId = msgs.First().id;
                                string preview = content.Length > 25 ? content.Substring(0, 25) + ".." : (string.IsNullOrEmpty(content) ? "[Медиа]" : content);

                                _repository.AddPendingDirectMessage(firstMsgId, preview);
                                lock (_pendingGroups) { _pendingGroups.Add((msgs, null, null, firstMsgId)); }
                                return;
                            }
                            else
                            {
                                targetTag = "TRASH";
                            }
                        }
                    }
                }
            }
        }

        if (targetTag == "Hmix" || targetTag == "Pmix")
        {
            bool hasVideo = msgs.Any(m => m.media is MessageMediaDocument doc &&
                                          doc.document is TL.Document d &&
                                          d.attributes.Any(a => a is DocumentAttributeVideo));
            if (targetTag == "Hmix") targetTag = hasVideo ? "Hvideo" : "Himages";
            if (targetTag == "Pmix") targetTag = hasVideo ? "Pvideo" : "Pimages";
        }

        if (targetTag == "IGNORE" || targetTag == null) targetTag = "TRASH";

        if (targetTag != null && _routingRules.ContainsKey(targetTag))
        {
            var route = _routingRules[targetTag];
            var targetChat = _allChats.Values.FirstOrDefault(c => c.Title == route.Chat);

            if (targetChat != null)
            {
                int[] msgIds = msgs.Select(m => m.id).ToArray();
                try
                {
                    await client.Messages_ForwardMessages(
                        from_peer: sourcePeer,
                        id: msgIds,
                        random_id: msgs.Select(_ => WTelegram.Helpers.RandomLong()).ToArray(),
                        to_peer: targetChat,
                        top_msg_id: route.TopicId ?? 0
                    );

                    if (isSourceChannel && sourcePeer is InputPeerChannel inputChannel) await client.Channels_DeleteMessages(inputChannel, msgIds);
                    else await client.Messages_DeleteMessages(msgIds, revoke: true);

                    Console.WriteLine($"[УСПЕХ] Отправлено в {targetTag} ({(isDomainRouting ? "по домену" : "по каналу")})");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ОШИБКА ПЕРЕСЫЛКИ] ID: {msgIds.FirstOrDefault()} -> {ex.Message}");
                }

                await Task.Delay(1000);
            }
        }
    }

    private string? Config(string what)
    {
        switch (what)
        {
            case "api_id": return Environment.GetEnvironmentVariable("API_ID");
            case "api_hash": return Environment.GetEnvironmentVariable("API_HASH");
            case "phone_number": return Environment.GetEnvironmentVariable("PHONE_NUMBER");
            case "verification_code": Console.Write("Введите код: "); return Console.ReadLine();
            case "password": Console.Write("Пароль 2FA: "); return Console.ReadLine();
            default: return null;
        }
    }
}