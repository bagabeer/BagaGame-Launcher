using System.Threading;
using System.Windows;
using System.Windows.Input;
using BagaGame.Controls;
using BagaGame.Models;
using BagaGame.Services;
using BagaGame.ViewModels;

namespace BagaGame.Views;

public partial class SteamAchievementsWindow : BagaWindow
{
    private readonly SteamAchievementsViewModel _viewModel;
    private readonly CancellationTokenSource _lifetime = new();

    public SteamAchievementsWindow(Game game, int appId)
    {
        InitializeComponent();

        _viewModel = new SteamAchievementsViewModel(
            game,
            appId,
            SteamAchievementsService.Shared,
            new LocalAchievementsService());
        DataContext = _viewModel;

        Loaded += async (_, _) => await _viewModel.LoadAsync(_lifetime.Token);
        Closed += (_, _) =>
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (!_lifetime.IsCancellationRequested)
        {
            await _viewModel.LoadAsync(_lifetime.Token);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}
