using System;
using System.Globalization;

namespace RecomendationApp.Models;

// Строка таблицы Ratings в локальной SQLite-базе
public class RatingRow
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string BookTitle { get; set; } = "";
    public int Rating { get; set; }
    public string CreatedAt { get; set; } = "";

    public string RatingText => $"{Rating}/10";

    // SQLite отдаёт "2026-10-27 16:17:29" (UTC) — показываем коротко и в местном времени
    public string DateText =>
        DateTime.TryParse(CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToLocalTime().ToString("d MMM yyyy, HH:mm", new CultureInfo("ru-RU"))
            : CreatedAt;
}
