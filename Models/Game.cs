using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using BagaGame.Services;

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

    /// <summary>True for an entry the user added by hand; only those can be repaired.</summary>
    public bool IsManual => string.Equals(
        Source,
        GameLibraryService.ManualSource,
        StringComparison.Ordinal);

    /// <summary>
    /// False when a manual entry points at an unplugged USB drive or an offline
    /// network share. Such a card is kept instead of being deleted.
    /// </summary>
    public bool IsPathAvailable
    {
        get
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(ExecutablePath))
                {
                    return File.Exists(ExecutablePath);
                }

                if (!string.IsNullOrWhiteSpace(InstallPath))
                {
                    return Directory.Exists(InstallPath);
                }

                // A store entry without a local folder is still launchable.
                return AppId > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool IsPathUnavailable => !IsPathAvailable;

    public string PathUnavailableText => Loc.T("Loc.Game.PathUnavailable");

    public string PathUnavailableTip => Loc.T("Loc.Game.PathUnavailableTip");

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

    /// <summary>Terse playtime for the card: "12 h", "45 min" or empty at zero.</summary>
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
                return Loc.Format("Loc.Playtime.Hours", hours);
            }

            var minutes = Math.Max(1, _playtimeSeconds / 60);
            return Loc.Format("Loc.Playtime.Minutes", minutes);
        }
    }

    /// <summary>Exact playtime for the tooltip: "Played: 12 h 34 min".</summary>
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
                ? Loc.Format("Loc.Playtime.TooltipHours", hours, minutes)
                : Loc.Format("Loc.Playtime.TooltipMinutes", Math.Max(1, minutes));
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
                    ? letters.Substring(0, 2).ToUpperInvariant()
                    : letters.ToUpperInvariant();
            }

            var first = words[0].FirstOrDefault(char.IsLetterOrDigit);
            var second = words[1].FirstOrDefault(char.IsLetterOrDigit);
            return string.Concat(first, second).ToUpperInvariant();
        }
    }

    /// <summary>Secondary line used by the list layout.</summary>
    public string Details
    {
        get
        {
            var source = Loc.SourceName(Source);
            return string.IsNullOrWhiteSpace(InstallPath)
                ? source
                : $"{source} · {InstallPath}";
        }
    }

    /// <summary>Re-reads every localized string after the language was switched.</summary>
    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(PlaytimeText));
        OnPropertyChanged(nameof(PlaytimeTooltip));
        OnPropertyChanged(nameof(PathUnavailableText));
        OnPropertyChanged(nameof(PathUnavailableTip));
        OnPropertyChanged(nameof(Details));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
