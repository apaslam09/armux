using System.Diagnostics;
using System.Text;
using Armux.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Armux.App;

/// <summary>
/// File panel opened from the explorer, in place of Warp's code editor (deviation D-17): read-only, showing the first
/// <see cref="MaxBytes"/> of a text file in the terminal font; binary files get an open button. settings.json opens
/// editable instead (Zed's "Edit in settings.json", D-31): Ctrl+S saves it and armux applies it at once.
/// </summary>
public sealed partial class FileView : Grid
{
    const int MaxBytes = 256 * 1024;

    readonly Func<string, string?>? save;
    readonly TextBox? editor;
    readonly TextBlock? status;
    /// <summary>The editor's text as last loaded or saved (in the text box's own line endings).</summary>
    string savedText = "";

    public bool Editable => editor is not null;
    bool Dirty => editor is not null && editor.Text != savedText;

    /// <param name="save">Makes the file editable: called with the text on Ctrl+S; returns an error to show, or null when saved.</param>
    public FileView(Surface surface, Func<string, string?>? save = null)
    {
        this.save = save;
        Background = Theme.Brush("Pane");
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());

        var head = new Grid { Padding = new Thickness(12, 4, 6, 4), ColumnSpacing = 8 };
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var path = Ui.Text(surface.FilePath, 11.5, Theme.Brush("TextFaint"));
        path.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(path);
        var open = new HyperlinkButton { Content = "Open with default app", FontSize = 11.5, Padding = new Thickness(6, 2, 6, 2) };
        open.Click += (_, _) => OpenExternally(surface.FilePath);
        SetColumn(open, 2);
        head.Children.Add(open);
        Children.Add(head);

        if (save is not null)
        {
            status = Ui.Text("", 11.5, Theme.Brush("TextDim"));
            status.VerticalAlignment = VerticalAlignment.Center;
            SetColumn(status, 1);
            head.Children.Add(status);
            editor = Editor();
            SetRow(editor, 1);
            Children.Add(editor);
            Reload(ReadAll(surface.FilePath));
            return;
        }

        var (text, note) = Read(surface.FilePath);
        var scroll = new ScrollViewer
        {
            Padding = new Thickness(12, 4, 12, 8),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
        };
        var stack = new StackPanel { Spacing = 6 };
        if (note is not null) stack.Children.Add(Ui.Text(note, 12, Theme.Brush("TextDim")));
        if (text is not null)
            stack.Children.Add(new TextBlock
            {
                Text = text,
                FontFamily = Theme.TerminalFont,
                FontSize = Theme.TerminalFontSize,
                LineHeight = Math.Round(Theme.TerminalFontSize * Theme.Settings.LineHeight),
                Foreground = Theme.Term(TermColor.Default),
                IsTextSelectionEnabled = true,
            });
        scroll.Content = stack;
        SetRow(scroll, 1);
        Children.Add(scroll);
    }

    TextBox Editor()
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            FontFamily = Theme.TerminalFont,
            FontSize = Theme.TerminalFontSize,
            Padding = new Thickness(12, 6, 12, 8),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        var bg = Theme.Brush("Pane");
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused" })
            box.Resources[key] = bg;
        foreach (var key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" })
            box.Resources[key] = Theme.Term(TermColor.Default);
        box.Background = bg;
        box.Foreground = Theme.Term(TermColor.Default);
        box.TextChanged += (_, _) => ShowStatus(null);
        box.PreviewKeyDown += OnEditorKey;
        return box;
    }

    void OnEditorKey(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl && e.Key == VirtualKey.S) { e.Handled = true; Save(); }
        else if (!ctrl && e.Key == VirtualKey.Tab && editor is not null)
        {
            // Tab indents (two spaces, as the file is written) instead of leaving the editor.
            e.Handled = true;
            int at = editor.SelectionStart;
            editor.SelectedText = "  ";
            editor.SelectionStart = at + 2;
            editor.SelectionLength = 0;
        }
    }

    void Save()
    {
        if (editor is null || save is null) return;
        var text = editor.Text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", Environment.NewLine);
        if (save(text) is { } error) { ShowStatus(error); return; }
        savedText = editor.Text;
        ShowStatus(null, saved: true);
    }

    void ShowStatus(string? error, bool saved = false)
    {
        if (status is null) return;
        status.Foreground = Theme.Brush(error is not null ? "Term.Red" : "TextDim");
        status.Text = error ?? (Dirty ? "Edited · Ctrl+S saves and applies" : saved ? "Saved and applied" : "Ctrl+S saves and applies");
        ToolTipService.SetToolTip(status, error);
    }

    /// <summary>The file changed on disk (a setting changed elsewhere): show the new text unless there are unsaved edits.</summary>
    public void Reload(string? text)
    {
        if (editor is null || text is null || Dirty) return;
        // The text box keeps lines ending in \r; the same text from disk changes nothing (and keeps the caret).
        if (text.Replace("\r\n", "\r").Replace('\n', '\r') == editor.Text) return;
        int caret = editor.SelectionStart;
        editor.Text = text;
        savedText = editor.Text;
        editor.SelectionStart = Math.Min(caret, editor.Text.Length);
        ShowStatus(null);
    }

    public void FocusEditor() => editor?.Focus(FocusState.Programmatic);

    static string? ReadAll(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The text to show and an optional note (truncated, binary, unreadable).</summary>
    static (string? Text, string? Note) Read(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var buf = new byte[(int)Math.Min(f.Length, MaxBytes)];
            f.ReadExactly(buf);
            if (Array.IndexOf(buf, (byte)0, 0, Math.Min(buf.Length, 8192)) >= 0)
                return (null, "Binary file. Use \"Open with default app\".");
            var text = Encoding.UTF8.GetString(buf).Replace("\t", "    ");
            return (text, f.Length > MaxBytes ? $"Showing the first {MaxBytes / 1024} KB of {f.Length / 1024} KB." : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, $"Can't read this file: {e.Message}");
        }
    }

    public static void OpenExternally(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { App.Log(nameof(OpenExternally), e); }
    }
}
