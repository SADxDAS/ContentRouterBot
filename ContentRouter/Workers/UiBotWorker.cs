using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types.Enums;
using BotUpdate = Telegram.Bot.Types.Update;
using BotType = Telegram.Bot.Types.Enums.UpdateType;
using InlineKeyboard = Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup;
using InlineButton = Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton;

public class UiBotWorker : BackgroundService
{
    private readonly IChannelRepository _repository;
    private readonly string[] _tags = { "Pvideo", "Pimages", "OnlyK", "Himages", "Hvideo", "Hmanga", "Hmix", "Hgame", "IGNORE", "OTHER" };
    private readonly HashSet<string> _skippedItems = new();
    private ITelegramBotClient _botClient;

    public UiBotWorker(IChannelRepository repository)
    {
        _repository = repository;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string botToken = Environment.GetEnvironmentVariable("BOT_TOKEN");
        if (string.IsNullOrEmpty(botToken)) return Task.CompletedTask;

        _botClient = new TelegramBotClient(botToken);

        _repository.OnNewUntaggedItem += () =>
        {
            _ = Task.Run(async () =>
            {
                long chatId = _repository.GetAdminChatId();
                if (chatId == 0) return;

                var active = _repository.GetActiveMenu();
                if (active != null) try { await _botClient.DeleteMessage(active.Value.ChatId, active.Value.MessageId); } catch { }

                try
                {
                    var nextCh = _repository.GetAvailableChannels().FirstOrDefault(c => !_repository.GetMappings().ContainsKey(c.Key) && !_skippedItems.Contains(c.Key.ToString()));
                    if (nextCh.Key != 0) { await ShowNextUntaggedChannel(_botClient, chatId, null, isNewMessage: true); return; }

                    var nextDom = _repository.GetAvailableDomains().FirstOrDefault(d => !_repository.GetDomainMappings().ContainsKey(d.Key) && !_skippedItems.Contains(d.Key));
                    if (nextDom.Key != null) { await ShowNextUntaggedDomain(_botClient, chatId, null, isNewMessage: true); return; }

                    var nextDir = _repository.GetPendingDirectMessages().FirstOrDefault(d => !_skippedItems.Contains("dir_" + d.Key));
                    if (nextDir.Key != 0) { await ShowNextUntaggedDirect(_botClient, chatId, null, isNewMessage: true); return; }
                }
                catch (Exception ex) { Console.WriteLine($"[UI-AUTO-PROMPT ERROR] {ex.Message}"); }
            });
        };

        _botClient.StartReceiving(HandleBotUpdateAsync, (b, e, c) => Task.CompletedTask, new ReceiverOptions { AllowedUpdates = new[] { BotType.Message, BotType.CallbackQuery } }, stoppingToken);
        return Task.CompletedTask;
    }

