using NativeTavern.Maui.Views;

namespace NativeTavern.Maui;

public partial class App : Application
{
    private readonly ChatPage chatPage;

    public App(ChatPage chatPage) => this.chatPage = chatPage;

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = base.CreateWindow(activationState);
        window.Page = chatPage;
        return window;
    }
}
