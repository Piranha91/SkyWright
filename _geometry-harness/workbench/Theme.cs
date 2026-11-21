using System.Windows;
using System.Windows.Markup;

namespace Workbench;

/// <summary>The window's dark theme (the default): its buttons and toggle buttons, and the pill switch the Start tab's probe tiles use.</summary>
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
  <Style TargetType="ListBox"><Setter Property="Background" Value="#1E1E1E"/><Setter Property="Foreground" Value="#E6E6E6"/><Setter Property="BorderBrush" Value="#3F3F46"/></Style>
  <Style TargetType="ListView"><Setter Property="Background" Value="#1E1E1E"/><Setter Property="Foreground" Value="#E6E6E6"/><Setter Property="BorderBrush" Value="#3F3F46"/></Style>
  <Style TargetType="ListViewItem">
    <Setter Property="Foreground" Value="#E6E6E6"/><Setter Property="ToolTip" Value="{Binding Tip}"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ListViewItem">
          <Border x:Name="Bd" Background="Transparent" Padding="0,3">
            <GridViewRowPresenter Columns="{TemplateBinding GridView.ColumnCollection}" Content="{TemplateBinding Content}"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Bd" Property="Background" Value="#2D2D33"/></Trigger>
            <Trigger Property="IsSelected" Value="True"><Setter TargetName="Bd" Property="Background" Value="#264F78"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType="GridViewColumnHeader">
    <Setter Property="Foreground" Value="#C8C8C8"/><Setter Property="HorizontalContentAlignment" Value="Left"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="GridViewColumnHeader">
          <Grid>
            <Border x:Name="Bd" Background="#252526" BorderBrush="#3F3F46" BorderThickness="0,0,1,1" Padding="6,4">
              <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center"/>
            </Border>
            <Thumb x:Name="PART_HeaderGripper" HorizontalAlignment="Right" Width="6" Cursor="SizeWE" Opacity="0"/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Bd" Property="Background" Value="#2D2D33"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType="TreeView"><Setter Property="Background" Value="#1E1E1E"/><Setter Property="Foreground" Value="#E6E6E6"/><Setter Property="BorderBrush" Value="#3F3F46"/></Style>
  <Style TargetType="TreeViewItem"><Setter Property="Foreground" Value="#E6E6E6"/></Style>
  <Style TargetType="TextBox"><Setter Property="Background" Value="#2D2D30"/><Setter Property="Foreground" Value="#E6E6E6"/><Setter Property="BorderBrush" Value="#55555C"/><Setter Property="CaretBrush" Value="#E6E6E6"/></Style>
  <Style TargetType="TabItem">
    <Setter Property="Foreground" Value="#C8C8C8"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="TabItem">
          <Border x:Name="Bd" Background="#2D2D30" BorderBrush="#3F3F46" BorderThickness="1,1,1,0" CornerRadius="4,4,0,0" Padding="12,4" Margin="0,0,2,0">
            <ContentPresenter ContentSource="Header" VerticalAlignment="Center" HorizontalAlignment="Center"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Bd" Property="Background" Value="#3A3A40"/></Trigger>
            <Trigger Property="IsSelected" Value="True">
              <Setter TargetName="Bd" Property="Background" Value="#252526"/>
              <Setter TargetName="Bd" Property="BorderBrush" Value="#3D8BFD"/>
              <Setter Property="Foreground" Value="#FFFFFF"/>
              <Setter Property="Panel.ZIndex" Value="1"/>
            </Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Foreground" Value="#6A6A6A"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style x:Key="ComboToggle" TargetType="ToggleButton">
    <Setter Property="Focusable" Value="False"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ToggleButton">
          <Border x:Name="Bd" Background="#2D2D30" BorderBrush="#55555C" BorderThickness="1" CornerRadius="3">
            <Path HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,7,0" Data="M 0 0 L 4 4 L 8 0 Z" Fill="#C8C8C8"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Bd" Property="BorderBrush" Value="#3D8BFD"/></Trigger>
            <Trigger Property="IsChecked" Value="True"><Setter TargetName="Bd" Property="Background" Value="#3A3A40"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType="ComboBox">
    <Setter Property="Foreground" Value="#E6E6E6"/>
    <Setter Property="MinHeight" Value="22"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ComboBox">
          <Grid>
            <ToggleButton Style="{StaticResource ComboToggle}" IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}" ClickMode="Press"/>
            <ContentPresenter x:Name="Content" IsHitTestVisible="False" Margin="6,2,22,2" VerticalAlignment="Center" HorizontalAlignment="Left"
                              Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"/>
            <TextBox x:Name="PART_EditableTextBox" Visibility="Hidden" Margin="2,1,20,1" VerticalAlignment="Center" Background="Transparent" BorderThickness="0"
                     Foreground="#E6E6E6" CaretBrush="#E6E6E6" IsReadOnly="{TemplateBinding IsReadOnly}"/>
            <Popup x:Name="PART_Popup" Placement="Bottom" AllowsTransparency="True" Focusable="False" PopupAnimation="Fade"
                   IsOpen="{TemplateBinding IsDropDownOpen}">
              <Border Background="#252526" BorderBrush="#55555C" BorderThickness="1" MinWidth="{TemplateBinding ActualWidth}" MaxHeight="{TemplateBinding MaxDropDownHeight}">
                <ScrollViewer SnapsToDevicePixels="True"><StackPanel IsItemsHost="True" KeyboardNavigation.DirectionalNavigation="Contained"/></ScrollViewer>
              </Border>
            </Popup>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsEditable" Value="True">
              <Setter TargetName="PART_EditableTextBox" Property="Visibility" Value="Visible"/>
              <Setter TargetName="Content" Property="Visibility" Value="Hidden"/>
            </Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.5"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType="ComboBoxItem">
    <Setter Property="Foreground" Value="#E6E6E6"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ComboBoxItem">
          <Border x:Name="Bd" Background="Transparent" Padding="6,3"><ContentPresenter/></Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Bd" Property="Background" Value="#264F78"/></Trigger>
            <Trigger Property="IsSelected" Value="True"><Setter TargetName="Bd" Property="Background" Value="#3D8BFD"/><Setter Property="Foreground" Value="#FFFFFF"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style x:Key="SbPage" TargetType="RepeatButton">
    <Setter Property="Focusable" Value="False"/>
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style x:Key="SbThumb" TargetType="Thumb">
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Thumb">
          <Border x:Name="Bd" Background="#5A5A60" CornerRadius="4" Margin="2"/>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Bd" Property="Background" Value="#7A7A82"/></Trigger>
            <Trigger Property="IsDragging" Value="True"><Setter TargetName="Bd" Property="Background" Value="#3D8BFD"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <ControlTemplate x:Key="VScroll" TargetType="ScrollBar">
    <Border Background="#1E1E1E" Width="12">
      <Track x:Name="PART_Track" IsDirectionReversed="True">
        <Track.DecreaseRepeatButton><RepeatButton Style="{StaticResource SbPage}" Command="ScrollBar.PageUpCommand"/></Track.DecreaseRepeatButton>
        <Track.Thumb><Thumb Style="{StaticResource SbThumb}"/></Track.Thumb>
        <Track.IncreaseRepeatButton><RepeatButton Style="{StaticResource SbPage}" Command="ScrollBar.PageDownCommand"/></Track.IncreaseRepeatButton>
      </Track>
    </Border>
  </ControlTemplate>
  <ControlTemplate x:Key="HScroll" TargetType="ScrollBar">
    <Border Background="#1E1E1E" Height="12">
      <Track x:Name="PART_Track" IsDirectionReversed="False">
        <Track.DecreaseRepeatButton><RepeatButton Style="{StaticResource SbPage}" Command="ScrollBar.PageLeftCommand"/></Track.DecreaseRepeatButton>
        <Track.Thumb><Thumb Style="{StaticResource SbThumb}"/></Track.Thumb>
        <Track.IncreaseRepeatButton><RepeatButton Style="{StaticResource SbPage}" Command="ScrollBar.PageRightCommand"/></Track.IncreaseRepeatButton>
      </Track>
    </Border>
  </ControlTemplate>
  <Style TargetType="ScrollBar">
    <Setter Property="Template" Value="{StaticResource VScroll}"/>
    <Style.Triggers>
      <Trigger Property="Orientation" Value="Horizontal"><Setter Property="Template" Value="{StaticResource HScroll}"/></Trigger>
    </Style.Triggers>
  </Style>
  <Style TargetType="Button">
    <Setter Property="Background" Value="#3A3A40"/><Setter Property="Foreground" Value="#E6E6E6"/><Setter Property="BorderBrush" Value="#55555C"/><Setter Property="Padding" Value="8,2"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Button">
          <Border x:Name="Bd" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="3" Padding="{TemplateBinding Padding}">
            <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Bd" Property="Background" Value="#4A4A52"/><Setter TargetName="Bd" Property="BorderBrush" Value="#3D8BFD"/></Trigger>
            <Trigger Property="IsPressed" Value="True"><Setter TargetName="Bd" Property="Background" Value="#264F78"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Foreground" Value="#6A6A6A"/><Setter TargetName="Bd" Property="Background" Value="#2D2D30"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType="ToggleButton">
    <Setter Property="Background" Value="#3A3A40"/><Setter Property="Foreground" Value="#E6E6E6"/><Setter Property="BorderBrush" Value="#55555C"/><Setter Property="Padding" Value="8,2"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ToggleButton">
          <Border x:Name="Bd" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="3" Padding="{TemplateBinding Padding}">
            <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Bd" Property="Background" Value="#4A4A52"/><Setter TargetName="Bd" Property="BorderBrush" Value="#3D8BFD"/></Trigger>
            <Trigger Property="IsChecked" Value="True"><Setter TargetName="Bd" Property="Background" Value="#264F78"/><Setter TargetName="Bd" Property="BorderBrush" Value="#3D8BFD"/><Setter Property="Foreground" Value="#FFFFFF"/></Trigger>
            <MultiTrigger>
              <MultiTrigger.Conditions><Condition Property="IsChecked" Value="True"/><Condition Property="IsMouseOver" Value="True"/></MultiTrigger.Conditions>
              <Setter TargetName="Bd" Property="Background" Value="#31629A"/>
            </MultiTrigger>
            <Trigger Property="IsPressed" Value="True"><Setter TargetName="Bd" Property="Background" Value="#1D3D5E"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Foreground" Value="#6A6A6A"/><Setter TargetName="Bd" Property="Background" Value="#2D2D30"/><Setter TargetName="Bd" Property="BorderBrush" Value="#3F3F46"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType="CheckBox">
    <Setter Property="Foreground" Value="#E6E6E6"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="CheckBox">
          <StackPanel Orientation="Horizontal" Background="Transparent">
            <Border x:Name="Box" Width="14" Height="14" Background="#2D2D30" BorderBrush="#6A6A72" BorderThickness="1" CornerRadius="2" VerticalAlignment="Center">
              <Path x:Name="Mark" Data="M 2 6 L 5 9 L 10 3" Stroke="#FFFFFF" StrokeThickness="2" Visibility="Collapsed"/>
            </Border>
            <ContentPresenter Margin="6,0,0,0" VerticalAlignment="Center"/>
          </StackPanel>
          <ControlTemplate.Triggers>
            <Trigger Property="IsChecked" Value="True"><Setter TargetName="Mark" Property="Visibility" Value="Visible"/><Setter TargetName="Box" Property="Background" Value="#3D8BFD"/><Setter TargetName="Box" Property="BorderBrush" Value="#3D8BFD"/></Trigger>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Box" Property="BorderBrush" Value="#3D8BFD"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Foreground" Value="#7A7A7A"/><Setter TargetName="Box" Property="Opacity" Value="0.4"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType="GroupBox">
    <Setter Property="Foreground" Value="#E6E6E6"/><Setter Property="Margin" Value="2,6,2,2"/><Setter Property="Padding" Value="4"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="GroupBox">
          <Border BorderBrush="#4A4A52" BorderThickness="1" CornerRadius="5" Padding="{TemplateBinding Padding}">
            <DockPanel>
              <ContentPresenter DockPanel.Dock="Top" ContentSource="Header" Margin="4,0,0,4"/>
              <ContentPresenter/>
            </DockPanel>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType="ToolTip"><Setter Property="Background" Value="#2D2D30"/><Setter Property="Foreground" Value="#E6E6E6"/><Setter Property="BorderBrush" Value="#55555C"/></Style>
  <Style TargetType="ContextMenu"><Setter Property="Background" Value="#252526"/><Setter Property="Foreground" Value="#E6E6E6"/><Setter Property="BorderBrush" Value="#55555C"/></Style>
  <Style TargetType="Expander">
    <Setter Property="Foreground" Value="#E6E6E6"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Expander">
          <DockPanel>
            <ToggleButton DockPanel.Dock="Top" IsChecked="{Binding IsExpanded, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}" Cursor="Hand">
              <ToggleButton.Template>
                <ControlTemplate TargetType="ToggleButton">
                  <StackPanel Orientation="Horizontal" Background="Transparent" Margin="0,2">
                    <Path x:Name="Arrow" Data="M 0 0 L 4 4 L 0 8 Z" Fill="#C8C8C8" VerticalAlignment="Center" Margin="2,0,6,0"/>
                    <ContentPresenter Content="{Binding Header, RelativeSource={RelativeSource AncestorType=Expander}}" VerticalAlignment="Center"/>
                  </StackPanel>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsChecked" Value="True"><Setter TargetName="Arrow" Property="Data" Value="M 0 0 L 8 0 L 4 4 Z"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </ToggleButton.Template>
            </ToggleButton>
            <ContentPresenter x:Name="Body" Visibility="Collapsed"/>
          </DockPanel>
          <ControlTemplate.Triggers>
            <Trigger Property="IsExpanded" Value="True"><Setter TargetName="Body" Property="Visibility" Value="Visible"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
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
