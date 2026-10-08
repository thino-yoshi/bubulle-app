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
        // Mouse.OverrideCursor ne vaut que pour les fenêtres de Bubulle : ailleurs, Windows garde son curseur.
        Mouse.OverrideCursor = Current;

        // Copie brute du curseur, pour le prêter à Windows (tout le PC, ou au-dessus des apps des bulles).
        if (_raw != IntPtr.Zero) DestroyCursor(_raw);
        _raw = Current != null ? LoadRaw(s.CursorPath, s.CursorScale) : IntPtr.Zero;
        TemporarySystemCursor(false);
        // Premier passage : si Bubulle avait été arrêté brutalement pendant un survol, la flèche de Windows
        // était restée remplacée. On recharge les curseurs normaux.
        if (!_startupReset)
        {
            _startupReset = true;
            SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0);
        }
        if (s.UseCustomCursor && s.CursorEverywhere && _raw != IntPtr.Zero) ApplySystemWide(s);
        else RemoveSystemWide(s);
    }

    // ---------- Curseur prêté à Windows ----------

    [DllImport("user32.dll")] private static extern bool SetSystemCursor(IntPtr cursor, uint id);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint param, IntPtr pv, uint winIni);
    private const uint OCR_NORMAL = 32512, SPI_SETCURSORS = 0x57, SPIF_UPDATEINIFILE = 1, SPIF_SENDCHANGE = 2;
    private const string CursorsKey = @"Control Panel\Cursors";

    private static IntPtr _raw;
    private static bool _temporary, _startupReset;

    private static string SystemCursorPath => Path.Combine(AppSettings.Dir, "cursor-windows.cur");

    /// <summary>
    /// « Partout sur le PC » : ton curseur devient la flèche de Windows (comme un thème de curseur, gardé au
    /// redémarrage). La flèche d'origine est notée pour pouvoir la remettre.
    /// </summary>
    private static void ApplySystemWide(AppSettings s)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(CursorsKey, writable: true);
            if (key == null) return;
            if (!s.SystemCursorApplied) s.PreviousArrow = key.GetValue("Arrow") as string ?? "";
            // Fichier à la bonne taille (la taille réglée dans Bubulle).
            using (var icon = System.Drawing.Icon.FromHandle(_raw))
            using (var bitmap = icon.ToBitmap())
            {
                GetIconInfo(_raw, out var ii);
                DeleteObject(ii.hbmMask);
                DeleteObject(ii.hbmColor);
                SaveCursorTo(SystemCursorPath, bitmap, ii.xHotspot, ii.yHotspot);
            }
            key.SetValue("Arrow", SystemCursorPath, Microsoft.Win32.RegistryValueKind.ExpandString);
            SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            s.SystemCursorApplied = true;
            s.Save();
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    /// <summary>Remet la flèche de Windows d'origine (si Bubulle l'avait remplacée).</summary>
    private static void RemoveSystemWide(AppSettings s)
    {
        if (!s.SystemCursorApplied) return;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(CursorsKey, writable: true);
            key?.SetValue("Arrow", s.PreviousArrow, Microsoft.Win32.RegistryValueKind.ExpandString);
            SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            s.SystemCursorApplied = false;
            s.Save();
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    /// <summary>
    /// « Seulement Bubulle » : quand la souris passe au-dessus d'une vraie app rangée dans une bulle (Chrome…),
    /// Windows reçoit le curseur le temps du survol, puis retrouve le sien.
    /// </summary>
    public static void TemporarySystemCursor(bool on)
    {
        if (on == _temporary) return;
        if (on)
        {
            if (_raw == IntPtr.Zero) return;
            // Windows détruit le curseur qu'on lui donne : on lui passe une copie.
            SetSystemCursor(CopyIcon(_raw), OCR_NORMAL);
        }
        else
        {
            // Recharge les curseurs normaux de Windows (sans toucher à ses réglages).
            SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0);
        }
        _temporary = on;
    }

    private static IntPtr LoadRaw(string path, int scalePercent)
    {
        var (w, h) = TargetSize(path, scalePercent);
        return LoadImage(IntPtr.Zero, path, IMAGE_CURSOR, w, h, LR_LOADFROMFILE);
    }

    /// <summary>Taille d'origine lue dans l'en-tête du .cur (0 = 256 px), multipliée par le réglage de taille.</summary>
    private static (int W, int H) TargetSize(string path, int scalePercent)
    {
        int w = 32, h = 32;
        if (path.EndsWith(".cur", StringComparison.OrdinalIgnoreCase))
        {
            var header = new byte[8];
            using (var f = File.OpenRead(path)) f.ReadExactly(header);
            w = header[6] == 0 ? 256 : header[6];
            h = header[7] == 0 ? 256 : header[7];
        }
        return (Math.Clamp(w * scalePercent / 100, 8, 256), Math.Clamp(h * scalePercent / 100, 8, 256));
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
        // Largeur et hauteur séparées : un curseur n'est pas toujours carré (sinon il serait déformé).
        var (w, hgt) = TargetSize(path, scalePercent);
        var h = LoadImage(IntPtr.Zero, path, IMAGE_CURSOR, w, hgt, LR_LOADFROMFILE);
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

    /// <summary>Copie le curseur affiché en ce moment (celui du jeu au premier plan). Retourne le chemin, ou null.</summary>
    public enum CaptureResult { Captured, StandardArrow, NoCursor }

    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Native.POINT pt);

    private static bool IsOverBubulle(Native.POINT pt) => Native.ProcessId(WindowFromPoint(pt)) == (uint)Environment.ProcessId;
    private static readonly IntPtr IDC_ARROW = new(32512);

    /// <summary>
    /// Surveille le curseur pendant un moment et copie le premier qui n'est pas la flèche normale de Windows.
    /// Un curseur choisi par le jeu mais masqué est accepté aussi ; si le jeu dessine lui-même son curseur
    /// dans son image, Windows n'en a aucun et la copie est impossible (NoCursor).
    /// </summary>
    public static async System.Threading.Tasks.Task<(CaptureResult Result, string? Path)> CaptureBestAsync(TimeSpan window)
    {
        var arrow = LoadCursor(IntPtr.Zero, IDC_ARROW);
        bool sawArrow = false;
        var end = DateTime.Now + window;
        do
        {
            var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            // Souris encore au-dessus de Bubulle (son propre curseur perso) : on attend qu'elle soit sur le jeu.
            if (GetCursorInfo(ref info) && info.hCursor != IntPtr.Zero && !IsOverBubulle(info.pt))
            {
                if (info.hCursor == arrow) sawArrow = true;
                else if (Copy(info.hCursor) is { } path) return (CaptureResult.Captured, path);
            }
            await System.Threading.Tasks.Task.Delay(100);
        }
        while (DateTime.Now < end);
        return sawArrow ? (CaptureResult.StandardArrow, null) : (CaptureResult.NoCursor, null);
    }

    public static string? CaptureCurrent()
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info) || (info.flags & CURSOR_SHOWING) == 0 || info.hCursor == IntPtr.Zero) return null;
        return Copy(info.hCursor);
    }

    /// <summary>Copie un curseur dans cursor.cur (image + point de clic). Retourne le chemin, ou null.</summary>
    private static string? Copy(IntPtr cursor)
    {
        var copy = CopyIcon(cursor);
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

    /// <summary>Enregistre une image comme curseur de Bubulle (cursor.cur), avec son point de clic.</summary>
    public static string SaveAsCursor(System.Drawing.Bitmap bitmap, int hotX, int hotY) => SaveCursorTo(CapturedPath, bitmap, hotX, hotY);

    private static string SaveCursorTo(string path, System.Drawing.Bitmap bitmap, int hotX, int hotY)
    {
        using var png = new MemoryStream();
        bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        Directory.CreateDirectory(AppSettings.Dir);
        WriteCur(path, png.ToArray(), bitmap.Width, bitmap.Height, hotX, hotY);
        return path;
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
