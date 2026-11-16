using System.Windows;
using System.Windows.Markup;

namespace Workbench;

/// <summary>The window's dark theme (the default) and the pill switch the Start tab's probe tiles use.</summary>
internal static class Theme
{
    internal const string Back = "#1E1E1E", Panel = "#252526", Tile = "#2D2D33", Line = "#3F3F46", Text = "#E6E6E6", Dim = "#9A9A9A", Accent = "#3D8BFD";

    const string Xaml = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <SolidColorBrush x:Key="{x:Static SystemColors.WindowBrushKey}" Color="#1E1E1E"/>
  <SolidColorBrush x:Key="{x:Static SystemColors.WindowTextBrushKey}" Color="#E6E6E6"/>
  <SolidColorBrush x:Key="{x:Static SystemColors.ControlBrushKey}" Color="#2D2D30"/>
  <SolidColorBrush x:Key="{x:Static SystemColors.ControlTextBrushKey}" Color="#E6E6E6"/>
  <SolidColorBrush x:Key="{x:Static SystemColors.GrayTextBrushKey}" Color="#8A8A8A"/>
  <SolidColorBrush x:Key="{x:Static SystemColors.HighlightBrushKey}" Color="#264F78"/>
  <SolidColorBrush x:Key="{x:Static SystemColors.HighlightTextBrushKey}" Color="#FFFFFF"/>
  <Style TargetType="Window"><Setter Property="Background" Value="#1E1E1E"/><Setter Property="Foreground" Value="#E6E6E6"/></Style>
  <Style TargetType="TabControl"><Setter Property="Background" Value="#252526"/><Setter Property="BorderBrush" Value="#3F3F46"/></Style>
  <Style TargetType="TabItem"><Setter Property="Foreground" Value="#111111"/></Style>
  <Style TargetType="ListBox"><Setter Property="Background" Value="#1E1E1E"/><Setter Property="Foreground" Value="#E6E6E6"/><Setter Property="BorderBrush" Value="#3F3F46"/></Style>
  <Style TargetType="TreeView"><Setter Property="Background" Value="#1E1E1E"/><Setter Property="Foreground" Value="#E6E6E6"/><Setter Property="BorderBrush" Value="#3F3F46"/></Style>
  <Style TargetType="TreeViewItem"><Setter Property="Foreground" Value="#E6E6E6"/></Style>
  <Style TargetType="TextBox"><Setter Property="Background" Value="#2D2D30"/><Setter Property="Foreground" Value="#E6E6E6"/><Setter Property="BorderBrush" Value="#55555C"/><Setter Property="CaretBrush" Value="#E6E6E6"/></Style>
  <Style TargetType="ComboBox"><Setter Property="Foreground" Value="#111111"/></Style>
  <Style TargetType="ComboBoxItem"><Setter Property="Foreground" Value="#E6E6E6"/></Style>
  <Style TargetType="Button"><Setter Property="Background" Value="#3A3A40"/><Setter Property="Foreground" Value="#E6E6E6"/><Setter Property="BorderBrush" Value="#55555C"/><Setter Property="Padding" Value="8,2"/></Style>
  <Style TargetType="CheckBox"><Setter Property="Foreground" Value="#E6E6E6"/></Style>
  <Style TargetType="GroupBox"><Setter Property="Foreground" Value="#E6E6E6"/><Setter Property="BorderBrush" Value="#4A4A52"/><Setter Property="Margin" Value="2,6,2,2"/><Setter Property="Padding" Value="4"/></Style>
  <Style TargetType="Expander"><Setter Property="Foreground" Value="#E6E6E6"/></Style>
  <Style TargetType="ProgressBar"><Setter Property="Foreground" Value="#3D8BFD"/><Setter Property="Background" Value="#3A3A40"/><Setter Property="BorderBrush" Value="#3A3A40"/></Style>
  <Style x:Key="Pill" TargetType="ToggleButton">
    <Setter Property="Width" Value="42"/><Setter Property="Height" Value="22"/><Setter Property="Cursor" Value="Hand"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ToggleButton">
          <Grid>
            <Border x:Name="Track" CornerRadius="11" Background="#55555C"/>
            <Ellipse x:Name="Knob" Width="16" Height="16" Fill="#D0D0D0" HorizontalAlignment="Left" Margin="3,0,0,0"/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsChecked" Value="True">
              <Setter TargetName="Track" Property="Background" Value="#3D8BFD"/>
              <Setter TargetName="Knob" Property="HorizontalAlignment" Value="Right"/>
              <Setter TargetName="Knob" Property="Margin" Value="0,0,3,0"/>
              <Setter TargetName="Knob" Property="Fill" Value="#FFFFFF"/>
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
              <Setter TargetName="Track" Property="Opacity" Value="0.35"/>
              <Setter TargetName="Knob" Property="Opacity" Value="0.45"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>
""";

    static ResourceDictionary? _dark;

    /// <summary>The dark dictionary, parsed once.</summary>
    internal static ResourceDictionary Dark => _dark ??= (ResourceDictionary)XamlReader.Parse(Xaml);

    /// <summary>Apply the dark theme to an application (the workbench's default).</summary>
    internal static void Apply(Application app) { if (!app.Resources.MergedDictionaries.Contains(Dark)) app.Resources.MergedDictionaries.Add(Dark); }

    internal static System.Windows.Media.SolidColorBrush Brush(string hex) => new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
}