    private async Task HandleBotUpdateAsync(ITelegramBotClient bot, BotUpdate update, CancellationToken ct)
    {
        if (update.Type == BotType.Message)
        {
            var msg = update.Message;
            if (msg != null && (msg.Text == "/start" || msg.Text == "/menu"))
            {
                try { await bot.DeleteMessage(msg.Chat.Id, msg.MessageId); } catch { }
                var activeMenu = _repository.GetActiveMenu();
                if (activeMenu != null) try { await bot.DeleteMessage(activeMenu.Value.ChatId, activeMenu.Value.MessageId); } catch { }
                await ShowMainMenu(bot, msg.Chat.Id, isNewMessage: true);
            }
        }
        else if (update.Type == BotType.CallbackQuery)
        {
            var cq = update.CallbackQuery;
            string[] data = cq.Data.Split('|');
            string action = data[0];

            try
            {
                if (action == "pause_bot") { _repository.IsPaused = true; await ShowMainMenu(bot, cq.Message.Chat.Id, cq.Message.MessageId); }
                else if (action == "resume_bot") { _repository.IsPaused = false; await ShowMainMenu(bot, cq.Message.Chat.Id, cq.Message.MessageId); }
                else if (action == "menu") await ShowMainMenu(bot, cq.Message.Chat.Id, cq.Message.MessageId);
                else if (action == "untagged_ch") { _skippedItems.Clear(); await ShowNextUntaggedChannel(bot, cq.Message.Chat.Id, cq.Message.MessageId); }
                else if (action == "untagged_dom") { _skippedItems.Clear(); await ShowNextUntaggedDomain(bot, cq.Message.Chat.Id, cq.Message.MessageId); }
                else if (action == "untagged_dir") { _skippedItems.Clear(); await ShowNextUntaggedDirect(bot, cq.Message.Chat.Id, cq.Message.MessageId); }

                else if (action == "set_ch") { _repository.SaveTag(long.Parse(data[1]), data[2]); await bot.AnswerCallbackQuery(cq.Id, $"✅ {data[2]}"); await ShowNextUntaggedChannel(bot, cq.Message.Chat.Id, cq.Message.MessageId); }
                else if (action == "ignore_ch") { _repository.SaveTag(long.Parse(data[1]), "IGNORE"); await bot.AnswerCallbackQuery(cq.Id, "🚫 Скрыто"); await ShowNextUntaggedChannel(bot, cq.Message.Chat.Id, cq.Message.MessageId); }
                else if (action == "skip_ch") { _skippedItems.Add(data[1]); await ShowNextUntaggedChannel(bot, cq.Message.Chat.Id, cq.Message.MessageId); }

                else if (action == "set_dom") { _repository.SaveDomainTag(data[1], data[2]); await bot.AnswerCallbackQuery(cq.Id, $"✅ {data[2]}"); await ShowNextUntaggedDomain(bot, cq.Message.Chat.Id, cq.Message.MessageId); }
                else if (action == "ignore_dom") { _repository.SaveDomainTag(data[1], "IGNORE"); await bot.AnswerCallbackQuery(cq.Id, "🚫 Скрыто"); await ShowNextUntaggedDomain(bot, cq.Message.Chat.Id, cq.Message.MessageId); }
                else if (action == "skip_dom") { _skippedItems.Add(data[1]); await ShowNextUntaggedDomain(bot, cq.Message.Chat.Id, cq.Message.MessageId); }

                else if (action == "set_dir") { _repository.SaveDirectMessageTag(int.Parse(data[1]), data[2]); await bot.AnswerCallbackQuery(cq.Id, $"✅ {data[2]}"); await ShowNextUntaggedDirect(bot, cq.Message.Chat.Id, cq.Message.MessageId); }
                else if (action == "skip_dir") { _skippedItems.Add("dir_" + data[1]); await ShowNextUntaggedDirect(bot, cq.Message.Chat.Id, cq.Message.MessageId); }

                else if (action == "edit_cats") { await ShowCategoriesForEdit(bot, cq.Message.Chat.Id, cq.Message.MessageId); }
                else if (action == "edit_list") { int p = data.Length > 2 ? int.Parse(data[2]) : 0; await ShowItemsByTag(bot, cq.Message.Chat.Id, cq.Message.MessageId, data[1], p); }
                else if (action == "remove_ch") { _repository.RemoveTag(long.Parse(data[1])); await bot.AnswerCallbackQuery(cq.Id, "🗑 Убрано"); await ShowCategoriesForEdit(bot, cq.Message.Chat.Id, cq.Message.MessageId); }
                else if (action == "remove_dom") { _repository.RemoveDomainTag(data[1]); await bot.AnswerCallbackQuery(cq.Id, "🗑 Убрано"); await ShowCategoriesForEdit(bot, cq.Message.Chat.Id, cq.Message.MessageId); }
            }
            catch (Exception ex) { Console.WriteLine($"[UI-ERROR] {ex.Message}"); }
        }
    }

