using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Armux.App;

/// <summary>
/// Prompt editor (Warp's input editor, Ctrl+G; deviation D-23): an ordinary multi-line text box under a terminal for
/// writing a long prompt with the mouse, selection, undo and spell check, then sending it in one paste. Each terminal
/// keeps its own draft while the editor is closed.
/// </summary>
public sealed partial class PromptEditor : Grid
{
    readonly TextBox box;

    /// <summary>Text to send, and whether Enter follows it (Send) or not (Paste).</summary>
    public event Action<string, bool>? Submitted;
    public event Action? CloseRequested;

    public PromptEditor()
    {
        Background = Theme.Brush("TabBar");
        BorderBrush = Theme.Brush("Divider");
        BorderThickness = new Thickness(0, 1, 0, 0);
        Padding = new Thickness(10, 6, 10, 10);
        RowSpacing = 6;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new Grid { ColumnSpacing = 6 };
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var hint = Ui.Text("Prompt editor   Enter send · Shift+Enter new line · Ctrl+Enter paste without Enter · Esc close", 12, Theme.Brush("TextDim"));
        hint.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(hint);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var insert = new Button { Content = "Paste", Padding = new Thickness(10, 3, 10, 4) };
        ToolTipService.SetToolTip(insert, "Paste into the terminal without Enter (Ctrl+Enter)");
        insert.Click += (_, _) => Submit(enter: false);
        var send = new Button { Content = "Send", Padding = new Thickness(12, 3, 12, 4), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        ToolTipService.SetToolTip(send, "Paste and press Enter (Enter)");
        // armux's accent, not the Windows accent color.
        foreach (var key in new[] { "AccentButtonBackground", "AccentButtonBackgroundPointerOver", "AccentButtonBackgroundPressed" })
            send.Resources[key] = Theme.Brush("Accent");
        send.Resources["AccentButtonForeground"] = send.Resources["AccentButtonForegroundPointerOver"] = Theme.Brush("SelectedText");
        send.Click += (_, _) => Submit(enter: true);
        buttons.Children.Add(insert);
        buttons.Children.Add(send);
        buttons.Children.Add(Ui.IconButton(Ui.Glyph(Ui.Close, 10, Theme.Brush("TextDim")), "Close (Esc or Ctrl+G)", () => CloseRequested?.Invoke(), 26));
        SetColumn(buttons, 1);
        head.Children.Add(buttons);
        Children.Add(head);

        box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            IsSpellCheckEnabled = true,
            MinHeight = 96,
            MaxHeight = 320,
            FontFamily = new FontFamily(Theme.UiFont),
            FontSize = 14,
            PlaceholderText = "Write a prompt…",
        };
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, "Prompt");
        // The default TextBox turns white-ish when focused; keep the editor's own color in every state.
        var bg = Theme.Brush("Pane");
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBackgroundDisabled" })
            box.Resources[key] = bg;
        foreach (var key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" })
            box.Resources[key] = Theme.Brush("Text");
        foreach (var key in new[] { "TextControlBorderBrush", "TextControlBorderBrushPointerOver" })
            box.Resources[key] = Theme.Brush("Border");
        box.Resources["TextControlBorderBrushFocused"] = Theme.Brush("Accent");
        // The template's focused state takes the placeholder color from a binding whose fallback ignores the
        // resources above, so the placeholder went near-black on focus. A set PlaceholderForeground wins in every state.
        box.PlaceholderForeground = Theme.Brush("TextDim");
        box.Background = bg;
        box.Foreground = Theme.Brush("Text");
        box.BorderBrush = Theme.Brush("Border");
        box.PreviewKeyDown += OnKey;
        SetRow(box, 1);
        Children.Add(box);
    }

    public void FocusText()
    {
        box.Focus(FocusState.Programmatic);
        box.SelectionStart = box.Text.Length;
    }

    void Submit(bool enter)
    {
        if (box.Text.Length == 0) return;
        var text = box.Text;
        box.Text = "";
        Submitted?.Invoke(text, enter);
    }

    static bool Down(VirtualKey k) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    void OnKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) { e.Handled = true; CloseRequested?.Invoke(); return; }
        // Enter sends, Ctrl+Enter pastes without Enter, Shift+Enter is left to the text box (a new line).
        if (e.Key != VirtualKey.Enter || Down(VirtualKey.Shift)) return;
        e.Handled = true;
        Submit(enter: !Down(VirtualKey.Control));
    }
}
