using System.Globalization;
using System.Windows;

namespace BagaGame.Services;

public enum AppLanguage
{
    Russian,
    English,
}

/// <summary>
/// Swaps the interface language at runtime. Strings live in
/// <c>Themes/Strings.ru.xaml</c> and <c>Themes/Strings.en.xaml</c>, the same way
/// the colour palette is swapped, so every <c>DynamicResource</c> in the window
/// updates immediately and no restart is needed.
/// </summary>
public static class Loc
{
    private static readonly string[] LanguageFiles = { "Strings.ru.xaml", "Strings.en.xaml" };

    // Immutable snapshot: reading a string from a background thread is safe and
    // never sees a half-swapped dictionary.
    private static IReadOnlyDictionary<string, string> _strings =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public static AppLanguage Current { get; private set; } = AppLanguage.Russian;

    /// <summary>Raised after the language was swapped; view models refresh text.</summary>
    public static event Action? Changed;

    /// <summary>Russian is the default; other system languages start in English.</summary>
    public static AppLanguage DetectSystemLanguage()
        => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            .Equals("ru", StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.Russian
            : AppLanguage.English;

    public static void Apply(AppLanguage language)
    {
        var dictionary = Load(language);
        SwapDictionary(dictionary);
        _strings = Snapshot(dictionary);
        Current = language;
        Changed?.Invoke();
    }

    /// <summary>Returns the localized string for a resource key.</summary>
    public static string T(string key)
        => _strings.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : key;

    /// <summary>Localized string with <see cref="string.Format(string, object[])"/> placeholders.</summary>
    public static string Format(string key, params object?[] args)
    {
        var template = T(key);

        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    /// <summary>
    /// Russian needs three plural forms and English two. English reuses the
    /// "many" form for zero, which is why the caller passes both.
    /// </summary>
    public static string Plural(int count, string oneKey, string fewKey, string manyKey)
    {
        if (Current == AppLanguage.English)
        {
            return T(count == 1 ? oneKey : manyKey);
        }

        var lastTwo = count % 100;
        if (lastTwo is >= 11 and <= 14)
        {
            return T(manyKey);
        }

        return (count % 10) switch
        {
            1 => T(oneKey),
            >= 2 and <= 4 => T(fewKey),
            _ => T(manyKey),
        };
    }

    /// <summary>Localized Steam genre for a canonical English genre name.</summary>
    public static string GenreName(string? canonical)
    {
        if (string.IsNullOrWhiteSpace(canonical))
        {
            return T("Loc.Group.Other");
        }

        var key = GenreKeys.TryGetValue(canonical.Trim(), out var mapped)
            ? mapped
            : null;

        return key is null ? canonical.Trim() : T(key);
    }

    /// <summary>
    /// Older builds stored the Russian genre label in library.json. The store
    /// keeps canonical English names now, so such values are mapped back.
    /// </summary>
    public static string NormalizeStoredGenre(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return string.Empty;
        }

        var value = stored.Trim();

        foreach (var pair in GenreKeys)
        {
            if (string.Equals(T(pair.Value), value, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(pair.Key, value, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Key;
            }
        }

        foreach (var pair in LegacyRussianGenres)
        {
            if (string.Equals(pair.Key, value, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return value;
    }

    /// <summary>Display name for a scan source ("Steam", "Folder", "Manual").</summary>
    public static string SourceName(string? source)
        => source switch
        {
            null or "" => string.Empty,
            "Folder" => T("Loc.Source.Folder"),
            "Manual" => T("Loc.Source.Manual"),
            _ => source,
        };

    private static readonly Dictionary<string, string> GenreKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Action"] = "Loc.Genre.Action",
        ["Adventure"] = "Loc.Genre.Adventure",
        ["Indie"] = "Loc.Genre.Indie",
        ["RPG"] = "Loc.Genre.RPG",
        ["Strategy"] = "Loc.Genre.Strategy",
        ["Simulation"] = "Loc.Genre.Simulation",
        ["Casual"] = "Loc.Genre.Casual",
        ["Racing"] = "Loc.Genre.Racing",
        ["Sports"] = "Loc.Genre.Sports",
        ["Massively Multiplayer"] = "Loc.Genre.MMO",
        ["Free to Play"] = "Loc.Genre.FreeToPlay",
        ["Early Access"] = "Loc.Genre.EarlyAccess",
        ["Animation & Modeling"] = "Loc.Genre.Animation",
        ["Audio Production"] = "Loc.Genre.Audio",
        ["Design & Illustration"] = "Loc.Genre.Design",
        ["Education"] = "Loc.Genre.Education",
        ["Game Development"] = "Loc.Genre.GameDevelopment",
        ["Photo Editing"] = "Loc.Genre.Photo",
        ["Software Training"] = "Loc.Genre.Training",
        ["Utilities"] = "Loc.Genre.Utilities",
        ["Video Production"] = "Loc.Genre.Video",
        ["Web Publishing"] = "Loc.Genre.Web",
        ["Accounting"] = "Loc.Genre.Accounting",
        ["Other"] = "Loc.Group.Other",
    };

    // Russian labels written by builds before the language switch existed.
    private static readonly Dictionary<string, string> LegacyRussianGenres = new(StringComparer.Ordinal)
    {
        ["Экшн"] = "Action",
        ["Приключения"] = "Adventure",
        ["Инди"] = "Indie",
        ["Стратегии"] = "Strategy",
        ["Симуляторы"] = "Simulation",
        ["Казуальные"] = "Casual",
        ["Гонки"] = "Racing",
        ["Спорт"] = "Sports",
        ["MMO"] = "Massively Multiplayer",
        ["Бесплатные"] = "Free to Play",
        ["Ранний доступ"] = "Early Access",
        ["Анимация"] = "Animation & Modeling",
        ["Аудио"] = "Audio Production",
        ["Дизайн"] = "Design & Illustration",
        ["Обучение"] = "Education",
        ["Разработка игр"] = "Game Development",
        ["Фото"] = "Photo Editing",
        ["Утилиты"] = "Utilities",
        ["Видео"] = "Video Production",
        ["Веб"] = "Web Publishing",
        ["Бухгалтерия"] = "Accounting",
        ["Другие"] = "Other",
    };

    private static ResourceDictionary Load(AppLanguage language)
        => new()
        {
            Source = new Uri(
                language == AppLanguage.English
                    ? "/BagaGame;component/Themes/Strings.en.xaml"
                    : "/BagaGame;component/Themes/Strings.ru.xaml",
                UriKind.Relative),
        };

    private static IReadOnlyDictionary<string, string> Snapshot(ResourceDictionary dictionary)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var key in dictionary.Keys)
        {
            if (key is string name && dictionary[key] is string value)
            {
                map[name] = value;
            }
        }

        return map;
    }

    private static void SwapDictionary(ResourceDictionary replacement)
    {
        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        var dictionaries = application.Resources.MergedDictionaries;

        for (var index = 0; index < dictionaries.Count; index++)
        {
            var source = dictionaries[index].Source?.OriginalString;
            if (source is null || !LanguageFiles.Any(name => source.EndsWith(name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            dictionaries[index] = replacement;
            return;
        }

        dictionaries.Add(replacement);
    }
}
