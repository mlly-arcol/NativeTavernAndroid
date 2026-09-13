using Microsoft.Extensions.DependencyInjection;
using NativeTavern.Maui.ViewModels;

namespace NativeTavern.Maui.Views;

public partial class CharactersPage : ContentPage
{
    private readonly CharactersViewModel viewModel;
    private readonly ChatViewModel chatViewModel;

    public CharactersPage(CharactersViewModel viewModel, ChatViewModel chatViewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        this.chatViewModel = chatViewModel;
        BindingContext = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await viewModel.RefreshAsync();
    }

    private async void OnStartChatClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: CharacterItemViewModel item }) return;
        var session = await viewModel.StartChatAsync(item);
        if (session is null) return;
        await chatViewModel.OpenSessionAsync(session);
        await Navigation.PopAsync();
    }

    private async void OnDeleteClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: CharacterItemViewModel item }) return;
        var confirmed = await DisplayAlertAsync(
            "删除角色", $"确定删除「{item.Name}」吗？已有对话记录会保留。", "删除", "取消");
        if (confirmed) await viewModel.DeleteAsync(item);
    }
}
