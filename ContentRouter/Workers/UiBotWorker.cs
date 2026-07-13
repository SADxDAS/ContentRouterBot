// Workers/UiBotWorker.cs
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
    private readonly string[] _tags = { "CODE", "MEDIA", "NSFW", "NSFW_VIDEOS", "NSFW_PICS", "HENTAI", "HENTAI_MANGA", "HENTAI_PICS", "HENTAI_GAMES", "LINK", "NOTE", "OTHER", "IGNORE" };

    private readonly HashSet<long> _skippedChannels = new();

    public UiBotWorker(IChannelRepository repository)
    {
        _repository = repository;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string botToken = Environment.GetEnvironmentVariable("BOT_TOKEN");
        if (string.IsNullOrEmpty(botToken)) return Task.CompletedTask;

        var uiBot = new TelegramBotClient(botToken);
        var receiverOptions = new ReceiverOptions { AllowedUpdates = new[] { BotType.Message, BotType.CallbackQuery } };

        uiBot.StartReceiving(HandleBotUpdateAsync, HandleBotErrorAsync, receiverOptions, stoppingToken);
        Console.WriteLine("[UI-WORKER] Панель управления кнопками активна.");
        return Task.CompletedTask;
    }

    private async Task HandleBotUpdateAsync(ITelegramBotClient bot, BotUpdate update, CancellationToken ct)
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
                _skippedChannels.Clear();
                await ShowNextUntagged(bot, cq.Message.Chat.Id, cq.Message.MessageId);
            }
            else if (action == "set")
            {
                long id = long.Parse(data[1]);
                _repository.SaveTag(id, data[2]);
                await bot.AnswerCallbackQuery(cq.Id, $"✅ Сохранено: {data[2]}");
                await ShowNextUntagged(bot, cq.Message.Chat.Id, cq.Message.MessageId);
            }
            else if (action == "ignore")
            {
                long id = long.Parse(data[1]);
                _repository.SaveTag(id, "IGNORE");
                await bot.AnswerCallbackQuery(cq.Id, "🚫 Скрыто от проверок");
                await ShowNextUntagged(bot, cq.Message.Chat.Id, cq.Message.MessageId);
            }
            else if (action == "skip")
            {
                _skippedChannels.Add(long.Parse(data[1]));
                await ShowNextUntagged(bot, cq.Message.Chat.Id, cq.Message.MessageId);
            }
            else if (action == "edit_cats") await ShowCategoriesForEdit(bot, cq.Message.Chat.Id, cq.Message.MessageId);
            else if (action == "edit_list") await ShowChannelsByTag(bot, cq.Message.Chat.Id, cq.Message.MessageId, data[1]);
            else if (action == "remove")
            {
                _repository.RemoveTag(long.Parse(data[1]));
                await bot.AnswerCallbackQuery(cq.Id, "🗑 Удалено");
                await ShowCategoriesForEdit(bot, cq.Message.Chat.Id, cq.Message.MessageId);
            }
        }
    }



    private async Task ShowCategoriesForEdit(ITelegramBotClient bot, long chatId, int msgId)
    {
        var mappings = _repository.GetMappings();
        var buttons = _tags.Select(tag =>
            new[] { InlineButton.WithCallbackData($"{tag} ({mappings.Values.Count(v => v == tag)})", $"edit_list|{tag}") }
        ).ToList();
        buttons.Add(new[] { InlineButton.WithCallbackData("🔙 В меню", "menu") });

        await bot.EditMessageText(chatId, msgId, "📂 Выберите категорию для просмотра:", replyMarkup: new InlineKeyboard(buttons));
    }

    private async Task ShowMainMenu(ITelegramBotClient bot, long chatId, int? msgId = null)
    {
        var available = _repository.GetAvailableChannels();
        var mappings = _repository.GetMappings();
        int untaggedCount = available.Keys.Count(k => !mappings.ContainsKey(k));

        var keyboard = new InlineKeyboard(new[] {
            new[] { InlineButton.WithCallbackData($"🔍 Разметить новые ({untaggedCount})", "untagged") },
            new[] { InlineButton.WithCallbackData($"📋 Мои привязки ({mappings.Count})", "edit_cats") }
        });

        // Замінили ** на <b> для HTML
        string text = "🎛 <b>Панель управления маршрутизатором</b>\nВыберите действие:";
        if (msgId.HasValue) await bot.EditMessageText(chatId, msgId.Value, text, replyMarkup: keyboard, parseMode: ParseMode.Html);
        else await bot.SendMessage(chatId, text, replyMarkup: keyboard, parseMode: ParseMode.Html);
    }

    private async Task ShowNextUntagged(ITelegramBotClient bot, long chatId, int msgId)
    {
        var available = _repository.GetAvailableChannels();
        var mappings = _repository.GetMappings();

        var next = available.FirstOrDefault(c => !mappings.ContainsKey(c.Key) && !_skippedChannels.Contains(c.Key));

        if (next.Key == 0)
        {
            var kb = new InlineKeyboard(new[] { new[] { InlineButton.WithCallbackData("🔙 В главное меню", "menu") } });
            await bot.EditMessageText(chatId, msgId, "🎉 <b>Все доступные каналы размечены!</b>", replyMarkup: kb, parseMode: ParseMode.Html);
            return;
        }

        var keyboard = new InlineKeyboard(new[] {
            new[] { InlineButton.WithUrl("👀 Открыть канал", next.Value.Url) },
            new[] { InlineButton.WithCallbackData("CODE", $"set|{next.Key}|CODE"), InlineButton.WithCallbackData("MEDIA", $"set|{next.Key}|MEDIA"), InlineButton.WithCallbackData("LINK", $"set|{next.Key}|LINK") },
            
            // Кнопки 18+ (Реальное)
            new[] { InlineButton.WithCallbackData("🔞 NSFW (Models)", $"set|{next.Key}|NSFW"), InlineButton.WithCallbackData("🔞 NSFW (Видео)", $"set|{next.Key}|NSFW_VIDEOS") },
            new[] { InlineButton.WithCallbackData("🔞 NSFW (Фото)", $"set|{next.Key}|NSFW_PICS") },
            
            // Кнопки 18+ (2D)
            new[] { InlineButton.WithCallbackData("🎨 H_MIX", $"set|{next.Key}|HENTAI"), InlineButton.WithCallbackData("🎨 H_MANGA", $"set|{next.Key}|HENTAI_MANGA") },
            new[] { InlineButton.WithCallbackData("🎨 H_PICS", $"set|{next.Key}|HENTAI_PICS"), InlineButton.WithCallbackData("🎮 H_GAMES", $"set|{next.Key}|HENTAI_GAMES") },

            new[] { InlineButton.WithCallbackData("NOTE", $"set|{next.Key}|NOTE"), InlineButton.WithCallbackData("OTHER", $"set|{next.Key}|OTHER") },
            new[] { InlineButton.WithCallbackData("⏩ Пропустить", $"skip|{next.Key}"), InlineButton.WithCallbackData("🚫 Игнор (LLM)", $"ignore|{next.Key}") },
            new[] { InlineButton.WithCallbackData("🔙 В меню", "menu") }
        });

        // Захист від HTML-спецсимволів у назві каналу (<, >, &)
        string safeTitle = next.Value.Title.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        await bot.EditMessageText(chatId, msgId, $"Куда отправлять посты из канала:\n👉 <b>{safeTitle}</b>", replyMarkup: keyboard, parseMode: ParseMode.Html);
    }

    private async Task ShowChannelsByTag(ITelegramBotClient bot, long chatId, int msgId, string tag)
    {
        var mappings = _repository.GetMappings();
        var available = _repository.GetAvailableChannels();
        var channels = mappings.Where(x => x.Value == tag).Take(50).ToList();

        var buttons = channels.Select(c => {
            string name = available.ContainsKey(c.Key) ? available[c.Key].Title : $"ID: {c.Key}";
            return new[] { InlineButton.WithCallbackData($"❌ {name}", $"remove|{c.Key}") };
        }).ToList();

        buttons.Add(new[] { InlineButton.WithCallbackData("🔙 Назад", "edit_cats") });

        // Замінили ** та * на HTML-теги <b> та <i>
        await bot.EditMessageText(chatId, msgId, $"📌 Привязки для <b>{tag}</b>:\n<i>(Нажми на канал, чтобы удалить привязку)</i>", replyMarkup: new InlineKeyboard(buttons), parseMode: ParseMode.Html);
    }
    private Task HandleBotErrorAsync(ITelegramBotClient bot, Exception ex, CancellationToken ct) => Task.CompletedTask;
}