using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bulles;

/// <summary>
/// Récupère le curseur d'un jeu qui dessine lui-même le sien (Windows n'en voit alors aucun).
/// Deux méthodes : chercher l'image dans les fichiers du jeu, ou la « photographier » à l'écran
/// (ce qui suit la souris sans changer d'une capture à l'autre, c'est le curseur ; le décor, lui, change).
/// </summary>
public static class GameCursorFinder
{
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Native.POINT pt);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Native.POINT pt);

    private static readonly Regex CursorName = new(@"cursor|pointer|curseur", RegexOptions.IgnoreCase);
    private static readonly string[] Extensions = { ".cur", ".ani", ".png", ".ico", ".bmp" };

    /// <summary>Programme du jeu au premier plan (pour savoir où chercher ses fichiers).</summary>
    public static string? ForegroundGameExe()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero || Native.ProcessId(h) == (uint)Environment.ProcessId) return null;
        return Native.ProcessPath(h);
    }

    /// <summary>
    /// Essaie les trois méthodes en même temps (pendant que tu bouges la souris sur le jeu) et garde la meilleure :
    /// 1. le vrai curseur de Windows, 2. une image trouvée dans les fichiers du jeu, 3. la photo à l'écran.
    /// </summary>
    public static async Task<(string? Path, string Message)> RecoverAsync()
    {
        using var cancel = new CancellationTokenSource();
        var game = ForegroundGameExe();
        var photo = PhotographAsync(TimeSpan.FromSeconds(6), cancel.Token);
        var files = game != null ? Task.Run(() => FindInGameFiles(game, cancel.Token)) : Task.FromResult(new List<string>());

        // 1. Le vrai curseur de Windows (le meilleur résultat, s'il existe).
        var (result, windowsCursor) = await AppCursor.CaptureBestAsync(TimeSpan.FromSeconds(2));
        if (result == AppCursor.CaptureResult.Captured && windowsCursor != null)
        {
            cancel.Cancel();
            return (windowsCursor, "Curseur du jeu copié.");
        }

        // 2. Une image de curseur dans les fichiers du jeu.
        var candidates = await Task.WhenAny(files, Task.Delay(TimeSpan.FromSeconds(15))) == files ? files.Result : new List<string>();
        foreach (var file in candidates)
        {
            try
            {
                if (ImageToCursor(file, AppCursor.CapturedPath) is { } fromFile)
                {
                    cancel.Cancel();
                    return (fromFile, $"Curseur trouvé dans les fichiers du jeu ({Path.GetFileName(file)}).");
                }
            }
            catch
            {
                // Image illisible : on passe à la suivante.
            }
        }

        // 3. La photo à l'écran.
        if (await photo is { } photographed) return (photographed, "Curseur photographié à l'écran (le jeu dessine le sien).");

        return (null, game == null
            ? "Le jeu n'était pas au premier plan. Clique une fois dans le jeu pendant le compte à rebours, puis bouge la souris dessus."
            : "Rien trouvé : le curseur de ce jeu n'est ni un curseur Windows, ni dans ses fichiers, ni assez net à l'écran (bouge la souris sur des zones variées). Utilise « Choisir un fichier… ».");
    }

    // ---------- Méthode « fichiers du jeu » ----------

    /// <summary>Images de curseur trouvées dans le dossier du jeu, les plus probables d'abord.</summary>
    public static List<string> FindInGameFiles(string exePath, CancellationToken cancel)
    {
        var found = new List<(string Path, int Score)>();
        var root = GameRoot(exePath);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 8 };
        int scanned = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", options))
            {
                if (cancel.IsCancellationRequested || ++scanned > 300_000) break;
                var name = Path.GetFileName(file);
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (!Extensions.Contains(ext) || !CursorName.IsMatch(name)) continue;
                long size;
                try { size = new FileInfo(file).Length; } catch { continue; }
                if (size == 0 || size > 1_000_000) continue;
                // Un vrai fichier de curseur passe avant une image ; « arrow », « default », « normal » sont les plus probables.
                int score = ext is ".cur" or ".ani" ? 100 : 0;
                if (Regex.IsMatch(name, "arrow|default|normal|main|base", RegexOptions.IgnoreCase)) score += 20;
                if (Regex.IsMatch(name, "busy|wait|loading|drag|resize|text|beam|hand|link|grab", RegexOptions.IgnoreCase)) score -= 20;
                found.Add((file, score));
            }
        }
        catch
        {
            // Dossier illisible : on garde ce qu'on a trouvé.
        }
        return found.OrderByDescending(f => f.Score).Select(f => f.Path).Take(20).ToList();
    }

    /// <summary>Dossier du jeu : on remonte depuis le programme tant qu'on reste dans le même jeu.</summary>
    private static string GameRoot(string exePath)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(exePath)!);
        for (int i = 0; i < 3 && dir.Parent != null; i++)
        {
            var parent = dir.Parent;
            // On s'arrête avant les dossiers qui contiennent plusieurs jeux ou programmes.
            if (Regex.IsMatch(parent.Name, "^(Program Files|Program Files \\(x86\\)|common|steamapps|Games|Epic Games|SteamLibrary)$", RegexOptions.IgnoreCase)
                || parent.Parent == null)
                break;
            dir = parent;
        }
        return dir.FullName;
    }

    /// <summary>Transforme une image (png, bmp, ico) en fichier curseur ; le point de clic est en haut à gauche.</summary>
    public static string? ImageToCursor(string imagePath, string destination)
    {
        var ext = Path.GetExtension(imagePath).ToLowerInvariant();
        if (ext is ".cur" or ".ani")
        {
            File.Copy(imagePath, Path.ChangeExtension(destination, ext), true);
            return Path.ChangeExtension(destination, ext);
        }
        using var source = ext == ".ico" ? new Icon(imagePath).ToBitmap() : new Bitmap(imagePath);
        if (source.Width < 8 || source.Height < 8 || source.Width > 128 || source.Height > 128) return null;
        int w = Math.Min(source.Width, 256), h = Math.Min(source.Height, 256);
        using var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, 0, 0, w, h);
        }
        return AppCursor.SaveAsCursor(bitmap, 0, 0);
    }

    // ---------- Méthode « photo » ----------

    private const int Box = 96;

    /// <summary>
    /// Prend des captures autour de la souris pendant que tu la bouges sur le jeu, puis garde les pixels
    /// identiques sur toutes les captures (le curseur). Retourne le chemin du curseur, ou null.
    /// </summary>
    public static async Task<string?> PhotographAsync(TimeSpan duration, CancellationToken cancel)
    {
        var samples = new List<Bitmap>();
        var last = new Native.POINT { X = int.MinValue };
        var end = DateTime.Now + duration;
        try
        {
            while (DateTime.Now < end && !cancel.IsCancellationRequested && samples.Count < 40)
            {
                await Task.Delay(90, CancellationToken.None);
                if (!GetCursorPos(out var p)) continue;
                if (Native.ProcessId(WindowFromPoint(p)) == (uint)Environment.ProcessId) continue;
                // Une nouvelle capture seulement si la souris a assez bougé (le décor derrière doit changer).
                if (last.X != int.MinValue && Math.Abs(p.X - last.X) + Math.Abs(p.Y - last.Y) < 40) continue;
                last = p;
                var shot = new Bitmap(Box, Box, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(shot))
                    g.CopyFromScreen(p.X - Box / 2, p.Y - Box / 2, 0, 0, new Size(Box, Box));
                samples.Add(shot);
            }
            // Une autre méthode a déjà réussi : surtout ne pas écraser son curseur.
            if (cancel.IsCancellationRequested) return null;
            return samples.Count >= 6 ? Extract(samples) : null;
        }
        finally
        {
            foreach (var s in samples) s.Dispose();
        }
    }

    /// <summary>Pixels stables d'une capture à l'autre = le curseur ; le reste devient transparent.</summary>
    private static string? Extract(List<Bitmap> samples)
    {
        int n = samples.Count;
        var pixels = samples.Select(ReadPixels).ToList();
        var mask = new bool[Box * Box];
        var color = new int[Box * Box];
        for (int i = 0; i < Box * Box; i++)
        {
            double r = 0, g = 0, b = 0;
            foreach (var px in pixels) { r += (px[i] >> 16) & 255; g += (px[i] >> 8) & 255; b += px[i] & 255; }
            r /= n; g /= n; b /= n;
            double variance = 0;
            foreach (var px in pixels)
            {
                double dr = ((px[i] >> 16) & 255) - r, dg = ((px[i] >> 8) & 255) - g, db = (px[i] & 255) - b;
                variance += dr * dr + dg * dg + db * db;
            }
            mask[i] = Math.Sqrt(variance / n) < 22;
            color[i] = (255 << 24) | ((int)r << 16) | ((int)g << 8) | (int)b;
        }

        // On ne garde que la tache stable qui touche le centre (là où est la souris).
        var keep = new bool[Box * Box];
        var queue = new Queue<int>();
        for (int dy = -3; dy <= 3; dy++)
            for (int dx = -3; dx <= 3; dx++)
            {
                int i = (Box / 2 + dy) * Box + Box / 2 + dx;
                if (mask[i] && !keep[i]) { keep[i] = true; queue.Enqueue(i); }
            }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue(), x = i % Box, y = i / Box;
            foreach (var (nx, ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
            {
                if (nx < 0 || ny < 0 || nx >= Box || ny >= Box) continue;
                int j = ny * Box + nx;
                if (mask[j] && !keep[j]) { keep[j] = true; queue.Enqueue(j); }
            }
        }

        int minX = Box, minY = Box, maxX = -1, maxY = -1, count = 0;
        for (int i = 0; i < Box * Box; i++)
        {
            if (!keep[i]) continue;
            count++;
            int x = i % Box, y = i / Box;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        int w = maxX - minX + 1, h = maxY - minY + 1;
        // Trop petit (rien trouvé) ou presque toute la capture (décor uniforme, pas un curseur).
        if (count < 20 || w < 5 || h < 5 || w > Box - 8 || h > Box - 8) return null;

        using var cursor = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (minY + y) * Box + minX + x;
                cursor.SetPixel(x, y, keep[i] ? Color.FromArgb(color[i]) : Color.Transparent);
            }
        // Le point de clic est là où était la souris : le centre de la capture.
        return AppCursor.SaveAsCursor(cursor, Math.Clamp(Box / 2 - minX, 0, w - 1), Math.Clamp(Box / 2 - minY, 0, h - 1));
    }

    private static int[] ReadPixels(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, Box, Box), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var result = new int[Box * Box];
            Marshal.Copy(data.Scan0, result, 0, result.Length);
            return result;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
