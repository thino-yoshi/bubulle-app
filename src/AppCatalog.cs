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
        "documentation", "website", "site web", "license", "licence",
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
        var dirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
        };
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        dynamic? shell = shellType != null ? Activator.CreateInstance(shellType) : null;
        var byName = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };

        foreach (var dir in dirs.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.lnk", options))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (byName.ContainsKey(name) || IsJunk(name)) continue;

                string target = "", args = "", iconPath = "";
                int iconIndex = 0;
                try
                {
                    var sc = shell!.CreateShortcut(file);
                    target = (string)sc.TargetPath;
                    args = (string)sc.Arguments;
                    (iconPath, iconIndex) = ParseIconLocation((string)sc.IconLocation);
                }
                catch
                {
                    // Raccourci illisible : on garde quand même l'entrée, lancée via le .lnk.
                }

                // Les raccourcis vers des documents (.chm, .url, .txt…) ne sont pas des applications.
                if (target.Length > 0 && !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                byName[name] = new AppEntry
                {
                    Name = name,
                    LaunchPath = file,
                    ProcessName = ProcessNameFor(target, args),
                    IconPath = iconPath.Length > 0 ? iconPath : target,
                    IconIndex = iconPath.Length > 0 ? iconIndex : 0,
                };
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

        if (shell != null) Marshal.FinalReleaseComObject(shell);
        return byName.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
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

public static class IconLoader
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Load(string iconPath, int iconIndex, string fallbackPath)
    {
        var key = iconPath + "|" + iconIndex + "|" + fallbackPath;
        if (Cache.TryGetValue(key, out var cached)) return cached;

        ImageSource? image = null;
        try
        {
            if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
            {
                if (iconPath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
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
