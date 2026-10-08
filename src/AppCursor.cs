using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace Bulles;

/// <summary>
/// Curseur perso de Bulles (par exemple celui d'AION2), affiché seulement au-dessus des fenêtres de Bulles.
/// Il peut être capturé depuis un jeu : quand le jeu est au premier plan, Windows affiche son curseur,
/// et on en fait une copie (image + point de clic).
/// </summary>
public static class AppCursor
{
    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public Native.POINT pt; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32.dll")] private static extern IntPtr CopyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

    private const int CURSOR_SHOWING = 1;

    public static string CapturedPath => Path.Combine(AppSettings.Dir, "cursor.cur");

    /// <summary>Le curseur chargé, ou null (curseur normal de Windows).</summary>
    public static Cursor? Current { get; private set; }

    /// <summary>Charge le curseur des paramètres et l'applique à Bulles.</summary>
    public static void Apply(AppSettings s)
    {
        Current = null;
        if (s.UseCustomCursor && File.Exists(s.CursorPath))
        {
            try
            {
                using var stream = File.OpenRead(s.CursorPath);
                Current = new Cursor(stream);
            }
            catch (Exception ex)
            {
                App.Log(ex);
            }
        }
        // Mouse.OverrideCursor ne vaut que pour les fenêtres de Bulles : ailleurs, Windows garde son curseur.
        Mouse.OverrideCursor = Current;
    }

    /// <summary>Le contenu d'une page web garde le curseur normal (main sur les liens, barre de texte).</summary>
    public static void SuspendOver(System.Windows.FrameworkElement element)
    {
        element.MouseEnter += (_, _) => Mouse.OverrideCursor = null;
        element.MouseLeave += (_, _) => Mouse.OverrideCursor = Current;
    }

    /// <summary>Copie le curseur affiché en ce moment (celui du jeu au premier plan). Retourne le chemin, ou null.</summary>
    public static string? CaptureCurrent()
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info) || (info.flags & CURSOR_SHOWING) == 0 || info.hCursor == IntPtr.Zero) return null;

        var copy = CopyIcon(info.hCursor);
        if (copy == IntPtr.Zero) return null;
        try
        {
            if (!GetIconInfo(copy, out var ii)) return null;
            DeleteObject(ii.hbmMask);
            DeleteObject(ii.hbmColor);

            using var icon = System.Drawing.Icon.FromHandle(copy);
            using var bitmap = icon.ToBitmap();
            using var png = new MemoryStream();
            bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            Directory.CreateDirectory(AppSettings.Dir);
            WriteCur(CapturedPath, png.ToArray(), bitmap.Width, bitmap.Height, ii.xHotspot, ii.yHotspot);
            return CapturedPath;
        }
        finally
        {
            Native.DestroyIcon(copy);
        }
    }

    /// <summary>Fichier .cur d'une seule image (PNG), avec son point de clic.</summary>
    private static void WriteCur(string path, byte[] png, int width, int height, int hotX, int hotY)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write((ushort)0);
        w.Write((ushort)2);
        w.Write((ushort)1);
        w.Write((byte)(width >= 256 ? 0 : width));
        w.Write((byte)(height >= 256 ? 0 : height));
        w.Write((byte)0);
        w.Write((byte)0);
        w.Write((ushort)hotX);
        w.Write((ushort)hotY);
        w.Write((uint)png.Length);
        w.Write((uint)22);
        w.Write(png);
    }
}
