using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using RecomendationApp.Models;

namespace RecomendationApp.Services;

// Все команды API рекомендательной системы
public enum ApiCommand
{
    Popular,        // GET /popular
    CollabUsers,    // GET /collab/users/{user_id}
    CollabBooks,    // GET /collab/books?title=
    Content,        // GET /content?title=
    Personal        // GET /personal/{user_id}
}

public class ApiException : Exception
{
    public ApiException(string message) : base(message) { }
}

public class ApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    public string BaseUrl { get; set; } = "http://127.0.0.1:8000";

    public static string Title(ApiCommand command) => command switch
    {
        ApiCommand.Popular => "Топ-10 популярных книг",
        ApiCommand.CollabUsers => "Похожие читатели (по пользователю)",
        ApiCommand.CollabBooks => "Похожие книги (по книге)",
        ApiCommand.Content => "Похожие книги по содержанию",
        ApiCommand.Personal => "Персональные рекомендации",
        _ => command.ToString()
    };

    public static string Description(ApiCommand command) => command switch
    {
        ApiCommand.Popular => "Книги с самым высоким взвешенным рейтингом.",
        ApiCommand.CollabUsers => "Что читают пользователи с похожими оценками.",
        ApiCommand.CollabBooks => "Книги, которые оценивают те же люди, что и выбранную.",
        ApiCommand.Content => "Совпадение по названию, автору и издателю (TF-IDF).",
        ApiCommand.Personal => "Смесь контентного и коллаборативного анализа по вашему профилю.",
        _ => ""
    };

    public static bool NeedsUser(ApiCommand command) =>
        command == ApiCommand.CollabUsers || command == ApiCommand.Personal;

    public static bool NeedsTitle(ApiCommand command) =>
        command == ApiCommand.CollabBooks || command == ApiCommand.Content;

    public static string ParamHint(ApiCommand command) => command switch
    {
        ApiCommand.Popular => "Параметры не нужны — просто нажмите «Показать»",
        ApiCommand.CollabUsers or ApiCommand.Personal => "User-ID из Ratings.csv, например 11676",
        ApiCommand.CollabBooks or ApiCommand.Content => "Точное название книги, например The Hobbit",
        _ => ""
    };

    // Готовые параметры, которые точно есть в модели API
    public static string Example(ApiCommand command) => command switch
    {
        ApiCommand.CollabUsers or ApiCommand.Personal => "11676",
        ApiCommand.CollabBooks or ApiCommand.Content => "The Hobbit",
        _ => ""
    };

    // Что означает score у каждого режима: шкалы у эндпоинтов разные
    public static string ScoreHint(ApiCommand command) => command switch
    {
        ApiCommand.Popular => "score = взвешенный рейтинг книги из 10 (формула IMDb)",
        ApiCommand.CollabUsers => "score = средний балл книги у 20 похожих читателей",
        ApiCommand.CollabBooks => "score = косинусное сходство профилей читателей",
        ApiCommand.Content => "score = косинусное сходство текста (название+автор+издатель)",
        ApiCommand.Personal => "score = сумма сходств с книгами, которые вы оценили",
        _ => ""
    };

    private string BuildUrl(ApiCommand command, string param) => command switch
    {
        ApiCommand.Popular => $"{BaseUrl}/popular",
        ApiCommand.CollabUsers => $"{BaseUrl}/collab/users/{param.Trim()}",
        ApiCommand.CollabBooks => $"{BaseUrl}/collab/books?title={Uri.EscapeDataString(param.Trim())}",
        ApiCommand.Content => $"{BaseUrl}/content?title={Uri.EscapeDataString(param.Trim())}",
        ApiCommand.Personal => $"{BaseUrl}/personal/{param.Trim()}",
        _ => throw new ApiException($"Неизвестная команда {command}")
    };

    public async Task<List<Book>> GetBooksAsync(ApiCommand command, string param)
    {
        var json = await GetRawAsync(BuildUrl(command, param));
        return JsonSerializer.Deserialize<List<Book>>(json, JsonOptions) ?? new List<Book>();
    }

    // Справка по командам берётся из OpenAPI-описания самого API
    public async Task<List<ApiHelpItem>> GetHelpAsync()
    {
        var json = await GetRawAsync($"{BaseUrl}/openapi.json");
        using var doc = JsonDocument.Parse(json);
        var items = new List<ApiHelpItem>();

        if (!doc.RootElement.TryGetProperty("paths", out var paths)) return items;

        foreach (var path in paths.EnumerateObject())
            foreach (var operation in path.Value.EnumerateObject())
            {
                var item = new ApiHelpItem { Method = operation.Name.ToUpperInvariant(), Path = path.Name };

                if (operation.Value.TryGetProperty("summary", out var summary))
                    item.Summary = summary.GetString() ?? "";

                if (operation.Value.TryGetProperty("parameters", out var parameters))
                    foreach (var p in parameters.EnumerateArray())
                    {
                        string name = p.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        bool required = p.TryGetProperty("required", out var r) && r.GetBoolean();
                        string type = p.TryGetProperty("schema", out var schema) &&
                                      schema.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                        item.Parameters.Add($"{name} — {(required ? "обязательный" : "опциональный")}, {type}");
                    }

                items.Add(item);
            }

        return items;
    }

    // Если API недоступен — показываем это же описание, но из кода приложения
    public static List<ApiHelpItem> FallbackHelp() => new()
    {
        new ApiHelpItem { Method = "GET", Path = "/popular", Summary = "Топ-10 популярных книг" },
        new ApiHelpItem
        {
            Method = "GET", Path = "/collab/users/{user_id}",
            Summary = "Похожие читатели (по пользователю)",
            Parameters = new List<string> { "user_id — обязательный, integer" }
        },
        new ApiHelpItem
        {
            Method = "GET", Path = "/collab/books?title=",
            Summary = "Похожие книги (по книге)",
            Parameters = new List<string> { "title — обязательный, string" }
        },
        new ApiHelpItem
        {
            Method = "GET", Path = "/content?title=",
            Summary = "Похожие книги по содержанию",
            Parameters = new List<string> { "title — обязательный, string" }
        },
        new ApiHelpItem
        {
            Method = "GET", Path = "/personal/{user_id}",
            Summary = "Персональные рекомендации",
            Parameters = new List<string> { "user_id — обязательный, integer" }
        },
    };

    private async Task<string> GetRawAsync(string url)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(url);
        }
        catch (Exception e)
        {
            throw new ApiException($"API недоступен по адресу {BaseUrl}. Запустите сервер: {e.Message}");
        }

        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new ApiException(ReadError(text) ?? $"API вернул {response.StatusCode}");

        return text;
    }

    // FastAPI описывает ошибку в поле detail
    private static string? ReadError(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("detail", out var detail))
                return detail.GetString();
        }
        catch { }
        return null;
    }
}
