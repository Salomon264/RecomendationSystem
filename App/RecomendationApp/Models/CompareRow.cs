namespace RecomendationApp.Models;

// Строка страницы «Сравнение режимов»: один режим API и его выдача
public class CompareRow
{
    public string Mode { get; set; } = "";
    public string Head { get; set; } = "";
    public string Books { get; set; } = "";
    public string Overlaps { get; set; } = "";

    // режим вернул тот же список, что и топ-популярное (значит данных для персонализации нет)
    public bool IsCopyOfPopular { get; set; }
    public string Warning => IsCopyOfPopular ? "совпадает с топ-популярным — персонализации нет" : "";
}