    private async Task ShowMainMenu(ITelegramBotClient bot, long chatId, int? msgId = null, bool isNewMessage = false)
    {
        var channels = _repository.GetAvailableChannels().Keys.Count(k => !_repository.GetMappings().ContainsKey(k));
        var domains = _repository.GetAvailableDomains().Count(d => !_repository.GetDomainMappings().ContainsKey(d.Key));
        var directMsgs = _repository.GetPendingDirectMessages().Count(d => !_skippedItems.Contains("dir_" + d.Key));
        var totalMappings = _repository.GetMappings().Count + _repository.GetDomainMappings().Count;

        bool isPaused = _repository.IsPaused;
        string pauseText = isPaused ? "▶️ Возобновить фильтрацию" : "⏸ Приостановить фильтрацию";
        string pauseAction = isPaused ? "resume_bot" : "pause_bot";

        var keyboard = new InlineKeyboard(new[] {
            new[] { InlineButton.WithCallbackData(pauseText, pauseAction) },
            new[] { InlineButton.WithCallbackData($"📢 Новые каналы ({channels})", "untagged_ch") },
            new[] { InlineButton.WithCallbackData($"🔗 Новые домены ({domains})", "untagged_dom") },
            new[] { InlineButton.WithCallbackData($"📁 Прямые файлы ({directMsgs})", "untagged_dir") },
            new[] { InlineButton.WithCallbackData($"📋 Мои привязки ({totalMappings})", "edit_cats") }
        });

        string text = $"🎛 <b>Маршрутизатор (SingleApp)</b>\nСтатус: {(isPaused ? "⏸ ПАУЗА" : "▶️ РАБОТАЕТ")}\nВыберите действие:";
        if (isNewMessage || msgId == null) { var msg = await bot.SendMessage(chatId, text, replyMarkup: keyboard, parseMode: ParseMode.Html); _repository.SetActiveMenu(chatId, msg.MessageId); }
        else { await bot.EditMessageText(chatId, msgId.Value, text, replyMarkup: keyboard, parseMode: ParseMode.Html); }
    }

    private async Task ShowNextUntaggedChannel(ITelegramBotClient bot, long chatId, int? msgId, bool isNewMessage = false)
    {
        var next = _repository.GetAvailableChannels().FirstOrDefault(c => !_repository.GetMappings().ContainsKey(c.Key) && !_skippedItems.Contains(c.Key.ToString()));
        if (next.Key == 0) { await ShowMainMenu(bot, chatId, msgId, isNewMessage); return; }

        string text = $"Куда отправлять посты из канала:\n👉 <b>{next.Value.Title.Replace("<", "").Replace(">", "")}</b>";
        var kb = GetTagsKeyboard(next.Key.ToString(), "ch", next.Value.Url);

        if (isNewMessage || msgId == null)
        {
            try { await bot.CopyMessage(chatId, chatId, next.Value.TriggerMsgId); } catch { }
            var msg = await bot.SendMessage(chatId, text, replyMarkup: kb, parseMode: ParseMode.Html);
            _repository.SetActiveMenu(chatId, msg.MessageId);
        }
        else { await bot.EditMessageText(chatId, msgId.Value, text, replyMarkup: kb, parseMode: ParseMode.Html); }
    }

    private async Task ShowNextUntaggedDomain(ITelegramBotClient bot, long chatId, int? msgId, bool isNewMessage = false)
    {
        var next = _repository.GetAvailableDomains().FirstOrDefault(d => !_repository.GetDomainMappings().ContainsKey(d.Key) && !_skippedItems.Contains(d.Key));
        if (next.Key == null) { await ShowMainMenu(bot, chatId, msgId, isNewMessage); return; }

        string text = $"Куда отправлять ссылки с доменом:\n👉 <b>{next.Key}</b>";
        var kb = GetTagsKeyboard(next.Key, "dom", null);

        if (isNewMessage || msgId == null)
        {
            try { await bot.CopyMessage(chatId, chatId, next.Value); } catch { }
            var msg = await bot.SendMessage(chatId, text, replyMarkup: kb, parseMode: ParseMode.Html);
            _repository.SetActiveMenu(chatId, msg.MessageId);
        }
        else { await bot.EditMessageText(chatId, msgId.Value, text, replyMarkup: kb, parseMode: ParseMode.Html); }
    }

