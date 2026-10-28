using System.Text.Json.Serialization;
using Avalonia.Media;

namespace RecomendationApp.Models;

// Ответ API: одна позиция рекомендации + служебные поля для отображения в списке
public class Book
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("image")] public string Image { get; set; } = "";

    // score есть не во всех ответах API
    [JsonPropertyName("score")] public double? Score { get; set; }

    // эти поля заполняет код окна, API их не отдаёт
    public int Rank { get; set; }
    public string Initials { get; set; } = "";
    public string Badge { get; set; } = "";
    public string BadgeHint { get; set; } = "";
    public double Percent { get; set; }
    public IBrush? Cover { get; set; }

    public string RankText => Rank > 0 ? Rank.ToString("00") : "";
    public string ScoreText => Score.HasValue ? Score.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "-";
}
