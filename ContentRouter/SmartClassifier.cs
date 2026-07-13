using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LLama;
using LLama.Common;
using LLama.Sampling;

public class SmartClassifier : IDisposable
{
    private readonly LLamaWeights _weights;
    private readonly StatelessExecutor _executor;

    public SmartClassifier(string modelPath)
    {
        var parameters = new ModelParams(modelPath)
        {
            // Увеличили размер контекста с 1024 до 4096, чтобы влезали большие посты
            ContextSize = 4096,
            GpuLayerCount = 20
        };

        _weights = LLamaWeights.LoadFromFile(parameters);
        _executor = new StatelessExecutor(_weights, parameters);
    }

    public async Task<string> PredictCategory(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "OTHER";

        // ПРЕДОХРАНИТЕЛЬ: Если текст огромный (больше 3000 символов), обрезаем его.
        // Нейросети хватит и первых абзацев, чтобы понять суть, а программа не упадет.
        if (text.Length > 3000)
        {
            text = text.Substring(0, 3000);
        }

        var inferenceParams = new InferenceParams()
        {
            MaxTokens = 5,
            AntiPrompts = new List<string> { "User:" },
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = 0.0f
            }
        };

        // Убрали <|begin_of_text|>, чтобы избежать предупреждения double BOS.
        // Инструкция обновлена с учетом разделения HENTAI и NSFW
        string prompt = $@"<|start_header_id|>system<|end_header_id|>
Ты безэмоциональный AI-роутер. Твоя задача проанализировать смысл текста и вернуть ТОЛЬКО ОДНО СЛОВО из списка: [CODE, NSFW, HENTAI, MEDIA, LINK, NOTE, OTHER].
- Если текст про программирование, IT, софт, разработку, github — верни CODE.
- Если текст содержит аниме 18+, хентай, hentai, 2D порно, эччи, мангу 18+ или названия аниме для взрослых — верни HENTAI.
- Если текст содержит 18+ контент с реальными людьми, OnlyFans, OF, сливы, соляки, моделей, нюдсы, эротику или соло-контент — верни NSFW.
- Если текст про видео, музыку, мемы, ютуб, игры (без 18+ подтекста) — верни MEDIA.
- Если текст содержит только ссылки без внятного описания — верни LINK.
- Если это личная заметка, мысль, список дел или скопированный текст для памяти — верни NOTE.
- Если текст о чем-то совершенно другом — верни OTHER.
Никогда не пиши пояснений. Верни ровно одно слово.<|eot_id|><|start_header_id|>user<|end_header_id|>
Текст для анализа: {text}<|eot_id|><|start_header_id|>assistant<|end_header_id|>";

        string response = "";

        await foreach (var token in _executor.InferAsync(prompt, inferenceParams))
        {
            response += token;
        }

        response = response.ToUpper().Trim();

        if (response.Contains("CODE")) return "CODE";
        if (response.Contains("HENTAI")) return "HENTAI";
        if (response.Contains("NSFW")) return "NSFW";
        if (response.Contains("MEDIA")) return "MEDIA";
        if (response.Contains("LINK")) return "LINK";
        if (response.Contains("NOTE")) return "NOTE";

        return "OTHER";
    }

    public void Dispose()
    {
        _weights?.Dispose();
    }
}