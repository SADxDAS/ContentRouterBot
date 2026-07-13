using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DotNetEnv;
using TL;
using WTelegram;

class Program
{
    private static readonly Dictionary<string, string> RoutingRules = new()
    {
        { "CODE", "Архив: Код" },
        { "MEDIA", "Архив: Медиа" },
        { "OTHER", "Архив: Разное" },
        { "LINK", "Links" },
        { "NOTE", "Notes" }
    };

    static async Task Main(string[] args)
    {
        Env.Load();
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("API_ID")))
        {
            Console.WriteLine("Ошибка: Файл .env не найден.");
            return;
        }

        // Шлях до нової моделі Llama 3
        using var classifier = new SmartClassifier("Models/Meta-Llama-3-8B-Instruct.Q4_K_M.gguf");
        Console.WriteLine("Нейросеть Llama загружена в память.");

        using var client = new Client(Config);
        await client.LoginUserIfNeeded();
        Console.WriteLine("Подключение установлено.");

        var chats = await client.Messages_GetAllChats();
        var allChats = chats.chats.Values.ToList();

        string sourceChatName = Environment.GetEnvironmentVariable("SOURCE_CHAT_NAME");
        var sourceChat = allChats.FirstOrDefault(c => c.Title == sourceChatName);

        if (sourceChat == null)
        {
            Console.WriteLine($"Ошибка: Группа '{sourceChatName}' не найдена.");
            return;
        }

        // 1. РАЗГРЕБАЕМ СТАРЫЕ ЗАВАЛЫ (з підтримкою альбомів)
        Console.WriteLine("Проверяю накопившиеся сообщения...");
        var history = await client.Messages_GetHistory(sourceChat, limit: 50);
        var messagesToProcess = history.Messages.OfType<Message>().ToList();
        messagesToProcess.Reverse();

        // Групуємо повідомлення за grouped_id (якщо grouped_id = 0, це одиночне повідомлення)
        var historyGroups = messagesToProcess
            .GroupBy(m => m.grouped_id == 0 ? m.id : m.grouped_id)
            .ToList();

        foreach (var group in historyGroups)
        {
            await ProcessMessageGroup(group.ToList(), client, classifier, sourceChat, allChats);
        }

        // 2. СЛУХАЧ РЕАЛЬНОГО ЧАСУ (з підтримкою альбомів)
        client.OnUpdates += async (UpdatesBase updates) =>
        {
            var validMessages = new List<Message>();

            // Збираємо всі нові повідомлення з поточного оновлення
            foreach (var update in updates.UpdateList)
            {
                Message msg = null;
                if (update is UpdateNewMessage unm) msg = unm.message as Message;
                else if (update is UpdateNewChannelMessage uncm) msg = uncm.message as Message;

                if (msg != null && msg.peer_id.ID == sourceChat.ID)
                {
                    validMessages.Add(msg);
                }
            }

            // Групуємо нові повідомлення в альбоми
            var liveGroups = validMessages
                .GroupBy(m => m.grouped_id == 0 ? m.id : m.grouped_id)
                .ToList();

            foreach (var group in liveGroups)
            {
                if (group.Any())
                {
                    Console.WriteLine($"\n[LIVE] Прилетела группа из {group.Count()} сообщений (Альбом/Пост)!");
                    await ProcessMessageGroup(group.ToList(), client, classifier, sourceChat, allChats);
                }
            }
        };

        Console.WriteLine("\n=== Бот перешел в режим ожидания. Нажми Ctrl+C для выхода ===");
        await Task.Delay(-1);
    }

    // НОВИЙ МЕТОД: Обробляє масив повідомлень (Альбом) як єдине ціле
    private static async Task ProcessMessageGroup(List<Message> msgs, Client client, SmartClassifier classifier, ChatBase sourceChat, List<ChatBase> allChats)
    {
        if (msgs.Count == 0) return;

        // Збираємо весь текст з альбому (зазвичай текст є тільки під однією фотографією)
        string content = string.Join(" ", msgs.Select(m => m.message?.Trim()).Where(s => !string.IsNullOrEmpty(s)));
        string targetChatName = RoutingRules["OTHER"];

        // Перевіряємо, чи є в цьому альбомі медіафайли
        bool hasMedia = msgs.Any(m => m.media != null && (m.media is MessageMediaPhoto || m.media is MessageMediaDocument));

        // КРОК 1: ПЕРЕВІРКА НА ЧИСТЕ МЕДІА (фото/відео БЕЗ ТЕКСТУ)
        if (string.IsNullOrEmpty(content) && hasMedia)
        {
            targetChatName = RoutingRules["MEDIA"];
        }
        // КРОК 2: ПЕРЕВІРКА НА ПОСИЛАННЯ
        else if (Uri.IsWellFormedUriString(content, UriKind.Absolute))
        {
            targetChatName = RoutingRules["LINK"];
        }
        // КРОК 3: ІНТЕЛЕКТУАЛЬНИЙ АНАЛІЗ ТЕКСТУ (Llama 3)
        else if (!string.IsNullOrEmpty(content))
        {
            string category = await classifier.PredictCategory(content);
            targetChatName = RoutingRules.ContainsKey(category) ? RoutingRules[category] : RoutingRules["OTHER"];
        }

        var targetChat = allChats.FirstOrDefault(c => c.Title == targetChatName);

        if (targetChat != null)
        {
            // Беремо масив усіх ID з цього альбому
            int[] msgIds = msgs.Select(m => m.id).ToArray();

            // Генеруємо випадкові ID для пересилання (вимога Telegram API)
            long[] randomIds = msgs.Select(_ => WTelegram.Helpers.RandomLong()).ToArray();

            // ПЕРЕСИЛАЄМО ВЕСЬ АЛЬБОМ ОДНІЄЮ КОМАНДОЮ
            await client.Messages_ForwardMessages(sourceChat, msgIds, randomIds, targetChat);

            string logText = content.Length > 20 ? content.Substring(0, 20) + "..." : (content == "" ? "[АЛЬБОМ БЕЗ ТЕКСТУ]" : content);
            Console.WriteLine($"[УСПЕХ] Переслано {msgs.Count} шт. в '{targetChatName}'. Текст: {logText}");

            // ВИДАЛЯЄМО ВЕСЬ АЛЬБОМ ЗІ СМІТНИКА
            if (sourceChat is Channel sourceChannel)
                await client.Channels_DeleteMessages(sourceChannel, msgIds);
            else
                await client.Messages_DeleteMessages(msgIds, revoke: true);

            await Task.Delay(1500); // Анти-спам (зробили трохи більше для альбомів)
        }
        else
        {
            Console.WriteLine($"[ОШИБКА] Целевая группа '{targetChatName}' не найдена!");
        }
    }

    private static string? Config(string what)
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