using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Bulles;

/// <summary>
/// Explorateur de fichiers façon menu de SAO (option « test » des paramètres) : chaque dossier ouvert ajoute
/// une liste blanche qui tourne comme une roue ; les dossiers précédents deviennent des panneaux gris avec
/// le dossier ouvert en orange. Un clic sur un panneau gris y revient, la croix rose ferme tout.
/// Le premier niveau reprend le volet de gauche de l'explorateur Windows (accès rapide, OneDrive, Ce PC).
/// </summary>
public sealed class ExplorerView : UserControl
{
    public const double ViewWidth = 880, ViewHeight = 460;
    private const double RowHeight = 37, WheelHeight = 340, WhiteWidth = 230, GrayWidth = 150, VisibleLevels = 3, PreviewSize = 210;

    private static readonly Brush Orange = Frozen(new SolidColorBrush(Color.FromRgb(0xF0, 0xA8, 0x18)));
    private static readonly Brush OrangeSoft = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xE2, 0xA6)));
    private static readonly Brush White = Frozen(new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF4)));
    private static readonly Brush RowLine = Frozen(new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)));
    private static readonly Brush TextDark = Frozen(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)));
    private static readonly Brush TextSoft = Frozen(new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)));
    private static readonly Brush DotDark = Frozen(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)));
    private static readonly Brush DotLight = Frozen(new SolidColorBrush(Color.FromRgb(0xE9, 0xE9, 0xE9)));
    private static readonly Brush PanelGray = Frozen(new SolidColorBrush(Color.FromRgb(0x5B, 0x5F, 0x66)));
    private static readonly Brush RowGray = Frozen(new SolidColorBrush(Color.FromRgb(0x73, 0x77, 0x7E)));
    private static readonly Brush RowGrayHover = Frozen(new SolidColorBrush(Color.FromRgb(0x82, 0x86, 0x8D)));
    private static readonly Brush TextGray = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)));
    private static readonly Brush Pink = Frozen(new SolidColorBrush(Color.FromRgb(0xC8, 0x23, 0x6A)));
    private static readonly Brush CaptionBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE)));
    private static readonly Brush HitBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0)));
    private static readonly System.Windows.Media.Effects.DropShadowEffect TextShadow = Frozen(new System.Windows.Media.Effects.DropShadowEffect
    {
        Color = Colors.Black, BlurRadius = 4, ShadowDepth = 1, Direction = 270, Opacity = 0.9,
    });

    /// <summary>Un élément : dossier (avec sa liste d'enfants, lue à l'ouverture) ou fichier.</summary>
    private sealed record Item(string Name, string? Path, string Glyph, Func<List<Item>>? Children, string Info = "")
    {
        public bool IsFolder => Children != null;
    }

    private sealed class Level
    {
        public required Item Node;
        public required List<Item> Kids { get; set; }
        public int Sel = -1;
        public int Focus;
        /// <summary>Premier élément affiché quand le niveau est un panneau gris (déplacé à la molette).</summary>
        public int? GrayFrom;
    }

    private readonly AppController _c;
    private readonly Action _close;
    private readonly StackPanel _chain = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly List<Level> _levels = new();

    public ExplorerView(AppController controller, Action close)
    {
        _c = controller;
        _close = close;
        Content = _chain;
        Background = Brushes.Transparent;
        Focusable = true;
        PreviewKeyDown += (_, e) =>
        {
            // Échap : on remonte d'un dossier, puis on ferme.
            if (e.Key != Key.Escape) return;
            if (_levels.Count > 1) GoBack(_levels.Count - 2);
            else CloseAll();
            e.Handled = true;
        };
        // L'arbre ouvert est gardé quand la bulle est cachée (fichier lancé, clic ailleurs…) ;
        // on ne repart du début qu'après la croix rose.
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) return;
            if (_resetOnShow || _levels.Count == 0)
            {
                _resetOnShow = false;
                Reset();
            }
            else
            {
                Build(animateLast: false);
                Focus();
            }
        };
    }

    private bool _resetOnShow = true;

    /// <summary>Croix rose / Échap au premier niveau : ferme l'explorateur, il repartira du début.</summary>
    private void CloseAll()
    {
        _resetOnShow = true;
        _close();
    }

    /// <summary>On repart toujours du premier niveau (le volet de gauche de l'explorateur).</summary>
    private void Reset()
    {
        _levels.Clear();
        var root = new Item("Ce PC", null, "", RootItems);
        var kids = root.Children!();
        _levels.Add(new Level { Node = root, Kids = kids, Focus = 0 });
        Build(animateLast: true);
        Focus();
    }

    // ---------- Contenu ----------

    /// <summary>Premier niveau : accès rapide (épinglés + fréquents), OneDrive, puis les disques de « Ce PC ».</summary>
    private static List<Item> RootItems()
    {
        var list = new List<Item>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddFolder(string name, string path)
        {
            if (!Directory.Exists(path) || !seen.Add(path.TrimEnd('\\'))) return;
            list.Add(Folder(name, path));
        }

        foreach (var (name, path) in QuickAccess()) AddFolder(name, path);
        if (list.Count == 0)
        {
            // Accès rapide illisible : les dossiers habituels.
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            AddFolder("Bureau", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            AddFolder("Téléchargements", System.IO.Path.Combine(home, "Downloads"));
            AddFolder("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            AddFolder("Images", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
            AddFolder("Musique", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
            AddFolder("Vidéos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        }

        var oneDrive = Environment.GetEnvironmentVariable("OneDrive");
        if (!string.IsNullOrEmpty(oneDrive) && Directory.Exists(oneDrive) && seen.Add(oneDrive.TrimEnd('\\')))
            list.Add(new Item(ShellName(oneDrive) ?? "OneDrive", oneDrive, "", () => ListFolder(oneDrive)));

        list.Add(new Item("Ce PC", null, "", Drives));
        return list;
    }

    /// <summary>Les dossiers de l'accès rapide, dans l'ordre de l'explorateur, avec leur nom affiché (« Bureau »…).</summary>
    private static IEnumerable<(string Name, string Path)> QuickAccess()
    {
        var result = new List<(string, string)>();
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null) return result;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic folder = shell.NameSpace("shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}");
            if (folder == null) return result;
            dynamic items = folder.Items();
            int count = items.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic item = items.Item(i);
                if (item == null || !(bool)item.IsFolder) continue;
                string path = item.Path;
                string name = item.Name;
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) result.Add((name, path));
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        return result;
    }

    private static string? ShellName(string path)
    {
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic folder = shell.NameSpace(path);
            return folder?.Title as string;
        }
        catch
        {
            return null;
        }
    }

    private static List<Item> Drives()
    {
        var list = new List<Item>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                string letter = d.Name.TrimEnd('\\');
                string label = string.IsNullOrEmpty(d.VolumeLabel) ? (d.DriveType == DriveType.Removable ? "Clé USB" : "Disque local") : d.VolumeLabel;
                string free = $"{FormatSize(d.AvailableFreeSpace)} libres";
                var root = d.RootDirectory.FullName;
                list.Add(new Item($"{label} ({letter})", root, d.DriveType == DriveType.Removable ? "" : "", () => ListFolder(root), free));
            }
            catch
            {
                // Lecteur qui ne répond pas : ignoré.
            }
        }
        return list;
    }

    private static Item Folder(string name, string path) => new(name, path, FolderGlyph(path), () => ListFolder(path));

    /// <summary>Contenu d'un dossier : sous-dossiers puis fichiers, sans les éléments cachés ou système.</summary>
    private static List<Item> ListFolder(string path)
    {
        var list = new List<Item>();
        try
        {
            var dir = new DirectoryInfo(path);
            const FileAttributes skip = FileAttributes.Hidden | FileAttributes.System;
            foreach (var d in dir.EnumerateDirectories().Where(d => (d.Attributes & skip) == 0).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
                list.Add(Folder(d.Name, d.FullName));
            foreach (var f in dir.EnumerateFiles().Where(f => (f.Attributes & skip) == 0).OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                string name = f.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".url", StringComparison.OrdinalIgnoreCase)
                    ? System.IO.Path.GetFileNameWithoutExtension(f.Name) : f.Name;
                list.Add(new Item(name, f.FullName, FileGlyph(f.Extension), null, FormatSize(f.Length)));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // Dossier protégé : affiché vide.
        }
        return list;
    }

    private static string FolderGlyph(string path)
    {
        string Known(Environment.SpecialFolder f) => Environment.GetFolderPath(f).TrimEnd('\\');
        var p = path.TrimEnd('\\');
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (p.Equals(Known(Environment.SpecialFolder.DesktopDirectory), StringComparison.OrdinalIgnoreCase)) return "";
        if (p.Equals(System.IO.Path.Combine(home, "Downloads"), StringComparison.OrdinalIgnoreCase)) return "";
        if (p.Equals(Known(Environment.SpecialFolder.MyDocuments), StringComparison.OrdinalIgnoreCase)) return "";
        if (p.Equals(Known(Environment.SpecialFolder.MyPictures), StringComparison.OrdinalIgnoreCase)) return "";
        if (p.Equals(Known(Environment.SpecialFolder.MyMusic), StringComparison.OrdinalIgnoreCase)) return "";
        if (p.Equals(Known(Environment.SpecialFolder.MyVideos), StringComparison.OrdinalIgnoreCase)) return "";
        return "";
    }

    private static string FileGlyph(string extension) => extension.ToLowerInvariant() switch
    {
        ".mp3" or ".wav" or ".flac" or ".m4a" or ".ogg" or ".aac" or ".wma" => "",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".ico" => "",
        ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm" => "",
        ".exe" or ".lnk" or ".url" or ".msi" or ".bat" => "",
        ".zip" or ".rar" or ".7z" => "",
        ".pdf" or ".doc" or ".docx" or ".txt" or ".md" or ".xlsx" or ".pptx" => "",
        _ => "",
    };

    private static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} Go"
        : bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.#} Mo"
        : bytes >= 1 << 10 ? $"{bytes / 1024.0:0} Ko"
        : $"{bytes} o";

    // ---------- Navigation ----------

    private void Open(int levelIndex, int itemIndex)
    {
        while (_levels.Count > levelIndex + 1) _levels.RemoveAt(_levels.Count - 1);
        var level = _levels[levelIndex];
        var item = level.Kids[itemIndex];
        if (item.IsFolder)
        {
            level.GrayFrom = null;
            level.Sel = itemIndex;
            var kids = item.Children!();
            _levels.Add(new Level { Node = item, Kids = kids, Focus = 0 });
            Build(animateLast: true);
            return;
        }
        // Fichier : premier clic = choisi (orange), deuxième clic = lancé avec son app.
        if (level.Sel == itemIndex && item.Path != null)
        {
            // L'arbre reste ouvert : l'app du fichier passe devant, l'explorateur garde sa place.
            Launch(item.Path);
            return;
        }
        level.Sel = itemIndex;
        Build(animateLast: false);
    }

    private static void Launch(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { /* « Non » à la demande de Windows. */ }
        catch (Exception ex) { App.Log(ex); }
    }

    // ---------- Clic droit ----------

    /// <summary>Menu d'un fichier ou d'un dossier : ouvrir, copier, renommer, corbeille, épingler, propriétés…</summary>
    private ContextMenu? ItemMenu(int levelIndex, int itemIndex)
    {
        var item = _levels[levelIndex].Kids[itemIndex];
        var path = item.Path;
        if (path == null) return null;
        bool isDrive = System.IO.Path.GetPathRoot(path)?.TrimEnd('\\').Equals(path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) == true;

        var menu = new ContextMenu();
        void Add(string header, Action action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) =>
            {
                try { action(); }
                catch (Exception ex) { App.Log(ex); }
            };
            menu.Items.Add(mi);
        }

        Add(item.IsFolder ? "Ouvrir" : "Ouvrir", () =>
        {
            if (item.IsFolder) Open(levelIndex, itemIndex);
            else Launch(path);
        });
        if (!item.IsFolder) Add("Ouvrir avec…", () => Process.Start("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {path}"));
        Add("Afficher dans l'explorateur Windows", () => Process.Start("explorer.exe", isDrive ? $"\"{path}\"" : $"/select,\"{path}\""));
        menu.Items.Add(new Separator());
        Add("Copier", () => Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { path }));
        Add("Copier le chemin", () => Clipboard.SetText(path));
        if (!isDrive)
        {
            Add("Renommer…", () => Rename(levelIndex, item));
            Add("Mettre à la corbeille…", () => Delete(levelIndex, item));
        }
        menu.Items.Add(new Separator());
        if (item.IsFolder && !isDrive) Add("Épingler dans l'accès rapide", () => ShellVerb(path, "pintohome"));
        Add("Propriétés", () => ShellVerb(path, "properties"));
        return menu;
    }

    /// <summary>Lance une action de l'explorateur Windows sur un élément (épingler, propriétés…).</summary>
    private static void ShellVerb(string path, string verb)
    {
        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type == null) return;
        dynamic shell = Activator.CreateInstance(type)!;
        var parent = System.IO.Path.GetDirectoryName(path.TrimEnd('\\'));
        if (parent == null)
        {
            // Un disque : il est dans « Ce PC ».
            dynamic drives = shell.NameSpace(17);
            drives?.ParseName(path)?.InvokeVerb(verb);
            return;
        }
        dynamic folder = shell.NameSpace(parent);
        dynamic target = folder?.ParseName(System.IO.Path.GetFileName(path.TrimEnd('\\')));
        target?.InvokeVerb(verb);
    }

    private void Rename(int levelIndex, Item item)
    {
        var dialog = new TextInputDialog("Renommer", "Nouveau nom", System.IO.Path.GetFileName(item.Path!.TrimEnd('\\')));
        dialog.ShowDialog();
        var name = dialog.Result?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        var target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(item.Path.TrimEnd('\\'))!, name);
        if (target.Equals(item.Path, StringComparison.Ordinal)) return;
        try
        {
            if (item.IsFolder) Directory.Move(item.Path, target);
            else File.Move(item.Path, target);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            new ChoiceDialog("Impossible de renommer", ex.Message, "OK", "Fermer").ShowDialog();
            return;
        }
        Reload(levelIndex);
    }

    private void Delete(int levelIndex, Item item)
    {
        var dialog = new ChoiceDialog($"Mettre « {item.Name} » à la corbeille ?",
            item.IsFolder ? "Le dossier et tout son contenu iront dans la corbeille (tu pourras les récupérer)." : "Tu pourras le récupérer dans la corbeille.",
            "Mettre à la corbeille", "Annuler");
        dialog.ShowDialog();
        if (dialog.Result != 1) return;
        try
        {
            const Microsoft.VisualBasic.FileIO.UIOption ui = Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs;
            const Microsoft.VisualBasic.FileIO.RecycleOption bin = Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin;
            if (item.IsFolder) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(item.Path!, ui, bin);
            else Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(item.Path!, ui, bin);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { App.Log(ex); }
        Reload(levelIndex);
    }

    /// <summary>Relit un niveau après un changement (renommer, corbeille) ; les niveaux suivants se ferment.</summary>
    private void Reload(int levelIndex)
    {
        while (_levels.Count > levelIndex + 1) _levels.RemoveAt(_levels.Count - 1);
        var level = _levels[levelIndex];
        level.Kids = level.Node.Children!();
        level.Sel = -1;
        level.GrayFrom = null;
        level.Focus = Math.Clamp(level.Focus, 0, Math.Max(0, level.Kids.Count - 1));
        Build(animateLast: false);
    }

    /// <summary>Clic sur un panneau gris : on revient à ce niveau (les suivants se ferment).</summary>
    private void GoBack(int levelIndex)
    {
        while (_levels.Count > levelIndex + 1) _levels.RemoveAt(_levels.Count - 1);
        var level = _levels[levelIndex];
        level.Sel = -1;
        Build(animateLast: false);
    }

    // ---------- Dessin ----------

    /// <summary>Bulles à droite : les panneaux s'enchaînent vers la gauche (et inversement).</summary>
    private bool TowardLeft => !_c.Settings.IsLeft;

    private void Build(bool animateLast)
    {
        _chain.Children.Clear();
        _chain.FlowDirection = TowardLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        _chain.HorizontalAlignment = TowardLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        int first = Math.Max(0, _levels.Count - (int)VisibleLevels);
        for (int i = first; i < _levels.Count; i++)
        {
            bool last = i == _levels.Count - 1;
            FrameworkElement panel = last ? WhitePanel(i) : GrayPanel(i);
            panel.FlowDirection = FlowDirection.LeftToRight;
            _chain.Children.Add(panel);
            // Image choisie (en orange) : son aperçu s'affiche à côté de la liste.
            if (last && _levels[i].Sel >= 0 && _levels[i].Kids[_levels[i].Sel] is { IsFolder: false, Path: { } path } && IsImage(path))
            {
                var preview = PreviewPanel(path, _levels[i].Kids[_levels[i].Sel]);
                preview.FlowDirection = FlowDirection.LeftToRight;
                _chain.Children.Add(preview);
                SlideIn(preview);
            }
            if (last && animateLast) SlideIn(panel);
        }
    }

    private FrameworkElement GrayPanel(int index)
    {
        var level = _levels[index];
        var stack = new StackPanel();
        int maxFrom = Math.Max(0, level.Kids.Count - 5);
        int from = Math.Clamp(level.GrayFrom ?? level.Sel - 2, 0, maxFrom);
        foreach (var (item, i) in level.Kids.Select((k, i) => (k, i)).Skip(from).Take(5))
        {
            bool on = i == level.Sel;
            var row = Row(item, on, back: true, showInfo: false);
            row.Width = GrayWidth;
            row.Margin = new Thickness(0, 2, 0, 2);
            int itemIndex = i;
            row.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (_dragClick) return; Open(index, itemIndex); };
            EnableFileDrag(row, level.Kids[itemIndex]);
            row.ContextMenu = ItemMenu(index, itemIndex);
            if (on)
            {
                // Encoche orange qui pointe vers le panneau suivant.
                var notch = new Polygon
                {
                    Fill = Orange, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center,
                    Points = TowardLeft
                        ? new PointCollection { new(0, 0), new(-9, 9), new(0, 18) }
                        : new PointCollection { new(0, 0), new(9, 9), new(0, 18) },
                    HorizontalAlignment = TowardLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                };
                var host = new Grid { ClipToBounds = false };
                host.Children.Add(row);
                host.Children.Add(notch);
                stack.Children.Add(host);
            }
            else stack.Children.Add(row);
        }
        var panel = new Border
        {
            Background = PanelGray, Padding = new Thickness(6, 4, 6, 4), Child = stack, Cursor = Cursors.Hand,
            Margin = new Thickness(5, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0.97, 0.97),
        };
        panel.MouseLeftButtonUp += (_, _) => GoBack(index);
        // Molette sur un panneau gris : on fait défiler sa liste, sans changer le dossier ouvert.
        panel.MouseWheel += (_, e) =>
        {
            e.Handled = true;
            int next = Math.Clamp(from + (e.Delta > 0 ? -1 : 1), 0, maxFrom);
            if (next == from) return;
            level.GrayFrom = next;
            Build(animateLast: false);
        };
        var wrap = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 5, 0) };
        panel.Margin = new Thickness(0);
        wrap.Children.Add(Header(index, GrayWidth + 12));
        wrap.Children.Add(panel);
        return wrap;
    }

    private FrameworkElement WhitePanel(int index)
    {
        var level = _levels[index];
        // La liste commence juste sous la croix ; elle ne prend que la hauteur de son contenu (au plus WheelHeight).
        int count = level.Kids.Count;
        int visible = (int)(WheelHeight / RowHeight);
        double height = Math.Min(visible, Math.Max(1, count)) * RowHeight;
        var canvas = new Canvas { Width = WhiteWidth, Height = height, Background = HitBrush, ClipToBounds = true };

        if (count == 0)
        {
            var empty = Row(new Item("Dossier vide", null, "", null), false, back: false, showInfo: false);
            empty.Width = WhiteWidth;
            empty.Cursor = Cursors.Arrow;
            canvas.Children.Add(empty);
        }
        void Layout()
        {
            canvas.Children.Clear();
            // Focus = premier élément affiché. L'élément choisi reste toujours visible.
            int maxTop = Math.Max(0, count - visible);
            if (level.Sel >= 0 && level.Sel < level.Focus) level.Focus = level.Sel;
            if (level.Sel >= level.Focus + visible) level.Focus = level.Sel - visible + 1;
            level.Focus = Math.Clamp(level.Focus, 0, maxTop);
            int top = level.Focus;
            for (int i = top; i < Math.Min(count, top + visible); i++)
            {
                var row = Row(level.Kids[i], i == level.Sel, back: false, showInfo: true);
                row.Width = WhiteWidth;
                Canvas.SetTop(row, (i - top) * RowHeight);
                int itemIndex = i;
                row.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (_dragClick) return; Open(index, itemIndex); };
                EnableFileDrag(row, level.Kids[itemIndex]);
                row.ContextMenu = ItemMenu(index, itemIndex);
                canvas.Children.Add(row);
            }
            // Fondu seulement du côté où il reste des éléments cachés : la liste « tourne » comme une roue.
            bool moreAbove = top > 0, moreBelow = top + visible < count;
            if (!moreAbove && !moreBelow) { canvas.OpacityMask = null; return; }
            var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            mask.GradientStops.Add(new GradientStop(moreAbove ? Colors.Transparent : Colors.Black, 0));
            mask.GradientStops.Add(new GradientStop(Colors.Black, moreAbove ? 0.18 : 0));
            mask.GradientStops.Add(new GradientStop(Colors.Black, moreBelow ? 0.82 : 1));
            mask.GradientStops.Add(new GradientStop(moreBelow ? Colors.Transparent : Colors.Black, 1));
            canvas.OpacityMask = mask;
        }
        if (count > 0) Layout();
        canvas.MouseWheel += (_, e) =>
        {
            e.Handled = true;
            if (count <= visible) return;
            level.Focus = Math.Clamp(level.Focus + (e.Delta > 0 ? -1 : 1), 0, count - visible);
            // On garde le fichier choisi seulement s'il reste à l'écran (sinon son aperçu disparaît aussi).
            if (level.Sel >= 0 && (level.Sel < level.Focus || level.Sel >= level.Focus + visible))
            {
                level.Sel = -1;
                Build(animateLast: false);
                return;
            }
            Layout();
        };

        // Petite pointe blanche vers le panneau gris précédent.
        var tip = new Polygon
        {
            Fill = White, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = TowardLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Points = TowardLeft
                ? new PointCollection { new(0, 0), new(8, 8), new(0, 16) }
                : new PointCollection { new(8, 0), new(0, 8), new(8, 16) },
            Visibility = index > 0 ? Visibility.Visible : Visibility.Collapsed,
            Margin = TowardLeft ? new Thickness(0, 0, -8, 0) : new Thickness(-8, 0, 0, 0),
        };
        var body = new Grid();
        body.Children.Add(canvas);
        body.Children.Add(tip);

        var stack = new StackPanel { Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(Header(index, WhiteWidth));
        stack.Children.Add(body);
        return stack;
    }

    /// <summary>Au-dessus de chaque panneau : sa croix rose (ferme ce panneau) et le nom du dossier.</summary>
    private FrameworkElement Header(int index, double width)
    {
        var caption = new TextBlock
        {
            Text = _levels[index].Node.Name, Foreground = CaptionBrush, Effect = TextShadow, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = width - 30,
        };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
        head.Children.Add(CloseButton(index));
        head.Children.Add(caption);
        return head;
    }

    /// <summary>
    /// Croix d'un panneau : ferme ce panneau (et ceux ouverts après lui) ; le panneau d'avant redevient la liste blanche.
    /// La croix du premier panneau ferme l'explorateur.
    /// </summary>
    private void CloseLevel(int index)
    {
        if (index <= 0)
        {
            CloseAll();
            return;
        }
        GoBack(index - 1);
    }

    // ---------- Aperçu des images ----------

    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff", ".jfif" };

    private static bool IsImage(string path) => ImageExtensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Panneau blanc avec la miniature, le nom, la taille en pixels et le poids du fichier.</summary>
    private FrameworkElement PreviewPanel(string path, Item item)
    {
        var image = new Image { Stretch = Stretch.Uniform, MaxWidth = PreviewSize, MaxHeight = PreviewSize, HorizontalAlignment = HorizontalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var loading = new TextBlock { Text = "Chargement…", Foreground = TextSoft, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var frame = new Grid { Width = PreviewSize, MinHeight = PreviewSize * 0.6, Background = Frozen(new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6))) };
        frame.Children.Add(loading);
        frame.Children.Add(image);

        var name = new TextBlock { Text = item.Name, Foreground = TextDark, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 8, 2, 0) };
        var details = new TextBlock { Text = item.Info, Foreground = TextSoft, FontSize = 11.5, Margin = new Thickness(2, 2, 2, 0) };
        var stack = new StackPanel();
        stack.Children.Add(frame);
        stack.Children.Add(name);
        stack.Children.Add(details);
        var card = new Border
        {
            Background = White, Padding = new Thickness(8), Child = stack, Width = PreviewSize + 16, Cursor = Cursors.Hand,
            ToolTip = "Clic : ouvrir l'image", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0),
        };
        card.MouseLeftButtonUp += (_, _) => Launch(path);

        // Chargement en arrière-plan, en taille réduite : une grosse photo ne bloque pas l'explorateur.
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(path);
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile;
                bitmap.DecodePixelWidth = (int)(PreviewSize * 2);
                bitmap.EndInit();
                bitmap.Freeze();
                var info = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(path), System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation, System.Windows.Media.Imaging.BitmapCacheOption.None);
                string size = $"{info.PixelWidth} × {info.PixelHeight} px · {item.Info}";
                Dispatcher.BeginInvoke(() =>
                {
                    image.Source = bitmap;
                    loading.Visibility = Visibility.Collapsed;
                    // Le cadre prend la forme de l'image (pas de bandes grises autour d'une photo en largeur).
                    frame.MinHeight = 0;
                    frame.Background = Brushes.Transparent;
                    details.Text = size;
                });
            }
            catch
            {
                Dispatcher.BeginInvoke(() => loading.Text = "Aperçu impossible");
            }
        });
        return card;
    }

    /// <summary>Une ligne : pastille ronde avec l'icône, nom, taille (ou infos) à droite.</summary>
    private static Border Row(Item item, bool on, bool back, bool showInfo)
    {
        var glyph = new TextBlock { Text = item.Glyph, FontFamily = BubbleGlyphs.Font, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var dot = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Child = glyph, VerticalAlignment = VerticalAlignment.Center };
        // Image : sa miniature dans le rond. App (exe, raccourci) : sa vraie icône.
        var kind = item.IsFolder || item.Path == null ? DotKind.Glyph : IsImage(item.Path) ? DotKind.Thumbnail : IsApp(item.Path) ? DotKind.AppIcon : DotKind.Glyph;
        Ellipse? thumb = null;
        if (kind == DotKind.Thumbnail)
        {
            thumb = new Ellipse { Width = 24, Height = 24, Fill = DotDark, StrokeThickness = 1.5 };
            dot.Child = thumb;
            LoadThumbnail(item.Path!, thumb);
        }
        else if (kind == DotKind.AppIcon && IconLoader.Load(item.Path!, 0, item.Path!) is { } icon)
        {
            var image = new Image { Source = icon, Width = 22, Height = 22 };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            dot.Child = image;
        }
        else kind = DotKind.Glyph;
        var name = new TextBlock { Text = item.Name, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(10, 0, 0, 0) };
        var info = new TextBlock { Text = showInfo ? item.Info : "", FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(dot, Dock.Left);
        DockPanel.SetDock(info, Dock.Right);
        dock.Children.Add(dot);
        dock.Children.Add(info);
        dock.Children.Add(name);
        var row = new Border
        {
            Height = back ? 34 : RowHeight, Padding = new Thickness(12, 0, 12, 0), Child = dock, Cursor = Cursors.Hand,
            BorderBrush = back ? PanelGray : RowLine, BorderThickness = new Thickness(0, 0, 0, back ? 0 : 1),
            SnapsToDevicePixels = true, UseLayoutRounding = true,
        };
        void Paint(bool hover)
        {
            row.Background = on ? Orange : back ? (hover ? RowGrayHover : RowGray) : hover ? OrangeSoft : White;
            name.Foreground = on ? Brushes.White : back ? TextGray : TextDark;
            info.Foreground = on ? Brushes.White : TextSoft;
            if (kind == DotKind.Glyph)
            {
                dot.Background = on ? Brushes.White : back ? DotLight : DotDark;
                glyph.Foreground = on ? Orange : back ? DotDark : Brushes.White;
            }
            // Miniature : cerclée de blanc quand la ligne est orange.
            if (thumb != null) thumb.Stroke = on ? Brushes.White : null;
        }
        Paint(false);
        row.MouseEnter += (_, _) => Paint(true);
        row.MouseLeave += (_, _) => Paint(false);
        return row;
    }

    // ---------- Glisser un fichier hors de l'explorateur ----------

    private Point? _pressPoint;
    private bool _dragClick;

    /// <summary>
    /// Maintenir puis glisser une ligne : le fichier (ou le dossier) part comme depuis l'explorateur Windows,
    /// vers Discord, Chrome, un dossier… Survoler une bulle pendant le glisser l'ouvre pour y déposer.
    /// </summary>
    private void EnableFileDrag(Border row, Item item)
    {
        if (item.Path == null) return;
        row.PreviewMouseLeftButtonDown += (_, e) => _pressPoint = e.GetPosition(this);
        row.MouseMove += (_, e) =>
        {
            if (_pressPoint is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
            var now = e.GetPosition(this);
            if (Math.Abs(now.X - start.X) < 6 && Math.Abs(now.Y - start.Y) < 6) return;
            _pressPoint = null;
            _dragClick = true;
            try
            {
                var data = new DataObject(DataFormats.FileDrop, new[] { item.Path });
                DragDrop.DoDragDrop(row, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            }
            catch (Exception ex)
            {
                App.Log(ex);
            }
            // Le relâchement du bouton a servi au dépôt : ce n'est pas un clic sur la ligne.
            Dispatcher.BeginInvoke(() => _dragClick = false, System.Windows.Threading.DispatcherPriority.Input);
        };
        row.PreviewMouseLeftButtonUp += (_, _) => _pressPoint = null;
    }

    private enum DotKind { Glyph, Thumbnail, AppIcon }

    private static readonly string[] AppExtensions = { ".exe", ".lnk", ".url", ".msi", ".appref-ms" };

    private static bool IsApp(string path) => AppExtensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

    private static readonly Dictionary<string, ImageSource?> Thumbnails = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Miniature d'une image (toute petite, en arrière-plan, gardée en mémoire pour la suite).</summary>
    private static void LoadThumbnail(string path, Ellipse target)
    {
        void Apply(ImageSource? source)
        {
            if (source != null) target.Fill = new ImageBrush(source) { Stretch = Stretch.UniformToFill };
        }
        if (Thumbnails.TryGetValue(path, out var cached))
        {
            Apply(cached);
            return;
        }
        var dispatcher = target.Dispatcher;
        System.Threading.Tasks.Task.Run(() =>
        {
            ImageSource? source = null;
            try
            {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(path);
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile;
                bitmap.DecodePixelWidth = 64;
                bitmap.EndInit();
                bitmap.Freeze();
                source = bitmap;
            }
            catch
            {
                // Image illisible : le rond reste gris.
            }
            dispatcher.BeginInvoke(() =>
            {
                Thumbnails[path] = source;
                Apply(source);
            });
        });
    }

    private FrameworkElement CloseButton(int index)
    {
        var g = new Grid
        {
            Width = 20, Height = 20, Cursor = Cursors.Hand, RenderTransformOrigin = new Point(0.5, 0.5),
            ToolTip = index == 0 ? "Fermer l'explorateur" : "Fermer ce dossier",
        };
        g.Children.Add(new Ellipse { Fill = Pink });
        g.Children.Add(new Ellipse { Stroke = Brushes.White, StrokeThickness = 1.5, Margin = new Thickness(1.5) });
        foreach (var flip in new[] { false, true })
            g.Children.Add(new Line
            {
                X1 = 7, Y1 = flip ? 13 : 7, X2 = 13, Y2 = flip ? 7 : 13,
                Stroke = Brushes.White, StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            });
        g.MouseEnter += (_, _) => g.RenderTransform = new ScaleTransform(1.12, 1.12);
        g.MouseLeave += (_, _) => g.RenderTransform = null;
        g.MouseLeftButtonUp += (_, e) => { e.Handled = true; CloseLevel(index); };
        return g;
    }

    private void SlideIn(FrameworkElement panel)
    {
        double from = TowardLeft ? 16 : -16;
        panel.RenderTransform = new TranslateTransform(from, 0);
        panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)));
        panel.RenderTransform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
