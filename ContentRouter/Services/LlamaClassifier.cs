// Services/LlamaClassifier.cs
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LLama;
using LLama.Common;
using LLama.Sampling;

public class LlamaClassifier : IAiClassifier, IDisposable
{
    private readonly LLamaWeights _weights;
    private readonly StatelessExecutor _executor;

    public LlamaClassifier(string modelPath)
    {
        var parameters = new ModelParams(modelPath)
        {
            ContextSize = 4096,
            GpuLayerCount = 20
        };
        _weights = LLamaWeights.LoadFromFile(parameters);
        _executor = new StatelessExecutor(_weights, parameters);
    }

    public async Task<string> PredictCategoryAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "OTHER";
        if (text.Length > 3000) text = text.Substring(0, 3000);

        var inferenceParams = new InferenceParams()
        {
            MaxTokens = 5,
            AntiPrompts = new List<string> { "User:" },
            SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.0f }
        };

        string prompt = $@"<|start_header_id|>system<|end_header_id|>
Ты безэмоциональный AI-роутер. Твоя задача проанализировать смысл текста и вернуть ТОЛЬКО ОДНО СЛОВО из списка: [CODE, MEDIA, LINK, NOTE, OTHER, NSFW, NSFW_VIDEOS, NSFW_PICS, HENTAI, HENTAI_MANGA, HENTAI_PICS, HENTAI_GAMES].
- Если текст про программирование, IT, софт, разработку — верни CODE.
- Если текст содержит хентай игры, визуальные новеллы 18+, eroge, adult games — верни HENTAI_GAMES.
- Если текст содержит хентай мангу, додзинси, комиксы 18+, главы, страницы — верни HENTAI_MANGA.
- Если текст содержит хентай арты, иллюстрации, картинки, CG паки — верни HENTAI_PICS.
- Если текст содержит хентай анимации, видео, эпизоды, или смешанный хентай контент — верни HENTAI.
- Если текст содержит 18+ видео с реальными людьми, порно ролики, porn, sex tapes — верни NSFW_VIDEOS.
- Если текст содержит 18+ фото/картинки с реальными людьми, порно фото, нюдсы, эротические фотосессии — верни NSFW_PICS.
- Если текст содержит 18+ контент с реальными моделями (OnlyFans, сливы, соло-контент, вебкам) — верни NSFW.
- Если текст про обычное видео, музыку, мемы, ютуб, игры (без 18+ подтекста) — верни MEDIA.
- Если текст содержит только ссылки без внятного описания — верни LINK.
- Если это личная заметка, мысль или список — верни NOTE.
- Если текст о чем-то совершенно другом — верни OTHER.
Никогда не пиши пояснений. Верни ровно одно слово.<|eot_id|><|start_header_id|>user<|end_header_id|>
Текст для анализа: {text}<|eot_id|><|start_header_id|>assistant<|end_header_id|>";

        string response = "";
        await foreach (var token in _executor.InferAsync(prompt, inferenceParams))
        {
            response += token;
        }

        response = response.ToUpper().Trim();

        // ВАЖНО: Длинные теги проверяем первыми!
        if (response.Contains("CODE")) return "CODE";
        if (response.Contains("HENTAI_MANGA")) return "HENTAI_MANGA";
        if (response.Contains("HENTAI_PICS")) return "HENTAI_PICS";
        if (response.Contains("HENTAI_GAMES")) return "HENTAI_GAMES";
        if (response.Contains("HENTAI")) return "HENTAI";
        if (response.Contains("NSFW_VIDEOS")) return "NSFW_VIDEOS";
        if (response.Contains("NSFW_PICS")) return "NSFW_PICS";
        if (response.Contains("NSFW")) return "NSFW";
        if (response.Contains("MEDIA")) return "MEDIA";
        if (response.Contains("LINK")) return "LINK";
        if (response.Contains("NOTE")) return "NOTE";
        return "OTHER";
    }
    public void Dispose() => _weights?.Dispose();
}