using System.Windows;
using System.Windows.Markup;

namespace Bulles;

/// <summary>Styles partagés de Bulles (curseurs fins, rangées de boutons) pour un rendu homogène.</summary>
public static class Theme
{
    private static Style? _slider;

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
}
