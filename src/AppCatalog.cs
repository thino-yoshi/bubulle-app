using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bulles;

public sealed class AppEntry
{
    public string Kind { get; init; } = "App";
    public string Url { get; init; } = "";
    public bool IsWeb => Kind == "Web";
    public string Name { get; init; } = "";
    public string LaunchPath { get; init; } = "";
    public string ProcessName { get; init; } = "";
    public string IconPath { get; init; } = "";
    public int IconIndex { get; init; }
}

/// <summary>Liste des applications installées, lue depuis les raccourcis du menu Démarrer.</summary>
public static class AppCatalog
{
    private static Task<List<AppEntry>>? _loading;

    private static readonly string[] JunkWords =
    {
        "uninstall", "désinstall", "desinstall", "readme", "lisez-moi", "release notes",
        "documentation", "website", "site web", "license", "licence", "add a new",
    };

    public static Task<List<AppEntry>> GetAsync() => _loading ??= RunOnSta(Scan);

    private static Task<T> RunOnSta<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { App.Log(ex); tcs.SetResult(default!); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    private static List<AppEntry> Scan()
    {
        // Menu Démarrer (avec sous-dossiers) + raccourcis du bureau.
        var dirs = new[]
        {
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"), true),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"), true),
            (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), false),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), false),
        };
        var byName = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var (dir, recurse) in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            var options = new EnumerationOptions { RecurseSubdirectories = recurse, IgnoreInaccessible = true };
            foreach (var file in Directory.EnumerateFiles(dir, "*.lnk", options))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (byName.ContainsKey(name) || IsJunk(name)) continue;
                var entry = FromShortcut(file);
                if (entry != null) byName[name] = entry;
            }
        }

        if (!byName.Values.Any(a => a.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase)))
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            byName["Explorateur de fichiers"] = new AppEntry
            {
                Name = "Explorateur de fichiers",
                LaunchPath = explorer,
                ProcessName = "explorer",
                IconPath = explorer,
            };
        }

        return byName.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Une app à partir d'un fichier choisi par toi : un programme (.exe) ou un raccourci (.lnk).</summary>
    public static AppEntry? FromFile(string path) =>
        path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? FromShortcut(path) : FromExe(path);

    public static AppEntry FromExe(string exePath) => new()
    {
        Name = FriendlyName(exePath),
        LaunchPath = exePath,
        ProcessName = Path.GetFileNameWithoutExtension(exePath),
        IconPath = exePath,
    };

    /// <summary>Nom affiché d'un programme : sa description (« Google Chrome ») plutôt que « chrome.exe ».</summary>
    public static string FriendlyName(string exePath)
    {
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
            foreach (var candidate in new[] { info.FileDescription, info.ProductName })
                if (!string.IsNullOrWhiteSpace(candidate)) return candidate.Trim();
        }
        catch
        {
            // Pas d'informations de version : on garde le nom du fichier.
        }
        return Path.GetFileNameWithoutExtension(exePath);
    }

    private static AppEntry? FromShortcut(string lnkPath)
    {
        string target = "", args = "", iconPath = "";
        int iconIndex = 0;
        dynamic? shell = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            var sc = shell!.CreateShortcut(lnkPath);
            target = (string)sc.TargetPath;
            args = (string)sc.Arguments;
            (iconPath, iconIndex) = ParseIconLocation((string)sc.IconLocation);
        }
        catch
        {
            // Raccourci illisible : on garde quand même l'entrée, lancée via le .lnk.
        }
        finally
        {
            if (shell != null) Marshal.FinalReleaseComObject(shell);
        }

        // Les raccourcis vers des documents (.chm, .url, .txt…) ne sont pas des applications.
        if (target.Length > 0 && !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;

        return new AppEntry
        {
            Name = Path.GetFileNameWithoutExtension(lnkPath),
            LaunchPath = lnkPath,
            ProcessName = ProcessNameFor(target, args),
            IconPath = iconPath.Length > 0 ? iconPath : target,
            IconIndex = iconPath.Length > 0 ? iconIndex : 0,
        };
    }

    private static bool IsJunk(string name)
    {
        var lower = name.ToLowerInvariant();
        return JunkWords.Any(lower.Contains);
    }

    private static (string path, int index) ParseIconLocation(string location)
    {
        if (string.IsNullOrWhiteSpace(location)) return ("", 0);
        int comma = location.LastIndexOf(',');
        string path = comma >= 0 ? location[..comma] : location;
        int index = comma >= 0 && int.TryParse(location[(comma + 1)..], out var i) ? i : 0;
        path = Environment.ExpandEnvironmentVariables(path.Trim());
        return (File.Exists(path) ? path : "", index);
    }

    /// <summary>Nom du processus qui possède la fenêtre (Discord passe par Update.exe --processStart Discord.exe).</summary>
    public static string ProcessNameFor(string target, string args)
    {
        if (string.IsNullOrEmpty(target)) return "";
        var file = Path.GetFileName(target);
        if (file.Equals("Update.exe", StringComparison.OrdinalIgnoreCase))
        {
            var m = Regex.Match(args ?? "", "--processStart\\s+\"?([^\"\\s]+)");
            if (m.Success) return Path.GetFileNameWithoutExtension(m.Groups[1].Value);
        }
        return Path.GetFileNameWithoutExtension(file);
    }
}

