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
        viewModel.MessageActionRequested += OnMessageActionRequested;
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

    private async void OnOpenCharactersClicked(object? sender, EventArgs e) =>
        await Navigation.PushAsync(services.GetRequiredService<CharactersPage>());

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

    private void OnRegenerateClicked(object? sender, EventArgs e)
    {
        if (sender is Button { BindingContext: ChatMessageViewModel message })
            viewModel.RegenerateCommand.Execute(message);
    }

    private async void OnSwipePrevClicked(object? sender, EventArgs e)
    {
        if (sender is Button { BindingContext: ChatMessageViewModel message })
            await viewModel.SwipePrevCommand.ExecuteAsync(message);
    }

    private async void OnSwipeNextClicked(object? sender, EventArgs e)
    {
        if (sender is Button { BindingContext: ChatMessageViewModel message })
            await viewModel.SwipeNextCommand.ExecuteAsync(message);
    }

    private void OnRemovePendingImageClicked(object? sender, EventArgs e)
    {
        if (sender is Button { BindingContext: string path })
            viewModel.RemovePendingImageCommand.Execute(path);
    }

    // Long-pressing a bubble opens the message action sheet.
    private async void OnMessageActionRequested(ChatMessageViewModel message)
    {
        string[] actions = message.IsAssistant ? ["复制", "编辑", "重新生成", "删除"] : ["复制", "编辑", "删除"];
        var action = await DisplayActionSheetAsync("消息操作", "取消", null, actions);
        switch (action)
        {
            case "复制":
                await viewModel.CopyMessageAsync(message);
                break;
            case "编辑":
                var edited = await DisplayPromptAsync("编辑消息", "修改消息内容（暂不支持换行）",
                    initialValue: message.Content);
                if (edited is not null) await viewModel.EditMessageAsync(message, edited);
                break;
            case "重新生成":
                viewModel.RegenerateCommand.Execute(message);
                break;
            case "删除":
                var confirmed = await DisplayAlertAsync("删除消息", "确定删除这条消息吗？", "删除", "取消");
                if (confirmed) await viewModel.DeleteMessageAsync(message);
                break;
        }
    }
}
