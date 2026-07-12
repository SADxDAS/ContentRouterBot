using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

public class SmartClassifier : IDisposable
{
    private readonly InferenceSession _session;

    // Наши категории для маршрутизации
    private readonly string[] _categories = { "Код и программирование", "Видео, музыка и мемы", "Разное", "Links, ссылки" };
    private readonly string[] _categoryKeys = { "CODE", "MEDIA", "OTHER","LINK" };

    public SmartClassifier(string modelFolderPath)
    {
        // Загружаем саму нейронку в оперативную память (выполняется один раз при старте)
        string modelPath = System.IO.Path.Combine(modelFolderPath, "model.onnx");
        _session = new InferenceSession(modelPath);
    }

    public string PredictCategory(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 3)
            return "OTHER";

        float bestScore = 0.2f; // Ставим порог срабатывания (всё что ниже 0.2 игнорируется)
        int bestIndex = 2; // По умолчанию кидаем в OTHER (индекс 2)

        for (int i = 0; i < _categories.Length; i++)
        {
            string hypothesis = $"Этот текст относится к теме: {_categories[i]}.";
            float entailmentScore = RunOnnxInference(text, hypothesis);

            // Теперь индекс изменится, только если уверенность больше текущей И больше 0.2
            if (entailmentScore > bestScore)
            {
                bestScore = entailmentScore;
                bestIndex = i;
            }
        }

        return _categoryKeys[bestIndex];
    }

    private float RunOnnxInference(string text, string hypothesis)
    {
        // Временная логика (заглушка) с поддержкой твоей новой категории LINK
        string lowerText = text.ToLower();

        if (hypothesis.Contains("Код") && (lowerText.Contains("код") || lowerText.Contains("git") || lowerText.Contains("c#"))) return 0.9f;
        if (hypothesis.Contains("Видео") && (lowerText.Contains("видео") || lowerText.Contains("мп3") || lowerText.Contains("youtube"))) return 0.9f;
        if (hypothesis.Contains("Links") && (lowerText.Contains("http") || lowerText.Contains("www"))) return 0.9f;

        return 0.1f; // Базовый шум, который теперь будет игнорироваться
    }

    public void Dispose()
    {
        _session?.Dispose();
    }
}