/// <summary>Apps web préréglées : il ne reste plus qu'à se connecter.</summary>
public static class WebPresets
{
    public const string BrowserUrl = "https://www.google.com";

    public static readonly IReadOnlyList<AppEntry> All = new[]
    {
        Web("Discord", "https://discord.com/app"),
        Web("Messenger", "https://www.messenger.com"),
        Web("WhatsApp", "https://web.whatsapp.com"),
        Web("YouTube", "https://www.youtube.com"),
        Web("Twitch", "https://www.twitch.tv"),
        Web("ChatGPT", "https://chatgpt.com"),
        Web("Claude", "https://claude.ai"),
        Web("Gmail", "https://mail.google.com"),
        Web("Instagram", "https://www.instagram.com"),
        Web("X (Twitter)", "https://x.com"),
        Web("Reddit", "https://www.reddit.com"),
        Web("Navigateur web", BrowserUrl),
    };

    private static AppEntry Web(string name, string url) => new() { Kind = "Web", Name = name, Url = url };

    /// <summary>Transforme « wiki.ffxiv.com » ou « https://… » en adresse web, sinon null.</summary>
    public static string? AsUrl(string text)
    {
        text = text.Trim();
        if (text.Length < 4 || text.Contains(' ')) return null;
        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!text.Contains('.')) return null;
            text = "https://" + text;
        }
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Host.Contains('.') ? uri.ToString() : null;
    }

    public static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
}

public static class IconLoader
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private static string IconDir => Path.Combine(AppSettings.Dir, "icons");

    /// <summary>Télécharge (une fois) l'icône d'un site et renvoie le chemin du fichier, ou "" si échec.</summary>
    public static async Task<string> FetchFaviconAsync(string url)
    {
        var host = WebPresets.HostOf(url);
        var file = Path.Combine(IconDir, host + ".png");
        if (File.Exists(file)) return file;
        try
        {
            var bytes = await Http.GetByteArrayAsync($"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(host)}&sz=128");
            Directory.CreateDirectory(IconDir);
            await File.WriteAllBytesAsync(file, bytes);
            return file;
        }
        catch (System.Net.Http.HttpRequestException)
        {
            // Site sans icône connue : la bulle affichera sa première lettre.
            return "";
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return "";
        }
    }

    /// <summary>Icône déjà téléchargée pour ce site, sans requête réseau.</summary>
    public static ImageSource? CachedFavicon(string url)
    {
        var file = Path.Combine(IconDir, WebPresets.HostOf(url) + ".png");
        return File.Exists(file) ? Load(file, 0, "") : null;
    }

    public static ImageSource? Load(string iconPath, int iconIndex, string fallbackPath)
    {
        var key = iconPath + "|" + iconIndex + "|" + fallbackPath;
        if (Cache.TryGetValue(key, out var cached)) return cached;

        ImageSource? image = null;
        try
        {
            if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
            {
                if (iconPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(iconPath);
                    bmp.EndInit();
                    bmp.Freeze();
                    image = bmp;
                }
                else if (iconPath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                {
                    using var ico = new System.Drawing.Icon(iconPath, 256, 256);
                    image = ToSource(ico.Handle);
                }
                else
                {
                    using var ico = System.Drawing.Icon.ExtractIcon(iconPath, iconIndex, 256);
                    if (ico != null) image = ToSource(ico.Handle);
                }
            }
        }
        catch
        {
            // On retombe sur l'icône fournie par le Shell ci-dessous.
        }

        image ??= FromShell(fallbackPath);
        Cache[key] = image;
        return image;
    }

    private static ImageSource? FromShell(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var info = new Native.SHFILEINFO();
        Native.SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info), Native.SHGFI_ICON | Native.SHGFI_LARGEICON);
        if (info.hIcon == IntPtr.Zero) return null;
        try { return ToSource(info.hIcon); }
        finally { Native.DestroyIcon(info.hIcon); }
    }

    private static ImageSource ToSource(IntPtr hIcon)
    {
        var source = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }
}
