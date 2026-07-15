using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
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
    private readonly string[] _tags = { "Pvideo", "Pimages", "OnlyK", "Himages", "Hvideo", "Hmanga", "Hmix", "Hgame", "IGNORE", "OTHER", "MANUAL","Pmix" }; 
    private readonly HashSet<string> _skippedItems = new();
    private ITelegramBotClient _botClient;
    private int _currentPreviewIndex = 0;
    private readonly HashSet<int> _ignoredPreviewIds = new();
    private bool _isScanning = false;
    private int _lastPreviewedMsgId = 0;
    private CancellationTokenSource _debounceCts;

    // ДОБАВЛЕНО: Потокобезопасная коллекция для хранения ID пересланных превью
    private readonly ConcurrentBag<int> _previewMessageIds = new();

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
            _debounceCts?.Cancel();
            _debounceCts = new CancellationTokenSource();
            var token = _debounceCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1500, token);
                    if (token.IsCancellationRequested) return;

                    long chatId = _repository.GetAdminChatId();
                    if (chatId == 0) return;

                    var active = _repository.GetActiveMenu();
                    var pendingMsgs = _repository.GetPendingDirectMessages().Keys.ToList();

                    if (pendingMsgs.Count > 0)
                    {
                        await ShowCurrentMessageMenu(chatId, active?.MessageId);
                        return;
                    }

                    var nextCh = _repository.GetAvailableChannels().FirstOrDefault(c => !_repository.GetMappings().ContainsKey(c.Key) && !_skippedItems.Contains(c.Key.ToString()));
                    if (nextCh.Key != 0) { await ShowNextUntaggedChannel(_botClient, chatId, active?.MessageId, isNewMessage: true); return; }

                    var nextDom = _repository.GetAvailableDomains().FirstOrDefault(d => !_repository.GetDomainMappings().ContainsKey(d.Key) && !_skippedItems.Contains(d.Key));
                    if (nextDom.Key != null) { await ShowNextUntaggedDomain(_botClient, chatId, active?.MessageId, isNewMessage: true); return; }
                }
                catch (TaskCanceledException) { }
                catch (Exception ex) { Console.WriteLine($"[UI-AUTO-PROMPT ERROR] {ex.Message}"); }
            });
        };

        _botClient.StartReceiving(HandleBotUpdateAsync, (b, e, c) => Task.CompletedTask, new ReceiverOptions { AllowedUpdates = new[] { BotType.Message, BotType.CallbackQuery } }, stoppingToken);
        return Task.CompletedTask;
    }

    // ДОБАВЛЕНО: Метод для очистки всех пересланных медиа из чата
    private async Task ClearPreviews(ITelegramBotClient bot, long chatId)
    {
        while (_previewMessageIds.TryTake(out int id))
        {
            try { await bot.DeleteMessage(chatId, id); } catch { }
        }
    }

    private async Task HandleBotUpdateAsync(ITelegramBotClient bot, BotUpdate update, CancellationToken ct)
    {
        if (update.Type == BotType.Message)
        {
            var msg = update.Message;
            if (msg != null)
            {
                // Если это команда (начинается со слэша)
                if (!string.IsNullOrEmpty(msg.Text) && msg.Text.StartsWith("/"))
                {
                    // Сразу удаляем команду из чата
                    try { await bot.DeleteMessage(msg.Chat.Id, msg.MessageId); } catch { }

                    if (msg.Text == "/start" || msg.Text == "/menu")
                    {
                        var activeMenu = _repository.GetActiveMenu();
                        if (activeMenu != null) try { await bot.DeleteMessage(activeMenu.Value.ChatId, activeMenu.Value.MessageId); } catch { }
                        await ShowMainMenu(bot, msg.Chat.Id, isNewMessage: true);
                    }
                    else if (msg.Text == "/scan")
                    {
                        _repository.RequestForceScan();
                        var tempMsg = await bot.SendMessage(msg.Chat.Id, "🔄 Сканирование истории запущено заново...");
                        await Task.Delay(3000); // Показываем сообщение 3 секунды
                        try { await bot.DeleteMessage(msg.Chat.Id, tempMsg.MessageId); } catch { } // Удаляем уведомление
                    }
                }
                else
                {
                    // Сохраняем ID пересланного юзерботом превью
                    _previewMessageIds.Add(msg.MessageId);
                }
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
                else if (action == "menu") { await ClearPreviews(bot, cq.Message.Chat.Id); await ShowMainMenu(bot, cq.Message.Chat.Id, cq.Message.MessageId); }
                else if (action == "untagged_ch") { _skippedItems.Clear(); await ShowNextUntaggedChannel(bot, cq.Message.Chat.Id, cq.Message.MessageId); }
                else if (action == "untagged_dom") { _skippedItems.Clear(); await ShowNextUntaggedDomain(bot, cq.Message.Chat.Id, cq.Message.MessageId); }

                else if (action == "untagged_dir") { _skippedItems.Clear(); _currentPreviewIndex = 0; await ShowCurrentMessageMenu(cq.Message.Chat.Id, cq.Message.MessageId); }

                else if (action == "next_msg") { await ClearPreviews(bot, cq.Message.Chat.Id); _currentPreviewIndex++; await ShowCurrentMessageMenu(cq.Message.Chat.Id, cq.Message.MessageId); }

                else if (action == "set_ch") { await ClearPreviews(bot, cq.Message.Chat.Id); _repository.SaveTag(long.Parse(data[1]), data[2]); await bot.AnswerCallbackQuery(cq.Id, $"✅ {data[2]}"); await ShowNextUntaggedChannel(bot, cq.Message.Chat.Id, cq.Message.MessageId); }
                else if (action == "ignore_ch") { await ClearPreviews(bot, cq.Message.Chat.Id); _repository.SaveTag(long.Parse(data[1]), "IGNORE"); await bot.AnswerCallbackQuery(cq.Id, "🚫 Скрыто"); await ShowNextUntaggedChannel(bot, cq.Message.Chat.Id, cq.Message.MessageId); }
                else if (action == "skip_ch") { await ClearPreviews(bot, cq.Message.Chat.Id); _skippedItems.Add(data[1]); await ShowNextUntaggedChannel(bot, cq.Message.Chat.Id, cq.Message.MessageId); }

                else if (action == "set_dom") { await ClearPreviews(bot, cq.Message.Chat.Id); _repository.SaveDomainTag(data[1], data[2]); await bot.AnswerCallbackQuery(cq.Id, $"✅ {data[2]}"); await ShowNextUntaggedDomain(bot, cq.Message.Chat.Id, cq.Message.MessageId); }
                else if (action == "ignore_dom") { await ClearPreviews(bot, cq.Message.Chat.Id); _repository.SaveDomainTag(data[1], "IGNORE"); await bot.AnswerCallbackQuery(cq.Id, "🚫 Скрыто"); await ShowNextUntaggedDomain(bot, cq.Message.Chat.Id, cq.Message.MessageId); }
                else if (action == "skip_dom") { await ClearPreviews(bot, cq.Message.Chat.Id); _skippedItems.Add(data[1]); await ShowNextUntaggedDomain(bot, cq.Message.Chat.Id, cq.Message.MessageId); }
                else if (action == "set_dir")
                {
                    await ClearPreviews(bot, cq.Message.Chat.Id); // Удаляем превью после выбора тега
                    _repository.SaveDirectMessageTag(int.Parse(data[1]), data[2]);
                    await bot.AnswerCallbackQuery(cq.Id, $"✅ {data[2]}");
                    await ShowCurrentMessageMenu(cq.Message.Chat.Id, cq.Message.MessageId);
                }
                else if (action == "skip_dir")
                {
                    await ClearPreviews(bot, cq.Message.Chat.Id); // Удаляем превью при пропуске
                    _currentPreviewIndex++;
                    await ShowCurrentMessageMenu(cq.Message.Chat.Id, cq.Message.MessageId);
                }

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
        var directMsgs = _repository.GetPendingDirectMessages().Count;
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

    private async Task ShowCurrentMessageMenu(long chatId, int? msgIdToEdit)
    {
        var pendingMsgs = _repository.GetPendingDirectMessages().Keys.ToList();

        if (pendingMsgs.Count == 0)
        {
            string emptyText = "🎉 Все файлы из Свалки отсортированы!";
            var kb = new InlineKeyboard(new[] { new[] { InlineButton.WithCallbackData("🔙 В меню", "menu") } });

            if (msgIdToEdit.HasValue) try { await _botClient.EditMessageText(chatId, msgIdToEdit.Value, emptyText, replyMarkup: kb); } catch { }
            else await _botClient.SendMessage(chatId, emptyText, replyMarkup: kb);
            return;
        }

        if (_currentPreviewIndex >= pendingMsgs.Count) _currentPreviewIndex = 0;

        int currentMsgId = pendingMsgs[_currentPreviewIndex];

        string text = $"<b>Куда отправить сообщение №{_currentPreviewIndex + 1} из {pendingMsgs.Count}?</b>\n⬆️ <i>Превью загружено выше</i>";

        // ДОБАВЛЕНО: Все доступные теги на клавиатуре
        var btns = new List<InlineButton[]> {
            new[] { InlineButton.WithCallbackData("🎥 Pvideo", $"set_dir|{currentMsgId}|Pvideo"), InlineButton.WithCallbackData("🖼 Pimages", $"set_dir|{currentMsgId}|Pimages") },
            new[] { InlineButton.WithCallbackData("🔀 Pmix", $"set_dir|{currentMsgId}|Pmix"), InlineButton.WithCallbackData("🌟 OnlyK", $"set_dir|{currentMsgId}|OnlyK") },
            new[] { InlineButton.WithCallbackData("🎞 Hvideo", $"set_dir|{currentMsgId}|Hvideo"), InlineButton.WithCallbackData("🎨 Himages", $"set_dir|{currentMsgId}|Himages") },
            new[] { InlineButton.WithCallbackData("📚 Hmanga", $"set_dir|{currentMsgId}|Hmanga"), InlineButton.WithCallbackData("🔀 Hmix", $"set_dir|{currentMsgId}|Hmix") },
            new[] { InlineButton.WithCallbackData("👾 Hgame", $"set_dir|{currentMsgId}|Hgame"), InlineButton.WithCallbackData("🗑 Trash", $"set_dir|{currentMsgId}|TRASH") },
            new[] { InlineButton.WithCallbackData("⏩ Далее", "next_msg"), InlineButton.WithCallbackData("🔙 В меню", "menu") }
        };

        if (_lastPreviewedMsgId != currentMsgId)
        {
            _repository.RequestPreview(currentMsgId);
            _lastPreviewedMsgId = currentMsgId;
            await Task.Delay(1000);
        }

        if (msgIdToEdit.HasValue)
        {
            try { await _botClient.DeleteMessage(chatId, msgIdToEdit.Value); } catch { }
        }

        var msg = await _botClient.SendMessage(chatId, text, replyMarkup: new InlineKeyboard(btns), parseMode: ParseMode.Html);
        _repository.SetActiveMenu(chatId, msg.MessageId);
    }

    private async Task ShowNextUntaggedChannel(ITelegramBotClient bot, long chatId, int? msgIdToEdit, bool isNewMessage = false)
    {
        var next = _repository.GetAvailableChannels().FirstOrDefault(c => !_repository.GetMappings().ContainsKey(c.Key) && !_skippedItems.Contains(c.Key.ToString()));
        if (next.Key == 0) { await ShowMainMenu(bot, chatId, msgIdToEdit, isNewMessage); return; }

        string text = $"Куда отправлять посты из канала:\n👉 <b>{next.Value.Title.Replace("<", "").Replace(">", "")}</b>\n⬆️ <i>Превью контента загружено выше</i>";
        var kb = GetTagsKeyboard(next.Key.ToString(), "ch", next.Value.Url);

        int triggerMsgId = next.Value.TriggerMsgId;
        // Запрашиваем превью у Юзербота
        if (triggerMsgId != 0 && _lastPreviewedMsgId != triggerMsgId)
        {
            _repository.RequestPreview(triggerMsgId);
            _lastPreviewedMsgId = triggerMsgId;
            await Task.Delay(1000); // Даем юзерботу секунду на пересылку превью
        }

        // Удаляем старое меню, чтобы новое появилось СТРОГО ПОД превью
        if (msgIdToEdit.HasValue)
        {
            try { await bot.DeleteMessage(chatId, msgIdToEdit.Value); } catch { }
        }

        var msg = await bot.SendMessage(chatId, text, replyMarkup: kb, parseMode: ParseMode.Html);
        _repository.SetActiveMenu(chatId, msg.MessageId);
    }
    private async Task ShowNextUntaggedDomain(ITelegramBotClient bot, long chatId, int? msgIdToEdit, bool isNewMessage = false)
    {
        var next = _repository.GetAvailableDomains().FirstOrDefault(d => !_repository.GetDomainMappings().ContainsKey(d.Key) && !_skippedItems.Contains(d.Key));
        if (next.Key == null) { await ShowMainMenu(bot, chatId, msgIdToEdit, isNewMessage); return; }

        string text = $"Куда отправлять ссылки с доменом:\n👉 <b>{next.Key}</b>\n⬆️ <i>Превью контента загружено выше</i>";
        var kb = GetTagsKeyboard(next.Key, "dom", null);

        int triggerMsgId = next.Value;
        // Запрашиваем превью у Юзербота
        if (triggerMsgId != 0 && _lastPreviewedMsgId != triggerMsgId)
        {
            _repository.RequestPreview(triggerMsgId);
            _lastPreviewedMsgId = triggerMsgId;
            await Task.Delay(1000); // Даем юзерботу секунду на пересылку превью
        }

        // Удаляем старое меню, чтобы новое появилось СТРОГО ПОД превью
        if (msgIdToEdit.HasValue)
        {
            try { await bot.DeleteMessage(chatId, msgIdToEdit.Value); } catch { }
        }

        var msg = await bot.SendMessage(chatId, text, replyMarkup: kb, parseMode: ParseMode.Html);
        _repository.SetActiveMenu(chatId, msg.MessageId);
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

        // ЗДЕСЬ ДОБАВЛЕНА КНОПКА MANUAL:
        btns.Add(new[] { InlineButton.WithCallbackData("👾 Hgame", $"set_{type}|{id}|Hgame"), InlineButton.WithCallbackData("🤔 Спрашивать", $"set_{type}|{id}|MANUAL") });

        btns.Add(new[] { InlineButton.WithCallbackData("⏩ Пропустить", $"skip_{type}|{id}"), InlineButton.WithCallbackData("🚫 Игнор", $"ignore_{type}|{id}") });
        btns.Add(new[] { InlineButton.WithCallbackData("🔙 В меню", "menu") });
        return new InlineKeyboard(btns);
    }
}