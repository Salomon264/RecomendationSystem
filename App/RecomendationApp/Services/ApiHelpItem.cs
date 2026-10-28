using System.Collections.Generic;

namespace RecomendationApp.Services;

// Строка справки по команде API
public class ApiHelpItem
{
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<string> Parameters { get; set; } = new();

    public string ParamsText => Parameters.Count == 0 ? "параметров нет" : string.Join(" · ", Parameters);
}
