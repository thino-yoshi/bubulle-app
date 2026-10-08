using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace Bulles;

/// <summary>
/// Progression des téléchargements en cours, par programme (« steam », « chrome », « msedge »…), de 0 à 1.
/// Windows ne permet pas de lire la jauge verte de la barre des tâches d'une autre app, donc on lit
/// directement ce que chaque app écrit sur le disque.
/// </summary>
public sealed class DownloadMonitor
{
    /// <summary>Navigateurs basés sur Chromium : programme et dossier de profils.</summary>
    private static readonly (string Exe, string UserData)[] Browsers =
    {
        ("chrome", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\User Data")),
        ("msedge", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Edge\User Data")),
        ("brave", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"BraveSoftware\Brave-Browser\User Data")),
    };

    // Taille totale des téléchargements Chrome, par fichier .crdownload (lue une fois dans l'historique).
    private readonly Dictionary<string, long> _totals = new(StringComparer.OrdinalIgnoreCase);
    // Steam : dernière quantité téléchargée et quand elle a bougé (pour ignorer un téléchargement en pause).
    private readonly Dictionary<string, (long Bytes, DateTime Changed)> _steamSeen = new();

    /// <summary>Lit l'état actuel. À appeler en arrière-plan (accès disque).</summary>
    public Dictionary<string, double> Poll()
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try { PollSteam(result); } catch (Exception ex) { App.Log(ex); }
        foreach (var (exe, userData) in Browsers)
        {
            try { PollChromium(exe, userData, result); } catch (Exception ex) { App.Log(ex); }
        }
        return result;
    }

    // ---------- Steam ----------

    private void PollSteam(Dictionary<string, double> result)
    {
        var steamPath = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string;
        if (string.IsNullOrEmpty(steamPath)) return;
        var libraries = new List<string> { Path.Combine(steamPath, "steamapps") };
        var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                libraries.Add(Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), "steamapps"));

        long done = 0, total = 0;
        var now = DateTime.Now;
        foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
        {
            foreach (var acf in Directory.EnumerateFiles(lib, "appmanifest_*.acf"))
            {
                var text = File.ReadAllText(acf);
                long toDownload = Field(text, "BytesToDownload"), downloaded = Field(text, "BytesDownloaded");
                if (toDownload <= 0 || downloaded >= toDownload) continue;

                // Un jeu en pause garde ses chiffres : on ne l'affiche que s'il a avancé il y a peu.
                if (!_steamSeen.TryGetValue(acf, out var seen) || seen.Bytes != downloaded) _steamSeen[acf] = seen = (downloaded, now);
                if (now - seen.Changed > TimeSpan.FromSeconds(45)) continue;
                done += downloaded;
                total += toDownload;
            }
        }
        if (total > 0) result["steam"] = (double)done / total;
    }

    private static long Field(string acf, string name)
    {
        var m = Regex.Match(acf, $"\"{name}\"\\s+\"(\\d+)\"");
        return m.Success && long.TryParse(m.Groups[1].Value, out var v) ? v : 0;
    }

    // ---------- Chrome, Edge, Brave ----------

    private void PollChromium(string exe, string userData, Dictionary<string, double> result)
    {
        if (!Directory.Exists(userData)) return;
        var downloads = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads";
        var partial = Directory.Exists(downloads) ? Directory.GetFiles(downloads, "*.crdownload") : Array.Empty<string>();
        if (partial.Length == 0) return;

        // Taille totale inconnue pour un nouveau fichier : on relit l'historique (copie, Chrome le garde ouvert).
        if (partial.Any(p => !_totals.ContainsKey(p))) ReadTotals(userData);

        long received = 0, total = 0;
        foreach (var p in partial)
        {
            if (!_totals.TryGetValue(p, out var size) || size <= 0) continue;
            try { received += new FileInfo(p).Length; } catch { continue; }
            total += size;
        }
        if (total > 0) result[exe] = Math.Min(1, (double)received / total);
    }

    private void ReadTotals(string userData)
    {
        var profiles = Directory.GetDirectories(userData).Where(d =>
        {
            var n = Path.GetFileName(d);
            return n == "Default" || n.StartsWith("Profile ", StringComparison.Ordinal);
        });
        foreach (var profile in profiles)
        {
            var history = Path.Combine(profile, "History");
            if (!File.Exists(history)) continue;
            var copy = Path.Combine(Path.GetTempPath(), "bulles-history.db");
            try
            {
                File.Copy(history, copy, overwrite: true);
                using var db = new SqliteConnection($"Data Source={copy};Mode=ReadOnly;Pooling=False");
                db.Open();
                using var cmd = db.CreateCommand();
                // state 0 = en cours.
                cmd.CommandText = "SELECT current_path, total_bytes FROM downloads WHERE state = 0";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var path = r.IsDBNull(0) ? "" : r.GetString(0);
                    if (path.Length > 0) _totals[path] = r.GetInt64(1);
                }
            }
            catch
            {
                // Historique illisible à ce moment-là : on réessaiera au prochain passage.
            }
            finally
            {
                try { File.Delete(copy); } catch { /* Pas grave. */ }
            }
        }
    }
}
