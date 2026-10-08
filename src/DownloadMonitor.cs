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

    private static readonly Regex SteamStart = new(@"^\[([^\]]+)\] AppID (\d+) update started : download (\d+)/(\d+)");
    private static readonly Regex SteamStats = new(@"^\[([^\]]+)\] stats: \(Invalid, 0\) : (\d+) Bytes");
    private static readonly Regex SteamRate = new(@"^\[([^\]]+)\] Current download rate: ([\d.]+) Mbps");

    /// <summary>
    /// Steam ne met pas à jour ses fichiers de jeu pendant un téléchargement, mais son journal
    /// (logs\content_log.txt) note la taille totale au départ, les octets reçus par blocs de ~5 min
    /// et la vitesse chaque minute. On additionne les blocs et on complète avec la vitesse.
    /// </summary>
    private static void PollSteam(Dictionary<string, double> result)
    {
        var steamPath = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string;
        if (string.IsNullOrEmpty(steamPath)) return;
        var log = Path.Combine(steamPath, "logs", "content_log.txt");
        if (!File.Exists(log)) return;
        var lines = TailLines(log, 512 * 1024);

        int start = -1;
        string app = "";
        long done = 0, total = 0;
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            var m = SteamStart.Match(lines[i]);
            if (!m.Success) continue;
            start = i;
            app = m.Groups[2].Value;
            done = long.Parse(m.Groups[3].Value);
            total = long.Parse(m.Groups[4].Value);
            break;
        }
        if (start < 0 || total <= 0) return;

        DateTime? lastStats = null, lastRate = null;
        double rate = 0;
        for (int i = start + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            // Téléchargement terminé ou annulé : plus de jauge.
            if (line.Contains($"AppID {app} ") && (line.Contains("Fully Installed") || line.Contains("update canceled") || line.Contains("finished update")))
                return;
            var s = SteamStats.Match(line);
            if (s.Success && DateTime.TryParse(s.Groups[1].Value, out var st))
            {
                done += long.Parse(s.Groups[2].Value);
                lastStats = st;
                continue;
            }
            var r = SteamRate.Match(line);
            if (r.Success && DateTime.TryParse(r.Groups[1].Value, out var rt))
            {
                rate = double.Parse(r.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                lastRate = rt;
            }
        }

        // Plus de vitesse notée depuis 2 min 30 : en pause ou fini.
        if (lastRate == null || DateTime.Now - lastRate.Value > TimeSpan.FromSeconds(150)) return;
        var since = lastStats ?? lastRate.Value;
        done += (long)(Math.Max(0, (DateTime.Now - since).TotalSeconds) * rate * 1_000_000 / 8);
        // La fenêtre de Steam appartient à « steamwebhelper » : une bulle créée depuis la fenêtre ouverte porte ce nom.
        result["steam"] = result["steamwebhelper"] = Math.Clamp((double)done / total, 0, 0.99);
    }

    /// <summary>Dernières lignes d'un fichier qu'une autre app est en train d'écrire.</summary>
    private static List<string> TailLines(string path, int maxBytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        fs.Seek(Math.Max(0, fs.Length - maxBytes), SeekOrigin.Begin);
        using var reader = new StreamReader(fs);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }

    // ---------- Chrome, Edge, Brave ----------

    private void PollChromium(string exe, string userData, Dictionary<string, double> result)
    {
        if (!Directory.Exists(userData)) return;
        var partial = DownloadFolders(userData)
            .Where(Directory.Exists)
            .SelectMany(d => Directory.GetFiles(d, "*.crdownload"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

    private readonly Dictionary<string, (List<string> Folders, DateTime Read)> _folders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>« Téléchargements » + le dossier choisi dans les réglages du navigateur (relu toutes les minutes).</summary>
    private List<string> DownloadFolders(string userData)
    {
        if (_folders.TryGetValue(userData, out var cached) && DateTime.Now - cached.Read < TimeSpan.FromMinutes(1)) return cached.Folders;
        var folders = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") };
        foreach (var prefs in Directory.GetDirectories(userData).Select(d => Path.Combine(d, "Preferences")).Where(File.Exists))
        {
            try
            {
                var m = Regex.Match(File.ReadAllText(prefs), "\"default_directory\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                if (m.Success) folders.Add(Regex.Unescape(m.Groups[1].Value));
            }
            catch
            {
                // Réglages illisibles : on garde « Téléchargements ».
            }
        }
        _folders[userData] = (folders, DateTime.Now);
        return folders;
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
