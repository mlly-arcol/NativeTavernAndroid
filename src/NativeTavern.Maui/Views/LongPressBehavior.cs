using System.Windows.Input;
using Microsoft.Maui.Controls;

namespace NativeTavern.Maui.Views;

// MAUI ships no built-in long-press gesture, so bridge the native Android
// LongClick listener to a command. BindingContext follows the attached view.
public class LongPressBehavior : PlatformBehavior<VisualElement, Android.Views.View>
{
    public static readonly BindableProperty CommandProperty =
        BindableProperty.Create(nameof(Command), typeof(ICommand), typeof(LongPressBehavior));

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    protected override void OnAttachedTo(VisualElement bindable, Android.Views.View platformView)
    {
        base.OnAttachedTo(bindable, platformView);
        platformView.LongClickable = true;
        platformView.LongClick += OnLongClick;
    }

    protected override void OnDetachedFrom(VisualElement bindable, Android.Views.View platformView)
    {
        base.OnDetachedFrom(bindable, platformView);
        platformView.LongClick -= OnLongClick;
    }

    private void OnLongClick(object? sender, Android.Views.View.LongClickEventArgs e)
    {
        if (Command?.CanExecute(null) == true) Command.Execute(null);
        e.Handled = true;
    }
}
