using System.Collections.Specialized;
using System.ComponentModel;
using Armux.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;

namespace Armux.App;

public interface IPaneHost
{
    bool IsFocused(Pane pane);
    /// <summary>Makes <paramref name="pane"/> the focused pane; <paramref name="focusTerminal"/> also gives its terminal the keyboard.</summary>
    void FocusPane(Pane pane, bool focusTerminal = true);
    void SelectSurface(Pane pane, Surface surface);
    void CloseSurface(Pane pane, Surface surface);
    void NewSurface(Pane pane, PanelKind kind);
    void SplitPane(Pane pane, SplitDirection direction);
    FrameworkElement PanelFor(Surface surface);
}

/// <summary>
/// A pane: its own tab bar of surfaces (cmux "surfaces"), the selected panel, and the blue notification
/// ring shown while any surface in it needs attention.
/// </summary>
public sealed partial class PaneView : Grid
{
    public Pane Pane { get; }
    readonly IPaneHost host;
    readonly StackPanel tabs = new() { Orientation = Orientation.Horizontal, Height = 32 };
    readonly Grid body = new();
    readonly Border ring = new() { BorderThickness = new Thickness(2), IsHitTestVisible = false };
    readonly Border editorHost = new();

    public PaneView(Pane pane, IPaneHost host)
    {
        Pane = pane;
        this.host = host;
        Background = Theme.Brush("Pane");
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var bar = new Grid { Background = Theme.Brush("TabBar") };
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(new ScrollViewer
        {
            Content = tabs,
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        var dim = Theme.Brush("TextDim");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 6, 0), Spacing = 0 };
        actions.Children.Add(Ui.IconButton(Ui.Glyph(Ui.Terminal, 12, dim), "New terminal (Ctrl+Shift+T)", () => host.NewSurface(Pane, PanelKind.Terminal), 26));
        actions.Children.Add(Ui.IconButton(Ui.Glyph(Ui.Globe, 12, dim), "New browser tab", () => host.NewSurface(Pane, PanelKind.Browser), 26));
        actions.Children.Add(Ui.IconButton(Ui.SplitRightIcon(dim), "Split right (Ctrl+Shift+D)", () => host.SplitPane(Pane, SplitDirection.Right), 26));
        actions.Children.Add(Ui.IconButton(Ui.SplitDownIcon(dim), "Split down (Ctrl+Alt+Shift+D)", () => host.SplitPane(Pane, SplitDirection.Down), 26));
        SetColumn(actions, 1);
        bar.Children.Add(actions);
        bar.Children.Add(new Rectangle { Height = 1, Fill = Theme.Brush("Divider"), VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false });
        SetColumnSpan(bar.Children[^1] as FrameworkElement, 2);
        Children.Add(bar);

        SetRow(body, 1);
        Children.Add(body);
        SetRow(editorHost, 2);
        Children.Add(editorHost);
        SetRowSpan(ring, 3);
        Children.Add(ring);

        // A click anywhere in the pane focuses it; a click in the prompt editor must keep the keyboard there.
        AddHandler(PointerPressedEvent, new PointerEventHandler((_, e) => host.FocusPane(Pane, focusTerminal: !InEditor(e.OriginalSource as DependencyObject))), true);
        pane.Surfaces.CollectionChanged += OnSurfaces;
        foreach (var s in pane.Surfaces) s.PropertyChanged += OnSurface;
        Refresh();
    }

    void OnSurfaces(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (Surface s in e.OldItems ?? Array.Empty<Surface>()) s.PropertyChanged -= OnSurface;
        foreach (Surface s in e.NewItems ?? Array.Empty<Surface>()) s.PropertyChanged += OnSurface;
        DispatcherQueue.TryEnqueue(Refresh);
    }

    void OnSurface(object? sender, PropertyChangedEventArgs e) => DispatcherQueue.TryEnqueue(Refresh);

