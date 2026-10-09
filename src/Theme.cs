using System.Windows;
using System.Windows.Markup;

namespace Bulles;

/// <summary>Styles partagés de Bulles (curseurs fins, rangées de boutons) pour un rendu homogène.</summary>
public static class Theme
{
    private static Style? _slider, _toggle, _button, _accentButton;

    /// <summary>Interrupteur arrondi (à la place des cases à cocher).</summary>
    public static Style Toggle => _toggle ??= (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
               TargetType="CheckBox">
          <Setter Property="Cursor" Value="Hand"/>
          <Setter Property="Focusable" Value="False"/>
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="CheckBox">
                <Border x:Name="track" Width="40" Height="22" CornerRadius="11" Background="#33FFFFFF">
                  <Ellipse x:Name="knob" Width="16" Height="16" Fill="White" HorizontalAlignment="Left" Margin="3,0,0,0"/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsChecked" Value="True">
                    <Setter TargetName="track" Property="Background" Value="#3DA5FF"/>
                    <Setter TargetName="knob" Property="HorizontalAlignment" Value="Right"/>
                    <Setter TargetName="knob" Property="Margin" Value="0,0,3,0"/>
                  </Trigger>
                  <Trigger Property="IsMouseOver" Value="True">
                    <Setter TargetName="track" Property="Opacity" Value="0.9"/>
                  </Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """);

    /// <summary>Bouton sombre arrondi.</summary>
    public static Style Button => _button ??= ButtonStyle("#22FFFFFF", "#38FFFFFF");

    /// <summary>Bouton bleu (action principale).</summary>
    public static Style AccentButton => _accentButton ??= ButtonStyle("#3DA5FF", "#5BB4FF");

    private static Style ButtonStyle(string background, string hover) => (Style)XamlReader.Parse($"""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
               TargetType="Button">
          <Setter Property="Foreground" Value="White"/>
          <Setter Property="FontSize" Value="12.5"/>
          <Setter Property="Cursor" Value="Hand"/>
          <Setter Property="Focusable" Value="False"/>
          <Setter Property="Padding" Value="12,6"/>
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="Button">
                <Border x:Name="bg" CornerRadius="8" Background="{background}" Padding="{"{"}TemplateBinding Padding{"}"}">
                  <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True">
                    <Setter TargetName="bg" Property="Background" Value="{hover}"/>
                  </Trigger>
                  <Trigger Property="IsEnabled" Value="False">
                    <Setter TargetName="bg" Property="Opacity" Value="0.5"/>
                  </Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """);

    /// <summary>Curseur fin : piste sombre arrondie, partie remplie à la couleur de Bulles, pastille blanche.</summary>
    public static Style Slider => _slider ??= (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
               TargetType="Slider">
          <Setter Property="IsMoveToPointEnabled" Value="True"/>
          <Setter Property="Focusable" Value="False"/>
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="Slider">
                <Grid Height="22" Background="Transparent">
                  <Border Height="4" CornerRadius="2" Background="#2EFFFFFF" VerticalAlignment="Center"/>
                  <Track x:Name="PART_Track" VerticalAlignment="Center">
                    <Track.DecreaseRepeatButton>
                      <RepeatButton Command="Slider.DecreaseLarge" Focusable="False">
                        <RepeatButton.Template>
                          <ControlTemplate TargetType="RepeatButton">
                            <Border Height="4" CornerRadius="2" Background="#3DA5FF"/>
                          </ControlTemplate>
                        </RepeatButton.Template>
                      </RepeatButton>
                    </Track.DecreaseRepeatButton>
                    <Track.IncreaseRepeatButton>
                      <RepeatButton Command="Slider.IncreaseLarge" Focusable="False">
                        <RepeatButton.Template>
                          <ControlTemplate TargetType="RepeatButton">
                            <Border Background="Transparent"/>
                          </ControlTemplate>
                        </RepeatButton.Template>
                      </RepeatButton>
                    </Track.IncreaseRepeatButton>
                    <Track.Thumb>
                      <Thumb>
                        <Thumb.Template>
                          <ControlTemplate TargetType="Thumb">
                            <Grid Width="16" Height="16">
                              <Ellipse x:Name="halo" Fill="#553DA5FF" Margin="-3" Opacity="0"/>
                              <Ellipse Fill="White"/>
                            </Grid>
                            <ControlTemplate.Triggers>
                              <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="halo" Property="Opacity" Value="1"/>
                              </Trigger>
                              <Trigger Property="IsDragging" Value="True">
                                <Setter TargetName="halo" Property="Opacity" Value="1"/>
                              </Trigger>
                            </ControlTemplate.Triggers>
                          </ControlTemplate>
                        </Thumb.Template>
                      </Thumb>
                    </Track.Thumb>
                  </Track>
                </Grid>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """);

    private static Style? _saoSlider;

    /// <summary>Jauge façon menu SAO : rail gris clair, partie remplie verte (barre de vie), curseur blanc cerclé.</summary>
    public static Style SaoSlider => _saoSlider ??= (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
               TargetType="Slider">
          <Setter Property="IsMoveToPointEnabled" Value="True"/>
          <Setter Property="Focusable" Value="False"/>
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="Slider">
                <Grid Height="22" Background="Transparent">
                  <Border Height="5" CornerRadius="2.5" Background="#D3D3D3" VerticalAlignment="Center"/>
                  <Track x:Name="PART_Track" VerticalAlignment="Center">
                    <Track.DecreaseRepeatButton>
                      <RepeatButton Command="Slider.DecreaseLarge" Focusable="False">
                        <RepeatButton.Template>
                          <ControlTemplate TargetType="RepeatButton">
                            <Border Height="5" CornerRadius="2.5">
                              <Border.Background>
                                <LinearGradientBrush StartPoint="0,0" EndPoint="1,0">
                                  <GradientStop Color="#4FC23A" Offset="0"/>
                                  <GradientStop Color="#8BE04E" Offset="1"/>
                                </LinearGradientBrush>
                              </Border.Background>
                            </Border>
                          </ControlTemplate>
                        </RepeatButton.Template>
                      </RepeatButton>
                    </Track.DecreaseRepeatButton>
                    <Track.IncreaseRepeatButton>
                      <RepeatButton Command="Slider.IncreaseLarge" Focusable="False">
                        <RepeatButton.Template>
                          <ControlTemplate TargetType="RepeatButton">
                            <Border Background="Transparent"/>
                          </ControlTemplate>
                        </RepeatButton.Template>
                      </RepeatButton>
                    </Track.IncreaseRepeatButton>
                    <Track.Thumb>
                      <Thumb>
                        <Thumb.Template>
                          <ControlTemplate TargetType="Thumb">
                            <Grid Width="18" Height="18">
                              <Ellipse x:Name="halo" Fill="#558BE04E" Margin="-4" Opacity="0"/>
                              <Ellipse Fill="White" Stroke="#8C8C8C" StrokeThickness="1.5"/>
                              <Ellipse Fill="#6CD43C" Width="7" Height="7"/>
                            </Grid>
                            <ControlTemplate.Triggers>
                              <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="halo" Property="Opacity" Value="1"/>
                              </Trigger>
                              <Trigger Property="IsDragging" Value="True">
                                <Setter TargetName="halo" Property="Opacity" Value="1"/>
                              </Trigger>
                            </ControlTemplate.Triggers>
                          </ControlTemplate>
                        </Thumb.Template>
                      </Thumb>
                    </Track.Thumb>
                  </Track>
                </Grid>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """);
}
