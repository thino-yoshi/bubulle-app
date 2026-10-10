using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace Bulles;

/// <summary>
/// « Toutes les applications » de Windows (le dossier virtuel shell:AppsFolder, celui du menu Démarrer) :
/// on y trouve aussi les apps du Microsoft Store et de la Xbox, qui n'ont pas de raccourci sur le disque.
/// </summary>
public static class StoreApps
{
    public const string Prefix = @"shell:AppsFolder\";

    /// <summary>Lance une app : chemin normal, ou app du Store (via son identifiant « shell:AppsFolder\… »).</summary>
    public static void Start(string target)
    {
        if (target.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
        else
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
    }

    /// <summary>Les apps de « Toutes les applications » (à appeler sur un thread STA).</summary>
    public static List<AppEntry> List()
    {
        var result = new List<AppEntry>();
        var executables = PackagedExecutables();
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null) return result;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            if (folder == null) return result;
            dynamic items = folder.Items();
            int count = items.Count;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    dynamic item = items.Item(i);
                    string name = item.Name;
                    string id = item.Path;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;

                    if (File.Exists(id) && id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        // App de bureau classique.
                        result.Add(new AppEntry { Name = name, LaunchPath = id, ProcessName = Path.GetFileNameWithoutExtension(id), IconPath = id });
                    }
                    else if (id.Contains('!'))
                    {
                        // App du Store : « Famille!Id ». Les apps de bureau empaquetées (Xbox, Claude…) ont un vrai
                        // programme, qu'on peut mettre en bulle ; les autres s'ouvrent normalement.
                        executables.TryGetValue(id, out var exe);
                        result.Add(new AppEntry { Name = name, LaunchPath = Prefix + id, ProcessName = exe ?? "", IconPath = Prefix + id });
                    }
                }
                catch
                {
                    // Élément illisible : ignoré.
                }
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        return result;
    }

    /// <summary>Identifiant d'app (« Famille!Id ») → nom du programme, pour les apps de bureau empaquetées.</summary>
    private static Dictionary<string, string> PackagedExecutables()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var manager = new Windows.Management.Deployment.PackageManager();
            foreach (var package in manager.FindPackagesForUser(string.Empty))
            {
                try
                {
                    var dir = package.InstalledLocation?.Path;
                    var manifest = dir != null ? Path.Combine(dir, "AppxManifest.xml") : null;
                    if (manifest == null || !File.Exists(manifest)) continue;
                    var doc = XDocument.Load(manifest);
                    foreach (var app in doc.Descendants().Where(e => e.Name.LocalName == "Application"))
                    {
                        var id = (string?)app.Attribute("Id");
                        var exe = (string?)app.Attribute("Executable");
                        var entry = (string?)app.Attribute("EntryPoint") ?? "";
                        if (id == null || exe == null) continue;
                        // Une vraie app UWP vit dans ApplicationFrameHost : impossible de la mettre dans une bulle.
                        if (!entry.Equals("Windows.FullTrustApplication", StringComparison.OrdinalIgnoreCase)) continue;
                        map[package.Id.FamilyName + "!" + id] = Path.GetFileNameWithoutExtension(exe);
                    }
                }
                catch
                {
                    // Paquet illisible : ignoré.
                }
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        return map;
    }

    // ---------- Icônes des apps du Store ----------

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindCtx, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    /// <summary>Icône d'un élément du Shell (« shell:AppsFolder\… »), transparence comprise.</summary>
    public static ImageSource? Icon(string parsingName, int size = 64)
    {
        IntPtr hbitmap = IntPtr.Zero;
        try
        {
            SHCreateItemFromParsingName(parsingName, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
            const int SIIGBF_ICONONLY = 0x4;
            if (factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_ICONONLY, out hbitmap) != 0 || hbitmap == IntPtr.Zero) return null;

            // Lecture des pixels en 32 bits, de haut en bas (hauteur négative), pour garder la transparence.
            var info = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = size, biHeight = -size, biPlanes = 1, biBitCount = 32 };
            var bits = new byte[size * size * 4];
            var dc = GetDC(IntPtr.Zero);
            try { if (GetDIBits(dc, hbitmap, 0, (uint)size, bits, ref info, 0) == 0) return null; }
            finally { ReleaseDC(IntPtr.Zero, dc); }
            var image = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, bits, size * 4);
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
        }
    }
}
