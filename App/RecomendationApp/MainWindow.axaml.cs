using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using RecomendationApp.Models;
using RecomendationApp.Services;

namespace RecomendationApp;

public partial class MainWindow : Window
{
    // Палитра «обложек»: книга получает цвет по позиции в списке
    private static readonly (Color From, Color To)[] Palette =
    {
        (Color.Parse("#5B8DEF"), Color.Parse("#3B5BD7")),
        (Color.Parse("#9B6DFF"), Color.Parse("#6C3CE0")),
        (Color.Parse("#FF8A65"), Color.Parse("#E05A3C")),
        (Color.Parse("#4FC3A1"), Color.Parse("#2E9E7E")),
        (Color.Parse("#FFC148"), Color.Parse("#E08A1E")),
        (Color.Parse("#61B4F4"), Color.Parse("#3A7BD5")),
    };

    private static readonly string[] SkipWords =
        { "the", "a", "an", "of", "and", "in", "on", "el", "le", "de", "der", "das", "il", "una", "un", "no", "book", "part" };

    private readonly ApiClient _api = new();
    private readonly RatingDb _db = new();
    private Book? _current;
    private bool _isReady;
    private List<string>? _popularTitles;

    // цвета статусов (те же, что в App.axaml)
    private static readonly IBrush GoodBrush = new SolidColorBrush(Color.Parse("#1E9E6A"));
    private static readonly IBrush WarnBrush = new SolidColorBrush(Color.Parse("#C77700"));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#6A7689"));

    public MainWindow()
    {
        InitializeComponent();
        _isReady = true;

        FillModes();
        BuildRatingChips();
        ApplyPages();
        ShowBook(null);
        RefreshSaved();
        UpdateConnection(false, "API не проверен");
        _ = CheckApiAsync();

        // при старте сразу показываем топ-популярное — приложение не пустое
        Opened += (_, _) => BtnLoad_Click(null, new RoutedEventArgs());
    }

    // ============= заполнение списков =============

    private void FillModes()
    {
        CmbMode.ItemsSource = Enum.GetValues<ApiCommand>()
            .Select(c => new ModeItem
            {
                Command = c,
                Name = ApiClient.Title(c),
                Description = ApiClient.Description(c),
            })
            .ToList();
        CmbMode.SelectedIndex = 0;
    }

    private void BuildRatingChips()
    {
        RatingPanel.Children.Clear();
        for (int i = 1; i <= 10; i++)
        {
            var chip = new ToggleButton
            {
                Content = i.ToString(),
                Tag = i,
            };
            chip.Classes.Add("chip");
            chip.Click += RatingChip_Click;
            RatingPanel.Children.Add(chip);
        }
    }

    // ============= навигация =============

