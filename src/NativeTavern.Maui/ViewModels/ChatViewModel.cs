using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NativeTavern.Models;
using NativeTavern.Services;

namespace NativeTavern.Maui.ViewModels;

// Lean chat prototype view model for MAUI. Mirrors the desktop ChatViewModel flow
// (streaming, stop, suggestions, status plugin) without WPF dispatcher usage:
// MAUI Android commands run on the UI thread, so awaited callbacks stay marshalled.
public partial class ChatViewModel(
    ChatService chatService,
    ReplySuggestionService replySuggestionService,
    CharacterStatusService characterStatusService,
    SettingsService settingsService,
    ILogger<ChatViewModel> logger) : ObservableObject
{
    private ChatSession? session;
    private CancellationTokenSource? generationCts;
    private TaskCompletionSource? generationCompletion;
    private CancellationTokenSource? suggestionCts;

    public ObservableCollection<ChatSession> Sessions { get; } = [];
    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];
    public ObservableCollection<string> ReplySuggestions { get; } = [];

    [ObservableProperty] private ChatSession? selectedSession;
    [ObservableProperty] private string inputText = string.Empty;
    [ObservableProperty] private bool isGenerating;
    [ObservableProperty] private bool hasSuggestions;
    [ObservableProperty] private bool isProviderConfigured;
    [ObservableProperty] private string? errorMessage;

    public event Action? MessagesChanged;

    public ChatSession? CurrentSession => session;
    public bool IsIdle => !IsGenerating;

    public async Task InitializeAsync()
    {
        await RefreshConfigurationAsync();
        var sessions = await chatService.GetSessionsAsync();
        var target = sessions.FirstOrDefault() ?? await chatService.CreateSessionAsync();
        await ReloadSessionsAsync(target.Id);
        await LoadSessionAsync(target);
    }

    public async Task RefreshConfigurationAsync() =>
        IsProviderConfigured = (await settingsService.LoadAsync()).IsConfigured;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (session is null || IsGenerating) return;
        ErrorMessage = null;
        ClearSuggestions();
        var input = InputText;
        InputText = string.Empty;
        using var cancellation = new CancellationTokenSource();
        BeginGeneration(cancellation);
        ChatMessageViewModel? assistant = null;
        try
        {
            await chatService.SendAsync(
                session, input, [],
                async (user, assistantMessage) =>
                {
                    var userVm = new ChatMessageViewModel(user, "你");
                    assistant = new ChatMessageViewModel(assistantMessage, "角色") { IsStreaming = true };
                    Messages.Add(userVm);
                    Messages.Add(assistant);
                    MessagesChanged?.Invoke();
                },
                (_, chunk) =>
                {
                    if (assistant is not null) assistant.Content += chunk;
                    MessagesChanged?.Invoke();
                    return Task.CompletedTask;
                },
                cancellation.Token);

            if (assistant is not null && assistant.Content.Length > 0)
                await RefreshSuggestionsAsync();
            await RefreshConfigurationStatusAsync(assistant);
            await ReloadSessionsAsync(session!.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Chat generation failed.");
            ErrorMessage = "生成失败，请检查设置页中的模型配置。";
            if (assistant is not null && assistant.Content.Length == 0) Messages.Remove(assistant);
        }
        finally
        {
            if (assistant is not null) assistant.IsStreaming = false;
            EndGeneration(cancellation);
            MessagesChanged?.Invoke();
        }
    }

    private async Task RefreshConfigurationStatusAsync(ChatMessageViewModel? assistant)
    {
        if (assistant is null) return;
        try
        {
            var status = await characterStatusService.UpdateAsync(session!, assistant.Model.SpeakerCharacterId
                                                                      ?? session!.CharacterId ?? 0, assistant.Model.Id);
            assistant.StatusSummary = status is null ? null : FormatStatus(status);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Character status update failed.");
        }
    }

    private static string FormatStatus(CharacterStatusSnapshot status)
    {
        var attributes = string.Join(" · ", status.Attributes.Take(4).Select(x => $"{x.Name} {x.Value}"));
        return string.IsNullOrWhiteSpace(attributes) ? status.Summary : $"{status.Summary}  |  {attributes}";
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => generationCts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanCreateChat))]
    private async Task NewChatAsync()
    {
        var created = await chatService.CreateSessionAsync();
        await ReloadSessionsAsync(created.Id);
        await LoadSessionAsync(created);
        ErrorMessage = null;
    }

    public async Task DeleteCurrentChatAsync()
    {
        if (session is null || isDeletingSession) return;
        isDeletingSession = true;
        try
        {
            if (IsGenerating)
            {
                var completion = generationCompletion?.Task;
                generationCts?.Cancel();
                if (completion is not null) await completion;
            }

            var deletedId = session!.Id;
            ClearSuggestions();
            await chatService.DeleteSessionAsync(deletedId);
            if (session?.Id == deletedId) session = null;

            var sessions = await chatService.GetSessionsAsync();
            var next = sessions.FirstOrDefault() ?? await chatService.CreateSessionAsync();
            await ReloadSessionsAsync(next.Id);
            await LoadSessionAsync(next);
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Deleting the current conversation failed.");
            ErrorMessage = "删除对话失败：" + ex.Message;
        }
        finally { isDeletingSession = false; }
    }

    private bool isDeletingSession;

    private async Task LoadSessionAsync(ChatSession target)
    {
        session = target;
        ClearSuggestions();
        SelectedSession = Sessions.FirstOrDefault(x => x.Id == target.Id) ?? SelectedSession;
        Messages.Clear();
        foreach (var message in await chatService.GetMessagesAsync(target.Id))
            Messages.Add(new ChatMessageViewModel(message, message.Role == ChatRole.User ? "你" : "角色"));
        MessagesChanged?.Invoke();
        if (Messages.LastOrDefault()?.IsUser == false)
            _ = RefreshSuggestionsAsync();
    }

    private async Task ReloadSessionsAsync(long selectedId)
    {
        var sessions = await chatService.GetSessionsAsync();
        Sessions.Clear();
        foreach (var item in sessions) Sessions.Add(item);
        SelectedSession = Sessions.FirstOrDefault(x => x.Id == selectedId);
    }

    partial void OnSelectedSessionChanged(ChatSession? value)
    {
        if (value is null || value.Id == session?.Id) return;
        if (IsGenerating) { SelectedSession = Sessions.FirstOrDefault(x => x.Id == session?.Id); return; }
        _ = SelectSessionAsync(value);
    }

    private async Task SelectSessionAsync(ChatSession target)
    {
        try
        {
            var stored = await chatService.GetSessionAsync(target.Id);
            if (stored is null) return;
            await LoadSessionAsync(stored);
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Loading chat session failed.");
            ErrorMessage = "加载聊天记录失败。";
        }
    }

    private async Task RefreshSuggestionsAsync()
    {
        var target = session;
        if (target is null) return;
        suggestionCts?.Cancel();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(generationCts?.Token ?? CancellationToken.None);
        suggestionCts = linked;
        try
        {
            var suggestions = await replySuggestionService.GenerateAsync(target, linked.Token);
            if (suggestionCts != linked || session?.Id != target.Id) return;
            ReplySuggestions.Clear();
            foreach (var suggestion in suggestions) ReplySuggestions.Add(suggestion);
            HasSuggestions = ReplySuggestions.Count > 0;
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (suggestionCts == linked) suggestionCts = null;
            linked.Dispose();
        }
    }

    private void ClearSuggestions()
    {
        suggestionCts?.Cancel();
        suggestionCts = null;
        ReplySuggestions.Clear();
        HasSuggestions = false;
    }

    public void UseSuggestion(string suggestion) => InputText = suggestion;

    private void BeginGeneration(CancellationTokenSource cancellation)
    {
        IsGenerating = true;
        generationCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        generationCts = cancellation;
    }

    private void EndGeneration(CancellationTokenSource cancellation)
    {
        if (generationCts == cancellation) generationCts = null;
        IsGenerating = false;
        generationCompletion?.TrySetResult();
        generationCompletion = null;
        SendCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        NewChatCommand.NotifyCanExecuteChanged();
    }

    private bool CanSend() => !IsGenerating && IsProviderConfigured && !string.IsNullOrWhiteSpace(InputText);
    private bool CanStop() => IsGenerating;
    private bool CanCreateChat() => !IsGenerating;

    partial void OnInputTextChanged(string value) => SendCommand.NotifyCanExecuteChanged();
    partial void OnIsGeneratingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIdle));
        SendCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        NewChatCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsProviderConfiguredChanged(bool value) => SendCommand.NotifyCanExecuteChanged();
}
