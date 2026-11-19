using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Workbench;

/// <summary>The "?" beside a probe's name and the one help popup it opens: the probe's name, "What it does" and
/// "How it works" from <see cref="ProbeHelp"/>, in the window's dark theme. Esc, the close button or a click outside
/// closes it; opening another probe's help replaces it.</summary>
internal static class ProbeHelpPopup
{
    static readonly Popup Pop = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom, PopupAnimation = PopupAnimation.Fade };
    static Button? _from;
    static string? _closedId;
    static DateTime _closedAt;
    static bool _focusBack;
    static string? _lastId;

    /// <summary>The probe whose help is open now, or null.</summary>
    internal static string? OpenId { get; private set; }

    static ProbeHelpPopup()
    {
        Pop.Closed += (_, _) =>
        {
            _closedId = _lastId; _closedAt = DateTime.UtcNow;
            if (!Pop.IsOpen) OpenId = null;
            // closed by Esc or its button: focus goes back to the "?" that opened it; a click elsewhere keeps its own focus
            if (_focusBack && _from is { IsVisible: true } b) b.Focus();
            _focusBack = false;
        };
    }

    /// <summary>The small round "?" button for one probe.</summary>
    internal static Button Button(string id)
    {
        var b = new Button
        {
            Content = "?", Width = 20, Height = 20, Padding = new Thickness(0), FontSize = 11, FontWeight = FontWeights.Bold,
            Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, Tag = id,
            ToolTip = "What this probe does and how it works",
        };
        b.Template = RoundTemplate();
        AutomationProperties.SetName(b, "Help: " + ProbeNames.Of(id));
        b.Click += (_, _) =>
        {
            // the click that closed this probe's popup (a click outside it, on its own "?") does not reopen it
            if (_closedId == id && (DateTime.UtcNow - _closedAt).TotalMilliseconds < 400) { _closedId = null; return; }
            Show(id, b);
        };
        b.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && OpenId == id) { Close(); e.Handled = true; } };
        return b;
    }

    /// <summary>Open the help for <paramref name="id"/> under <paramref name="target"/>, closing any other first.</summary>
    internal static void Show(string id, Button target)
    {
        Pop.IsOpen = false;
        _from = target;
        var body = Content(id, Close);
        Pop.Child = body;
        Pop.PlacementTarget = target;
        OpenId = _lastId = id;
        Pop.IsOpen = true;
        // keyboard focus into the popup, so Esc and Tab work there at once
        body.Dispatcher.BeginInvoke(() => { if (Pop.IsOpen && Pop.Child is FrameworkElement f && f.Tag is UIElement first) first.Focus(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Close the open help, if any.</summary>
    internal static void Close() { _focusBack = Pop.IsOpen; Pop.IsOpen = false; OpenId = null; }

    /// <summary>The popup's face for one probe: title, close button, and the two headed sections in a scrolling body.
    /// A probe with no entry says so in one sentence.</summary>
    internal static FrameworkElement Content(string id, Action? close = null)
    {
        var help = ProbeHelp.Of(id);
        var title = new TextBlock { Text = ProbeNames.Of(id), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush(Theme.Text), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var x = new Button { Content = "Close", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top, ToolTip = "Close (Esc)" };
        AutomationProperties.SetName(x, "Close help");
        x.Click += (_, _) => close?.Invoke();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(x, Dock.Right);
        head.Children.Add(x); head.Children.Add(title);
        var text = new StackPanel();
        TextBlock Para(string s) => new() { Text = s, TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush(Theme.Text), FontSize = 13, LineHeight = 19, Margin = new Thickness(0, 0, 0, 8) };
        TextBlock Heading(string s) => new() { Text = s, FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = Theme.Brush(Theme.Accent), Margin = new Thickness(0, 4, 0, 4) };
        if (help is null) text.Children.Add(Para(ProbeHelp.Missing));
        else
        {
            text.Children.Add(Heading("What it does"));
            text.Children.Add(Para(help.What));
            text.Children.Add(Heading("How it works"));
            foreach (var p in help.Paragraphs) text.Children.Add(Para(p));
        }
        var scroll = new ScrollViewer { Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = true, Padding = new Thickness(0, 0, 6, 0) };
        AutomationProperties.SetName(scroll, "Help text");
        var dock = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head); dock.Children.Add(scroll);
        var face = new Border
        {
            Background = Theme.Brush(Theme.Panel), BorderBrush = Theme.Brush(Theme.Accent), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 10, 10, 10), Width = 480, MaxHeight = 460, Child = dock, Tag = scroll,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 3, Opacity = 0.5, Color = Colors.Black },
            Margin = new Thickness(0, 2, 8, 8),
        };
        KeyboardNavigation.SetTabNavigation(face, KeyboardNavigationMode.Cycle);
        face.SetValue(TextElement.ForegroundProperty, Theme.Brush(Theme.Text));
        // the popup inherits from the "?" it is placed on (bold, small): its own weight and size are set here
        face.SetValue(TextElement.FontWeightProperty, FontWeights.Normal);
        face.SetValue(TextElement.FontSizeProperty, 13.0);
        face.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { close?.Invoke(); e.Handled = true; } };
        return face;
    }

    /// <summary>The "?" drawn as a small circle in the theme's button colours.</summary>
    static ControlTemplate RoundTemplate()
    {
        const string xaml = """
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
  <Grid>
    <Ellipse x:Name="Ring" Fill="#3A3A40" Stroke="#6A6A72" StrokeThickness="1"/>
    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
  </Grid>
  <ControlTemplate.Triggers>
    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Ring" Property="Stroke" Value="#3D8BFD"/><Setter TargetName="Ring" Property="Fill" Value="#4A4A52"/></Trigger>
    <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Ring" Property="Stroke" Value="#3D8BFD"/><Setter TargetName="Ring" Property="StrokeThickness" Value="2"/></Trigger>
    <Trigger Property="IsPressed" Value="True"><Setter TargetName="Ring" Property="Fill" Value="#264F78"/></Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>
""";
        return _round ??= (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    static ControlTemplate? _round;
}
