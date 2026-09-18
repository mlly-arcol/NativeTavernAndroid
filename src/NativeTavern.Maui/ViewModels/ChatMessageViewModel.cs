using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NativeTavern.Models;

namespace NativeTavern.Maui.ViewModels;

public partial class ChatMessageViewModel : ObservableObject
{
    public ChatMessageViewModel(ChatMessage model, string roleLabel)
    {
        Model = model;
        Content = model.Content;
        RoleLabel = roleLabel;
        SwipeIndex = model.CurrentSwipeIndex;
        Attachments.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasAttachments));
    }

    public ChatMessage Model { get; }
    public bool IsUser => Model.Role == ChatRole.User;
    public bool IsAssistant => !IsUser;

    public string RoleLabel { get; }
    public bool HasStatusSummary => StatusSummary is not null;
    public bool HasAttachments => Attachments.Count > 0;
    public bool HasSwipeNav => SwipeCount > 1;
    public string SwipePosition => $"{SwipeIndex + 1}/{SwipeCount}";

    public ObservableCollection<ChatAttachment> Attachments { get; } = [];

    public event Action<ChatMessageViewModel>? LongPressRequested;

    [ObservableProperty] private string content = string.Empty;
    [ObservableProperty] private bool isStreaming;
    [ObservableProperty] private string? statusSummary;
    [ObservableProperty] private int swipeCount;
    [ObservableProperty] private int swipeIndex;

    partial void OnStatusSummaryChanged(string? value) => OnPropertyChanged(nameof(HasStatusSummary));
    partial void OnSwipeCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasSwipeNav));
        OnPropertyChanged(nameof(SwipePosition));
    }
    partial void OnSwipeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SwipePosition));
    }

    [RelayCommand]
    private void LongPress() => LongPressRequested?.Invoke(this);
}
