// Services/IAiClassifier.cs
using System.Threading.Tasks;

public interface IAiClassifier
{
    Task<string> PredictCategoryAsync(string text);
}