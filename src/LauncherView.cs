using System;
using System.Collections.Generic;
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
/// Contenu d'une bulle Lanceur, façon menu SAO : un panneau blanc de catégories (Jeux, Apps, Sites),
/// puis la liste des apps de la catégorie choisie. Le panneau des catégories passe en gris derrière,
/// comme un dossier, quand la liste s'ouvre. Un clic lance l'app normalement (hors bulle).
/// </summary>
public sealed class LauncherView : UserControl
{
    public const double ViewWidth = 470, ViewHeight = 520;
    private const double CategoryWidth = 168, ListWidth = 250, RowHeight = 40, MaxRows = 11;

    private static readonly Brush Orange = Frozen(new SolidColorBrush(Color.FromRgb(0xF0, 0xA8, 0x18)));
    private static readonly Brush OrangeSoft = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xE2, 0xA6)));
    private static readonly Brush PanelWhite = Frozen(new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF4)));
    private static readonly Brush RowLine = Frozen(new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)));
    private static readonly Brush TextDark = Frozen(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)));
    private static readonly Brush DotDark = Frozen(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)));
    private static readonly Brush PanelGray = Frozen(new SolidColorBrush(Color.FromRgb(0x5B, 0x5F, 0x66)));
    private static readonly Brush RowGray = Frozen(new SolidColorBrush(Color.FromRgb(0x73, 0x77, 0x7E)));
    private static readonly Brush TextGray = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)));
    private static readonly Brush DotLight = Frozen(new SolidColorBrush(Color.FromRgb(0xE9, 0xE9, 0xE9)));
    private static readonly Brush HoldBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x9B, 0xDC, 0xFF)));

    public const string Games = "Jeux", Apps = "Apps", Sites = "Sites";
    private static readonly (string Name, string Glyph)[] Categories =
    {
        (Games, ""), (Apps, ""), (Sites, ""),
    };
    private const string AddCategory = "Ajouter";

    private readonly AppController _c;
    private readonly BubbleConfig _launcher;
    private readonly StackPanel _root = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _categoryPanel;
    private readonly StackPanel _categoryRows = new();
    private readonly Polygon _listArrow;
    private readonly Border _listPanel;
    private readonly StackPanel _listRows = new();
    private string? _open;

    public LauncherView(AppController controller, BubbleConfig launcher)
    {
        _c = controller;
        _launcher = launcher;
        Focusable = true;

        _categoryPanel = MakePanel(_categoryRows, CategoryWidth);
        _categoryPanel.RenderTransformOrigin = new Point(0.5, 0.5);
        _categoryPanel.RenderTransform = new ScaleTransform(1, 1);
        _listArrow = new Polygon
        {
            Points = new PointCollection { new(12, 0), new(0, 10), new(12, 20) },
            Fill = PanelWhite, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        var scroller = new ScrollViewer
        {
            Content = _listRows, MaxHeight = RowHeight * MaxRows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _listPanel = MakePanel(scroller, ListWidth);
        _listPanel.Visibility = Visibility.Collapsed;

        _root.Children.Add(_categoryPanel);
        _root.Children.Add(_listArrow);
        _root.Children.Add(_listPanel);
        _createPanel = BuildCreatePanel();
        _root.Children.Add(_createPanel);
        ApplySide();

        Content = _root;
        Background = Brushes.Transparent;
        PreviewKeyDown += (_, e) =>
        {
            // Échap : on revient d'abord au panneau des catégories.
            if (e.Key == Key.Escape && _creating)
            {
                CloseList();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _confirmRow != null)
            {
                CancelConfirm();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && _confirmRow is { Tag: LauncherApp app })
            {
                CancelConfirm();
                _c.LaunchFromLauncher(_launcher, app);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _open != null)
            {
                CloseList();
                e.Handled = true;
            }
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Refresh(reset: true);
        };
        Refresh(reset: true);
    }

    public void FocusSearch() => Focus();

    public void Refresh() => Refresh(reset: false);

    /// <summary>
    /// Bulles à droite de l'écran : les panneaux s'ouvrent vers la gauche (miroir), et inversement.
    /// Relu à chaque affichage : le côté des bulles peut changer pendant que Bubulle tourne.
    /// </summary>
    private void ApplySide()
    {
        bool left = _c.Settings.IsLeft;
        _root.FlowDirection = left ? FlowDirection.LeftToRight : FlowDirection.RightToLeft;
        _root.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        foreach (UIElement child in _root.Children) ((FrameworkElement)child).FlowDirection = FlowDirection.LeftToRight;
        _listArrow.FlowDirection = _root.FlowDirection;
    }

    private void Refresh(bool reset)
    {
        if (reset) _open = null;
        ApplySide();
        if (reset) _creating = false;
        var visible = VisibleCategories();
        if (_open != null && !visible.Any(c => c.Name == _open)) _open = null;
        bool back = _open != null || _creating;

        _categoryRows.Children.Clear();
        int i = 0;
        foreach (var (name, glyph, custom) in visible)
        {
            var row = Row(GlyphDot(glyph), name, name == _open, back);
            var category = name;
            row.MouseLeftButtonUp += (_, _) =>
            {
                if (_open == category) CloseList();
                else OpenList(category);
            };
            var menu = new ContextMenu();
            if (custom)
            {
                var rename = new MenuItem { Header = "Renommer" };
                rename.Click += (_, _) => RenameCategory(category);
                menu.Items.Add(rename);
            }
            var delete = new MenuItem { Header = "Supprimer la catégorie" };
            delete.Click += (_, _) => DeleteCategory(category);
            menu.Items.Add(delete);
            row.ContextMenu = menu;
            _categoryRows.Children.Add(row);
            Appear(row, i++);
        }
        var add = Row(GlyphDot(AddGlyph), AddCategory, _creating, back);
        add.ToolTip = "Créer une catégorie";
        add.MouseLeftButtonUp += (_, _) =>
        {
            if (_creating) CloseList();
            else StartCreate();
        };
        _categoryRows.Children.Add(add);
        Appear(add, i);

        SetBackLook(back);
        _createPanel.Visibility = _creating ? Visibility.Visible : Visibility.Collapsed;
        if (_open != null) FillList(animate: false);
        else _listPanel.Visibility = Visibility.Collapsed;
        _listArrow.Visibility = back ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- Créer une catégorie ----------

    private static readonly string[] CategoryGlyphs =
    {
        "\uE7FC", "\uE8D6", "\uE714", "\uE8BD", "\uE821", "\uE734",
        "\uEB51", "\uE8B7", "\uE774", "\uE943", "\uE719", "\uE722",
    };
    private const string AddGlyph = "\uE710";
    private static readonly Brush ErrorBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xC8, 0x23, 0x6A)));
    private static readonly Brush HintBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)));

    private readonly Border _createPanel;
    private TextBox? _nameBox;
    private TextBlock? _createError;
    private string _newGlyph = "\uE7FC";
    private bool _creating;

    /// <summary>Panneau « Nouvelle catégorie » : un nom, une icône, puis la croix ou le rond pour valider.</summary>
    private Border BuildCreatePanel()
    {
        var stack = new StackPanel();
        stack.Children.Add(new Border
        {
            Background = Orange, Height = 34, Padding = new Thickness(14, 0, 14, 0),
            Child = new TextBlock { Text = "Nouvelle catégorie", Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center },
        });

        _nameBox = new TextBox
        {
            FontSize = 13.5, Foreground = TextDark, Background = Brushes.Transparent, CaretBrush = Orange,
            BorderThickness = new Thickness(0), Padding = new Thickness(2, 4, 2, 4), MaxLength = 24,
        };
        var placeholder = new TextBlock { Text = "Nom (ex. Musique)", Foreground = HintBrush, FontSize = 13.5, Margin = new Thickness(4, 4, 0, 0), IsHitTestVisible = false };
        _nameBox.TextChanged += (_, _) =>
        {
            placeholder.Visibility = _nameBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_createError != null) _createError.Visibility = Visibility.Collapsed;
        };
        var nameHost = new Grid();
        nameHost.Children.Add(_nameBox);
        nameHost.Children.Add(placeholder);
        // Soulignement orange dessiné à part : celui de la zone de texte passe au bleu Windows quand on clique dedans.
        stack.Children.Add(new Border { Child = nameHost, BorderBrush = Orange, BorderThickness = new Thickness(0, 0, 0, 2), Margin = new Thickness(14, 12, 14, 4) });

        _createError = new TextBlock { Foreground = ErrorBrush, FontSize = 11.5, Margin = new Thickness(14, 2, 14, 0), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        stack.Children.Add(_createError);

        stack.Children.Add(new TextBlock { Text = "Icône", Foreground = HintBrush, FontSize = 11.5, Margin = new Thickness(14, 10, 14, 6) });
        var icons = new WrapPanel { Margin = new Thickness(10, 0, 10, 10) };
        void PaintIcons()
        {
            foreach (Border dot in icons.Children)
            {
                bool on = (string)dot.Tag == _newGlyph;
                dot.Background = on ? Orange : DotDark;
                dot.BorderBrush = on ? Brushes.White : Brushes.Transparent;
            }
        }
        foreach (var glyph in CategoryGlyphs)
        {
            var dot = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Margin = new Thickness(4), Tag = glyph, Cursor = Cursors.Hand,
                BorderThickness = new Thickness(2),
                Child = new TextBlock { Text = glyph, FontFamily = BubbleGlyphs.Font, FontSize = 13, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            dot.MouseLeftButtonUp += (_, _) => { _newGlyph = glyph; PaintIcons(); };
            icons.Children.Add(dot);
        }
        PaintIcons();
        stack.Children.Add(icons);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(ConfirmButton(yes: false, CloseList));
        buttons.Children.Add(new Border { Width = 14 });
        buttons.Children.Add(ConfirmButton(yes: true, CreateCategory));
        stack.Children.Add(new Border { Height = 46, BorderBrush = RowLine, BorderThickness = new Thickness(0, 1, 0, 0), Child = buttons });

        _nameBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            CreateCategory();
            e.Handled = true;
        };
        var panel = MakePanel(stack, ListWidth);
        panel.Visibility = Visibility.Collapsed;
        return panel;
    }

    private void StartCreate()
    {
        _open = null;
        _creating = true;
        _nameBox!.Text = "";
        _createError!.Visibility = Visibility.Collapsed;
        Refresh(reset: false);
        SlideIn(_createPanel);
        Dispatcher.BeginInvoke(() => { _nameBox.Focus(); Keyboard.Focus(_nameBox); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private bool NameTaken(string name) =>
        AllCategoryNames().Append(AddCategory).Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));

    private void CreateCategory()
    {
        var name = _nameBox!.Text.Trim();
        string? error = name.Length == 0 ? "Donne un nom à ta catégorie." : NameTaken(name) ? "Cette catégorie existe déjà." : null;
        if (error != null)
        {
            _createError!.Text = error;
            _createError.Visibility = Visibility.Visible;
            _nameBox.Focus();
            return;
        }
        _launcher.Categories.Add(new LauncherCategory { Name = name, Glyph = _newGlyph });
        _c.Settings.Save();
        // On ouvre directement la nouvelle catégorie : il ne reste qu'à y ajouter des jeux avec le « + ».
        OpenList(name);
    }

    private void RenameCategory(string category)
    {
        var custom = _launcher.Categories.FirstOrDefault(c => c.Name == category);
        if (custom == null) return;
        var dialog = new TextInputDialog("Renommer", "Nom de la catégorie", category);
        dialog.ShowDialog();
        var name = dialog.Result?.Trim();
        if (string.IsNullOrEmpty(name) || name == category || NameTaken(name)) return;
        custom.Name = name;
        foreach (var app in _launcher.Apps.Where(a => a.Category == category)) app.Category = name;
        if (_open == category) _open = name;
        _c.Settings.Save();
        Refresh();
    }

    // ---------- Panneaux ----------

    private void OpenList(string category)
    {
        _creating = false;
        _open = category;
        Refresh(reset: false);
        FillList(animate: true);
        SlideIn(_listPanel);
    }

    /// <summary>Retire du lanceur toutes les apps d'une catégorie, après confirmation (rien n'est désinstallé).</summary>
    private void DeleteCategory(string category)
    {
        var apps = _launcher.Apps.Where(a => CategoryOf(a) == category).ToList();
        var custom = _launcher.Categories.FirstOrDefault(c => c.Name == category);
        if (apps.Count == 0 && custom == null) return;
        var dialog = new ChoiceDialog(
            $"Supprimer la catégorie « {category} » ?",
            apps.Count == 0 ? "Elle est vide, rien d'autre ne sera retiré."
                : $"{(apps.Count == 1 ? "L'app" : $"Les {apps.Count} apps")} de cette catégorie {(apps.Count == 1 ? "sera retirée" : "seront retirées")} du lanceur. "
                + "Rien n'est désinstallé de ton PC, tu pourras les rajouter avec le « + ».",
            "Supprimer", "Annuler");
        dialog.ShowDialog();
        if (dialog.Result != 1) return;
        foreach (var app in apps) _launcher.Apps.Remove(app);
        if (custom != null) _launcher.Categories.Remove(custom);
        _c.Settings.Save();
        if (_open == category) _open = null;
        Refresh();
    }

    private void CloseList()
    {
        _creating = false;
        _open = null;
        Refresh(reset: false);
    }

    private void FillList(bool animate)
    {
        _confirmRow = null;
        _confirmParts.Clear();
        _listRows.Children.Clear();
        int i = 0;
        foreach (var app in _launcher.Apps.Where(a => CategoryOf(a) == _open))
        {
            var row = AppRow(app);
            _listRows.Children.Add(row);
            if (animate) Appear(row, i++);
        }
        var add = AddRow();
        _listRows.Children.Add(add);
        if (animate) Appear(add, i);
        _listPanel.Visibility = Visibility.Visible;
        _listArrow.Visibility = Visibility.Visible;
    }

    /// <summary>Panneau en arrière-plan : il passe en gris et recule un peu, comme un dossier.</summary>
    private void SetBackLook(bool back)
    {
        _categoryPanel.Background = back ? PanelGray : PanelWhite;
        var scale = (ScaleTransform)_categoryPanel.RenderTransform;
        var anim = new DoubleAnimation(back ? 0.96 : 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = new QuadraticEase() };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private static Border MakePanel(UIElement content, double width) => new()
    {
        Width = width, Background = PanelWhite, CornerRadius = new CornerRadius(3), Child = content,
        VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true,
    };

    // ---------- Lignes ----------

    /// <summary>Une ligne : pastille ronde + texte. Orange si choisie, grise si son panneau est en arrière-plan.</summary>
    private static Border Row(FrameworkElement dot, string text, bool selected, bool back) => Row(dot, text, () => selected, back, out _);

    private static Border Row(FrameworkElement dot, string text, Func<bool> isSelected, bool back, out Action repaint)
    {
        var label = new TextBlock
        {
            Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(10, 0, 0, 0),
        };
        var dock = new DockPanel { LastChildFill = true };
        dock.Children.Add(dot);
        dock.Children.Add(label);
        var row = new Border
        {
            Height = RowHeight, Padding = new Thickness(12, 0, 12, 0), Child = dock, Cursor = Cursors.Hand,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        bool hovered = false;
        void Paint(bool hover)
        {
            hovered = hover;
            bool selected = isSelected();
            row.Background = selected ? Orange : back ? RowGray : hover ? OrangeSoft : Brushes.Transparent;
            row.BorderBrush = back ? PanelGray : RowLine;
            label.Foreground = selected ? Brushes.White : back ? TextGray : TextDark;
            var circle = dot as Border ?? (dot as Panel)?.Children.OfType<Border>().LastOrDefault();
            if (circle is { Child: TextBlock glyph })
            {
                circle.Background = selected ? Brushes.White : back ? DotLight : DotDark;
                glyph.Foreground = selected ? Orange : back ? DotDark : Brushes.White;
            }
        }
        Paint(false);
        repaint = () => Paint(hovered);
        row.MouseEnter += (_, _) => Paint(true);
        row.MouseLeave += (_, _) => Paint(false);
        return row;
    }

    private static Border GlyphDot(string glyph) => new()
    {
        Width = 24, Height = 24, CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = glyph, FontFamily = BubbleGlyphs.Font, FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        },
    };

    private Border AppRow(LauncherApp app)
    {
        var icon = app.Kind == "Web"
            ? (string.IsNullOrEmpty(app.IconPath) ? IconLoader.CachedFavicon(app.Url) : IconLoader.Load(app.IconPath, 0, ""))
            : IconLoader.Load(app.IconPath, app.IconIndex, app.LaunchPath);
        FrameworkElement dot;
        if (icon != null)
        {
            var image = new Image { Source = icon, Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            dot = image;
        }
        else dot = GlyphDot(app.Kind == "Web" ? "" : "");

        Border? row = null;
        var confirm = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 8, 0) };
        // Croix rose (non) d'abord, puis rond bleu (oui).
        confirm.Children.Add(ConfirmButton(yes: false, CancelConfirm));
        confirm.Children.Add(ConfirmButton(yes: true, () => { CancelConfirm(); _c.LaunchFromLauncher(_launcher, app); }));
        var lead = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        lead.Children.Add(confirm);
        lead.Children.Add(dot);
        row = Row(lead, app.Name, () => row != null && _confirmRow == row, false, out var repaint);
        _confirmParts[row] = (confirm, repaint);
        row.ToolTip = app.Name;
        row.Tag = app;
        // Clic court : lancer. Appui long : la ligne se décroche et se range plus haut ou plus bas.
        row.MouseLeftButtonDown += (_, e) => BeginHold(row, e);
        row.MouseMove += (_, e) => OnHoldMove(row, e);
        row.MouseLeftButtonUp += (_, e) => { EndHold(row, app); e.Handled = true; };
        row.ContextMenu = AppMenu(app);
        return row;
    }

    /// <summary>Dernière case de la liste : un « + » au centre pour ajouter un jeu ou une app dans cette catégorie.</summary>
    private Border AddRow()
    {
        var plus = new Grid { Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        plus.Children.Add(new Ellipse { Fill = DotDark });
        foreach (var vertical in new[] { true, false })
            plus.Children.Add(new Line
            {
                X1 = vertical ? 13 : 7, Y1 = vertical ? 7 : 13, X2 = vertical ? 13 : 19, Y2 = vertical ? 19 : 13,
                Stroke = Brushes.White, StrokeThickness = 3.4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            });
        plus.RenderTransformOrigin = new Point(0.5, 0.5);
        plus.RenderTransform = new ScaleTransform(1, 1);

        var row = new Border
        {
            Height = RowHeight, Child = plus, Cursor = Cursors.Hand, Background = PanelWhite,
            ToolTip = _open == Sites ? "Ajouter un site" : "Ajouter un jeu ou une app",
        };
        row.MouseEnter += (_, _) =>
        {
            row.Background = OrangeSoft;
            plus.RenderTransform = new ScaleTransform(1.12, 1.12);
        };
        row.MouseLeave += (_, _) =>
        {
            row.Background = PanelWhite;
            plus.RenderTransform = new ScaleTransform(1, 1);
        };
        row.MouseLeftButtonUp += (_, _) => _c.AddToLauncher(_launcher, _open);
        return row;
    }

    private ContextMenu AppMenu(LauncherApp app)
    {
        var menu = new ContextMenu();
        var rename = new MenuItem { Header = "Renommer" };
        rename.Click += (_, _) =>
        {
            var dialog = new TextInputDialog("Renommer", "Nom affiché dans le lanceur", app.Name);
            dialog.ShowDialog();
            if (dialog.Result == null || dialog.Result == app.Name) return;
            app.Name = dialog.Result;
            _c.Settings.Save();
            Refresh();
        };
        menu.Items.Add(rename);

        // Corriger le tri automatique.
        var category = new MenuItem { Header = "Ranger dans" };
        foreach (var name in AllCategoryNames())
        {
            var target = name;
            var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = CategoryOf(app) == name };
            item.Click += (_, _) =>
            {
                app.Category = AutoCategory(app) == target ? "" : target;
                _c.Settings.Save();
                Refresh();
            };
            category.Items.Add(item);
        }
        menu.Items.Add(category);

        var others = _c.Settings.Bubbles.Where(b => b.IsLauncher && b != _launcher).ToList();
        if (others.Count > 0)
        {
            var move = new MenuItem { Header = "Déplacer vers" };
            foreach (var other in others)
            {
                var target = other;
                var item = new MenuItem { Header = target.Name };
                item.Click += (_, _) => _c.MoveLauncherApp(app, _launcher, target);
                move.Items.Add(item);
            }
            menu.Items.Add(move);
        }
        var remove = new MenuItem { Header = "Retirer du lanceur" };
        remove.Click += (_, _) => { _launcher.Apps.Remove(app); _c.Settings.Save(); Refresh(); };
        menu.Items.Add(remove);
        if (app.Kind != "Web" && File.Exists(app.LaunchPath))
        {
            var reveal = new MenuItem { Header = "Ouvrir l'emplacement du fichier" };
            reveal.Click += (_, _) => _c.RevealFile(app.LaunchPath);
            menu.Items.Add(reveal);
        }
        return menu;
    }

    // ---------- Catégories automatiques ----------

    public static string CategoryOf(LauncherApp app) => string.IsNullOrEmpty(app.Category) ? AutoCategory(app) : app.Category;

    private IEnumerable<string> AllCategoryNames() => Categories.Select(c => c.Name).Concat(_launcher.Categories.Select(c => c.Name));

    /// <summary>Catégories affichées : Jeux, Apps et Sites s'ils ont des apps, puis toutes celles que tu as créées.</summary>
    private List<(string Name, string Glyph, bool Custom)> VisibleCategories()
    {
        var list = Categories
            .Where(c => _launcher.Apps.Any(a => CategoryOf(a) == c.Name))
            .Select(c => (c.Name, c.Glyph, false)).ToList();
        list.AddRange(_launcher.Categories.Select(c => (c.Name, c.Glyph, true)));
        return list;
    }

    private static readonly string[] GameFolders =
    {
        @"\steamapps\", @"\epic games\", @"\riot games\", @"\gog galaxy\games\", @"\ubisoft game launcher\games\",
        @"\xboxgames\", @"\battle.net\", @"\ea games\", @"\rockstar games\", @"\games\",
    };
    private static readonly string[] GameLinks = { "steam://", "com.epicgames.launcher://", "uplay://", "battlenet://", "origin://", "riotclient" };

    private static string AutoCategory(LauncherApp app)
    {
        if (app.Kind == "Web") return Sites;
        var path = app.LaunchPath ?? "";
        var lower = path.ToLowerInvariant();
        if (GameLinks.Any(lower.Contains) || GameFolders.Any(lower.Contains)) return Games;
        try
        {
            // Raccourci internet (.url) de Steam, Epic… : on regarde l'adresse qu'il ouvre.
            if (lower.EndsWith(".url") && File.Exists(path))
            {
                var text = File.ReadAllText(path).ToLowerInvariant();
                if (GameLinks.Any(text.Contains)) return Games;
                return text.Contains("url=http") ? Sites : Apps;
            }
            // Raccourci (.lnk) : la cible est dans le fichier, en clair.
            if (lower.EndsWith(".lnk") && File.Exists(path))
            {
                var text = System.Text.Encoding.Unicode.GetString(File.ReadAllBytes(path)).ToLowerInvariant()
                    + System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path)).ToLowerInvariant();
                if (GameFolders.Any(text.Contains) || GameLinks.Any(text.Contains)) return Games;
            }
        }
        catch
        {
            // Fichier illisible : rangé dans les apps.
        }
        return Apps;
    }

    // ---------- Animations ----------

    /// <summary>Les lignes apparaissent l'une après l'autre.</summary>
    private static void Appear(FrameworkElement row, int index)
    {
        var begin = TimeSpan.FromMilliseconds(index * 30);
        row.Opacity = 0;
        row.RenderTransform = new TranslateTransform(-10, 0);
        row.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { BeginTime = begin });
        row.RenderTransform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-10, 0, TimeSpan.FromMilliseconds(180)) { BeginTime = begin, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }

    private static void SlideIn(FrameworkElement panel)
    {
        panel.RenderTransform = new TranslateTransform(-14, 0);
        panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)));
        panel.RenderTransform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-14, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }

    // ---------- Réorganiser la liste (appui long puis glisser) ----------

    private Border? _held;
    private bool _dragging, _holdCancelled;
    private Point _holdStart;
    private System.Windows.Threading.DispatcherTimer? _holdTimer;

    private void BeginHold(Border row, MouseButtonEventArgs e)
    {
        _held = row;
        _dragging = false;
        _holdCancelled = false;
        _holdStart = e.GetPosition(_listRows);
        row.CaptureMouse();
        e.Handled = true;

        row.BorderBrush = HoldBrush;
        row.BorderThickness = new Thickness(2);
        _holdTimer?.Stop();
        _holdTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _holdTimer.Tick += (_, _) =>
        {
            _holdTimer!.Stop();
            if (_held != row || _holdCancelled) return;
            _dragging = true;
            row.Opacity = 0.85;
            Panel.SetZIndex(row, 10);
            row.RenderTransform = new TranslateTransform();
        };
        _holdTimer.Start();
    }

    private void OnHoldMove(Border row, MouseEventArgs e)
    {
        if (_held != row) return;
        var p = e.GetPosition(_listRows);
        if (!_dragging)
        {
            if (!_holdCancelled && (p - _holdStart).Length > 8) CancelHoldLook(row);
            return;
        }
        // Case visée calculée sur la hauteur des lignes (pas sur leur position, qui bouge pendant le rangement).
        int count = _listRows.Children.Count;
        int current = _listRows.Children.IndexOf(row);
        double f = p.Y / RowHeight;
        int target = Math.Clamp((int)f, 0, count - 1);
        bool wellInside = Math.Abs(f - Math.Floor(f) - 0.5) < 0.38;
        if (wellInside && target != current && current >= 0 && _listRows.Children[target] is Border { Tag: LauncherApp other })
        {
            var app = (LauncherApp)row.Tag;
            _launcher.Apps.Remove(app);
            _launcher.Apps.Insert(_launcher.Apps.IndexOf(other) + (target > current ? 1 : 0), app);
            _listRows.Children.Remove(row);
            _listRows.Children.Insert(target, row);
            current = target;
        }
        row.RenderTransform = new TranslateTransform(0, p.Y - current * RowHeight - RowHeight / 2);
    }

    private void EndHold(Border row, LauncherApp app)
    {
        if (_held != row) return;
        _holdTimer?.Stop();
        row.ReleaseMouseCapture();
        _held = null;
        if (_dragging)
        {
            _dragging = false;
            row.RenderTransform = null;
            Panel.SetZIndex(row, 0);
            row.Opacity = 1;
            CancelHoldLook(row);
            _c.Settings.Save();
            return;
        }
        bool cancelled = _holdCancelled;
        CancelHoldLook(row);
        if (!cancelled) AskConfirm(row);
    }

    // ---------- Confirmation façon SAO (rond bleu = oui, croix rose = non) ----------

    private static readonly Brush YesBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x2F, 0x7F, 0xD0)));
    private static readonly Brush NoBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xC8, 0x23, 0x6A)));
    private readonly Dictionary<Border, (FrameworkElement Buttons, Action Repaint)> _confirmParts = new();
    private Border? _confirmRow;

    /// <summary>Clic sur un jeu : la ligne passe en orange et propose « oui » / « non » à gauche.</summary>
    private void AskConfirm(Border row)
    {
        if (_confirmRow == row) return;
        CancelConfirm();
        _confirmRow = row;
        if (!_confirmParts.TryGetValue(row, out var parts)) return;
        parts.Buttons.Visibility = Visibility.Visible;
        parts.Buttons.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        parts.Repaint();
    }

    private void CancelConfirm()
    {
        var row = _confirmRow;
        _confirmRow = null;
        if (row == null || !_confirmParts.TryGetValue(row, out var parts)) return;
        parts.Buttons.Visibility = Visibility.Collapsed;
        parts.Repaint();
    }

    private static FrameworkElement ConfirmButton(bool yes, Action click)
    {
        const double size = 26;
        var g = new Grid { Width = size, Height = size, Margin = new Thickness(0, 0, 4, 0), Cursor = Cursors.Hand, ToolTip = yes ? "Lancer" : "Annuler" };
        g.Children.Add(new Ellipse { Fill = yes ? YesBrush : NoBrush });
        g.Children.Add(new Ellipse { Stroke = Brushes.White, StrokeThickness = 1.6, Margin = new Thickness(2) });
        if (yes)
            g.Children.Add(new Ellipse { Stroke = Brushes.White, StrokeThickness = 2.6, Width = 11, Height = 11 });
        else
        {
            g.Children.Add(new Line { X1 = 9, Y1 = 9, X2 = 17, Y2 = 17, Stroke = Brushes.White, StrokeThickness = 2.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
            g.Children.Add(new Line { X1 = 17, Y1 = 9, X2 = 9, Y2 = 17, Stroke = Brushes.White, StrokeThickness = 2.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        }
        g.RenderTransformOrigin = new Point(0.5, 0.5);
        g.RenderTransform = new ScaleTransform(1, 1);
        g.MouseEnter += (_, _) => ((ScaleTransform)g.RenderTransform).ScaleX = ((ScaleTransform)g.RenderTransform).ScaleY = 1.12;
        g.MouseLeave += (_, _) => ((ScaleTransform)g.RenderTransform).ScaleX = ((ScaleTransform)g.RenderTransform).ScaleY = 1;
        // Le clic ne doit pas remonter jusqu'à la ligne (appui long, lancement).
        g.MouseLeftButtonDown += (_, e) => e.Handled = true;
        g.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        return g;
    }

    private void CancelHoldLook(Border row)
    {
        _holdCancelled = true;
        _holdTimer?.Stop();
        row.BorderBrush = RowLine;
        row.BorderThickness = new Thickness(0, 0, 0, 1);
    }

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
