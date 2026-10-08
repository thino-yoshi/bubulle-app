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
                Current = LoadScaled(s.CursorPath, s.CursorScale);
                if (Current == null)
                {
                    using var stream = File.OpenRead(s.CursorPath);
                    Current = new Cursor(stream);
                }
            }
            catch (Exception ex)
            {
                App.Log(ex);
            }
        }
        // Mouse.OverrideCursor ne vaut que pour les fenêtres de Bulles : ailleurs, Windows garde son curseur.
        Mouse.OverrideCursor = Current;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hinst, string name, uint type, int cx, int cy, uint load);
    [DllImport("user32.dll")] private static extern bool DestroyCursor(IntPtr cursor);
    private const uint IMAGE_CURSOR = 2, LR_LOADFROMFILE = 0x10;

    private sealed class CursorHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        public CursorHandle(IntPtr h) : base(true) => SetHandle(h);
        protected override bool ReleaseHandle() => DestroyCursor(handle);
    }

    /// <summary>
    /// Charge le curseur à la taille voulue : Windows redimensionne l'image et le point de clic suit.
    /// Taille d'origine lue dans l'en-tête du fichier .cur (0 = 256 px).
    /// </summary>
    private static Cursor? LoadScaled(string path, int scalePercent)
    {
        int size = 32;
        if (path.EndsWith(".cur", StringComparison.OrdinalIgnoreCase))
        {
            var header = new byte[8];
            using (var f = File.OpenRead(path)) f.ReadExactly(header);
            size = header[6] == 0 ? 256 : header[6];
        }
        int target = Math.Clamp(size * scalePercent / 100, 8, 256);
        var h = LoadImage(IntPtr.Zero, path, IMAGE_CURSOR, target, target, LR_LOADFROMFILE);
        return h == IntPtr.Zero ? null : System.Windows.Interop.CursorInteropHelper.Create(new CursorHandle(h));
    }

    /// <summary>Image du curseur (pour l'aperçu dans les paramètres), ou null.</summary>
    public static System.Windows.Media.ImageSource? Preview(string path)
    {
        if (!File.Exists(path)) return null;
        var h = LoadImage(IntPtr.Zero, path, IMAGE_CURSOR, 0, 0, LR_LOADFROMFILE);
        if (h == IntPtr.Zero) return null;
        try
        {
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(h, System.Windows.Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyCursor(h);
        }
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