    private async Task ShowNextUntaggedDirect(ITelegramBotClient bot, long chatId, int? msgId, bool isNewMessage = false)
    {
        var next = _repository.GetPendingDirectMessages().FirstOrDefault(d => !_skippedItems.Contains("dir_" + d.Key));
        if (next.Key == 0) { await ShowMainMenu(bot, chatId, msgId, isNewMessage); return; }

        var btns = new List<InlineButton[]> {
            new[] { InlineButton.WithCallbackData("🔀 Pmix (Смешанное)", $"set_dir|{next.Key}|Pmix") },
            new[] { InlineButton.WithCallbackData("🔀 Hmix (Смешанное)", $"set_dir|{next.Key}|Hmix") },
            new[] { InlineButton.WithCallbackData("🗑 В Trash (Мусор)", $"set_dir|{next.Key}|TRASH") },
            new[] { InlineButton.WithCallbackData("⏩ Пропустить", $"skip_dir|{next.Key}"), InlineButton.WithCallbackData("🔙 В меню", "menu") }
        };

        // --- ИСПРАВЛЕНИЕ ---
        // Сюда нужно вписать ID вашего канала/группы "Свалки" (обязательно с минусом в начале, если это группа/канал)
        // Можно брать из , если добавите в .env
        long sourceChatId = long.Parse(Environment.GetEnvironmentVariable("SOURCE_CHAT_ID"));

        // Теперь ссылка генерируется на оригинальную Свалку, где лежит сообщение
        string cleanChatId = sourceChatId.ToString().Replace("-100", "");
        string msgUrl = $"https://t.me/c/{cleanChatId}/{next.Key}";

        string text = $"Куда отправить файл/сообщение?\n👉 <a href=\"{msgUrl}\">Посмотреть оригинал в Свалке</a>\nПревью: <i>{next.Value.Replace("<", "").Replace(">", "")}</i>";

        if (isNewMessage || msgId == null)
        {
            // Теперь бот будет корректно брать файл ИЗ свалки и присылать вам в меню для предпросмотра
            try { await bot.CopyMessage(chatId, sourceChatId, next.Key); }
            catch (Exception ex) { Console.WriteLine($"[ПРЕВЬЮ ОШИБКА] {ex.Message}"); }

            var msg = await bot.SendMessage(chatId, text, replyMarkup: new InlineKeyboard(btns), parseMode: ParseMode.Html);
            _repository.SetActiveMenu(chatId, msg.MessageId);
        }
        else
        {
            await bot.EditMessageText(chatId, msgId.Value, text, replyMarkup: new InlineKeyboard(btns), parseMode: ParseMode.Html);
        }
    }
    private async Task ShowCategoriesForEdit(ITelegramBotClient bot, long chatId, int msgId)
    {
        var ch = _repository.GetMappings(); var dom = _repository.GetDomainMappings();
        var buttons = new List<InlineButton[]>();
        for (int i = 0; i < _tags.Length; i += 2)
        {
            var row = new List<InlineButton>();
            string t1 = _tags[i]; row.Add(InlineButton.WithCallbackData($"{t1} ({ch.Values.Count(v => v == t1) + dom.Values.Count(v => v == t1)})", $"edit_list|{t1}"));
            if (i + 1 < _tags.Length) { string t2 = _tags[i + 1]; row.Add(InlineButton.WithCallbackData($"{t2} ({ch.Values.Count(v => v == t2) + dom.Values.Count(v => v == t2)})", $"edit_list|{t2}")); }
            buttons.Add(row.ToArray());
        }
        buttons.Add(new[] { InlineButton.WithCallbackData("🔙 В меню", "menu") });
        await bot.EditMessageText(chatId, msgId, "📂 <b>Выберите категорию:</b>", replyMarkup: new InlineKeyboard(buttons), parseMode: ParseMode.Html);
    }

