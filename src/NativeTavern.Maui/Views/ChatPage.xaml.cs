using Microsoft.Extensions.DependencyInjection;
using NativeTavern.Maui.ViewModels;

namespace NativeTavern.Maui.Views;

public partial class ChatPage : ContentPage
{
    private readonly ChatViewModel viewModel;
    private readonly IServiceProvider services;

    public ChatPage(ChatViewModel viewModel, IServiceProvider services)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        this.services = services;
        BindingContext = viewModel;
        viewModel.MessagesChanged += ScrollToLatest;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (viewModel.Messages.Count == 0 && viewModel.Sessions.Count == 0)
            await viewModel.InitializeAsync();
        else
            await viewModel.RefreshConfigurationAsync();
    }

    private void ScrollToLatest()
    {
        if (viewModel.Messages.Count == 0) return;
        Dispatcher.Dispatch(() =>
            MessagesList.ScrollTo(viewModel.Messages[^1], position: ScrollToPosition.End, animate: false));
    }

    private async void OnNewChatClicked(object? sender, EventArgs e) =>
        await viewModel.NewChatCommand.ExecuteAsync(null);

    private async void OnDeleteChatClicked(object? sender, EventArgs e)
    {
        var confirmed = await DisplayAlertAsync("删除对话", "确定删除当前对话吗？此操作无法撤销。", "删除", "取消");
        if (confirmed) await viewModel.DeleteCurrentChatAsync();
    }

    private async void OnOpenSettingsClicked(object? sender, EventArgs e) =>
        await Navigation.PushAsync(services.GetRequiredService<SettingsPage>());

    private void OnSuggestionClicked(object? sender, EventArgs e)
    {
        if (sender is Button { BindingContext: string suggestion })
            viewModel.UseSuggestion(suggestion);
    }

    private async void OnInputCompleted(object? sender, EventArgs e)
    {
        if (viewModel.SendCommand.CanExecute(null))
            await viewModel.SendCommand.ExecuteAsync(null);
    }
}