    private void Nav_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_isReady) return;
        ApplyPages();
    }

    private void ApplyPages()
    {
        int page = Nav.SelectedIndex;
        PageRecommend.IsVisible = page == 0;
        PageCompare.IsVisible = page == 1;
        PageSaved.IsVisible = page == 2;
        PageHelp.IsVisible = page == 3;
        DetailPanel.IsVisible = page == 0;

        if (page == 2) RefreshSaved();
    }

    // ============= параметры по умолчанию =============

    private void BtnExample_Click(object? sender, RoutedEventArgs e)
    {
        if (CmbMode.SelectedItem is not ModeItem mode) return;
        TxtParam.Text = ApiClient.Example(mode.Command);
    }

    // ============= выбор режима и запрос =============

    private void CmbMode_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_isReady || CmbMode.SelectedItem is not ModeItem mode) return;

        TxtModeDesc.Text = mode.Description;
        TxtParamHint.Text = ApiClient.ParamHint(mode.Command);
        TxtParam.Watermark = mode.Command == ApiCommand.Popular
            ? "параметр не нужен"
            : ApiClient.ParamHint(mode.Command);

        // для режимов по пользователю подставляем мой User-ID, если поле пустое
        if (ApiClient.NeedsUser(mode.Command) && string.IsNullOrWhiteSpace(TxtParam.Text) && TryMyId(out var id))
            TxtParam.Text = id.ToString();
    }

    private async void BtnLoad_Click(object? sender, RoutedEventArgs e)
    {
        if (CmbMode.SelectedItem is not ModeItem mode) return;

        string param = TxtParam.Text?.Trim() ?? "";
        if (mode.Command != ApiCommand.Popular && param.Length == 0)
        {
            SetStatus(ApiClient.ParamHint(mode.Command), true);
            return;
        }

        _api.BaseUrl = NormalizeBase();
        BtnLoad.IsEnabled = false;
        Prog.IsVisible = true;
        ShowBook(null);
        TxtCount.Text = "";
        TxtScoreHint.Text = ApiClient.ScoreHint(mode.Command);
        EmptyState.IsVisible = false;
        SetStatus("Запрос к API…", false);
        UpdateConnection(null, "запрос…");

        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var books = await _api.GetBooksAsync(mode.Command, param);
            watch.Stop();
            Decorate(books, mode.Command);

            LstResults.ItemsSource = books;
            TxtCount.Text = books.Count == 0 ? "" : $"{books.Count} книг за {watch.ElapsedMilliseconds} мс";
            EmptyState.IsVisible = books.Count == 0;
            SetStatus($"«{mode.Name}»: {books.Count} книг, ответ API за {watch.ElapsedMilliseconds} мс", false);
            UpdateConnection(true, "API подключён");

            await CompareWithPopularAsync(mode.Command, books);
        }
        catch (ApiException ex)
        {
            LstResults.ItemsSource = null;
            EmptyState.IsVisible = true;
            SetStatus(ex.Message, true);
            UpdateConnection(false, "API недоступен");
        }
        finally
        {
            Prog.IsVisible = false;
            BtnLoad.IsEnabled = true;
        }
    }

    // Показываем, насколько выдача отличается от «просто топа» — это и есть признак персонализации
    private async Task CompareWithPopularAsync(ApiCommand mode, List<Book> books)
    {
        if (!ApiClient.NeedsUser(mode) || books.Count == 0) return;

        List<string> top;
        try
        {
            top = _popularTitles ??= (await _api.GetBooksAsync(ApiCommand.Popular, ""))
                .Select(b => b.Title).ToList();
        }
        catch { return; }

        int same = books.Count(b => top.Contains(b.Title));
        TxtScoreHint.Text = same == books.Count
            ? "совпадает с топ-популярным: у пользователя нет истории в модели, персонализации нет"
            : $"{ApiClient.ScoreHint(mode)} · с топ-популярным пересекается {same} из {books.Count}";
    }

    private string NormalizeBase()
    {
        var url = (TxtBase.Text ?? "").Trim().TrimEnd('/');
        if (url.Length == 0) url = "http://127.0.0.1:8000";
        if (url != _api.BaseUrl) _popularTitles = null; // сменили сервер — прошлый топ неактуален
        return url;
    }

    private async void BtnCheck_Click(object? sender, RoutedEventArgs e)
    {
        _api.BaseUrl = (TxtBase.Text ?? "").Trim().TrimEnd('/');
        BtnCheck.IsEnabled = false;
        BtnCheck.Content = "Проверяю…";

        try
        {
            var help = await _api.GetHelpAsync();
            HelpList.ItemsSource = help;
            UpdateConnection(true, "API подключён");
            SetStatus($"API отвечает: {_api.BaseUrl}", false);
        }
        catch (ApiException ex)
        {
            UpdateConnection(false, "API недоступен");
            SetStatus(ex.Message, true);
        }
        finally
        {
            BtnCheck.Content = "Проверить соединение";
            BtnCheck.IsEnabled = true;
        }
    }

    private async Task CheckApiAsync()
    {
        _api.BaseUrl = (TxtBase.Text ?? "").Trim().TrimEnd('/');
        try
        {
            HelpList.ItemsSource = await _api.GetHelpAsync();
            UpdateConnection(true, "API подключён");
        }
        catch
        {
            HelpList.ItemsSource = ApiClient.FallbackHelp();
            UpdateConnection(false, "API недоступен — запустите сервер");
        }
    }

    // ============= карточка книги и оценка =============

    private void LstResults_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_isReady) return;
        ShowBook(LstResults.SelectedItem as Book);
    }

    private void ShowBook(Book? book)
    {
        _current = book;
        DetailEmpty.IsVisible = book == null;
        DetailBody.IsVisible = book != null;
        DetailNote.Text = "";

        foreach (var chip in RatingPanel.Children.OfType<ToggleButton>())
            chip.IsChecked = false;

        if (book == null) return;

        DetailCover.Background = book.Cover;
        DetailInitials.Text = book.Initials;
        DetailTitle.Text = book.Title;
        DetailAuthor.Text = string.IsNullOrWhiteSpace(book.Author) ? "автор не указан" : book.Author;
        DetailScore.Text = book.Badge.Length == 0
            ? "рекомендация без оценки похожести"
            : $"{book.Badge} — {book.BadgeHint}";

        // если книгу уже оценивали, подсветить прошлую оценку
        if (TryMyId(out var id) && _db.GetLatestRating(id, book.Title) is int saved)
            foreach (var chip in RatingPanel.Children.OfType<ToggleButton>())
                if (chip.Tag is int value && value == saved)
                    chip.IsChecked = true;
    }

    private void RatingChip_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: int value } chip) return;
        if (_current == null)
        {
            chip.IsChecked = false;
            return;
        }

        if (!TryMyId(out var id))
        {
            SetStatus("Сначала укажите свой User-ID в левой панели", true);
            chip.IsChecked = false;
            return;
        }

        try
        {
            _db.Save(id, _current.Title, value);

            // группа без GroupName: оставляем активным только нажатый чип
            foreach (var other in RatingPanel.Children.OfType<ToggleButton>())
                other.IsChecked = ReferenceEquals(other, chip);

            DetailNote.Text = $"Оценка {value}/10 сохранена в базу для пользователя {id}.";
            SetStatus($"Сохранено: «{_current.Title}» — {value}/10", false);
            RefreshSaved();
        }
        catch (Exception ex)
        {
            SetStatus($"Не удалось сохранить: {ex.Message}", true);
            chip.IsChecked = false;
        }
    }

    // ============= сравнение режимов =============

    private static readonly string[] ShortNames =
        { "топ-популярное", "похожие читатели", "похожие книги", "по контенту", "персональные" };

    private async void BtnCompare_Click(object? sender, RoutedEventArgs e)
    {
        _api.BaseUrl = NormalizeBase();
        string uid = (TxtCompareUser.Text ?? "").Trim();
        string title = (TxtCompareTitle.Text ?? "").Trim();
        if (uid.Length == 0 || title.Length == 0)
        {
            SetStatus("Для сравнения нужны User-ID и название книги", true);
            return;
        }

        BtnCompare.IsEnabled = false;
        ProgCompare.IsVisible = true;
        TxtCompareSummary.Text = "Запрашиваю все режимы…";
        LstCompare.ItemsSource = null;

        // один и тот же вход уходит в каждый эндпоинт API
        var specs = new (ApiCommand Cmd, string Param, string Name)[]
        {
            (ApiCommand.Popular, "", ShortNames[0]),
            (ApiCommand.CollabUsers, uid, ShortNames[1]),
            (ApiCommand.Personal, uid, ShortNames[4]),
            (ApiCommand.CollabBooks, title, ShortNames[2]),
            (ApiCommand.Content, title, ShortNames[3]),
        };

        var lists = new List<List<Book>>();
        var heads = new List<string>();

        foreach (var (cmd, param, name) in specs)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var books = await _api.GetBooksAsync(cmd, param);
                watch.Stop();
                lists.Add(books);
                heads.Add($"{name} — {ApiClient.Title(cmd)} · {watch.ElapsedMilliseconds} мс · {books.Count}");
            }
            catch (ApiException ex)
            {
                watch.Stop();
                lists.Add(new List<Book>());
                heads.Add($"{name} — {ApiClient.Title(cmd)} · ошибка: {ex.Message}");
            }
        }

        var sets = lists.Select(l => new HashSet<string>(l.Select(b => b.Title))).ToList();
        var rows = new List<CompareRow>();

        for (int i = 0; i < specs.Length; i++)
        {
            var overlap = new List<string>();
            for (int j = 0; j < specs.Length; j++)
                if (j != i)
                    overlap.Add($"{specs[j].Name}: {sets[i].Count(sets[j].Contains)}");

            rows.Add(new CompareRow
            {
                Mode = specs[i].Name,
                Head = heads[i],
                Books = lists[i].Count == 0
                    ? "нет данных"
                    : string.Join("\n", lists[i].Select((b, k) => $"{k + 1,2}. {b.Title} — {b.ScoreText}")),
                Overlaps = "пересечения со другими режимами · " + string.Join(" · ", overlap),
                IsCopyOfPopular = i > 0 && sets[i].Count > 0 && sets[i].SetEquals(sets[0]),
            });
        }

        int total = lists.Sum(l => l.Count);
        int unique = sets.SelectMany(s => s).Distinct().Count();
        int topOverlap = 0;
        for (int i = 1; i < sets.Count; i++)
            topOverlap = Math.Max(topOverlap, sets[i].Count(sets[0].Contains));

        TxtCompareSummary.Text = $"{specs.Length} запросов · {total} позиций · уникальных книг {unique} · " +
                                 $"максимум совпадений с топом {topOverlap}/10";

        LstCompare.ItemsSource = rows;
        ProgCompare.IsVisible = false;
        BtnCompare.IsEnabled = true;
        SetStatus($"Сравнение готово: {unique} уникальных книг из {total} позиций", false);
    }

    // ============= мои оценки =============

    private void BtnRefresh_Click(object? sender, RoutedEventArgs e) => RefreshSaved();

    private void RefreshSaved()
    {
        var rows = string.IsNullOrWhiteSpace(TxtFilterId.Text)
            ? _db.GetAll()
            : int.TryParse(TxtFilterId.Text.Trim(), out var id)
                ? _db.GetByUser(id)
                : new List<RatingRow>();

        LstSaved.ItemsSource = rows;
        SavedEmpty.IsVisible = rows.Count == 0;
        TxtDbInfo.Text = $"Всего оценок в базе: {_db.TotalCount()}";
    }

    // ============= вспомогательное =============

    // score у разных команд API в разных шкалах, поэтому показываем его понятно
    private static void Decorate(List<Book> books, ApiCommand mode)
    {
        double max = books.Count == 0 ? 0 : books.Max(b => b.Score ?? 0);

        for (int i = 0; i < books.Count; i++)
        {
            var b = books[i];
            b.Rank = i + 1;
            b.Initials = Initials(b.Title);
            b.Cover = CoverBrush(i);

            if (!b.Score.HasValue) { b.Badge = ""; b.BadgeHint = ""; b.Percent = 0; continue; }

            double s = b.Score.Value;
            b.Percent = max > 0 ? Math.Clamp(s / max * 100, 0, 100) : 0;

            if (mode == ApiCommand.Popular)
            {
                b.Badge = s.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                b.BadgeHint = "рейтинг из 10";
            }
            else
            {
                b.Badge = s.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                b.BadgeHint = $"{b.Percent:0}% лучшего в списке";
            }
        }
    }

    private static string Initials(string title)
    {
        var words = title
            .Split(new[] { ' ', '-', ',', ':', '(' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 0 && char.IsLetter(w[0]) && !SkipWords.Contains(w.ToLowerInvariant()))
            .Take(2)
            .ToArray();

        return words.Length == 0
            ? "?"
            : new string(words.Select(w => char.ToUpperInvariant(w[0])).ToArray());
    }

    private static IBrush CoverBrush(int index)
    {
        var (from, to) = Palette[index % Palette.Length];
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(from, 0),
                new GradientStop(to, 1),
            },
        };
    }

    private bool TryMyId(out int id)
    {
        id = 0;
        return int.TryParse((TxtMyId.Text ?? "").Trim(), out id);
    }

    private void UpdateConnection(bool? ok, string text)
    {
        TxtConn.Text = text;
        if (ok == null) return;
        StatusDot.Fill = ok.Value ? GoodBrush : WarnBrush;
    }

    private void SetStatus(string text, bool isError)
    {
        TxtStatus.Text = text;
        TxtStatus.Foreground = isError ? WarnBrush : MutedBrush;
    }
}
