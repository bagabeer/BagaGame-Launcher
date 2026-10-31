using System.Windows;
using System.Windows.Media.Animation;
using BagaGame.Services.Audio;
using BagaGame.Views;

namespace BagaGame.Services;

/// <summary>
/// The switch between the library window and Big Picture. Both windows share
/// the same dark backdrop, so a veil inside Big Picture hides the swap and then
/// fades out: the library turns into the television interface instead of
/// flashing the desktop.
/// </summary>
public static class BigPictureTransition
{
    public static void Enter(Window from, BigPictureWindow to, bool sounds)
    {
        UiSounds.PlayEnter(sounds);

        // The veil starts fully opaque, so the window swap is invisible.
        to.PrepareForShow();
        from.Hide();
        to.Show();
        to.Activate();

        // A window opened right after a hidden owner has no foreground rights;
        // the short topmost flip brings the mode forward reliably.
        to.Topmost = true;
        to.Topmost = false;

        to.PlayEntrance();
    }

    public static void Exit(BigPictureWindow from, Window to, bool sounds, Action? completed = null)
    {
        UiSounds.PlayExit(sounds);

        from.PlayExit(() =>
        {
            from.CloseApproved();

            // The library fades in rather than popping back at full brightness.
            to.Opacity = 1;
            to.Show();
            to.Activate();

            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            };

            fade.Completed += (_, _) => to.Opacity = 1;
            to.BeginAnimation(UIElement.OpacityProperty, fade);

            completed?.Invoke();
        });
    }
}
