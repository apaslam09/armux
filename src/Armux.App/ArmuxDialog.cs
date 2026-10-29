using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Armux.App;

/// <summary>
/// armux's own modal dialog (deviation D-36), in place of ContentDialog: a ContentDialog made in code gets WinUI's
/// old square template, and the rounded default style can't be reached from code in this app. Same shape as
/// ContentDialog's API (ShowAsync, PrimaryButtonClick with Cancel, ContentDialogResult), drawn with the Settings page's
/// flat controls: a rounded card on a dimmed window, a flat Cancel button and an accent primary button.
/// Enter presses the primary button and Esc cancels. The buttons keep ContentDialog's automation ids
/// (PrimaryButton, CloseButton) for UI Automation tests.
/// </summary>
public sealed class ArmuxDialog
{
    public sealed class ButtonClickArgs { public bool Cancel { get; set; } }

    /// <summary>Before the dialog closes with Primary; set <see cref="ButtonClickArgs.Cancel"/> to keep it open.</summary>
    public event Action<ArmuxDialog, ButtonClickArgs>? PrimaryButtonClick;

    readonly XamlRoot root;
    readonly Popup popup;
    readonly Grid overlay;
    readonly Button primaryButton;
    TaskCompletionSource<ContentDialogResult>? done;
    UIElement? focusedBefore;

    public ArmuxDialog(XamlRoot root, string title, object body, string primary, string close)
    {
        this.root = root;
        overlay = new Grid { Background = Theme.Brush("Overlay"), RequestedTheme = Theme.Dark ? ElementTheme.Dark : ElementTheme.Light };
        // StyleControls covers text boxes too; layering StyleTextBox on the same dictionary throws (0x800F0902).
        Ui.StyleControls(overlay.Resources);

        var card = new Border
        {
            Background = Theme.Brush("Palette"),
            BorderBrush = Theme.Brush("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(24, 20, 24, 20),
            MinWidth = 360,
            MaxWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var stack = new StackPanel { Spacing = 14 };
        stack.Children.Add(Ui.Text(title, 18, Theme.Brush("Text"), semibold: true));
        stack.Children.Add(body switch
        {
            UIElement e => e,
            _ => new TextBlock { Text = body?.ToString() ?? "", TextWrapping = TextWrapping.Wrap, FontSize = 14, Foreground = Theme.Brush("Text") },
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        var closeButton = Button(close, "CloseButton");
        closeButton.Click += (_, _) => Finish(ContentDialogResult.None);
        primaryButton = Button(primary, "PrimaryButton");
        // The accent button: armux's blue, not the Windows accent color.
        foreach (var key in new[] { "ButtonBackground", "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed" })
            primaryButton.Resources[key] = Theme.Brush("Accent");
        primaryButton.Resources["ButtonBackgroundPointerOver"] = Theme.Brush("Link");
        primaryButton.Resources["ButtonBackgroundPressed"] = Theme.Brush("Accent");
        foreach (var key in new[] { "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed" })
            primaryButton.Resources[key] = Theme.Brush("SelectedText");
        primaryButton.Click += (_, _) => Primary();
        buttons.Children.Add(primaryButton);
        buttons.Children.Add(closeButton);
        stack.Children.Add(buttons);
        card.Child = stack;
        overlay.Children.Add(card);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(overlay, title);

        overlay.KeyDown += OnKey;
        popup = new Popup { XamlRoot = root, Child = overlay, IsLightDismissEnabled = false };
    }

    static Button Button(string text, string automationId)
    {
        var b = new Button { Content = text, MinWidth = 96, MinHeight = 32, Padding = new Thickness(14, 5, 14, 6), FontSize = 13.5, CornerRadius = new CornerRadius(6) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(b, automationId);
        return b;
    }

    void OnKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) { e.Handled = true; Finish(ContentDialogResult.None); }
        // A focused button presses itself on Enter; anywhere else (a text box) Enter means the primary button.
        else if (e.Key == VirtualKey.Enter && e.OriginalSource is not Microsoft.UI.Xaml.Controls.Primitives.ButtonBase) { e.Handled = true; Primary(); }
    }

    void Primary()
    {
        var args = new ButtonClickArgs();
        PrimaryButtonClick?.Invoke(this, args);
        if (!args.Cancel) Finish(ContentDialogResult.Primary);
    }

    public Task<ContentDialogResult> ShowAsync()
    {
        done = new TaskCompletionSource<ContentDialogResult>();
        focusedBefore = FocusManager.GetFocusedElement(root) as UIElement;
        Fit();
        root.Changed += OnRootChanged;
        popup.IsOpen = true;
        // The first text box gets the keyboard (rename, new file), otherwise the primary button.
        overlay.Loaded += (_, _) =>
        {
            if (FirstTextBox(overlay) is { } box) { box.Focus(FocusState.Programmatic); box.SelectAll(); }
            else primaryButton.Focus(FocusState.Programmatic);
        };
        return done.Task;
    }

    void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Fit();

    /// <summary>The dimmed backdrop covers the whole window.</summary>
    void Fit()
    {
        overlay.Width = root.Size.Width;
        overlay.Height = root.Size.Height;
    }

    void Finish(ContentDialogResult result)
    {
        if (done is null || done.Task.IsCompleted) return;
        root.Changed -= OnRootChanged;
        popup.IsOpen = false;
        focusedBefore?.Focus(FocusState.Programmatic);
        done.SetResult(result);
    }

    static TextBox? FirstTextBox(DependencyObject d)
    {
        if (d is TextBox t) return t;
        for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(d); i++)
            if (FirstTextBox(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(d, i)) is { } found) return found;
        return null;
    }
}
