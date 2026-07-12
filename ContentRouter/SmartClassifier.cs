using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Tokenizers.DotNet; // Підключаємо бібліотеку для читання tokenizer.json

public class SmartClassifier : IDisposable
{
    private readonly InferenceSession _session;
    private readonly Tokenizer _tokenizer;

    // Наші категорії для маршрутизації
    private readonly string[] _categories = { "Код и программирование", "Видео, музыка и мемы", "Разное", "Links, ссылки", "Заметки, Notes, текст" };
    private readonly string[] _categoryKeys = { "CODE", "MEDIA", "OTHER", "LINK","NOTE" };

    public SmartClassifier(string modelFolderPath)
    {
        string modelPath = System.IO.Path.Combine(modelFolderPath, "model.onnx");
        string tokenizerPath = System.IO.Path.Combine(modelFolderPath, "tokenizer.json");

        // Завантажуємо модель та токенізатор у пам'ять
        _session = new InferenceSession(modelPath);
        _tokenizer = new Tokenizer(tokenizerPath);
    }

    public string PredictCategory(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 3)
            return "OTHER";

        float bestScore = 0.2f; // Поріг впевненості (можна підкрутити від 0.3 до 0.7)
        int bestIndex = 2; // OTHER за замовчуванням

        for (int i = 0; i < _categories.Length; i++)
        {
            // Формуємо гіпотезу (Zero-Shot Classification)
            string hypothesis = $"Этот текст относится к теме: {_categories[i]}.";
            float entailmentScore = RunOnnxInference(text, hypothesis);

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
        // З'єднуємо текст та гіпотезу спеціальними токенами формату XLM-R
        string combined = $"<s>{text}</s></s>{hypothesis}</s>";

        // 1. Токенізація: перетворюємо текст на числа
        var encoded = _tokenizer.Encode(combined);

        // Дістаємо ID токенів (кастуємо в long, бо ONNX вимагає 64-бітні числа)
        long[] inputIds = encoded.Select(id => (long)id).ToArray();

        // Маска уваги (всі одиниці, бо ми не використовуємо доповнення/паддінг)
        long[] attentionMask = Enumerable.Repeat(1L, inputIds.Length).ToArray();

        int length = inputIds.Length;

        // 2. Пакуємо в ONNX Тензори
        var inputIdsTensor = new DenseTensor<long>(inputIds, new[] { 1, length });
        var attentionMaskTensor = new DenseTensor<long>(attentionMask, new[] { 1, length });

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor)
        };

        // 3. Проганяємо через нейромережу
        using var results = _session.Run(inputs);

        // Модель повертає 3 числа (Логити):
        // [0] - Contradiction (Суперечить)
        // [1] - Neutral (Нейтрально)
        // [2] - Entailment (Логічне слідування / Підходить)
        var logits = results.First().AsEnumerable<float>().ToArray();

        // 4. Переводимо абстрактні числа у відсотки (0.0 - 1.0) через Softmax
        var probabilities = ApplySoftmax(logits);

        // Повертаємо ймовірність того, що текст підходить до гіпотези
        return probabilities[2];
    }

    // Математична функція для перетворення логітів на відсотки
    private float[] ApplySoftmax(float[] logits)
    {
        float maxLogit = logits.Max();
        float sumExp = 0;
        float[] expLogits = new float[logits.Length];

        for (int i = 0; i < logits.Length; i++)
        {
            expLogits[i] = (float)Math.Exp(logits[i] - maxLogit);
            sumExp += expLogits[i];
        }

        for (int i = 0; i < logits.Length; i++)
        {
            expLogits[i] /= sumExp;
        }

        return expLogits;
    }

    public void Dispose()
    {
        _session?.Dispose();
        if (_tokenizer is IDisposable disposableTokenizer)
            disposableTokenizer.Dispose();
    }
}