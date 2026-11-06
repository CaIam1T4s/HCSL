using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using HCSL.Frontend;
using HCSL.Host.ViewModels;

namespace HCSL.Host.Views.Pages;

public partial class DownloadsPage : UserControl
{
    private SteamCmdController? subscribed;
    private Guid? lastChallenge;
    private SteamCmdController? Model => (DataContext as ShellViewModel)?.SteamCmd;
    public DownloadsPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Subscribe();
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => { if (subscribed is not null) subscribed.PropertyChanged -= Changed; subscribed = null; SecretInput.Text = ""; };
    }
    private void Subscribe()
    {
        if (subscribed is not null) subscribed.PropertyChanged -= Changed;
        subscribed = Model;
        if (subscribed is not null) subscribed.PropertyChanged += Changed;
        SecretInput.Text = "";
        lastChallenge = Model?.ChallengeId;
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (Model?.ChallengeId != lastChallenge || Model?.NeedsInput != true) SecretInput.Text = "";
        lastChallenge = Model?.ChallengeId;
    }
    private async void ConnectDaemon(object? sender, RoutedEventArgs e) { if (Model is { } model) await model.ConnectAsync(); }
    private async void StartProbe(object? sender, RoutedEventArgs e) { if (Model is { } model) await model.StartAsync(); }
    private async void InstallServer(object? sender, RoutedEventArgs e) { if (Model is { } model) await model.InstallAsync(); }
    private async void ValidateServer(object? sender, RoutedEventArgs e) { if (Model is { } model) await model.ValidateAsync(); }
    private async void CancelProbe(object? sender, RoutedEventArgs e) { if (Model is { } model) await model.CancelAsync(); }
    private async void RecoverProbe(object? sender, RoutedEventArgs e) { if (Model is { } model) await model.RecoverAsync(); }
    private async void SubmitSecret(object? sender, RoutedEventArgs e)
    {
        var secret = SecretInput.Text ?? "";
        SecretInput.Text = "";
        if (Model is { } model) await model.SubmitAsync(secret);
    }
}
