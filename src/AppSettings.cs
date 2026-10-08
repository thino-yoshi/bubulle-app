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
    /// <summary>"Mixer" = mélangeur audio intégré à Bulles.</summary>
    public bool IsMixer => Kind == "Mixer";
    /// <summary>"Launcher" = répertoire d'apps lancées normalement (hors bulle).</summary>
    public bool IsLauncher => Kind == "Launcher";
    /// <summary>Apps lancées en vraie fenêtre ou dans une bulle-fenêtre ?</summary>
    public bool IsWindowApp => Kind == "App";

    /// <summary>Logo choisi (caractère de la police d'icônes Windows), vide = icône d'origine.</summary>
    public string Glyph { get; set; } = "";

    /// <summary>Contenu d'une bulle Lanceur.</summary>
    public List<LauncherApp> Apps { get; set; } = new();
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

    /// <summary>Volume de la bulle en % (jauge du haut-parleur).</summary>
    public int Volume { get; set; } = 100;
}

/// <summary>Une app dans un lanceur : programme du PC ou site web (ouvert dans ton navigateur).</summary>
public class LauncherApp
{
    public string Kind { get; set; } = "App";
    public string Name { get; set; } = "";
    public string LaunchPath { get; set; } = "";
    public string Url { get; set; } = "";
    public string IconPath { get; set; } = "";
    public int IconIndex { get; set; }
}

public class AppSettings
{
    /// <summary>Le premier lanceur a été ajouté (une seule fois, tu peux le retirer ensuite).</summary>
    public bool DefaultLauncherAdded { get; set; }

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

    /// <summary>Position et taille du mini-lecteur (unités WPF), 0 = coin par défaut.</summary>
    public double MiniLeft { get; set; }
    public double MiniTop { get; set; }
    public double MiniWidth { get; set; }
    public double MiniHeight { get; set; }

    /// <summary>Curseur perso affiché au-dessus de Bulles (ex. celui d'AION2).</summary>
    public bool UseCustomCursor { get; set; }
    public string CursorPath { get; set; } = "";
    /// <summary>Taille du curseur perso, en % de sa taille d'origine.</summary>
    public int CursorScale { get; set; } = 100;
    /// <summary>« Bubulle » (Bubulle et les apps dans ses bulles) ou « System » (tout le PC).</summary>
    public string CursorScope { get; set; } = "Bubulle";
    public bool CursorEverywhere => CursorScope == "System";
    /// <summary>Flèche Windows d'origine, pour la remettre quand on arrête « Partout sur le PC ».</summary>
    public string PreviousArrow { get; set; } = "";
    public bool SystemCursorApplied { get; set; }

    /// <summary>Sons d'interface : déploiement / repli des bulles.</summary>
    public bool SoundEnabled { get; set; } = true;
    public int SoundVolume { get; set; } = 70;
    public string OpenSoundPath { get; set; } = "";
    /// <summary>Le son fourni avec l'app a été proposé une fois (on ne remplace jamais un choix déjà fait).</summary>
    public bool DefaultSoundApplied { get; set; }
    public string CloseSoundPath { get; set; } = "";

    /// <summary>Charge les sites web en arrière-plan au démarrage (pastilles de notification).</summary>
    public bool PreloadWeb { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public List<BubbleConfig> Bubbles { get; set; } = new();

    public bool IsLeft => Side == "Left";

    private static string? _dir;

    /// <summary>Dossier des données (%APPDATA%\Bubulle). L'ancien dossier « Bulles » est repris une fois.</summary>
    public static string Dir => _dir ??= ResolveDir();

    private static string ResolveDir()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(roaming, "Bubulle");
        var old = Path.Combine(roaming, "Bulles");
        if (Directory.Exists(dir) || !Directory.Exists(old)) return dir;
        try
        {
            // L'app s'appelait « Bulles » : on déplace tout (bulles, sons, curseur, connexions web)…
            Directory.Move(old, dir);
            // …et on corrige les chemins enregistrés dans les réglages.
            var file = Path.Combine(dir, "settings.json");
            if (File.Exists(file))
                File.WriteAllText(file, File.ReadAllText(file).Replace(@"\\Roaming\\Bulles\\", @"\\Roaming\\Bubulle\\"));
            return dir;
        }
        catch
        {
            // Dossier encore utilisé (ancienne version ouverte) : on continue avec l'ancien.
            return old;
        }
    }
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
