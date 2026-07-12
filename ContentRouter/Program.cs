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

        using var classifier = new SmartClassifier("my_onnx_model");
        Console.WriteLine("Нейросеть загружена в память.");

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

        // 1. СНАЧАЛА РАЗГРЕБАЕМ СТАРЫЕ ЗАВАЛЫ (если бот был выключен)
        Console.WriteLine("Проверяю накопившиеся сообщения...");
        var history = await client.Messages_GetHistory(sourceChat, limit: 50);
        var messagesToProcess = history.Messages.OfType<Message>().ToList();

        messagesToProcess.Reverse(); // От старых к новым
        foreach (var msg in messagesToProcess)
        {
            await ProcessSingleMessage(msg, client, classifier, sourceChat, allChats);
        }


        // 2. ПОДКЛЮЧАЕМ СЛУШАТЕЛЯ РЕАЛЬНОГО ВРЕМЕНИ
        client.OnUpdates += async (UpdatesBase updates) =>
        {
            // Библиотека теперь сразу отдает объект UpdatesBase, так что проверка типов больше не нужна!
            foreach (var update in updates.UpdateList)
            {
                // Ловим новые сообщения из групп (UpdateNewMessage) и каналов (UpdateNewChannelMessage)
                Message msg = null;
                if (update is UpdateNewMessage unm) msg = unm.message as Message;
                else if (update is UpdateNewChannelMessage uncm) msg = uncm.message as Message;

                // Если это сообщение и оно именно из нашей Свалки
                if (msg != null && msg.peer_id.ID == sourceChat.ID)
                {
                    Console.WriteLine("\n[LIVE] Прилетело новое сообщение!");
                    await ProcessSingleMessage(msg, client, classifier, sourceChat, allChats);
                }
            }
        };

        // 3. БЛОКИРУЕМ ЗАКРЫТИЕ ПРОГРАММЫ
        Console.WriteLine("\n=== Бот перешел в режим ожидания. Нажми Ctrl+C для выхода ===");
        await Task.Delay(-1); // Бесконечное ожидание
    }

    // Вынесли логику маршрутизации в отдельный метод, чтобы не дублировать код
    private static async Task ProcessSingleMessage(Message msg, Client client, SmartClassifier classifier, ChatBase sourceChat, List<ChatBase> allChats)
    {
        string content = msg.message;
        if (string.IsNullOrWhiteSpace(content) && msg.media != null)
            content = $"[Медиафайл: {msg.media.GetType().Name}]";

        string category = classifier.PredictCategory(content);
        string targetChatName = RoutingRules.ContainsKey(category) ? RoutingRules[category] : RoutingRules["OTHER"];

        var targetChat = allChats.FirstOrDefault(c => c.Title == targetChatName);

        if (targetChat != null)
        {
            long randomId = WTelegram.Helpers.RandomLong();
            await client.Messages_ForwardMessages(sourceChat, new[] { msg.id }, new[] { randomId }, targetChat);

            Console.WriteLine($"[УСПЕХ] Отправлено в '{targetChatName}'");

            if (sourceChat is Channel sourceChannel)
                await client.Channels_DeleteMessages(sourceChannel, new[] { msg.id });
            else
                await client.Messages_DeleteMessages(new[] { msg.id }, revoke: true);

            await Task.Delay(1000); // Анти-спам
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