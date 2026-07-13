using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using TL;
using WTelegram;
using TlMessage = TL.Message;
using TlChannel = TL.Channel;
using TlPeerChannel = TL.PeerChannel;
using TlUpdatesBase = TL.UpdatesBase; // Добавили алиас для обновлений

public class UserBotWorker : BackgroundService
{
    private readonly IChannelRepository _repository;
    private readonly IAiClassifier _classifier;
    private Client client;

    private readonly Dictionary<string, (string Chat, int? TopicId)> _routingRules = new()
    {
        { "CODE", ("Архив: Код", null) },
        { "MEDIA", ("Архив: Медиа", null) },
        { "LINK", ("Links", null) },
        { "NOTE", ("Архив: Нотатки", null) },
        { "OTHER", ("Архив: Разное", null) },

        { "NSFW", ("Архив 18+", 7) },
        { "NSFW_VIDEOS", ("Архив 18+", 13) },
        { "NSFW_PICS", ("Архив 18+", 20) },
        { "HENTAI", ("Архив 18+", 18) },
        { "HENTAI_MANGA", ("Архив 18+", 11) },
        { "HENTAI_PICS", ("Архив 18+", 9) },
        { "HENTAI_GAMES", ("Архив 18+", 16) }
    };

    public UserBotWorker(IChannelRepository repository, IAiClassifier classifier)
    {
        _repository = repository;
        _classifier = classifier;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        client = new Client(Config);
        await client.LoginUserIfNeeded();
        Console.WriteLine("[USERBOT-WORKER] Юзербот успешно подключен.");

        var chats = await client.Messages_GetAllChats();
        var allChats = chats.chats.Values.ToList();

        foreach (var chat in allChats)
        {
            if (chat is TlChannel ch && ch.IsChannel && !ch.IsGroup && !_routingRules.Values.Any(v => v.Chat == ch.Title))
            {
                string url = !string.IsNullOrEmpty(ch.username) ? $"https://t.me/{ch.username}" : $"https://t.me/c/{ch.ID}/1";
                _repository.AddAvailableChannel(ch.ID, ch.Title, url);
            }
        }

        string sourceChatName = Environment.GetEnvironmentVariable("SOURCE_CHAT_NAME") ?? "Свалка";
        var sourceChat = allChats.FirstOrDefault(c => c.Title == sourceChatName);

        // ИСПРАВЛЕНИЕ 1: Защита от ненайденной свалки
        if (sourceChat == null)
        {
            Console.WriteLine($"\n[КРИТИЧЕСКАЯ ОШИБКА] Исходный чат (Свалка) '{sourceChatName}' не найден!");
            Console.WriteLine("Бот работает, но ему нечего фильтровать. Проверьте название чата.\n");
        }
        else
        {
            Console.WriteLine($"[USERBOT-WORKER] Успешно слушаю чат: {sourceChat.Title}");

            // ИСПРАВЛЕНИЕ 2: Обработка старых (накопившихся) сообщений при старте
            try
            {
                Console.WriteLine("[USERBOT-WORKER] Проверяю накопившиеся сообщения в свалке...");
                var history = await client.Messages_GetHistory(sourceChat, limit: 50);
                if (history != null && history.Messages.Length > 0)
                {
                    var messagesToProcess = history.Messages.OfType<TlMessage>().ToList();
                    messagesToProcess.Reverse(); // Обрабатываем от старых к новым

                    var historyGroups = messagesToProcess
                        .GroupBy(m => m.grouped_id == 0 ? m.id : m.grouped_id)
                        .ToList();

                    foreach (var group in historyGroups)
                    {
                        if (group.Any()) await ProcessMessageGroup(group.ToList(), sourceChat, allChats);
                    }
                    Console.WriteLine("[USERBOT-WORKER] Накопившиеся сообщения разобраны.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ОШИБКА] Не удалось прочитать историю: {ex.Message}");
            }
        }

        // ИСПРАВЛЕНИЕ 3: Переименовали OnUpdates в OnUpdate (стандарт WTelegramClient) и обернули в try-catch
        // Замени блок _client.OnUpdate += ... на такой вариант:
        client.OnUpdates += async (UpdatesBase updates) =>
        {
            if (sourceChat == null) return;

            try
            {
                var validMessages = new List<TlMessage>();
                // В WTelegramClient 4.4.7 обновления приходят через updates
                // Если updates - это TlUpdatesBase, то список сообщений лежит в UpdateList
                foreach (var update in updates.UpdateList)
                {
                    TlMessage msg = null;
                    if (update is UpdateNewMessage unm) msg = unm.message as TlMessage;
                    else if (update is UpdateNewChannelMessage uncm) msg = uncm.message as TlMessage;

                    if (msg != null && msg.peer_id.ID == sourceChat.ID)
                        validMessages.Add(msg);
                }

                var liveGroups = validMessages.GroupBy(m => m.grouped_id == 0 ? m.id : m.grouped_id).ToList();
                foreach (var group in liveGroups)
                {
                    if (group.Any()) await ProcessMessageGroup(group.ToList(), sourceChat, allChats);
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

    private async Task ProcessMessageGroup(List<TlMessage> msgs, ChatBase sourceChat, List<ChatBase> allChats)
    {
        if (msgs.Count == 0) return;
        string content = string.Join(" ", msgs.Select(m => m.message?.Trim()).Where(s => !string.IsNullOrEmpty(s)));

        (string Chat, int? TopicId)? targetRoute = null;

        var fwdMsg = msgs.FirstOrDefault(m => m.fwd_from != null);
        if (fwdMsg != null && fwdMsg.fwd_from.from_id is TlPeerChannel pc)
        {
            string mappedTag = _repository.GetTagForChannel(pc.channel_id);

            // ИСПРАВЛЕНИЕ 4: Проверяем наличие ключа в _routingRules, чтобы избежать KeyNotFoundException
            if (mappedTag != null && mappedTag != "IGNORE" && _routingRules.ContainsKey(mappedTag))
            {
                targetRoute = _routingRules[mappedTag];
                Console.WriteLine($"[FAST-ROUTE] Найдено совпадение в репозитории: {targetRoute.Value.Chat} (Тема: {targetRoute.Value.TopicId?.ToString() ?? "Нет"})");
            }
            else
            {
                var fwdChat = allChats.FirstOrDefault(c => c.ID == pc.channel_id);
                if (fwdChat != null)
                {
                    content = $"[Переслано из: {fwdChat.Title}]\n" + content;
                    string url = fwdChat is TlChannel ch && !string.IsNullOrEmpty(ch.username) ? $"https://t.me/{ch.username}" : $"https://t.me/c/{fwdChat.ID}/1";
                    _repository.AddAvailableChannel(pc.channel_id, fwdChat.Title, url);
                }
            }
        }

        if (targetRoute == null)
        {
            bool hasMedia = msgs.Any(m => m.media != null && (m.media is MessageMediaPhoto || m.media is MessageMediaDocument));
            if (string.IsNullOrEmpty(content) && hasMedia) targetRoute = _routingRules["MEDIA"];
            else if (Uri.IsWellFormedUriString(content, UriKind.Absolute)) targetRoute = _routingRules["LINK"];
            else if (!string.IsNullOrEmpty(content))
            {
                string category = await _classifier.PredictCategoryAsync(content);
                targetRoute = _routingRules.ContainsKey(category) ? _routingRules[category] : _routingRules["OTHER"];
            }
            else targetRoute = _routingRules["OTHER"];
        }

        var targetChat = allChats.FirstOrDefault(c => c.Title == targetRoute.Value.Chat);
        if (targetChat != null)
        {
            int[] msgIds = msgs.Select(m => m.id).ToArray();
            long[] randomIds = msgs.Select(_ => WTelegram.Helpers.RandomLong()).ToArray();

            await client.Messages_ForwardMessages(
                from_peer: sourceChat,
                id: msgIds,
                random_id: randomIds,
                to_peer: targetChat,
                top_msg_id: targetRoute.Value.TopicId ?? 0
            );

            if (sourceChat is TlChannel sourceChannel) await client.Channels_DeleteMessages(sourceChannel, msgIds);
            else await client.Messages_DeleteMessages(msgIds, revoke: true);
            await Task.Delay(1500);
        }
        else
        {
            Console.WriteLine($"[ОШИБКА] Группа/Канал '{targetRoute.Value.Chat}' не найдена! Проверь название.");
        }
    }

    private string? Config(string what)
    {
        switch (what)
        {
            case "api_id": return Environment.GetEnvironmentVariable("API_ID");
            case "api_hash": return Environment.GetEnvironmentVariable("API_HASH");
            case "phone_number": return Environment.GetEnvironmentVariable("PHONE_NUMBER");
            case "verification_code": Console.Write("Введите код из Telegram: "); return Console.ReadLine();
            case "password": Console.Write("Введите облачный пароль (2FA): "); return Console.ReadLine();
            default: return null;
        }
    }
}