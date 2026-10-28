using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace BagaGame.Models;

/// <summary>
/// One game found on this computer.
/// </summary>
public sealed class Game : INotifyPropertyChanged
{
    private ImageSource? _cover;
    private string _title = string.Empty;
    private string _group = string.Empty;
    private long _playtimeSeconds;

    /// <summary>Stable identifier used for renames and hidden games.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Steam AppID for cover downloads; 0 for other stores.</summary>
    public int AppId { get; init; }

    public string Source { get; init; } = string.Empty;

    public string Title
    {
        get => _title;
        set
        {
            if (_title == value)
            {
                return;
            }

            _title = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Details));
            OnPropertyChanged(nameof(Initials));
        }
    }

    public string? InstallPath { get; init; }

    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Header used while the library is grouped. Empty when grouping is off.
    /// Assigned by the view model, not by the scanner.
    /// </summary>
    public string Group
    {
        get => _group;
        set
        {
            if (string.Equals(_group, value, StringComparison.Ordinal))
            {
                return;
            }

            _group = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Cover is assigned asynchronously after the card has been created.</summary>
    public ImageSource? Cover
    {
        get => _cover;
        set
        {
            if (ReferenceEquals(_cover, value))
            {
                return;
            }

            _cover = value;
            OnPropertyChanged();
        }
    }

    /// <summary>True when the card can start the game (exe on disk or Steam AppID).</summary>
    public bool CanLaunch => !string.IsNullOrWhiteSpace(ExecutablePath) || AppId > 0;

    /// <summary>
    /// Seconds the game has been played since the launcher started tracking it.
    /// Grows while a matching process is alive (see PlaytimeTracker).
    /// </summary>
    public long PlaytimeSeconds
    {
        get => _playtimeSeconds;
        set
        {
            var normalized = value < 0 ? 0 : value;
            if (_playtimeSeconds == normalized)
            {
                return;
            }

            _playtimeSeconds = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPlaytime));
            OnPropertyChanged(nameof(PlaytimeText));
            OnPropertyChanged(nameof(PlaytimeTooltip));
        }
    }

    public bool HasPlaytime => _playtimeSeconds > 0;

    /// <summary>Terse playtime for the card: "12 ч", "45 мин" or empty at zero.</summary>
    public string PlaytimeText
    {
        get
        {
            if (_playtimeSeconds <= 0)
            {
                return string.Empty;
            }

            var hours = _playtimeSeconds / 3600;
            if (hours > 0)
            {
                return $"{hours} ч";
            }

            var minutes = Math.Max(1, _playtimeSeconds / 60);
            return $"{minutes} мин";
        }
    }

    /// <summary>Exact playtime for the tooltip: "Наиграно: 12 ч 34 мин".</summary>
    public string PlaytimeTooltip
    {
        get
        {
            if (_playtimeSeconds <= 0)
            {
                return string.Empty;
            }

            var hours = _playtimeSeconds / 3600;
            var minutes = _playtimeSeconds % 3600 / 60;

            return hours > 0
                ? $"Наиграно: {hours} ч {minutes} мин"
                : $"Наиграно: {Math.Max(1, minutes)} мин";
        }
    }

    /// <summary>Two-letter monogram shown while no cover is available.</summary>
    public string Initials
    {
        get
        {
            var words = Title
                .Split(new[] { ' ', '-', ':', '(', ')', '.', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(word => word.Any(char.IsLetterOrDigit))
                .ToArray();

            if (words.Length == 0)
            {
                return "?";
            }

            if (words.Length == 1)
            {
                var letters = new string(words[0].Where(char.IsLetterOrDigit).ToArray());
                return letters.Length >= 2
                    ? letters[..2].ToUpperInvariant()
                    : letters.ToUpperInvariant();
            }

            var first = words[0].FirstOrDefault(char.IsLetterOrDigit);
            var second = words[1].FirstOrDefault(char.IsLetterOrDigit);
            return string.Concat(first, second).ToUpperInvariant();
        }
    }

    /// <summary>Secondary line used by the list layout.</summary>
    public string Details => string.IsNullOrWhiteSpace(InstallPath)
        ? Source
        : $"{Source} · {InstallPath}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