    public void Refresh()
    {
        bool focused = host.IsFocused(Pane);
        tabs.Children.Clear();
        foreach (var s in Pane.Surfaces)
        {
            bool active = ReferenceEquals(s, Pane.Selected);
            var tab = Tab(s, active, focused);
            tabs.Children.Add(tab);
            // A new tab in a narrow pane can land past the edge of the tab strip; scroll it into view.
            if (active) DispatcherQueue.TryEnqueue(() => tab.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false }));
        }

        if (Pane.Selected is { } sel)
        {
            var panel = host.PanelFor(sel);
            if (!body.Children.Contains(panel))
            {
                body.Children.Clear();
                if (panel.Parent is Panel old) old.Children.Remove(panel);
                body.Children.Add(panel);
            }
        }
        // Orange while a program in the pane waits for an answer; blue for an unread notification.
        ring.BorderBrush = Pane.Waiting ? Theme.Brush("Waiting") : Pane.NeedsAttention ? Theme.Brush("Accent") : null;
        ring.Opacity = 1;
    }

    FrameworkElement Tab(Surface s, bool active, bool paneFocused)
    {
        var g = new Grid { Padding = new Thickness(10, 0, 4, 0), MaxWidth = 240, ColumnSpacing = 6, Height = 32 };
        foreach (var w in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        g.Background = active ? Theme.Brush("TabActive") : null;
        var fg = Theme.Brush(active ? "Text" : "TextDim");

        var icon = Ui.Glyph(s.Kind switch { PanelKind.Browser => Ui.Globe, PanelKind.File => Ui.Document, _ => Ui.Terminal }, 11, fg);
        icon.VerticalAlignment = VerticalAlignment.Center;
        var title = Ui.Text(s.Title, 12.5, fg);
        title.VerticalAlignment = VerticalAlignment.Center;
        var dot = new Ellipse { Width = 7, Height = 7, Fill = Theme.Brush(s.Waiting ? "Waiting" : "Accent"), VerticalAlignment = VerticalAlignment.Center, Visibility = s.Unread || s.Waiting ? Visibility.Visible : Visibility.Collapsed };
        if (s.Waiting) ToolTipService.SetToolTip(dot, "Waiting for your answer");
        var close = Ui.IconButton(Ui.Glyph(Ui.Close, 9, Theme.Brush("TextFaint")), "Close (Ctrl+Shift+W)", () => host.CloseSurface(Pane, s), 20);
        close.VerticalAlignment = VerticalAlignment.Center;
        SetColumn(title, 1); SetColumn(dot, 2); SetColumn(close, 3);
        g.Children.Add(icon); g.Children.Add(title); g.Children.Add(dot); g.Children.Add(close);

        // Active tab: accent underline in the focused pane, a neutral one elsewhere.
        if (active)
        {
            var line = new Rectangle { Height = 2, VerticalAlignment = VerticalAlignment.Bottom, Fill = Theme.Brush(paneFocused ? "Accent" : "Border") };
            SetColumnSpan(line, 4);
            g.Children.Add(line);
            g.Margin = new Thickness(0, 0, 0, 0);
        }
        var sep = new Rectangle { Width = 1, Fill = Theme.Brush("Divider"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, -4, 0) };
        SetColumn(sep, 3);
        g.Children.Add(sep);
        g.Tapped += (_, _) => host.SelectSurface(Pane, s);
        return g;
    }

    bool InEditor(DependencyObject? d)
    {
        for (; d is not null && !ReferenceEquals(d, this); d = VisualTreeHelper.GetParent(d))
            if (ReferenceEquals(d, editorHost)) return true;
        return false;
    }

    /// <summary>Shows a prompt editor under the pane's content, or removes it (null).</summary>
    public void ShowEditor(UIElement? editor)
    {
        if (editor is FrameworkElement { Parent: Border old } && !ReferenceEquals(old, editorHost)) old.Child = null;
        editorHost.Child = editor;
    }

    public bool EditorOpen => editorHost.Child is not null;

    /// <summary>cmux "flash focused panel": pulse the ring twice.</summary>
    public void Flash()
    {
        ring.BorderBrush = Theme.Brush("Accent");
        var anim = new DoubleAnimationUsingKeyFrames();
        double t = 0;
        foreach (var v in new[] { 1.0, 0.0, 1.0, 0.0, 1.0 })
        {
            anim.KeyFrames.Add(new LinearDoubleKeyFrame { Value = v, KeyTime = TimeSpan.FromMilliseconds(t) });
            t += 140;
        }
        var sb = new Storyboard();
        Storyboard.SetTarget(anim, ring);
        Storyboard.SetTargetProperty(anim, "Opacity");
        sb.Children.Add(anim);
        sb.Completed += (_, _) => Refresh();
        sb.Begin();
    }

    public void Detach()
    {
        Pane.Surfaces.CollectionChanged -= OnSurfaces;
        foreach (var s in Pane.Surfaces) s.PropertyChanged -= OnSurface;
    }
}
