using CommunityToolkit.Mvvm.ComponentModel;
using NativeTavern.Models;

namespace NativeTavern.Maui.ViewModels;

public partial class ChatMessageViewModel : ObservableObject
{
    public ChatMessageViewModel(ChatMessage model, string roleLabel)
    {
        Model = model;
        Content = model.Content;
        RoleLabel = roleLabel;
    }

    public ChatMessage Model { get; }
    public bool IsUser => Model.Role == ChatRole.User;
    public bool IsAssistant => !IsUser;

    public string RoleLabel { get; }
    public bool HasStatusSummary => StatusSummary is not null;

    [ObservableProperty] private string content = string.Empty;
    [ObservableProperty] private bool isStreaming;
    [ObservableProperty] private string? statusSummary;

    partial void OnStatusSummaryChanged(string? value) => OnPropertyChanged(nameof(HasStatusSummary));
}
