using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LLama;
using LLama.Common;
using LLama.Sampling; // Требуется для DefaultSamplingPipeline в новых версиях

public class SmartClassifier : IDisposable
{
    private readonly LLamaWeights _weights;
    private readonly StatelessExecutor _executor;

    public SmartClassifier(string modelPath)
    {
        var parameters = new ModelParams(modelPath)
        {
            ContextSize = 1024,
            GpuLayerCount = 20
        };

        _weights = LLamaWeights.LoadFromFile(parameters);

        // В новых версиях StatelessExecutor сам создает нужный контекст из весов
        _executor = new StatelessExecutor(_weights, parameters);
    }

    // Делаем метод асинхронным, так как новая LLamaSharp работает только асинхронно
    public async Task<string> PredictCategory(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "OTHER";

        // Новая структура параметров для LLamaSharp 0.20+
        var inferenceParams = new InferenceParams()
        {
            MaxTokens = 5,
            AntiPrompts = new List<string> { "User:" },
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = 0.0f
            }
        };

        string prompt = $@"<|begin_of_text|><|start_header_id|>system<|end_header_id|>
Ты безэмоциональный AI-роутер. Твоя задача проанализировать смысл текста и вернуть ТОЛЬКО ОДНО СЛОВО из списка: [CODE, MEDIA, LINK, OTHER, NOTE].
- Если текст про программирование, IT, софт, разработку, github — верни CODE.
- Если текст про видео, музыку, мемы, ютуб, игры, развлечения — верни MEDIA.
- Если текст содержит только ссылки без внятного описания — верни LINK.
- Если текст содержит записи(текст, примечания) — верни NOTE.
- Если текст о чем-то другом (быт, новости, мысли) — верни OTHER.
Никогда не пиши пояснений. Верни ровно одно слово.<|eot_id|><|start_header_id|>user<|end_header_id|>
Текст для анализа: {text}<|eot_id|><|start_header_id|>assistant<|end_header_id|>";

        string response = "";

        // Используем InferAsync вместо Infer и собираем токены через await foreach
        await foreach (var token in _executor.InferAsync(prompt, inferenceParams))
        {
            response += token;
        }

        response = response.ToUpper().Trim();

        if (response.Contains("CODE")) return "CODE";
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