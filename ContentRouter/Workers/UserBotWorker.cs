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

public class RouteInfo
{
    public string Chat { get; set; }
    public int? TopicId { get; set; }
}

public class UserBotWorker : BackgroundService
{
    private readonly IChannelRepository _repository;
    private Client client;

    private readonly List<(List<TlMessage> Msgs, long? ChannelId, string Domain, int? DirectMsgId)> _pendingGroups = new();
    private InputPeer _sourcePeer;
    private bool _isSourceChannel;
    private Dictionary<long, ChatBase> _allChats = new();

    private Dictionary<string, RouteInfo> _routingRules = new();

    // Специальные переменные для новой логики
    private readonly HashSet<int> _ignoredPreviewIds = new();
    private bool _isScanning = false;

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
                _routingRules = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, RouteInfo>>(json);
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

        string sourceChatName = Environment.GetEnvironmentVariable("SOURCE_CHAT_NAME") ?? "Свалка";

        if (sourceChatName.ToLower() == "избранное" || sourceChatName.ToLower() == "me" || sourceChatName.ToLower() == "saved messages")
        {
            _sourcePeer = InputPeer.Self;
            _isSourceChannel = false;
            Console.WriteLine("[USERBOT-WORKER] Слушаю 'Избранное' (Saved Messages).");
        }
        else
        {
            var sourceChat = _allChats.Values.FirstOrDefault(c => c.Title == sourceChatName);
            if (sourceChat != null) { _sourcePeer = sourceChat; _isSourceChannel = sourceChat is TlChannel; Console.WriteLine($"[USERBOT-WORKER] Слушаю чат: {sourceChat.Title}"); }
            else
            {
                string cleanName = sourceChatName.Replace("@", "").ToLower();
                var sourceUser = allUsers.FirstOrDefault(u => (u.username != null && u.username.ToLower() == cleanName) || u.first_name == sourceChatName);
                if (sourceUser != null) { _sourcePeer = sourceUser; Console.WriteLine($"[USERBOT-WORKER] Слушаю бота: {sourceUser.username}"); }
                else Console.WriteLine($"\n[КРИТИЧЕСКАЯ ОШИБКА] Свалка '{sourceChatName}' не найдена!");
            }
        }

        _repository.OnChannelTagAssigned += (channelId, tag) => { _ = Task.Run(async () => { List<(List<TlMessage> Msgs, long? ChannelId, string Domain, int? DirectMsgId)> toProcess; lock (_pendingGroups) { toProcess = _pendingGroups.Where(p => p.ChannelId == channelId).ToList(); foreach (var p in toProcess) _pendingGroups.Remove(p); } foreach (var p in toProcess) await ProcessMessageGroup(p.Msgs, _sourcePeer, _isSourceChannel); }); };
        _repository.OnDomainTagAssigned += (domain, tag) => { _ = Task.Run(async () => { List<(List<TlMessage> Msgs, long? ChannelId, string Domain, int? DirectMsgId)> toProcess; lock (_pendingGroups) { toProcess = _pendingGroups.Where(p => p.Domain == domain).ToList(); foreach (var p in toProcess) _pendingGroups.Remove(p); } foreach (var p in toProcess) await ProcessMessageGroup(p.Msgs, _sourcePeer, _isSourceChannel); }); };
        _repository.OnDirectMessageTagAssigned += (msgId, tag) => { _ = Task.Run(async () => { List<(List<TlMessage> Msgs, long? ChannelId, string Domain, int? DirectMsgId)> toProcess; lock (_pendingGroups) { toProcess = _pendingGroups.Where(p => p.DirectMsgId == msgId).ToList(); foreach (var p in toProcess) _pendingGroups.Remove(p); } foreach (var p in toProcess) await ProcessMessageGroup(p.Msgs, _sourcePeer, _isSourceChannel, tag); }); };

        _repository.OnPreviewRequested += (msgId) =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var botikUser = allUsers.FirstOrDefault(u => u.username != null && u.username.ToLower() == "hoomelanderbot");
                    InputPeer targetPeer = botikUser;