    // =====================================
    // ПАГИНАЦИЯ ДЛЯ СПИСКА КАНАЛОВ (ПО 20)
    // =====================================
    private async Task ShowItemsByTag(ITelegramBotClient bot, long chatId, int msgId, string tag, int page = 0)
    {
        int pageSize = 20;
        var allChannels = _repository.GetMappings().Where(x => x.Value == tag).ToList();
        var allDomains = _repository.GetDomainMappings().Where(x => x.Value == tag).ToList();

        var combined = allChannels.Select(c => new { Id = c.Key.ToString(), Type = "ch", Name = _repository.GetAvailableChannels().ContainsKey(c.Key) ? _repository.GetAvailableChannels()[c.Key].Title : $"ID: {c.Key}" })
            .Concat(allDomains.Select(d => new { Id = d.Key, Type = "dom", Name = d.Key }))
            .ToList();

        int totalPages = (int)Math.Ceiling(combined.Count / (double)pageSize);
        var pageItems = combined.Skip(page * pageSize).Take(pageSize).ToList();
        var buttons = new List<InlineButton[]>();

        foreach (var item in pageItems)
        {
            string name = item.Name;
            if (name.Length > 25) name = name.Substring(0, 25) + "..";
            buttons.Add(new[] { InlineButton.WithCallbackData($"❌ {(item.Type == "ch" ? "📢" : "🔗")} {name}", $"remove_{item.Type}|{item.Id}") });
        }

        var navRow = new List<InlineButton>();
        if (page > 0) navRow.Add(InlineButton.WithCallbackData("⬅️ Назад", $"edit_list|{tag}|{page - 1}"));
        if (page < totalPages - 1) navRow.Add(InlineButton.WithCallbackData("Вперед ➡️", $"edit_list|{tag}|{page + 1}"));
        if (navRow.Any()) buttons.Add(navRow.ToArray());

        buttons.Add(new[] { InlineButton.WithCallbackData("🔙 Назад к категориям", "edit_cats") });
        await bot.EditMessageText(chatId, msgId, $"📌 Привязки для <b>{tag}</b> (Стр. {page + 1}/{Math.Max(1, totalPages)})", replyMarkup: new InlineKeyboard(buttons), parseMode: ParseMode.Html);
    }

    private InlineKeyboard GetTagsKeyboard(string id, string type, string url)
    {
        var btns = new List<InlineButton[]>();
        if (!string.IsNullOrEmpty(url)) btns.Add(new[] { InlineButton.WithUrl("👀 Открыть канал", url) });
        btns.Add(new[] { InlineButton.WithCallbackData("🎥 Pvideo", $"set_{type}|{id}|Pvideo"), InlineButton.WithCallbackData("🖼 Pimages", $"set_{type}|{id}|Pimages") });
        btns.Add(new[] { InlineButton.WithCallbackData("🔀 Pmix (Авто)", $"set_{type}|{id}|Pmix"), InlineButton.WithCallbackData("🌟 OnlyK", $"set_{type}|{id}|OnlyK") });
        btns.Add(new[] { InlineButton.WithCallbackData("🎞 Hvideo", $"set_{type}|{id}|Hvideo"), InlineButton.WithCallbackData("🎨 Himages", $"set_{type}|{id}|Himages") });
        btns.Add(new[] { InlineButton.WithCallbackData("📚 Hmanga", $"set_{type}|{id}|Hmanga"), InlineButton.WithCallbackData("🔀 Hmix (Авто)", $"set_{type}|{id}|Hmix") });
        btns.Add(new[] { InlineButton.WithCallbackData("👾 Hgame", $"set_{type}|{id}|Hgame") });
        btns.Add(new[] { InlineButton.WithCallbackData("⏩ Пропустить", $"skip_{type}|{id}"), InlineButton.WithCallbackData("🚫 Игнор", $"ignore_{type}|{id}") });
        btns.Add(new[] { InlineButton.WithCallbackData("🔙 В меню", "menu") });
        return new InlineKeyboard(btns);
    }
}