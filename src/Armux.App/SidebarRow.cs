using System.ComponentModel;
using Armux.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Armux.App;

/// <summary>
/// One workspace in the vertical sidebar (cmux "vertical tabs"): color rail, unread badge, title,
/// latest status or notification, branch · directory, ports and PR line.
/// </summary>
public sealed partial class SidebarRow : Grid
{
    readonly Workspace ws;
    readonly AppState state;
    readonly Border card = new() { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 11), MinHeight = 64 };
    readonly Rectangle rail = new() { Width = 3, RadiusX = 1.5, RadiusY = 1.5, Margin = new Thickness(0, 8, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
    /// <summary>Activity dot before the status line: working, needs input, error, or idle.</summary>
    readonly Ellipse dot = new() { Width = 7, Height = 7, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly Rectangle underline = new() { Height = 2, RadiusX = 1, RadiusY = 1, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(16, 0, 16, 0) };
    readonly Border badge = new() { MinWidth = 16, Height = 16, CornerRadius = new CornerRadius(8), Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock badgeText = new() { FontSize = 10.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly FontIcon pin;
    readonly TextBlock title, status, location, pr;
    bool hover;

    public SidebarRow(Workspace ws, AppState state, Action<Workspace> select, Action<Workspace, FrameworkElement> menu)
    {
        this.ws = ws;
        this.state = state;
        Margin = new Thickness(8, 1, 8, 4);

        title = Ui.Text("", 13.5, Theme.Brush("Text"), semibold: true);
        status = Ui.Text("", 12, Theme.Brush("TextDim"));
        status.TextWrapping = TextWrapping.Wrap;
        status.MaxLines = 2;
        location = Ui.Text("", 11.5, Theme.Brush("TextFaint"));
        pr = Ui.Text("", 11.5, Theme.Brush("Link"));
        pin = Ui.Glyph(Ui.Pin, 10, Theme.Brush("TextFaint"));
        pin.Margin = new Thickness(6, 0, 0, 0);
        badge.Child = badgeText;

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(badge);
        head.Children.Add(title);
        head.Children.Add(pin);

        var statusRow = new Grid();
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        statusRow.ColumnDefinitions.Add(new ColumnDefinition());
        dot.VerticalAlignment = VerticalAlignment.Top;
        dot.Margin = new Thickness(1, 6, 7, 0);
        statusRow.Children.Add(dot);
        SetColumn(status, 1);
        statusRow.Children.Add(status);

        var body = new StackPanel { Spacing = 3 };
        body.Children.Add(head);
        body.Children.Add(statusRow);
        body.Children.Add(location);
        body.Children.Add(pr);
        card.Child = body;

        Children.Add(card);
        Children.Add(rail);
        Children.Add(underline);

        PointerEntered += (_, _) => { hover = true; Update(); };
        PointerExited += (_, _) => { hover = false; Update(); };
        Tapped += (_, _) => select(ws);
        RightTapped += (_, e) => { menu(ws, this); e.Handled = true; };

        ws.PropertyChanged += OnChanged;
        state.PropertyChanged += OnChanged;
        Unloaded += (_, _) => { ws.PropertyChanged -= OnChanged; state.PropertyChanged -= OnChanged; };
        Update();
    }

    void OnChanged(object? sender, PropertyChangedEventArgs e) => DispatcherQueue.TryEnqueue(Update);

    void Update()
    {
        card.Background = ws.Selected ? Theme.Brush("SelectedRow") : hover ? Theme.Brush("SidebarHover") : null;
        title.Text = ws.Title;
        status.Text = ws.Status;
        status.Visibility = string.IsNullOrEmpty(ws.Status) ? Visibility.Collapsed : Visibility.Visible;
        dot.Visibility = status.Visibility;
        dot.Fill = ws.Activity switch
        {
            AgentState.Working => Theme.Hex("#22C55E"),
            AgentState.NeedsInput => Theme.Brush("Waiting"),
            AgentState.Error => Theme.Hex("#EF4444"),
            _ => Theme.Brush("TextFaint"),
        };
        // Waiting for an answer is the state to notice from across the room: the status line turns orange too.
        status.Foreground = Theme.Brush(ws.Activity == AgentState.NeedsInput ? "Waiting" : "TextDim");
        location.Text = string.IsNullOrEmpty(ws.Ports) ? ws.Location : $"{ws.Location}  {ws.Ports}";
        pr.Text = ws.Pr;
        pr.Visibility = ws.HasPr ? Visibility.Visible : Visibility.Collapsed;
        pin.Visibility = ws.Pinned ? Visibility.Visible : Visibility.Collapsed;

        badge.Visibility = ws.HasUnread ? Visibility.Visible : Visibility.Collapsed;
        badgeText.Text = ws.UnreadCount.ToString();
        badge.Background = Theme.Brush("Accent");
        badgeText.Foreground = Theme.Brush("SelectedText");

        var color = ws.Color is null ? null : Theme.Hex(ws.Color);
        rail.Fill = color;
        rail.Visibility = color is not null && state.Indicator == IndicatorStyle.Accent ? Visibility.Visible : Visibility.Collapsed;
        underline.Fill = color;
        underline.Visibility = color is not null && state.Indicator == IndicatorStyle.Underline ? Visibility.Visible : Visibility.Collapsed;
    }
}
