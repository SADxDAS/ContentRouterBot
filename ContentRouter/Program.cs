using DotNetEnv;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TL;
using Telegram.Bot;
using Telegram.Bot.Polling;
// Алиасы для библиотеки юзербота (WTelegram)
using WTelegram;
// Алиасы для библиотеки официального бота (Telegram.Bot)
using BotClient = Telegram.Bot.TelegramBotClient;
using BotType = Telegram.Bot.Types.Enums.UpdateType;
using BotUpdate = Telegram.Bot.Types.Update;
using InlineButton = Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton;
using InlineKeyboard = Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup;
using TlChannel = TL.Channel;
using TlChatBase = TL.ChatBase;
using TlMessage = TL.Message;
using TlPeerChannel = TL.PeerChannel;
using TlUpdateNewChannelMessage = TL.UpdateNewChannelMessage;
using TlUpdateNewMessage = TL.UpdateNewMessage;
using TlUpdatesBase = TL.UpdatesBase;

class Program
{
    private static readonly Dictionary<string, string> RoutingRules = new()
    {
        { "CODE", "Архив: Код" }, { "MEDIA", "Архив: Медиа" }, { "NSFW", "OnlyModels" },
        { "HENTAI", "Хентай" }, { "LINK", "Links" }, { "NOTE", "Архив: Нотатки" }, { "OTHER", "Архив: Разное" }
    };

    private static readonly string[] Tags = { "CODE", "MEDIA", "NSFW", "HENTAI", "LINK", "NOTE", "OTHER" };

    private static Dictionary<long, string> ChannelMappings = new();
    private static Dictionary<long, string> AvailableChannels = new(); // Кэш названий каналов
    private static HashSet<long> SkippedChannels = new(); // Пропущенные в текущей сессии
    private const string DB_FILE = "channels_db.json";

    static async Task Main(string[] args)
    {
        Env.Load();
        LoadDatabase();

        // 1. ЗАПУСК ОФИЦИАЛЬНОГО БОТА (UI)
        string botToken = Environment.GetEnvironmentVariable("BOT_TOKEN");
        if (string.IsNullOrEmpty(botToken))
        {
            Console.WriteLine("Ошибка: Добавьте BOT_TOKEN в .env");
            return;
        }
        var uiBot = new BotClient(botToken);
        var receiverOptions = new Telegram.Bot.Polling.ReceiverOptions { AllowedUpdates = new[] { BotType.Message, BotType.CallbackQuery } };
        uiBot.StartReceiving(HandleBotUpdateAsync, HandleBotErrorAsync, receiverOptions);
        Console.WriteLine("UI-Бот запущен. Напиши ему /start в личные сообщения.");

        // 2. ЗАПУСК НЕЙРОСЕТИ И ЮЗЕРБОТА (Маршрутизатор)
        using var classifier = new SmartClassifier("Models/Meta-Llama-3-8B-Instruct.Q4_K_M.gguf");
        Console.WriteLine("Нейросеть загружена в память.");

        using var client = new Client(Config);
        await client.LoginUserIfNeeded();
        Console.WriteLine("Юзербот подключен.");

        var chats = await client.Messages_GetAllChats();
        var allChats = chats.chats.Values.ToList();

        // Собираем все каналы в кэш для удобного UI
        foreach (var chat in allChats)
        {
            if (chat is TlChannel ch && ch.IsChannel && !ch.IsGroup)
                AvailableChannels[ch.ID] = ch.Title;
        }

        string sourceChatName = Environment.GetEnvironmentVariable("SOURCE_CHAT_NAME") ?? "Свалка";
        var sourceChat = allChats.FirstOrDefault(c => c.Title == sourceChatName);

        client.OnUpdates += async (TlUpdatesBase updates) =>
        {
            var validMessages = new List<TlMessage>();
            foreach (var update in updates.UpdateList)
            {
                TlMessage msg = null;
                if (update is TlUpdateNewMessage unm) msg = unm.message as TlMessage;
                else if (update is TlUpdateNewChannelMessage uncm) msg = uncm.message as TlMessage;

                if (msg != null && msg.peer_id.ID == sourceChat?.ID)
                    validMessages.Add(msg);
            }

            var liveGroups = validMessages.GroupBy(m => m.grouped_id == 0 ? m.id : m.grouped_id).ToList();
            foreach (var group in liveGroups)
            {
                if (group.Any()) await ProcessMessageGroup(group.ToList(), client, classifier, sourceChat, allChats);
            }
        };

        await Task.Delay(-1);
    }

