using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpiffoCON.Core.Bridge;
using SpiffoCON.Core.Map;

namespace SpiffoCON.Controls;

/// <summary>
/// Draws a <see cref="MapScene"/> with the players and vehicles of the bridge on it. Wheel zooms around
/// the pointer, dragging pans, a click on a player selects them.
/// </summary>
public sealed class WorldMapView : FrameworkElement
{
    /// <summary>Screen pixels per world square: the most zoomed in.</summary>
    const double MaxScale = 8;
    const double PlayerRadius = 6;

    double _scale = 0.05;
    Point _origin; // world point at the top-left corner
    // until the user moves the map, it keeps showing all of it as the view is resized
    bool _moved;

    public WorldMapView()
    {
        Focusable = true;
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
    }

    // ---- properties ----

    public static readonly DependencyProperty SceneProperty = DependencyProperty.Register(
        nameof(Scene), typeof(MapScene), typeof(WorldMapView), new PropertyMetadata(null, (d, e) => ((WorldMapView)d).OnSceneChanged(e.OldValue is null)));

    public MapScene? Scene
    {
        get => (MapScene?)GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    public static readonly DependencyProperty PlayersProperty = DependencyProperty.Register(
        nameof(Players), typeof(IEnumerable), typeof(WorldMapView), new PropertyMetadata(null, OnCollectionChanged));

    public IEnumerable? Players
    {
        get => (IEnumerable?)GetValue(PlayersProperty);
        set => SetValue(PlayersProperty, value);
    }

    public static readonly DependencyProperty VehiclesProperty = DependencyProperty.Register(
        nameof(Vehicles), typeof(IEnumerable), typeof(WorldMapView), new PropertyMetadata(null, OnCollectionChanged));

    public IEnumerable? Vehicles
    {
        get => (IEnumerable?)GetValue(VehiclesProperty);
        set => SetValue(VehiclesProperty, value);
    }

    /// <summary>The safehouses the bridge reports (v7), drawn as outlined areas.</summary>
    public static readonly DependencyProperty SafehousesProperty = DependencyProperty.Register(
        nameof(Safehouses), typeof(IEnumerable), typeof(WorldMapView), new PropertyMetadata(null, OnCollectionChanged));

    public IEnumerable? Safehouses
    {
        get => (IEnumerable?)GetValue(SafehousesProperty);
        set => SetValue(SafehousesProperty, value);
    }

    public static readonly DependencyProperty ShowSafehousesProperty = DependencyProperty.Register(
        nameof(ShowSafehouses), typeof(bool), typeof(WorldMapView), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool ShowSafehouses
    {
        get => (bool)GetValue(ShowSafehousesProperty);
        set => SetValue(ShowSafehousesProperty, value);
    }

    public static readonly DependencyProperty ShowVehiclesProperty = DependencyProperty.Register(
        nameof(ShowVehicles), typeof(bool), typeof(WorldMapView), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool ShowVehicles
    {
        get => (bool)GetValue(ShowVehiclesProperty);
        set => SetValue(ShowVehiclesProperty, value);
    }

    public static readonly DependencyProperty SatelliteProperty = DependencyProperty.Register(
        nameof(Satellite), typeof(bool), typeof(WorldMapView), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool Satellite
    {
        get => (bool)GetValue(SatelliteProperty);
        set => SetValue(SatelliteProperty, value);
    }

    /// <summary>The selected player's username.</summary>
    public static readonly DependencyProperty SelectedPlayerProperty = DependencyProperty.Register(
        nameof(SelectedPlayer), typeof(string), typeof(WorldMapView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public string? SelectedPlayer
    {
        get => (string?)GetValue(SelectedPlayerProperty);
        set => SetValue(SelectedPlayerProperty, value);
    }

    /// <summary>What is under the pointer: coordinates, and the player or vehicle there.</summary>
    public static readonly DependencyProperty PointerTextProperty = DependencyProperty.Register(
        nameof(PointerText), typeof(string), typeof(WorldMapView), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public string PointerText
    {
        get => (string)GetValue(PointerTextProperty);
        set => SetValue(PointerTextProperty, value);
    }

    /// <summary>The square that was right-clicked (for the context menu).</summary>
    public static readonly DependencyProperty ContextSquareProperty = DependencyProperty.Register(
        nameof(ContextSquare), typeof(Point), typeof(WorldMapView), new FrameworkPropertyMetadata(default(Point), FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public Point ContextSquare
    {
        get => (Point)GetValue(ContextSquareProperty);
        set => SetValue(ContextSquareProperty, value);
    }

    static void OnCollectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (WorldMapView)d;
        if (e.OldValue is INotifyCollectionChanged old)
            old.CollectionChanged -= view.OnItemsChanged;
        if (e.NewValue is INotifyCollectionChanged now)
            now.CollectionChanged += view.OnItemsChanged;
        view.InvalidateVisual();
    }

    void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    void OnSceneChanged(bool first)
    {
        _tiles.Clear();
        _recent.Clear();
        _pending.Clear();
        _labelCache.Clear();
        // the first map shows all of it; a new copy of it (satellite added) keeps the view
        if (first)
            Fit();
        CenterPending();
        InvalidateVisual();
    }

    // ---- view ----

    // never above MaxScale: a scene with nothing drawn has no extent
    double MinScale => Scene is { } s && ActualWidth > 0
        ? Math.Min(Math.Min(ActualWidth / s.WorldBounds.Width, ActualHeight / s.WorldBounds.Height) * 0.8, MaxScale) : 0.01;

    Point ToScreen(double x, double y) => new((x - _origin.X) * _scale, (y - _origin.Y) * _scale);

    Point ToWorld(Point screen) => new(_origin.X + screen.X / _scale, _origin.Y + screen.Y / _scale);

    /// <summary>The whole map in view.</summary>
    public void Fit()
    {
        if (Scene is not { } scene || ActualWidth <= 0 || ActualHeight <= 0)
            return;
        var b = scene.WorldBounds;
        _scale = Math.Min(Math.Min(ActualWidth / b.Width, ActualHeight / b.Height), MaxScale);
        _origin = new Point(b.X + b.Width / 2 - ActualWidth / 2 / _scale, b.Y + b.Height / 2 - ActualHeight / 2 / _scale);
        _moved = false;
        InvalidateVisual();
    }

    /// <summary>Centers on a square, zooming in to at least <paramref name="minScale"/>.</summary>
    public void CenterOn(double x, double y, double minScale = 1)
    {
        if (_holdView)
            return;
        // asked before the map is there (the tab was never shown): done once it is
        if (ActualWidth <= 0 || Scene is null)
        {
            // a later request to follow (no zoom) does not undo the zoom of the one still waiting
            _pendingCenter = (x, y, Math.Max(minScale, _pendingCenter?.MinScale ?? 0), Environment.TickCount64);
            return;
        }
        _pendingCenter = null;
        _scale = Math.Clamp(Math.Max(_scale, minScale), MinScale, MaxScale);
        _origin = new Point(x - ActualWidth / 2 / _scale, y - ActualHeight / 2 / _scale);
        _moved = true;
        InvalidateVisual();
    }

    (double X, double Y, double MinScale, long Asked)? _pendingCenter;

    /// <summary>Set while a right-click selects a player: the map must stay under the menu about to open.</summary>
    bool _holdView;

    void CenterPending()
    {
        if (_pendingCenter is not { } c)
            return;
        // asked a moment ago, while the map was loading: not a click from minutes before a map download
        if (Environment.TickCount64 - c.Asked < 60_000)
            CenterOn(c.X, c.Y, c.MinScale);
        else if (Scene is not null && ActualWidth > 0)
            _pendingCenter = null;
    }

    void ZoomAt(Point screen, double factor)
    {
        var world = ToWorld(screen);
        _scale = Math.Clamp(_scale * factor, MinScale, MaxScale);
        _origin = new Point(world.X - screen.X / _scale, world.Y - screen.Y / _scale);
        // zoomed while dragging: the drag goes on from this view
        if (_dragStart is not null)
        {
            _dragStart = screen;
            _dragOrigin = _origin;
        }
        _moved = true;
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (!_moved)
        {
            Fit();
            CenterPending();
            return;
        }
        // keep the same point in the middle
        var old = sizeInfo.PreviousSize;
        _origin = new Point(_origin.X + (old.Width - ActualWidth) / 2 / _scale, _origin.Y + (old.Height - ActualHeight) / 2 / _scale);
    }

    // ---- input ----

    Point? _dragStart;
    Point _dragOrigin;
    bool _dragged;

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        ZoomAt(e.GetPosition(this), Math.Pow(1.25, e.Delta / 120.0));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        if (e.ClickCount == 2)
        {
            ZoomAt(e.GetPosition(this), 2);
            e.Handled = true;
            return;
        }
        _dragStart = e.GetPosition(this);
        _dragOrigin = _origin;
        _dragged = false;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_dragStart is { } start && IsMouseCaptured)
        {
            var d = p - start;
            if (_dragged || Math.Abs(d.X) + Math.Abs(d.Y) > 4)
            {
                _dragged = true;
                _moved = true;
                _origin = new Point(_dragOrigin.X - d.X / _scale, _dragOrigin.Y - d.Y / _scale);
                InvalidateVisual();
            }
        }
        UpdatePointerText(p);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_dragStart is not null && !_dragged && PlayerAt(e.GetPosition(this)) is { } player)
            SelectedPlayer = player.Username;
        _dragStart = null;
        ReleaseMouseCapture();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        var world = ToWorld(e.GetPosition(this));
        ContextSquare = new Point(Math.Floor(world.X), Math.Floor(world.Y));
        if (PlayerAt(e.GetPosition(this)) is { } player)
        {
            // selected where it is: centering it now would move it from under the menu
            _holdView = true;
            SelectedPlayer = player.Username;
            _holdView = false;
        }
        base.OnMouseRightButtonDown(e);
    }

    protected override void OnMouseLeave(MouseEventArgs e) => PointerText = "";

    void UpdatePointerText(Point screen)
    {
        if (Scene is null)
            return;
        var world = ToWorld(screen);
        var text = $"{Math.Floor(world.X):0}, {Math.Floor(world.Y):0}";
        if (PlayerAt(screen) is { } p)
            text += $" · {Describe(p)}";
        else if (ShowVehicles && VehicleAt(screen) is { } v)
            text += $" · {v.Script} #{v.Id}" + (v.Driver is { } driver ? $", driven by {driver}" : "") + (v.EngineRunning == true ? ", engine on" : "");
        else if (ShowSafehouses && SafehouseAt(world) is { } safehouse)
            text += " · " + ViewModels.BridgeViewModel.Describe(safehouse);
        PointerText = text;
    }

    static string Describe(BridgePlayer p) =>
        p.Username + (p.Dead == true ? " (dead)" : p.Health is { } h ? $" (health {h})" : "")
        + (p.Vehicle is { Length: > 0 } car ? $", in {car}" : "") + (p.Z is { } z and not 0 ? $", floor {z}" : "")
        + (p.ZombiesNear is > 0 and var near ? $", {near} zombie{(near == 1 ? "" : "s")} near" : "");

    /// <summary>The safehouse a square is in.</summary>
    BridgeSafehouse? SafehouseAt(Point world) =>
        Safehouses?.OfType<BridgeSafehouse>().FirstOrDefault(s => s is { X: { } x, Y: { } y, W: { } w, H: { } h }
            && world.X >= x && world.X < x + w && world.Y >= y && world.Y < y + h);

    /// <summary>The player whose marker is at a point of the view (for the right-click menu).</summary>
    public string? PlayerNameAt(Point screen) => PlayerAt(screen)?.Username;

    BridgePlayer? PlayerAt(Point screen) =>
        Players?.OfType<BridgePlayer>()
            .Where(p => p.X is not null && p.Y is not null)
            .Select(p => (p, d: (ToScreen(p.X!.Value + 0.5, p.Y!.Value + 0.5) - screen).Length))
            .Where(t => t.d <= PlayerRadius + 5)
            .OrderBy(t => t.d).Select(t => t.p).FirstOrDefault();

    BridgeVehicle? VehicleAt(Point screen) =>
        Vehicles?.OfType<BridgeVehicle>()
            .Where(v => v.X is not null && v.Y is not null)
            .Select(v => (v, d: (ToScreen(v.X!.Value + 0.5, v.Y!.Value + 0.5) - screen).Length))
            .Where(t => t.d <= 8)
            .OrderBy(t => t.d).Select(t => t.v).FirstOrDefault();

    // ---- drawing ----

    protected override void OnRender(DrawingContext dc)
    {
        var size = new Rect(0, 0, ActualWidth, ActualHeight);
        var scene = Scene;
        bool satellite = Satellite && scene?.Satellite is not null;
        dc.DrawRectangle(scene is null ? Brushes.Transparent : satellite ? SatelliteBackground : MapScene.Paper, null, size);
        if (scene is null)
            return;

        var view = new Rect(ToWorld(new Point(0, 0)), ToWorld(new Point(ActualWidth, ActualHeight)));
        dc.PushTransform(new MatrixTransform(_scale, 0, 0, _scale, -_origin.X * _scale, -_origin.Y * _scale));
        if (satellite)
        {
            DrawTiles(dc, scene.Satellite!, forest: false, view);
            // the base game's image has nothing of the mods: they are drawn as a map there
            if (scene.ModCells is { } modCells)
            {
                dc.DrawGeometry(MapScene.Paper, null, modCells);
                dc.PushClip(modCells);
                DrawVectors(dc, scene, view);
                dc.Pop();
            }
        }
        else
        {
            if (scene.Forest is { } forest)
                DrawTiles(dc, forest, forest: true, view);
            // cells a mod redraws: the base game's forest there is not theirs
            if (scene.ModCells is { } modCells)
                dc.DrawGeometry(MapScene.Paper, null, modCells);
            DrawVectors(dc, scene, view);
        }
        dc.Pop();

        if (ShowSafehouses)
            DrawSafehouses(dc);
        DrawLabels(dc, scene, satellite);
        if (ShowVehicles)
            DrawVehicles(dc);
        DrawPlayers(dc);
    }

    static readonly Brush SatelliteBackground = Frozen(Color.FromRgb(0x2B, 0x33, 0x22));

    // layers from the bottom up, and the zoom (pixels per square) where each starts to show
    static readonly (MapLayer Layer, double MinScale)[] Order =
    [
        (MapLayer.Forest, 0),
        (MapLayer.Water, 0),
        (MapLayer.RoadTrail, 0.2),
        (MapLayer.RoadTertiary, 0.1),
        (MapLayer.RoadSecondary, 0),
        (MapLayer.RoadPrimary, 0),
        (MapLayer.Railway, 0.25),
        (MapLayer.Building, 0.25),
        (MapLayer.BuildingCommunity, 0.25),
        (MapLayer.BuildingHospitality, 0.25),
        (MapLayer.BuildingIndustrial, 0.25),
        (MapLayer.BuildingMedical, 0.25),
        (MapLayer.BuildingRestaurants, 0.25),
        (MapLayer.BuildingRetail, 0.25),
    ];

    void DrawVectors(DrawingContext dc, MapScene scene, Rect view)
    {
        var visible = scene.Blocks.Where(b => b.Bounds.IntersectsWith(view)).ToList();
        // one screen pixel, in squares: keeps thin roads and outlines visible when zoomed out
        double pixel = 1 / _scale;
        foreach (var (layer, minScale) in Order)
        {
            if (_scale < minScale)
                continue;
            var fill = MapScene.Fills[layer];
            bool road = layer is MapLayer.RoadPrimary or MapLayer.RoadSecondary or MapLayer.RoadTertiary or MapLayer.RoadTrail or MapLayer.Railway;
            // roads: visible when zoomed out; forest and water: no hairline where a cell's polygon meets the next one
            var outline = road && _scale < 1 ? Frozen(new Pen(fill, pixel * (layer is MapLayer.RoadPrimary or MapLayer.RoadSecondary ? 1.5 : 1)))
                : layer is MapLayer.Forest or MapLayer.Water && _scale >= 0.3 ? Frozen(new Pen(fill, pixel)) : null;
            var linePen = Frozen(new Pen(fill, Math.Max(2, pixel * 1.5)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
            foreach (var block in visible)
            {
                if (block.Areas.TryGetValue(layer, out var area))
                    dc.DrawGeometry(fill, outline, area);
                if (block.Lines.TryGetValue(layer, out var line))
                    dc.DrawGeometry(null, linePen, line);
            }
        }
    }

    // ---- tiles ----

    readonly Dictionary<(bool Forest, int Level, int X, int Y), BitmapSource?> _tiles = [];
    readonly HashSet<(bool, int, int, int)> _pending = [];
    readonly LinkedList<(bool, int, int, int)> _recent = [];
    const int MaxTiles = 600;

    void DrawTiles(DrawingContext dc, MapPyramid pyramid, bool forest, Rect view)
    {
        // the level whose pixels are closest to (and not smaller than) a screen pixel
        int level = Math.Clamp((int)Math.Floor(Math.Log2(1 / _scale)), 0, pyramid.Levels - 1);
        int squares = MapPyramid.TileSquares(level);
        int x0 = Math.Max(0, (int)Math.Floor((view.Left - pyramid.MinX) / squares));
        int y0 = Math.Max(0, (int)Math.Floor((view.Top - pyramid.MinY) / squares));
        int x1 = (int)Math.Floor((Math.Min(view.Right, pyramid.MaxX) - pyramid.MinX) / squares);
        int y1 = (int)Math.Floor((Math.Min(view.Bottom, pyramid.MaxY) - pyramid.MinY) / squares);
        // a hair of overlap hides the seams between tiles
        double overlap = 0.6 / _scale;
        for (int ty = y0; ty <= y1; ty++)
        {
            for (int tx = x0; tx <= x1; tx++)
            {
                var rect = new Rect(pyramid.MinX + tx * squares, pyramid.MinY + ty * squares, squares, squares);
                var tile = Tile(pyramid, forest, level, tx, ty, out bool ready);
                if (ready)
                {
                    if (tile is not null)
                        dc.DrawImage(tile, Inflate(rect, overlap));
                    continue;
                }
                // not decoded yet: the coarser tile around it, if there is one
                for (int up = level + 1; up < pyramid.Levels; up++)
                {
                    int shift = up - level;
                    if (_tiles.TryGetValue((forest, up, tx >> shift, ty >> shift), out var parent) && parent is not null)
                    {
                        int big = MapPyramid.TileSquares(up);
                        dc.PushClip(new RectangleGeometry(rect));
                        dc.DrawImage(parent, Inflate(new Rect(pyramid.MinX + (tx >> shift) * big, pyramid.MinY + (ty >> shift) * big, big, big), overlap));
                        dc.Pop();
                        break;
                    }
                }
            }
        }
    }

    static Rect Inflate(Rect r, double by)
    {
        r.Inflate(by, by);
        return r;
    }

    BitmapSource? Tile(MapPyramid pyramid, bool forest, int level, int x, int y, out bool ready)
    {
        var key = (forest, level, x, y);
        if (_tiles.TryGetValue(key, out var tile))
        {
            ready = true;
            return tile;
        }
        ready = false;
        if (_pending.Add(key))
        {
            var scene = Scene;
            Task.Run(() => Decode(pyramid.ReadTile(level, x, y), forest)).ContinueWith(t =>
            {
                _pending.Remove(key);
                // a new map meanwhile: this tile belongs to the old one
                if (!ReferenceEquals(scene, Scene))
                    return;
                _tiles[key] = t.IsCompletedSuccessfully ? t.Result : null;
                _recent.AddLast(key);
                while (_recent.Count > MaxTiles)
                {
                    _tiles.Remove(_recent.First!.Value);
                    _recent.RemoveFirst();
                }
                InvalidateVisual();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        return null;
    }

    /// <summary>A PNG tile; forest tiles are a white mask, painted in the game's forest color.</summary>
    static BitmapSource? Decode(byte[]? png, bool forest)
    {
        if (png is null)
            return null;
        var frame = BitmapFrame.Create(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        BitmapSource bitmap = frame;
        if (forest && frame.Palette is { } palette && frame.Format.BitsPerPixel <= 8)
        {
            var colors = palette.Colors.Select(c => c.A > 0 && c.R > 128 ? MapScene.ForestColor : Colors.Transparent).ToList();
            int stride = (frame.PixelWidth * frame.Format.BitsPerPixel + 7) / 8;
            var pixels = new byte[stride * frame.PixelHeight];
            frame.CopyPixels(pixels, stride, 0);
            bitmap = BitmapSource.Create(frame.PixelWidth, frame.PixelHeight, 96, 96, frame.Format, new BitmapPalette(colors), pixels, stride);
        }
        bitmap.Freeze();
        return bitmap;
    }

    // ---- labels and markers ----

    readonly Dictionary<(string, bool), Geometry> _labelCache = [];

    void DrawLabels(DrawingContext dc, MapScene scene, bool satellite)
    {
        var halo = satellite ? LabelHaloDark : LabelHaloLight;
        var fill = satellite ? Brushes.White : LabelText;
        foreach (var label in scene.Labels)
        {
            // towns name the map when zoomed out; smaller places only closer in
            if (!label.Town && _scale < 0.12)
                continue;
            var at = ToScreen(label.X, label.Y);
            if (at.X < -200 || at.Y < -50 || at.X > ActualWidth + 200 || at.Y > ActualHeight + 50)
                continue;
            var key = (label.Text, label.Town);
            if (!_labelCache.TryGetValue(key, out var geometry))
            {
                var text = new FormattedText(label.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, label.Town ? FontWeights.Bold : FontWeights.SemiBold, FontStretches.Normal),
                    label.Town ? 14 : 11.5, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                geometry = text.BuildGeometry(new Point(-text.Width / 2, -text.Height / 2));
                geometry.Freeze();
                _labelCache[key] = geometry;
            }
            dc.PushTransform(new TranslateTransform(Math.Round(at.X), Math.Round(at.Y)));
            dc.DrawGeometry(null, halo, geometry);
            dc.DrawGeometry(fill, null, geometry);
            dc.Pop();
        }
    }

    static readonly Brush LabelText = Frozen(Color.FromRgb(0x3A, 0x34, 0x2C));
    static readonly Pen LabelHaloLight = Frozen(new Pen(Frozen(Color.FromArgb(0xD0, 219, 215, 192)), 3) { LineJoin = PenLineJoin.Round });
    static readonly Pen LabelHaloDark = Frozen(new Pen(Frozen(Color.FromArgb(0xC0, 0, 0, 0)), 3) { LineJoin = PenLineJoin.Round });

    static readonly Brush VehicleFill = Frozen(Color.FromRgb(0x4F, 0xA3, 0xE0));
    static readonly Brush VehicleDriven = Frozen(Color.FromRgb(0x2E, 0xCC, 0x71));
    static readonly Pen MarkerOutline = Frozen(new Pen(Frozen(Color.FromRgb(0x1A, 0x1A, 0x1A)), 1.5));

    static readonly Brush SafehouseFill = Frozen(Color.FromArgb(0x38, 0x2E, 0xCC, 0x71));
    static readonly Pen SafehouseOutline = Frozen(new Pen(Frozen(Color.FromRgb(0x1E, 0x9E, 0x55)), 2));

    void DrawSafehouses(DrawingContext dc)
    {
        if (Safehouses is null)
            return;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var s in Safehouses.OfType<BridgeSafehouse>())
        {
            if (s is not { X: { } x, Y: { } y, W: { } w, H: { } h })
                continue;
            var rect = new Rect(ToScreen(x, y), ToScreen(x + w, y + h));
            if (rect.Right < 0 || rect.Bottom < 0 || rect.Left > ActualWidth || rect.Top > ActualHeight)
                continue;
            // far out it is a dot: kept visible
            if (rect.Width < 8 || rect.Height < 8)
                rect.Inflate(Math.Max(0, (8 - rect.Width) / 2), Math.Max(0, (8 - rect.Height) / 2));
            dc.DrawRectangle(SafehouseFill, SafehouseOutline, rect);
            // whose it is, once there is room for it
            if (rect.Width < 60 || string.IsNullOrEmpty(s.Owner))
                continue;
            var text = new FormattedText(s.Owner, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.White, dip)
            {
                MaxTextWidth = Math.Max(1, rect.Width - 6), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis,
            };
            var box = new Rect(Math.Round(rect.Left + 2), Math.Round(rect.Top + 2), text.Width + 6, text.Height + 2);
            dc.DrawRoundedRectangle(NameBackground, null, box, 3, 3);
            dc.DrawText(text, new Point(box.X + 3, box.Y + 1));
        }
    }

    void DrawVehicles(DrawingContext dc)
    {
        if (Vehicles is null)
            return;
        foreach (var v in Vehicles.OfType<BridgeVehicle>())
        {
            if (v.X is not { } x || v.Y is not { } y)
                continue;
            var at = ToScreen(x + 0.5, y + 0.5);
            if (!OnScreen(at))
                continue;
            dc.DrawRoundedRectangle(v.Driver is null ? VehicleFill : VehicleDriven, MarkerOutline, new Rect(at.X - 5, at.Y - 3.5, 10, 7), 2, 2);
        }
    }

    static readonly Brush PlayerFill = Frozen(Color.FromRgb(0xF2, 0xC1, 0x4E));
    static readonly Brush PlayerSelected = Frozen(Color.FromRgb(0x29, 0xB6, 0xF6));
    static readonly Brush PlayerDead = Frozen(Color.FromRgb(0x8A, 0x8A, 0x8A));
    static readonly Brush NameBackground = Frozen(Color.FromArgb(0xC8, 0x20, 0x20, 0x20));
    static readonly Pen SelectedRing = Frozen(new Pen(Frozen(Color.FromRgb(0x29, 0xB6, 0xF6)), 2));

    void DrawPlayers(DrawingContext dc)
    {
        if (Players is null)
            return;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        // the selected one last, on top of the others
        foreach (var p in Players.OfType<BridgePlayer>().OrderBy(p => p.Username == SelectedPlayer))
        {
            if (p.X is not { } x || p.Y is not { } y)
                continue;
            var at = ToScreen(x + 0.5, y + 0.5);
            if (!OnScreen(at))
                continue;
            bool selected = p.Username == SelectedPlayer;
            if (selected)
                dc.DrawEllipse(null, SelectedRing, at, PlayerRadius + 5, PlayerRadius + 5);
            dc.DrawEllipse(p.Dead == true ? PlayerDead : selected ? PlayerSelected : PlayerFill, MarkerOutline, at, PlayerRadius, PlayerRadius);

            var name = p.Username + (p.Z is { } z and not 0 ? $" · floor {z}" : "");
            var text = new FormattedText(name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 12, Brushes.White, dip);
            var box = new Rect(Math.Round(at.X + PlayerRadius + 4), Math.Round(at.Y - text.Height / 2 - 1), text.Width + 8, text.Height + 2);
            dc.DrawRoundedRectangle(NameBackground, null, box, 3, 3);
            dc.DrawText(text, new Point(box.X + 4, box.Y + 1));
        }
    }

    bool OnScreen(Point p) => p.X > -150 && p.Y > -30 && p.X < ActualWidth + 30 && p.Y < ActualHeight + 30;

    static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    static SolidColorBrush Frozen(Color color) => Frozen(new SolidColorBrush(color));
}
