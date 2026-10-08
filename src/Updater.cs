using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Bulles;

/// <summary>
/// Mises à jour depuis GitHub : au démarrage, Bubulle regarde la dernière version publiée
/// (github.com/thino-yoshi/bubulle-app, « Releases »). Si elle est plus récente, le paquet est
/// téléchargé en silence, puis on propose de redémarrer. Les réglages (%APPDATA%\Bubulle)
/// ne sont jamais touchés : seuls les fichiers de l'app sont remplacés.
/// </summary>
public static class Updater
{
    private const string Repo = "thino-yoshi/bubulle-app";

    public static Version Current => typeof(App).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    private static string Root => Path.Combine(AppSettings.Dir, "update");

    /// <summary>Version prête à installer (téléchargée et décompressée), ou null.</summary>
    public static (Version Version, string Folder)? Ready { get; private set; }

    /// <summary>Cherche une nouvelle version et la prépare. Retourne la version prête, ou null.</summary>
    public static async Task<Version?> CheckAndDownloadAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Bubulle/" + Current);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest) || latest <= Current) return null;

        // Le paquet de mise à jour : le .zip de l'app (l'installateur .exe sert aux nouvelles installations).
        var asset = doc.RootElement.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(a => (a.GetProperty("name").GetString() ?? "").EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase));
        if (asset.ValueKind == JsonValueKind.Undefined) return null;
        var url = asset.GetProperty("browser_download_url").GetString()!;

        var folder = Path.Combine(Root, latest.ToString());
        if (!File.Exists(Path.Combine(folder, "Bubulle.exe")))
        {
            Directory.CreateDirectory(Root);
            var zip = Path.Combine(Root, $"{latest}.zip");
            await using (var download = await http.GetStreamAsync(url))
            await using (var file = File.Create(zip))
                await download.CopyToAsync(file);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            ZipFile.ExtractToDirectory(zip, folder);
            File.Delete(zip);
            if (!File.Exists(Path.Combine(folder, "Bubulle.exe"))) return null;
        }
        Ready = (latest, folder);
        return latest;
    }

    /// <summary>
    /// Installe la version prête : un petit script attend que Bubulle soit fermé, copie les nouveaux
    /// fichiers par-dessus l'app, puis relance Bubulle. À appeler juste avant de quitter.
    /// </summary>
    public static bool StartInstall()
    {
        if (Ready is not { } ready) return false;
        var appDir = AppContext.BaseDirectory.TrimEnd('\\');
        var exe = Path.Combine(appDir, "Bubulle.exe");
        var script = Path.Combine(Root, "install.ps1");
        // Chemins entre apostrophes PowerShell : une apostrophe dans un nom de dossier se double.
        static string Q(string s) => s.Replace("'", "''");
        File.WriteAllText(script, $$"""
            $ErrorActionPreference = 'Stop'
            try { Wait-Process -Id {{Environment.ProcessId}} -Timeout 30 } catch {}
            Start-Sleep -Milliseconds 500
            Copy-Item -Path '{{Q(ready.Folder)}}\*' -Destination '{{Q(appDir)}}' -Recurse -Force
            Remove-Item -Path '{{Q(ready.Folder)}}' -Recurse -Force -ErrorAction SilentlyContinue
            Start-Process -FilePath '{{Q(exe)}}'
            """);
        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        return true;
    }

    /// <summary>Au démarrage : une version téléchargée la dernière fois et pas encore installée ?</summary>
    public static void FindPending()
    {
        if (!Directory.Exists(Root)) return;
        foreach (var dir in Directory.GetDirectories(Root))
        {
            if (Version.TryParse(Path.GetFileName(dir), out var v) && v > Current && File.Exists(Path.Combine(dir, "Bubulle.exe")))
                Ready = (v, dir);
            else
                try { Directory.Delete(dir, true); } catch { /* Ancienne version, déjà installée. */ }
        }
    }
}
