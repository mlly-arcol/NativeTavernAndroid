using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;
using NativeTavern.Models;
using NativeTavern.Services;

namespace NativeTavern.Maui.ViewModels;

public sealed class CharacterItemViewModel(Character model)
{
    public Character Model { get; } = model;
    public string Name => Model.Name;
    public string Tags => Model.Tags;
    public bool HasTags => !string.IsNullOrWhiteSpace(Model.Tags);
    public bool HasAvatar => File.Exists(Model.AvatarPath);
    public bool ShowPlaceholder => !HasAvatar;
    public ImageSource? AvatarSource => HasAvatar ? ImageSource.FromFile(Model.AvatarPath) : null;
}

// Character library for mobile: import PNG/JSON character cards (shared
// CharacterCardImporter) and start a chat bound to the picked character.
public partial class CharactersViewModel(
    ICharacterService characterService,
    ChatService chatService,
    ILogger<CharactersViewModel> logger) : ObservableObject
{
    private static readonly PickOptions ImportOptions = new()
    {
        PickerTitle = "选择角色卡（PNG / JSON）",
        FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            [DevicePlatform.Android] = ["image/png", "application/json", "text/plain", "application/octet-stream"]
        })
    };

    public ObservableCollection<CharacterItemViewModel> Characters { get; } = [];

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? statusMessage;

    public async Task RefreshAsync()
    {
        try
        {
            var characters = await characterService.SearchAsync(string.Empty, false);
            Characters.Clear();
            foreach (var character in characters) Characters.Add(new CharacterItemViewModel(character));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Loading characters failed.");
            StatusMessage = "加载角色列表失败。";
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (IsBusy) return;
        FileResult? picked;
        try
        {
            picked = await FilePicker.PickAsync(ImportOptions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Character card picking failed.");
            StatusMessage = "打开文件选择器失败。";
            return;
        }
        if (picked is null) return;

        IsBusy = true;
        try
        {
            var character = await characterService.ImportAsync(picked.FullPath);
            StatusMessage = $"已导入「{character.Name}」。";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Importing character card failed.");
            StatusMessage = ex is InvalidDataException or FileNotFoundException
                ? ex.Message
                : "导入失败：" + ex.Message;
        }
        finally { IsBusy = false; }
    }

    public async Task<ChatSession?> StartChatAsync(CharacterItemViewModel item)
    {
        if (IsBusy) return null;
        IsBusy = true;
        try
        {
            return await chatService.CreateSessionAsync(item.Model);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Creating a chat for character {CharacterId} failed.", item.Model.Id);
            StatusMessage = "创建对话失败：" + ex.Message;
            return null;
        }
        finally { IsBusy = false; }
    }

    public async Task DeleteAsync(CharacterItemViewModel item)
    {
        try
        {
            await characterService.DeleteAsync(item.Model);
            Characters.Remove(item);
            StatusMessage = $"已删除「{item.Name}」。";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Deleting character {CharacterId} failed.", item.Model.Id);
            StatusMessage = "删除失败：" + ex.Message;
        }
    }
}
