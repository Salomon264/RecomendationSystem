using RecomendationApp.Services;

namespace RecomendationApp.Models;

// Пункт списка режимов API в окне
public class ModeItem
{
    public ApiCommand Command { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
}