    // ==========================================
    // ЛОГИКА UI (ОФИЦИАЛЬНЫЙ БОТ С КНОПКАМИ)
    // ==========================================
    private static async Task HandleBotUpdateAsync(Telegram.Bot.ITelegramBotClient bot, BotUpdate update, CancellationToken ct)
    {
        if (update.Type == BotType.Message && update.Message?.Text == "/start")
        {
            await ShowMainMenu(bot, update.Message.Chat.Id);
        }
        else if (update.Type == BotType.CallbackQuery)
        {
            var cq = update.CallbackQuery;
            string[] data = cq.Data.Split('|');
            string action = data[0];

            if (action == "menu") await ShowMainMenu(bot, cq.Message.Chat.Id, cq.Message.MessageId);
            else if (action == "untagged")
            {
                SkippedChannels.Clear(); // Сбрасываем пропуски
                await ShowNextUntagged(bot, cq.Message.Chat.Id, cq.Message.MessageId);
            }
            else if (action == "set")
            {
                long id = long.Parse(data[1]);
                ChannelMappings[id] = data[2];
                SaveDatabase();
                await bot.AnswerCallbackQueryAsync(cq.Id, $"✅ Сохранено: {data[2]}");
                await ShowNextUntagged(bot, cq.Message.Chat.Id, cq.Message.MessageId);
            }
            else if (action == "skip")
            {
                SkippedChannels.Add(long.Parse(data[1]));
                await ShowNextUntagged(bot, cq.Message.Chat.Id, cq.Message.MessageId);
            }
            else if (action == "edit_cats") await ShowCategoriesForEdit(bot, cq.Message.Chat.Id, cq.Message.MessageId);
            else if (action == "edit_list") await ShowChannelsByTag(bot, cq.Message.Chat.Id, cq.Message.MessageId, data[1]);
            else if (action == "remove")
            {
                ChannelMappings.Remove(long.Parse(data[1]));
                SaveDatabase();
                await bot.AnswerCallbackQueryAsync(cq.Id, "🗑 Канал удален из базы");
                await ShowCategoriesForEdit(bot, cq.Message.Chat.Id, cq.Message.MessageId);
            }
        }
    }

    private static async Task ShowMainMenu(Telegram.Bot.ITelegramBotClient bot, long chatId, int? msgId = null)
    {
        int untaggedCount = AvailableChannels.Keys.Count(k => !ChannelMappings.ContainsKey(k) && !RoutingRules.ContainsValue(AvailableChannels[k]));

        var keyboard = new InlineKeyboard(new[] {
            new[] { InlineButton.WithCallbackData($"🔍 Разметить новые ({untaggedCount})", "untagged") },
            new[] { InlineButton.WithCallbackData($"📋 Мои привязки ({ChannelMappings.Count})", "edit_cats") }
        });

        string text = "🎛 **Панель управления маршрутизатором**\nВыберите действие:";
        if (msgId.HasValue) await bot.EditMessageTextAsync(chatId, msgId.Value, text, replyMarkup: keyboard, parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown);
        else await bot.SendMessageAsync(chatId, text, replyMarkup: keyboard, parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown);
    }

    private static async Task ShowNextUntagged(Telegram.Bot.ITelegramBotClient bot, long chatId, int msgId)
    {
        // Ищем первый канал, которого нет в базе, который не архив и который мы еще не пропустили
        var next = AvailableChannels.FirstOrDefault(c =>
            !ChannelMappings.ContainsKey(c.Key) &&
            !RoutingRules.ContainsValue(c.Value) &&
            !SkippedChannels.Contains(c.Key));

        if (next.Key == 0)
        {
            var kb = new InlineKeyboard(new[] { new[] { InlineButton.WithCallbackData("🔙 В главное меню", "menu") } });
            await bot.EditMessageTextAsync(chatId, msgId, "🎉 **Все доступные каналы размечены!**", replyMarkup: kb, parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown);
            return;
        }

        var keyboard = new InlineKeyboard(new[] {
            new[] { InlineButton.WithCallbackData("CODE", $"set|{next.Key}|CODE"), InlineButton.WithCallbackData("MEDIA", $"set|{next.Key}|MEDIA") },
            new[] { InlineButton.WithCallbackData("NSFW", $"set|{next.Key}|NSFW"), InlineButton.WithCallbackData("HENTAI", $"set|{next.Key}|HENTAI") },
            new[] { InlineButton.WithCallbackData("LINK", $"set|{next.Key}|LINK"), InlineButton.WithCallbackData("NOTE", $"set|{next.Key}|NOTE") },
            new[] { InlineButton.WithCallbackData("OTHER", $"set|{next.Key}|OTHER") },
            new[] { InlineButton.WithCallbackData("⏩ Пропустить", $"skip|{next.Key}") },
            new[] { InlineButton.WithCallbackData("🔙 В меню", "menu") }
        });

        await bot.EditMessageTextAsync(chatId, msgId, $"Куда отправлять посты из канала:\n👉 **{next.Value}**", replyMarkup: keyboard, parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown);
    }

