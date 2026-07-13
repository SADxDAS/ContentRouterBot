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

    // Впиши сюда правильные TopicID твоей супер-группы "Архив 18+"
    private readonly Dictionary<string, (string Chat, int? TopicId)> _routingRules = new()
    {
        { "Pvideo", ("Архив 18+", 13) },
        { "Pimages", ("Архив 18+", 20) },
        { "OnlyK", ("Архив 18+", 7) },
        { "Himages", ("Архив 18+", 9) },
        { "Hvideo", ("Архив 18+", 18) },
        { "Hmanga", ("Архив 18+", 11) },
        { "Hgame", ("Архив 18+", 16) }
    };

    public UserBotWorker(IChannelRepository repository)
    {
        _repository = repository;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        client = new Client(Config);
        await client.LoginUserIfNeeded();
        Console.WriteLine("[USERBOT-WORKER] Юзербот успешно подключен.");

        // Получаем все диалоги (и группы, и ботов)
        var dialogs = await client.Messages_GetAllDialogs();
        var allChats = dialogs.chats.Values.ToList();
        var allUsers = dialogs.users.Values.ToList();

        foreach (var chat in allChats)
        {
            if (chat is TlChannel ch && ch.IsChannel && !ch.IsGroup && !_routingRules.Values.Any(v => v.Chat == ch.Title))
            {
                string url = !string.IsNullOrEmpty(ch.username) ? $"https://t.me/{ch.username}" : $"https://t.me/c/{ch.ID}/1";
                _repository.AddAvailableChannel(ch.ID, ch.Title, url);
            }
        }

        string sourceChatName = Environment.GetEnvironmentVariable("SOURCE_CHAT_NAME") ?? "Свалка";

        InputPeer sourcePeer = null;
        long sourcePeerId = 0;
        bool isSourceChannel = false;

        // Пытаемся найти свалку среди Групп и Каналов
        var sourceChat = allChats.FirstOrDefault(c => c.Title == sourceChatName);
        if (sourceChat != null)
        {
            sourcePeer = sourceChat;
            sourcePeerId = sourceChat.ID;
            isSourceChannel = sourceChat is TlChannel;
            Console.WriteLine($"[USERBOT-WORKER] Успешно слушаю чат: {sourceChat.Title}");
        }
        else
        {
            // Пытаемся найти свалку среди Ботов (по username или имени)
            string cleanName = sourceChatName.Replace("@", "").ToLower();
            var sourceUser = allUsers.FirstOrDefault(u => (u.username != null && u.username.ToLower() == cleanName) || u.first_name == sourceChatName);
            if (sourceUser != null)
            {
                sourcePeer = sourceUser;
                sourcePeerId = sourceUser.ID;
                Console.WriteLine($"[USERBOT-WORKER] Успешно слушаю бота: {sourceUser.username}");
            }
            else
            {
                Console.WriteLine($"\n[КРИТИЧЕСКАЯ ОШИБКА] Свалка '{sourceChatName}' не найдена ни среди групп, ни среди ботов!");
            }
        }

        if (sourcePeer != null)
        {
            try
            {
                Console.WriteLine("[USERBOT-WORKER] Проверяю накопившиеся сообщения...");
                var history = await client.Messages_GetHistory(sourcePeer, limit: 50);
                if (history != null && history.Messages.Length > 0)
                {
                    var messagesToProcess = history.Messages.OfType<TlMessage>().ToList();
                    messagesToProcess.Reverse();

                    var historyGroups = messagesToProcess
                        .GroupBy(m => m.grouped_id == 0 ? m.id : m.grouped_id)
                        .ToList();

                    foreach (var group in historyGroups)
                    {
                        if (group.Any()) await ProcessMessageGroup(group.ToList(), sourcePeer, isSourceChannel, allChats);
                    }
                    Console.WriteLine("[USERBOT-WORKER] Накопившиеся сообщения разобраны.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ОШИБКА ИСТОРИИ] {ex.Message}");
            }
        }

        // ИСПРАВЛЕНИЕ: Используем правильный делегат (TL.UpdatesBase)
        client.OnUpdates += async (TL.UpdatesBase updates) =>
        {
            if (sourcePeerId == 0) return;

            try
            {
                var validMessages = new List<TlMessage>();

                // В WTelegramClient свойство UpdateList доступно прямо из UpdatesBase
                foreach (var update in updates.UpdateList)
                {
                    TlMessage msg = null;
                    if (update is UpdateNewMessage unm) msg = unm.message as TlMessage;
                    else if (update is UpdateNewChannelMessage uncm) msg = uncm.message as TlMessage;

                    if (msg != null && msg.peer_id.ID == sourcePeerId)
                    {
                        // Игнорируем меню UI-бота (SingleMessageApp)
                        var activeMenu = _repository.GetActiveMenu();
                        if (activeMenu != null && msg.id == activeMenu.Value.MessageId) continue;

                        validMessages.Add(msg);
                    }
                }

                var liveGroups = validMessages.GroupBy(m => m.grouped_id == 0 ? m.id : m.grouped_id).ToList();
                foreach (var group in liveGroups)
                {
                    if (group.Any()) await ProcessMessageGroup(group.ToList(), sourcePeer, isSourceChannel, allChats);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ОШИБКА В РЕАЛТАЙМЕ] {ex.Message}");
            }
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
        }
    }

    private async Task ProcessMessageGroup(List<TlMessage> msgs, InputPeer sourcePeer, bool isSourceChannel, List<ChatBase> allChats)
    {
        if (msgs.Count == 0) return;
        string content = string.Join(" ", msgs.Select(m => m.message?.Trim()).Where(s => !string.IsNullOrEmpty(s)));

        string targetTag = null;
        bool isDomainRouting = false;

        var fwdMsg = msgs.FirstOrDefault(m => m.fwd_from != null);

        if (fwdMsg != null && fwdMsg.fwd_from.from_id is TlPeerChannel pc)
        {
            string mappedTag = _repository.GetTagForChannel(pc.channel_id);
            if (mappedTag != null && mappedTag != "IGNORE")
            {
                targetTag = mappedTag;
            }
            else if (mappedTag == null)
            {
                var fwdChat = allChats.FirstOrDefault(c => c.ID == pc.channel_id);
                if (fwdChat != null)
                {
                    string url = fwdChat is TlChannel ch && !string.IsNullOrEmpty(ch.username) ? $"https://t.me/{ch.username}" : $"https://t.me/c/{fwdChat.ID}/1";
                    _repository.AddAvailableChannel(pc.channel_id, fwdChat.Title, url);
                }
            }
        }
        else
        {
            var match = Regex.Match(content, @"https?://(?:www\.)?([^/]+)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                string domain = match.Groups[1].Value.ToLower();
                if (domain != "telegra.ph" && domain != "t.me")
                {
                    string mappedTag = _repository.GetTagForDomain(domain);
                    if (mappedTag != null && mappedTag != "IGNORE")
                    {
                        targetTag = mappedTag;
                        isDomainRouting = true;
                    }
                    else if (mappedTag == null)
                    {
                        _repository.AddAvailableDomain(domain);
                    }
                }
            }
        }

        if (targetTag != null)
        {
            // Безопасное приведение DocumentBase к Document перед поиском атрибутов
            bool hasVideo = msgs.Any(m => m.media is MessageMediaDocument doc &&
                                          doc.document is TL.Document d &&
                                          d.attributes.Any(a => a is DocumentAttributeVideo));

            bool hasTelegraph = content.Contains("telegra.ph");

            if (targetTag == "Hmix")
            {
                if (hasTelegraph) targetTag = "Hmanga";
                else if (hasVideo) targetTag = "Hvideo";
                else targetTag = "Himages";
            }
            if (targetTag == "Pmix")
            {
                if (hasVideo) targetTag = "Pvideo";
                else targetTag = "Pimages";
            }
        }

        if (targetTag != null && _routingRules.ContainsKey(targetTag))
        {
            var route = _routingRules[targetTag];
            var targetChat = allChats.FirstOrDefault(c => c.Title == route.Chat);

            if (targetChat != null)
            {
                int[] msgIds = msgs.Select(m => m.id).ToArray();

                await client.Messages_ForwardMessages(
                    from_peer: sourcePeer,
                    id: msgIds,
                    random_id: msgs.Select(_ => WTelegram.Helpers.RandomLong()).ToArray(),
                    to_peer: targetChat,
                    top_msg_id: route.TopicId ?? 0
                );

                // Удаляем оригиналы из свалки
                if (isSourceChannel && sourcePeer is InputPeerChannel inputChannel)
                {
                    await client.Channels_DeleteMessages(inputChannel, msgIds);
                }
                else
                {
                    await client.Messages_DeleteMessages(msgIds, revoke: true);
                }

                Console.WriteLine($"[УСПЕХ] Отправлено в {targetTag} ({(isDomainRouting ? "по домену" : "по каналу")})");
                await Task.Delay(1000);
            }
            else
            {
                Console.WriteLine($"[ОШИБКА] Группа/Канал '{route.Chat}' не найдена!");
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