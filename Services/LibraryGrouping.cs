using System.Globalization;
using System.Text;
using BagaGame.Models;

namespace BagaGame.Services;

/// <summary>How the library grid is split into sections.</summary>
public enum LibraryGrouping
{
    None,
    Alphabet,
    Genre,
    Series,
}

/// <summary>
/// Builds the group title for every game: a letter, a Steam genre or a game
/// series (Resident Evil, Silent Hill, …).
/// </summary>
public static class LibraryGroupingHelper
{
    public const string OtherGenre = "Другие";
    public const string NoSeries = "Без серии";
    public const string DigitsGroup = "0–9";
    public const string OtherLetter = "#";

    // Franchises that are easy to recognise by their title. A series becomes
    // a group only when at least two games from the library belong to it.
    private static readonly (string Name, string[] Keys)[] KnownSeries =
    {
        ("Age of Empires", new[] { "age of empires" }),
        ("Assassin's Creed", new[] { "assassins creed", "assassin", "ac " }),
        ("Battlefield", new[] { "battlefield" }),
        ("BioShock", new[] { "bioshock" }),
        ("Borderlands", new[] { "borderlands" }),
        ("Call of Duty", new[] { "call of duty", "cod " }),
        ("Counter-Strike", new[] { "counter strike", "counterstrike", "cs " }),
        ("Crysis", new[] { "crysis" }),
        ("Cyberpunk", new[] { "cyberpunk" }),
        ("Dark Souls", new[] { "dark souls" }),
        ("Dead Island", new[] { "dead island" }),
        ("Dead Space", new[] { "dead space" }),
        ("Diablo", new[] { "diablo" }),
        ("Dragon Age", new[] { "dragon age" }),
        ("Dying Light", new[] { "dying light", "dl2", "dl " }),
        ("The Elder Scrolls", new[] { "the elder scrolls", "elder scrolls", "skyrim" }),
        ("Fallout", new[] { "fallout" }),
        ("Far Cry", new[] { "far cry" }),
        ("Final Fantasy", new[] { "final fantasy" }),
        ("Forza", new[] { "forza" }),
        ("God of War", new[] { "god of war" }),
        ("Grand Theft Auto", new[] { "grand theft auto", "gta" }),
        ("Halo", new[] { "halo" }),
        ("Half-Life", new[] { "half life", "halflife", "hl2", "hl " }),
        ("Hitman", new[] { "hitman" }),
        ("Mafia", new[] { "mafia" }),
        ("Mass Effect", new[] { "mass effect" }),
        ("Metro", new[] { "metro" }),
        ("Monster Hunter", new[] { "monster hunter" }),
        ("Mortal Kombat", new[] { "mortal kombat" }),
        ("Need for Speed", new[] { "need for speed", "nfs" }),
        ("Overwatch", new[] { "overwatch" }),
        ("Persona", new[] { "persona" }),
        ("Portal", new[] { "portal" }),
        ("Red Dead", new[] { "red dead", "rdr" }),
        ("Resident Evil", new[] { "resident evil", "biohazard", "re2", "re3", "re4", "re7", "re8", "re village" }),
        ("Silent Hill", new[] { "silent hill", "sh2", "shf", "townfall" }),
        ("The Sims", new[] { "the sims", "sims" }),
        ("Star Wars", new[] { "star wars" }),
        ("StarCraft", new[] { "starcraft" }),
        ("Street Fighter", new[] { "street fighter" }),
        ("Tekken", new[] { "tekken" }),
        ("Tomb Raider", new[] { "tomb raider" }),
        ("Total War", new[] { "total war" }),
        ("Warcraft", new[] { "warcraft" }),
        ("The Witcher", new[] { "the witcher", "witcher" }),
        ("Yakuza", new[] { "yakuza", "like a dragon" }),
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "this", "that", "game", "games", "игра", "игры", "new", "my", "and", "for",
    };

    /// <summary>First letter (or digit) of the title, used by the A–Z grouping.</summary>
    public static string AlphabetGroup(string? title)
    {
        if (!string.IsNullOrWhiteSpace(title))
        {
            foreach (var character in title)
            {
                if (char.IsDigit(character))
                {
                    return DigitsGroup;
                }

                if (char.IsLetter(character))
                {
                    return char.ToUpperInvariant(character).ToString();
                }
            }
        }

        return OtherLetter;
    }

    /// <summary>
    /// Maps game id → series title. Games outside a series are absent, so the
    /// caller can put them into a single trailing "Без серии" group.
    /// </summary>
    public static Dictionary<string, string> BuildSeriesMap(IReadOnlyList<Game> games)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var game in games)
        {
            var normalized = Normalize(game.Title);
            if (normalized.Length == 0)
            {
                continue;
            }

            foreach (var series in KnownSeries)
            {
                if (series.Keys.Any(key => MatchesPrefix(normalized, key)))
                {
                    map[game.Id] = series.Name;
                    break;
                }
            }
        }

        // Unknown franchises: a shared first word in two or more titles
        // ("Dead Space", "Dead Space Remake") is a good enough hint.
        var generic = new Dictionary<string, List<Game>>(StringComparer.OrdinalIgnoreCase);

        foreach (var game in games)
        {
            if (map.ContainsKey(game.Id))
            {
                continue;
            }

            var word = FirstSignificantWord(game.Title);
            if (word is null)
            {
                continue;
            }

            if (!generic.TryGetValue(word, out var list))
            {
                list = new List<Game>();
                generic[word] = list;
            }

            list.Add(game);
        }

        foreach (var pair in generic)
        {
            if (pair.Value.Count < 2)
            {
                continue;
            }

            var display = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(pair.Key);
            foreach (var game in pair.Value)
            {
                map[game.Id] = display;
            }
        }

        // A series with a single game is noise, not a useful group.
        var counts = map.Values
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in map)
        {
            if (counts.TryGetValue(pair.Value, out var count) && count >= 2)
            {
                result[pair.Key] = pair.Value;
            }
        }

        return result;
    }

    /// <summary>Lower-case title with single spaces, apostrophes removed.</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;

        foreach (var character in value.ToLowerInvariant())
        {
            if (character is '\'' or '’' or '`')
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                lastWasSpace = false;
            }
            else if (!lastWasSpace && builder.Length > 0)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    private static bool MatchesPrefix(string normalizedTitle, string key)
    {
        if (!normalizedTitle.StartsWith(key, StringComparison.Ordinal))
        {
            return false;
        }

        if (key.EndsWith(" ", StringComparison.Ordinal) || normalizedTitle.Length == key.Length)
        {
            return true;
        }

        return !char.IsLetterOrDigit(normalizedTitle[key.Length]);
    }

    private static string? FirstSignificantWord(string? title)
    {
        var normalized = Normalize(title);
        if (normalized.Length == 0)
        {
            return null;
        }

        var word = normalized.Split(' ')[0];
        return word.Length >= 4 && !StopWords.Contains(word) ? word : null;
    }
}
