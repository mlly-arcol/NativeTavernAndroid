using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NativeTavern.Models;
using NativeTavern.Providers;
using NativeTavern.Services;

namespace NativeTavern.Maui.ViewModels;

public partial class SettingsViewModel(
    SettingsService settingsService,
    ProviderRouter provider,
    ILogger<SettingsViewModel> logger) : ObservableObject
{
    public ObservableCollection<ProviderProfile> ProviderProfiles { get; } = [.. ProviderProfile.All];

    [ObservableProperty] private ProviderProfile? selectedProvider;
    [ObservableProperty] private string baseUrl = "https://api.openai.com/v1";
    [ObservableProperty] private string apiKey = string.Empty;
    [ObservableProperty] private string model = string.Empty;
    [ObservableProperty] private double temperature = 0.8;
    [ObservableProperty] private double topP = 1.0;
    [ObservableProperty] private int maxTokens = 1024;
    [ObservableProperty] private bool includeCharacterContext;
    [ObservableProperty] private bool includeKnowledgeContext;
    [ObservableProperty] private bool includeImageContext;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? statusMessage;

    public event Action? Saved;

    public async Task InitializeAsync()
    {
        var resolved = await settingsService.LoadResolvedAsync();
        SelectedProvider = ProviderProfiles.FirstOrDefault(x => x.Id == resolved.Settings.ProviderId)
                           ?? ProviderProfiles[0];
        BaseUrl = resolved.Settings.BaseUrl;
        ApiKey = resolved.ApiKey;
        Model = resolved.Settings.Model;
        Temperature = resolved.Settings.Temperature;
        TopP = resolved.Settings.TopP;
        MaxTokens = resolved.Settings.MaxTokens;
        IncludeCharacterContext = resolved.Settings.IncludeCharacterContext;
        IncludeKnowledgeContext = resolved.Settings.IncludeKnowledgeContext;
        IncludeImageContext = resolved.Settings.IncludeImageContext;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!TryBuildSettings(out var settings)) return;
        IsBusy = true;
        try
        {
            await settingsService.SaveAsync(settings, ApiKey);
            StatusMessage = "已保存。";
            Saved?.Invoke();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save provider settings.");
            StatusMessage = "保存失败，请查看日志。";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (!TryBuildSettings(out var settings)) return;
        IsBusy = true;
        StatusMessage = "正在测试连接…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await provider.TestConnectionAsync(settings, ApiKey, timeout.Token);
            StatusMessage = "连接成功。";
        }
        catch (ProviderException ex) { StatusMessage = ex.Message; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Connection test failed.");
            StatusMessage = "连接测试失败，请检查网络。";
        }
        finally { IsBusy = false; }
    }

    private bool TryBuildSettings(out ProviderSettings settings)
    {
        settings = new ProviderSettings
        {
            ProviderId = SelectedProvider?.Id ?? "openai-compatible",
            BaseUrl = BaseUrl.Trim(),
            Model = Model.Trim(),
            Temperature = Temperature,
            TopP = TopP,
            MaxTokens = MaxTokens,
            IncludeCharacterContext = IncludeCharacterContext,
            IncludeKnowledgeContext = IncludeKnowledgeContext,
            IncludeImageContext = IncludeImageContext
        };
        if (!ProviderSettings.IsValidBaseUrl(settings.BaseUrl))
        { StatusMessage = "请输入有效的 Base URL。"; return false; }
        if (string.IsNullOrWhiteSpace(settings.Model))
        { StatusMessage = "请填写模型名称。"; return false; }
        if (SelectedProvider?.RequiresApiKey == true && string.IsNullOrWhiteSpace(ApiKey))
        { StatusMessage = $"{SelectedProvider.DisplayName} 需要 API Key。"; return false; }
        if (Temperature is < 0 or > 2 || TopP is < 0 or > 1 || MaxTokens <= 0)
        { StatusMessage = "生成参数超出有效范围。"; return false; }
        return true;
    }

    partial void OnSelectedProviderChanged(ProviderProfile? value)
    {
        if (value is null || string.IsNullOrWhiteSpace(value.DefaultBaseUrl)) return;
        BaseUrl = value.DefaultBaseUrl;
    }
}