    private static async Task ShowCategoriesForEdit(Telegram.Bot.ITelegramBotClient bot, long chatId, int msgId)
    {
        var buttons = Tags.Select(tag =>
            new[] { InlineButton.WithCallbackData($"{tag} ({ChannelMappings.Values.Count(v => v == tag)})", $"edit_list|{tag}") }
        ).ToList();
        buttons.Add(new[] { InlineButton.WithCallbackData("🔙 В меню", "menu") });

        await bot.EditMessageTextAsync(chatId, msgId, "📂 Выберите категорию для просмотра привязанных каналов:", replyMarkup: new InlineKeyboard(buttons));
    }

    private static async Task ShowChannelsByTag(Telegram.Bot.ITelegramBotClient bot, long chatId, int msgId, string tag)
    {
        var channels = ChannelMappings.Where(x => x.Value == tag).Take(50).ToList(); // Лимит кнопок Телеграма
        var buttons = channels.Select(c =>
        {
            string name = AvailableChannels.ContainsKey(c.Key) ? AvailableChannels[c.Key] : $"ID: {c.Key}";
            return new[] { InlineButton.WithCallbackData($"❌ {name}", $"remove|{c.Key}") };
        }).ToList();

        buttons.Add(new[] { InlineButton.WithCallbackData("🔙 Назад к категориям", "edit_cats") });

        await bot.EditMessageTextAsync(chatId, msgId, $"📌 Привязки для **{tag}**\n*(Нажми на канал, чтобы удалить привязку)*", replyMarkup: new InlineKeyboard(buttons), parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown);
    }

    private static Task HandleBotErrorAsync(Telegram.Bot.ITelegramBotClient bot, Exception ex, CancellationToken ct)
    {
        Console.WriteLine($"[UI БОТ ОШИБКА] {ex.Message}");
        return Task.CompletedTask;
    }

    // ==========================================
    // ЛОГИКА МАРШРУТИЗАЦИИ (ЮЗЕРБОТ)
    // ==========================================
    private static async Task ProcessMessageGroup(List<TlMessage> msgs, Client client, SmartClassifier classifier, TlChatBase sourceChat, List<TlChatBase> allChats)
    {
        if (msgs.Count == 0) return;
        string content = string.Join(" ", msgs.Select(m => m.message?.Trim()).Where(s => !string.IsNullOrEmpty(s)));
        string targetChatName = null;

        // FAST-ROUTING (Обход нейросети)
        var fwdMsg = msgs.FirstOrDefault(m => m.fwd_from != null);
        if (fwdMsg != null && fwdMsg.fwd_from.from_id is TlPeerChannel pc)
        {
            if (ChannelMappings.TryGetValue(pc.channel_id, out string mappedTag))
            {
                targetChatName = RoutingRules[mappedTag];
                Console.WriteLine($"[FAST-ROUTE] Канал в базе! Отправляем в {targetChatName}.");
            }
            else
            {
                var fwdChat = allChats.FirstOrDefault(c => c.ID == pc.channel_id);
                if (fwdChat != null)
                {
                    content = $"[Переслано из: {fwdChat.Title}]\n" + content;
                    AvailableChannels[pc.channel_id] = fwdChat.Title; // Добавляем неизвестный канал в кэш для UI
                }
            }
        }

        // ЕСЛИ НЕ В БАЗЕ — ДУМАЕТ LLAMA 3
        if (targetChatName == null)
        {
            bool hasMedia = msgs.Any(m => m.media != null && (m.media is TL.MessageMediaPhoto || m.media is TL.MessageMediaDocument));
            if (string.IsNullOrEmpty(content) && hasMedia) targetChatName = RoutingRules["MEDIA"];
            else if (Uri.IsWellFormedUriString(content, UriKind.Absolute)) targetChatName = RoutingRules["LINK"];
            else if (!string.IsNullOrEmpty(content))
            {
                string category = await classifier.PredictCategory(content);
                targetChatName = RoutingRules.ContainsKey(category) ? RoutingRules[category] : RoutingRules["OTHER"];
            }
            else targetChatName = RoutingRules["OTHER"];
        }

        var targetChat = allChats.FirstOrDefault(c => c.Title == targetChatName);
        if (targetChat != null)
        {
            int[] msgIds = msgs.Select(m => m.id).ToArray();
            long[] randomIds = msgs.Select(_ => WTelegram.Helpers.RandomLong()).ToArray();
            await client.Messages_ForwardMessages(sourceChat, msgIds, randomIds, targetChat);
            if (sourceChat is TlChannel sourceChannel) await client.Channels_DeleteMessages(sourceChannel, msgIds);
            else await client.Messages_DeleteMessages(msgIds, revoke: true);
            await Task.Delay(1500);
        }
    }

    private static void SaveDatabase() => File.WriteAllText(DB_FILE, JsonSerializer.Serialize(ChannelMappings));
    private static void LoadDatabase()
    {
        if (File.Exists(DB_FILE)) ChannelMappings = JsonSerializer.Deserialize<Dictionary<long, string>>(File.ReadAllText(DB_FILE)) ?? new();
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