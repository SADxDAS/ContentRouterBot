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
    private readonly HashSet<string> _skippedItems = new();

    public UiBotWorker(IChannelRepository repository)
    {
        _repository = repository;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string botToken = Environment.GetEnvironmentVariable("BOT_TOKEN");
        if (string.IsNullOrEmpty(botToken)) return Task.CompletedTask;

        var uiBot = new TelegramBotClient(botToken);
        uiBot.StartReceiving(HandleBotUpdateAsync, (b, e, c) => Task.CompletedTask, new ReceiverOptions { AllowedUpdates = new[] { BotType.Message, BotType.CallbackQuery } }, stoppingToken);
        return Task.CompletedTask;
    }

    private async Task HandleBotUpdateAsync(ITelegramBotClient bot, BotUpdate update, CancellationToken ct)
    {
        // SINGLE MESSAGE APP LOGIC
        if (update.Type == BotType.Message)
        {
            if (update.Message != null)
            {
                // Пытаемся удалить сообщение пользователя, чтобы не засорять чат
                try { await bot.DeleteMessage(update.Message.Chat.Id, update.Message.MessageId); } catch { }

                // Удаляем старое меню
                var activeMenu = _repository.GetActiveMenu();
                if (activeMenu != null)
                {
                    try { await bot.DeleteMessage(activeMenu.Value.ChatId, activeMenu.Value.MessageId); } catch { }
                }

                // Рисуем новое меню
                await ShowMainMenu(bot, update.Message.Chat.Id, isNewMessage: true);
            }
        }
        else if (update.Type == BotType.CallbackQuery)
        {
            var cq = update.CallbackQuery;
            string[] data = cq.Data.Split('|');
            string action = data[0];

            try
            {
                if (action == "menu") await ShowMainMenu(bot, cq.Message.Chat.Id, cq.Message.MessageId);
                else if (action == "untagged_ch") { _skippedItems.Clear(); await ShowNextUntaggedChannel(bot, cq.Message.Chat.Id, cq.Message.MessageId); }
                else if (action == "untagged_dom") { _skippedItems.Clear(); await ShowNextUntaggedDomain(bot, cq.Message.Chat.Id, cq.Message.MessageId); }

                else if (action == "set_ch") { _repository.SaveTag(long.Parse(data[1]), data[2]); await bot.AnswerCallbackQuery(cq.Id, $"✅ {data[2]}"); await ShowNextUntaggedChannel(bot, cq.Message.Chat.Id, cq.Message.MessageId); }
                else if (action == "ignore_ch") { _repository.SaveTag(long.Parse(data[1]), "IGNORE"); await bot.AnswerCallbackQuery(cq.Id, "🚫 Скрыто"); await ShowNextUntaggedChannel(bot, cq.Message.Chat.Id, cq.Message.MessageId); }
                else if (action == "skip_ch") { _skippedItems.Add(data[1]); await ShowNextUntaggedChannel(bot, cq.Message.Chat.Id, cq.Message.MessageId); }

                else if (action == "set_dom") { _repository.SaveDomainTag(data[1], data[2]); await bot.AnswerCallbackQuery(cq.Id, $"✅ {data[2]}"); await ShowNextUntaggedDomain(bot, cq.Message.Chat.Id, cq.Message.MessageId); }
                else if (action == "ignore_dom") { _repository.SaveDomainTag(data[1], "IGNORE"); await bot.AnswerCallbackQuery(cq.Id, "🚫 Скрыто"); await ShowNextUntaggedDomain(bot, cq.Message.Chat.Id, cq.Message.MessageId); }
                else if (action == "skip_dom") { _skippedItems.Add(data[1]); await ShowNextUntaggedDomain(bot, cq.Message.Chat.Id, cq.Message.MessageId); }
            }
            catch (Exception ex) { Console.WriteLine($"[UI-ERROR] {ex.Message}"); }
        }
    }

    private async Task ShowMainMenu(ITelegramBotClient bot, long chatId, int? msgId = null, bool isNewMessage = false)
    {
        var channels = _repository.GetAvailableChannels().Keys.Count(k => !_repository.GetMappings().ContainsKey(k));
        var domains = _repository.GetAvailableDomains().Count(d => !_repository.GetDomainMappings().ContainsKey(d));

        var keyboard = new InlineKeyboard(new[] {
            new[] { InlineButton.WithCallbackData($"📢 Новые каналы ({channels})", "untagged_ch") },
            new[] { InlineButton.WithCallbackData($"🔗 Новые домены ({domains})", "untagged_dom") }
        });

        string text = "🎛 <b>Маршрутизатор (SingleApp)</b>\nВыберите действие:";

        if (isNewMessage)
        {
            var msg = await bot.SendMessage(chatId, text, replyMarkup: keyboard, parseMode: ParseMode.Html);
            _repository.SetActiveMenu(chatId, msg.MessageId);
        }
        else if (msgId.HasValue)
        {
            await bot.EditMessageText(chatId, msgId.Value, text, replyMarkup: keyboard, parseMode: ParseMode.Html);
        }
    }

    private async Task ShowNextUntaggedChannel(ITelegramBotClient bot, long chatId, int msgId)
    {
        var next = _repository.GetAvailableChannels().FirstOrDefault(c => !_repository.GetMappings().ContainsKey(c.Key) && !_skippedItems.Contains(c.Key.ToString()));
        if (next.Key == 0) { await ShowMainMenu(bot, chatId, msgId); return; }

        var kb = GetTagsKeyboard(next.Key.ToString(), "ch", next.Value.Url);
        string safeTitle = next.Value.Title.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        await bot.EditMessageText(chatId, msgId, $"Куда отправлять посты из канала:\n👉 <b>{safeTitle}</b>", replyMarkup: kb, parseMode: ParseMode.Html);
    }

    private async Task ShowNextUntaggedDomain(ITelegramBotClient bot, long chatId, int msgId)
    {
        var next = _repository.GetAvailableDomains().FirstOrDefault(d => !_repository.GetDomainMappings().ContainsKey(d) && !_skippedItems.Contains(d));
        if (next == null) { await ShowMainMenu(bot, chatId, msgId); return; }

        var kb = GetTagsKeyboard(next, "dom", null);
        await bot.EditMessageText(chatId, msgId, $"Куда отправлять ссылки с доменом:\n👉 <b>{next}</b>", replyMarkup: kb, parseMode: ParseMode.Html);
    }

    private InlineKeyboard GetTagsKeyboard(string id, string type, string url)
    {
        var btns = new List<InlineButton[]>();
        if (!string.IsNullOrEmpty(url)) btns.Add(new[] { InlineButton.WithUrl("👀 Открыть канал", url) });

        // Pmix - это тег, который бот будет разбивать на Pvideo и Pimages
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