using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NativeTavern.Data;
using NativeTavern.Data.Repositories;
using NativeTavern.Helpers;
using NativeTavern.Importers;
using NativeTavern.Maui.Security;
using NativeTavern.Maui.ViewModels;
using NativeTavern.Maui.Views;
using NativeTavern.Providers;
using NativeTavern.Security;
using NativeTavern.Services;

namespace NativeTavern.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        // Redirect every managed path (database, avatars, attachments, plugins, logs)
        // to Android app data before anything reads AppPaths.
        AppPaths.UseRoot(FileSystem.AppDataDirectory);
        AppPaths.EnsureCreated();
        AppVersion.Use(typeof(App).Assembly.GetName().Version ?? new Version(0, 1, 0));

        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.Services.AddSingleton(_ => new DatabaseConnectionFactory(AppPaths.DatabaseFile));
        builder.Services.AddSingleton<DatabaseInitializer>();
        builder.Services.AddSingleton<SettingsRepository>();
        builder.Services.AddSingleton<ISecretProtector, SecureStorageSecretProtector>();
        builder.Services.AddSingleton<SettingsService>();

        builder.Services.AddSingleton<CharacterRepository>();
        builder.Services.AddSingleton<ChatSessionRepository>();
        builder.Services.AddSingleton<ChatMessageRepository>();
        builder.Services.AddSingleton<MessageSwipeRepository>();
        builder.Services.AddSingleton<ChatAttachmentRepository>();
        builder.Services.AddSingleton<PromptRepository>();
        builder.Services.AddSingleton<KnowledgeRepository>();
        builder.Services.AddSingleton<CharacterStatusRepository>();

        builder.Services.AddSingleton<CharacterCardImporter>();
        builder.Services.AddSingleton<ICharacterService, CharacterService>();
        builder.Services.AddSingleton<KnowledgeService>();
        builder.Services.AddSingleton<AttachmentService>();
        builder.Services.AddSingleton<ConversationSummaryService>();
        builder.Services.AddSingleton<PluginService>();
        builder.Services.AddSingleton<CharacterStatusService>();
        builder.Services.AddSingleton<ReplySuggestionService>();

        builder.Services.AddHttpClient<OpenAICompatibleProvider>(client => client.Timeout = Timeout.InfiniteTimeSpan);
        builder.Services.AddHttpClient<ClaudeProvider>(client => client.Timeout = Timeout.InfiniteTimeSpan);
        builder.Services.AddSingleton<ProviderRouter>();
        builder.Services.AddSingleton<ProviderDiscoveryService>();
        builder.Services.AddSingleton<ILLMProvider>(provider => provider.GetRequiredService<ProviderRouter>());
        builder.Services.AddSingleton<ChatService>();

        builder.Services.AddSingleton<ChatViewModel>();
        builder.Services.AddSingleton<SettingsViewModel>();
        builder.Services.AddSingleton<CharactersViewModel>();
        builder.Services.AddSingleton<ChatPage>();
        builder.Services.AddSingleton<SettingsPage>();
        builder.Services.AddSingleton<CharactersPage>();
        builder.Services.AddSingleton<App>();

        builder.Logging.AddDebug();

        var app = builder.Build();
        // Create/upgrade the SQLite schema before any page touches the database.
        app.Services.GetRequiredService<DatabaseInitializer>()
            .InitializeAsync().GetAwaiter().GetResult();
        return app;
    }
}
