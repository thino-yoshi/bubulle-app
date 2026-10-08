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
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest) || latest <= Current)
        {
            Log($"Vérifié : dernière version {tag}, installée v{Current} → à jour.");
            return null;
        }
        Log($"Nouvelle version {tag} (installée v{Current}) : téléchargement.");

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
            if (!File.Exists(Path.Combine(folder, "Bubulle.exe")))
            {
                Log("Paquet téléchargé mais incomplet (Bubulle.exe absent).");
                return null;
            }
        }
        Ready = (latest, folder);
        Log($"v{latest} prête à installer.");
        return latest;
    }

    /// <summary>Journal des mises à jour (%APPDATA%\Bubulle\update\update.log), pour comprendre un blocage.</summary>
    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Root);
            var file = Path.Combine(Root, "update.log");
            if (File.Exists(file) && new FileInfo(file).Length > 200_000) File.Delete(file);
            File.AppendAllText(file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch
        {
            // Le journal ne doit jamais empêcher la mise à jour.
        }
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
        // Le script note tout dans install.log, réessaie si des fichiers sont encore occupés,
        // et relance toujours Bubulle à la fin (même si la copie a échoué : on réessaiera au lancement suivant).
        File.WriteAllText(script, $$"""
            $log = '{{Q(Path.Combine(Root, "install.log"))}}'
            function Note($m) { Add-Content -Path $log -Value ("[{0:yyyy-MM-dd HH:mm:ss}] {1}" -f (Get-Date), $m) }
            Note 'Installation de v{{ready.Version}} dans {{Q(appDir)}}'
            try { Wait-Process -Id {{Environment.ProcessId}} -Timeout 30 -ErrorAction Stop } catch {}
            $ok = $false
            for ($i = 0; $i -lt 30 -and -not $ok; $i++) {
                try {
                    Copy-Item -Path '{{Q(ready.Folder)}}\*' -Destination '{{Q(appDir)}}' -Recurse -Force -ErrorAction Stop
                    $ok = $true
                } catch {
                    Note ("Fichiers occupés, nouvel essai : " + $_.Exception.Message)
                    Start-Sleep -Seconds 1
                }
            }
            if ($ok) {
                Remove-Item -Path '{{Q(ready.Folder)}}' -Recurse -Force -ErrorAction SilentlyContinue
                Note 'Installation réussie.'
            } else {
                Note 'Échec : la mise à jour sera retentée au prochain lancement.'
            }
            Start-Process -FilePath '{{Q(exe)}}'
            """, new System.Text.UTF8Encoding(true));
        Log($"Installation de v{ready.Version} lancée.");
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
        // Téléchargement interrompu (app fermée en cours de route) : il sera refait.
        foreach (var zip in Directory.GetFiles(Root, "*.zip"))
            try { File.Delete(zip); } catch { /* En cours d'utilisation : au prochain lancement. */ }
        foreach (var dir in Directory.GetDirectories(Root))
        {
            if (Version.TryParse(Path.GetFileName(dir), out var v) && v > Current && File.Exists(Path.Combine(dir, "Bubulle.exe")))
                Ready = (v, dir);
            else
                try { Directory.Delete(dir, true); } catch { /* Ancienne version, déjà installée. */ }
        }
    }
}
