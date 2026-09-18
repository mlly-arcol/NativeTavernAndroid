using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;
using NativeTavern.Data.Repositories;
using NativeTavern.Models;
using NativeTavern.Services;

namespace NativeTavern.Maui.ViewModels;

// Lean chat prototype view model for MAUI. Mirrors the desktop ChatViewModel flow
// (streaming, stop, suggestions, status plugin) without WPF dispatcher usage:
// MAUI Android commands run on the UI thread, so awaited callbacks stay marshalled.
public partial class ChatViewModel(
    ChatService chatService,
    ChatAttachmentRepository attachmentRepository,
    ReplySuggestionService replySuggestionService,
    CharacterStatusService characterStatusService,
    SettingsService settingsService,
    ILogger<ChatViewModel> logger) : ObservableObject
{
    private const long MaxImageSize = 10 * 1024 * 1024;

    private static readonly PickOptions ImagePickOptions = new()
    {
        PickerTitle = "选择图片（PNG / JPG / WEBP / GIF）",
        FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            [DevicePlatform.Android] = ["image/*"]
        })
    };

    private ChatSession? session;
    private CancellationTokenSource? generationCts;
    private TaskCompletionSource? generationCompletion;
    private CancellationTokenSource? suggestionCts;

    public ObservableCollection<ChatSession> Sessions { get; } = [];
    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];
    public ObservableCollection<string> ReplySuggestions { get; } = [];
    public ObservableCollection<string> PendingImages { get; } = [];

    [ObservableProperty] private ChatSession? selectedSession;
    [ObservableProperty] private string inputText = string.Empty;
    [ObservableProperty] private bool isGenerating;
    [ObservableProperty] private bool hasSuggestions;
    [ObservableProperty] private bool isProviderConfigured;
    [ObservableProperty] private string? errorMessage;

    public event Action? MessagesChanged;
    public event Action<ChatMessageViewModel>? MessageActionRequested;

    public ChatSession? CurrentSession => session;
    public bool IsIdle => !IsGenerating;
    public bool HasPendingImages => PendingImages.Count > 0;

    // Entry point used by CharactersPage after "start chat" creates a new session.
    public async Task OpenSessionAsync(ChatSession target)
    {
        var stored = await chatService.GetSessionAsync(target.Id) ?? target;
        await ReloadSessionsAsync(stored.Id);
        await LoadSessionAsync(stored);
        ErrorMessage = null;
    }

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
        var images = PendingImages.ToArray();
        InputText = string.Empty;
        PendingImages.Clear();
        PendingImagesChanged();
        using var cancellation = new CancellationTokenSource();
        BeginGeneration(cancellation);
        ChatMessageViewModel? assistant = null;
        try
        {
            await chatService.SendAsync(
                session, input, images,
                async (user, assistantMessage) =>
                {
                    var userVm = new ChatMessageViewModel(user, "你");
                    foreach (var attachment in await chatService.GetAttachmentsAsync(user.Id))
                        userVm.Attachments.Add(attachment);
                    assistant = new ChatMessageViewModel(assistantMessage, "角色") { IsStreaming = true };
                    AddMessage(userVm);
                    AddMessage(assistant);
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
            {
                await RefreshMessageMetaAsync(assistant);
                await RefreshSuggestionsAsync();
            }
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

    [RelayCommand]
    private async Task RegenerateAsync(ChatMessageViewModel? target)
    {
        if (target is null || session is null || IsGenerating || target.IsUser) return;
        ErrorMessage = null;
        ClearSuggestions();
        using var cancellation = new CancellationTokenSource();
        BeginGeneration(cancellation);
        target.IsStreaming = true;
        try
        {
            (_, var count) = await chatService.GenerateAlternativeAsync(
                session, target.Model,
                content =>
                {
                    target.Content = content;
                    MessagesChanged?.Invoke();
                    return Task.CompletedTask;
                },
                cancellation.Token);
            target.SwipeCount = count;
            target.SwipeIndex = target.Model.CurrentSwipeIndex;
            if (target.Content.Length > 0) await RefreshSuggestionsAsync();
            await ReloadSessionsAsync(session.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Regenerating the reply failed.");
            ErrorMessage = "重新生成失败，请检查设置页中的模型配置。";
        }
        finally
        {
            target.IsStreaming = false;
            // Cancellation may have restored the original swipe content on the model.
            target.Content = target.Model.Content;
            EndGeneration(cancellation);
            MessagesChanged?.Invoke();
        }
    }

    [RelayCommand]
    private async Task SwipePrevAsync(ChatMessageViewModel? target)
    {
        if (target is null || IsGenerating || target.SwipeIndex <= 0) return;
        await chatService.SelectSwipeAsync(target.Model, target.SwipeIndex - 1);
        target.SwipeIndex = target.Model.CurrentSwipeIndex;
        target.Content = target.Model.Content;
    }

    [RelayCommand]
    private async Task SwipeNextAsync(ChatMessageViewModel? target)
    {
        if (target is null || IsGenerating || target.SwipeIndex >= target.SwipeCount - 1) return;
        await chatService.SelectSwipeAsync(target.Model, target.SwipeIndex + 1);
        target.SwipeIndex = target.Model.CurrentSwipeIndex;
        target.Content = target.Model.Content;
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task AttachImagesAsync()
    {
        IEnumerable<FileResult?> picked;
        try
        {
            picked = await FilePicker.Default.PickMultipleAsync(ImagePickOptions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Image picking failed.");
            ErrorMessage = "打开文件选择器失败。";
            return;
        }
        foreach (var file in picked)
        {
            if (file is null) continue;
            var path = file.FullPath;
            if (string.IsNullOrEmpty(path) || !AttachmentService.IsSupportedImage(path)) continue;
            if (new FileInfo(path).Length > MaxImageSize)
            {
                ErrorMessage = "单张图片不能超过 10 MB。";
                continue;
            }
            if (!PendingImages.Contains(path, StringComparer.OrdinalIgnoreCase))
                PendingImages.Add(path);
        }
        PendingImagesChanged();
    }

    [RelayCommand]
    private void RemovePendingImage(string? path)
    {
        if (path is not null) PendingImages.Remove(path);
        PendingImagesChanged();
    }

    private void PendingImagesChanged()
    {
        OnPropertyChanged(nameof(HasPendingImages));
        SendCommand.NotifyCanExecuteChanged();
    }

    public Task CopyMessageAsync(ChatMessageViewModel target) => Clipboard.SetTextAsync(target.Content);

    public async Task EditMessageAsync(ChatMessageViewModel target, string newContent)
    {
        if (string.IsNullOrWhiteSpace(newContent) || newContent == target.Model.Content) return;
        target.Model.Content = newContent;
        await chatService.UpdateMessageAsync(target.Model);
        target.Content = target.Model.Content;
        if (target.IsAssistant) await RefreshMessageMetaAsync(target);
    }

    public async Task DeleteMessageAsync(ChatMessageViewModel target)
    {
        if (IsGenerating) return;
        await chatService.DeleteMessageAsync(target.Model.Id);
        Messages.Remove(target);
        _ = RefreshSuggestionsAsync();
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
        var attachments = await attachmentRepository.GetBySessionAsync(target.Id);
        var byMessage = attachments.GroupBy(x => x.ChatMessageId)
            .ToDictionary(group => group.Key, group => group.ToList());
        foreach (var message in await chatService.GetMessagesAsync(target.Id))
        {
            var viewModel = new ChatMessageViewModel(message, message.Role == ChatRole.User ? "你" : "角色");
            if (byMessage.TryGetValue(message.Id, out var list))
                foreach (var attachment in list) viewModel.Attachments.Add(attachment);
            if (viewModel.IsAssistant)
                viewModel.SwipeCount = await chatService.GetSwipeCountAsync(message);
            AddMessage(viewModel);
        }
        MessagesChanged?.Invoke();
        if (Messages.LastOrDefault()?.IsUser == false)
            _ = RefreshSuggestionsAsync();
    }

    private void AddMessage(ChatMessageViewModel viewModel)
    {
        viewModel.LongPressRequested += message => MessageActionRequested?.Invoke(message);
        Messages.Add(viewModel);
    }

    private async Task RefreshMessageMetaAsync(ChatMessageViewModel viewModel)
    {
        if (!viewModel.IsAssistant) return;
        viewModel.SwipeCount = await chatService.GetSwipeCountAsync(viewModel.Model);
        viewModel.SwipeIndex = viewModel.Model.CurrentSwipeIndex;
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

    private bool CanSend() => !IsGenerating && IsProviderConfigured &&
                              (!string.IsNullOrWhiteSpace(InputText) || PendingImages.Count > 0);
    private bool CanStop() => IsGenerating;
    private bool CanCreateChat() => !IsGenerating;

    partial void OnInputTextChanged(string value) => SendCommand.NotifyCanExecuteChanged();
    partial void OnIsGeneratingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIdle));
        SendCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        NewChatCommand.NotifyCanExecuteChanged();
        AttachImagesCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsProviderConfiguredChanged(bool value) => SendCommand.NotifyCanExecuteChanged();
}
