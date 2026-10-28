using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace RecomendationApp.Services;

// Локальная SQLite-баз: сохраняет оценки пользователя
public class RatingDb
{
    private readonly string _connStr;

    public RatingDb()
    {
        var dataDir = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "RecommendationApp");
        System.IO.Directory.CreateDirectory(dataDir);
        _connStr = $"Data Source={System.IO.Path.Combine(dataDir, "ratings.db")}";
        Init();
    }

    private void Init()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Ratings (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId INTEGER NOT NULL,
                BookTitle TEXT NOT NULL,
                Rating INTEGER NOT NULL CHECK(Rating >= 1 AND Rating <= 10),
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now'))
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connStr);
        conn.Open();
        return conn;
    }

    // Сохранить оценку
    public void Save(int userId, string bookTitle, int rating)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Ratings (UserId, BookTitle, Rating, CreatedAt)
            VALUES ($uid, $title, $rate, datetime('now'));
            """;
        cmd.Parameters.AddWithValue("$uid", userId);
        cmd.Parameters.AddWithValue("$title", bookTitle);
        cmd.Parameters.AddWithValue("$rate", rating);
        cmd.ExecuteNonQuery();
    }

    // Все оценки
    public List<Models.RatingRow> GetAll()
    {
        var rows = new List<Models.RatingRow>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, UserId, BookTitle, Rating, CreatedAt FROM Ratings ORDER BY Id DESC;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(new Models.RatingRow
            {
                Id = reader.GetInt32(0),
                UserId = reader.GetInt32(1),
                BookTitle = reader.GetString(2),
                Rating = reader.GetInt32(3),
                CreatedAt = reader.GetString(4)
            });
        return rows;
    }

    // Оценки конкретного пользователя
    public List<Models.RatingRow> GetByUser(int userId)
    {
        var rows = new List<Models.RatingRow>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, UserId, BookTitle, Rating, CreatedAt FROM Ratings WHERE UserId = $uid ORDER BY Id DESC;";
        cmd.Parameters.AddWithValue("$uid", userId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(new Models.RatingRow
            {
                Id = reader.GetInt32(0),
                UserId = reader.GetInt32(1),
                BookTitle = reader.GetString(2),
                Rating = reader.GetInt32(3),
                CreatedAt = reader.GetString(4)
            });
        return rows;
    }

    // Последняя оценка книги этим пользователем (для подсветки чипа)
    public int? GetLatestRating(int userId, string bookTitle)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Rating FROM Ratings WHERE UserId = $uid AND BookTitle = $title ORDER BY Id DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$uid", userId);
        cmd.Parameters.AddWithValue("$title", bookTitle);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt32(value);
    }

    public int TotalCount()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Ratings;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
