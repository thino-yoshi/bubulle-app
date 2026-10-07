using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Bulles;

public class BubbleConfig
{
    /// <summary>"App" = vraie fenêtre Windows mise dans le cadre, "Web" = page intégrée à Bulles.</summary>
    public string Kind { get; set; } = "App";
    public string Url { get; set; } = "";
    public bool IsWeb => Kind == "Web";
    public string Name { get; set; } = "";
    public string LaunchPath { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public string IconPath { get; set; } = "";
    public int IconIndex { get; set; }

    /// <summary>Opacité en %, 0 = valeur par défaut des paramètres.</summary>
    public int Opacity { get; set; }

    /// <summary>Taille mémorisée de la bulle-fenêtre (unités WPF), 0 = taille par défaut.</summary>
    public int Width { get; set; }
    public int Height { get; set; }

    public string Hotkey { get; set; } = "";
}

public class AppSettings
{
    public string MainHotkey { get; set; } = "Alt+Space";
    public bool PerBubbleHotkeys { get; set; }
    public string Side { get; set; } = "Right";

    /// <summary>Nom système de l'écran des bulles (ex. \\.\DISPLAY2), vide = écran principal.</summary>
    public string Screen { get; set; } = "";

    /// <summary>Position verticale de la bulle principale, en fraction de la hauteur d'écran.</summary>
    public double MainY { get; set; } = 0.2;

    public int DefaultOpacity { get; set; } = 100;
    public int DefaultWidthPct { get; set; } = 45;
    public int DefaultHeightPct { get; set; } = 65;
    public bool AutoHide { get; set; }
    public bool StartWithWindows { get; set; }
    public List<BubbleConfig> Bubbles { get; set; } = new();

    public bool IsLeft => Side == "Left";

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bulles");
    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }
}