                    if (targetPeer != null && _sourcePeer != null)
                    {
                        var updates = await client.Messages_ForwardMessages(
                            from_peer: _sourcePeer,
                            id: new[] { msgId },
                            random_id: new[] { WTelegram.Helpers.RandomLong() },
                            to_peer: targetPeer
                        );

                        // Сохраняем ID пересланного превью, чтобы юзербот не принял его за мусор
                        foreach (var u in updates.UpdateList)
                        {
                            if (u is UpdateNewMessage unm && unm.message is TlMessage m) _ignoredPreviewIds.Add(m.id);
                        }
                    }
                    else
                    {
                        Console.WriteLine("[ПРЕВЬЮ] Не удалось найти бота @hoomelanderbot для пересылки.");
                    }
                }
                catch (Exception ex) { Console.WriteLine($"[ОШИБКА ПРЕВЬЮ] {ex.Message}"); }
            });
        };

        // Подписываемся на команду /scan из UI
        _repository.OnForceScanRequested += StartDeepScan;

        // Запускаем сканирование при старте
        if (_sourcePeer != null)
        {
            StartDeepScan();
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

    private void StartDeepScan()
    {
        if (_isScanning) return; // Защита от двойного запуска

        _ = Task.Run(async () =>
        {
            _isScanning = true;
            try
            {
                Console.WriteLine("[USERBOT-WORKER] Начинаю глубокое сканирование истории...");
                int offsetId = 0;
                int errorCount = 0;

                while (true)
                {
                    if (_repository.IsPaused)
                    {
                        await Task.Delay(2000);
                        continue;
                    }

                    try
                    {
                        var history = await client.Messages_GetHistory(_sourcePeer, limit: 100, offset_id: offsetId);

                        Dictionary<long, ChatBase> historyChats = new();
                        if (history is TL.Messages_Messages mm) historyChats = mm.chats;
                        else if (history is TL.Messages_MessagesSlice ms) historyChats = ms.chats;
                        else if (history is TL.Messages_ChannelMessages cm) historyChats = cm.chats;
                        foreach (var c in historyChats) _allChats[c.Key] = c.Value;

                        if (history == null || history.Messages.Length == 0)
                        {
                            Console.WriteLine("[USERBOT-WORKER] Конец истории достигнут (0 сообщений).");
                            break;
                        }

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

                        offsetId = history.Messages.Min(m => m.ID);
                        errorCount = 0;

                        await Task.Delay(500);
                    }
                    catch (Exception ex)
                    {
                        errorCount++;
                        if (ex.Message.Contains("FLOOD_WAIT_"))
                        {
                            var match = Regex.Match(ex.Message, @"FLOOD_WAIT_(\d+)");
                            if (match.Success && int.TryParse(match.Groups[1].Value, out int sec))
                            {
                                Console.WriteLine($"[ПАУЗА] Телеграм просит подождать {sec} секунд...");
                                await Task.Delay((sec + 2) * 1000);
                            }
                            else await Task.Delay(5000);
                        }
                        else
                        {
                            await Task.Delay(3000);
                            if (errorCount >= 3)
                            {
                                Console.WriteLine("[СДВИГ] Принудительно пропускаем битый блок истории...");
                                offsetId = Math.Max(0, offsetId - 1);
                                errorCount = 0;
                            }
                        }
                    }
                }
                Console.WriteLine("[USERBOT-WORKER] Вся история полностью разобрана.");
            }
            finally
            {
                _isScanning = false; // Разрешаем сканировать снова
            }
        });
    }

    private async Task ProcessMessageGroup(List<TlMessage> msgs, InputPeer sourcePeer, bool isSourceChannel, string predefinedTag = null)
    {
        if (_repository.IsPaused && predefinedTag == null) return;

        msgs = msgs.Where(msg =>
        {
            // 1. Игнорируем только те сообщения, которые юзербот переслал как превью
            if (_ignoredPreviewIds.Contains(msg.id)) return false;

            string text = msg.message ?? "";

            // 2. Игнорируем технические команды бота (/scan, /menu и т.д.)
            if (text.StartsWith("/")) return false;

            // 3. Игнорируем тексты UI-менюшек
            if (text.Contains("Маршрутизатор (SingleApp)")) return false;
            if (text.Contains("Куда отправить сообщение")) return false;
            if (text.Contains("Куда отправлять посты")) return false;
            if (text.Contains("Куда отправлять ссылки")) return false;
            if (text.Contains("Выберите категорию")) return false;
            if (text.Contains("Привязки для")) return false;
            if (text.Contains("Все файлы из Свалки отсортированы")) return false;
            if (text.Contains("Сканирование истории запущено")) return false;

            var activeMenu = _repository.GetActiveMenu();
            if (activeMenu != null && msg.id == activeMenu.Value.MessageId) return false;

            return true;
        }).ToList();

        if (msgs.Count == 0) return;

        string content = string.Join(" ", msgs.Select(m => m.message?.Trim()).Where(s => !string.IsNullOrEmpty(s)));

        bool isBlockedByTelegram = content.Contains("couldn't be displayed on your device") ||
                                   content.Contains("violates the Telegram Terms of Service");
        if (isBlockedByTelegram)
        {
            Console.WriteLine($"[СИСТЕМА] Найдено заблокированное сообщение (ID: {msgs.First().id}). Удаляем без пересылки...");
            int[] badIds = msgs.Select(m => m.id).ToArray();
            try
            {
                if (isSourceChannel && sourcePeer is InputPeerChannel inputChannel) await client.Channels_DeleteMessages(inputChannel, badIds);
                else await client.Messages_DeleteMessages(badIds, revoke: true);
            }
            catch { }
            return;
        }

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

                        if (mappedTag == "MANUAL")
                        {
                            int firstMsgId = msgs.First().id;
                            string preview = content.Length > 25 ? content.Substring(0, 25) + ".." : (string.IsNullOrEmpty(content) ? "[Медиа]" : content);
                            _repository.AddPendingDirectMessage(firstMsgId, preview);
                            lock (_pendingGroups) { _pendingGroups.Add((msgs, null, null, firstMsgId)); }
                            return;
                        }
                        else if (mappedTag != null) targetTag = mappedTag;
                        else
                        {
                            var fwdChat = _allChats.ContainsKey(pc.channel_id) ? _allChats[pc.channel_id] : null;
                            string title = fwdChat != null ? fwdChat.Title : $"Скрытый/Новый канал ID: {pc.channel_id}";
                            string url = fwdChat is TlChannel ch && !string.IsNullOrEmpty(ch.username) ? $"https://t.me/{ch.username}" : $"https://t.me/c/{pc.channel_id}/1";

                            _repository.AddAvailableChannel(pc.channel_id, title, url, msgs.First().id);

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

                                if (mappedTag == "MANUAL")
                                {
                                    int firstMsgId = msgs.First().id;
                                    string preview = content.Length > 25 ? content.Substring(0, 25) + ".." : (string.IsNullOrEmpty(content) ? "[Медиа]" : content);
                                    _repository.AddPendingDirectMessage(firstMsgId, preview);
                                    lock (_pendingGroups) { _pendingGroups.Add((msgs, null, null, firstMsgId)); }
                                    return;
                                }
                                else if (mappedTag != null)
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

                // Умный цикл ожидания и пересылки
                int retries = 3;
                while (retries > 0)
                {
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

                        Console.WriteLine($"[УСПЕХ] Отправлено в {targetTag} ({(isDomainRouting ? "по домену" : "по каналу или вручную")})");
                        break; // Успех
                    }
                    catch (RpcException rpcEx) when (rpcEx.Code == 420)
                    {
                        int sec = rpcEx.X;
                        if (sec == 0) sec = 60;

                        Console.WriteLine($"\n[ОГРАНИЧЕНИЕ TELEGRAM] Ждем {sec} сек. (около {sec / 60} мин.). БОТ НЕ ЗАВИС, НЕ ВЫКЛЮЧАЙТЕ ЕГО!");
                        await Task.Delay((sec + 2) * 1000);
                        continue; // Пробуем снова
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ОШИБКА ПЕРЕСЫЛКИ] ID: {msgIds.FirstOrDefault()} -> {ex.Message}");
                        break;
                    }
                    retries--;
                }

                await Task.Delay(1000);
            }
            else
            {
                Console.WriteLine($"[ОШИБКА МАРШРУТИЗАЦИИ] Чат назначения '{route.Chat}' не найден в списке диалогов! Пересылка отменена.");
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