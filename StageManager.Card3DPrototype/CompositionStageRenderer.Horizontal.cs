using System.Numerics;
using Windows.UI.Composition;

namespace StageManager.Card3DPrototype;

internal sealed partial class CompositionStageRenderer
{
    private readonly Dictionary<string, HorizontalContainer> _horizontalCards = new();
    private SidebarScrollRange _horizontalRange;
    private readonly HorizontalEdgeScrollController _horizontalEdgeScroll = new();
    private bool _edgeScrollBlocked;
    public void StopEdgeScroll()
    {
        if (_horizontalEdgeScroll.Velocity != 0)
        {
            _horizontalEdgeScroll.Stop(); _scrollMotion.FreezeAtCurrent(); _scrollTimer.Stop();
        }
        _edgeScrollBlocked = true;
    }
    public void UpdateEdgeScroll(Point pointer, bool blocked)
    {
        _edgeScrollBlocked = blocked;
        var wasActive = _horizontalEdgeScroll.Velocity != 0;
        var velocity = _horizontalEdgeScroll.Update(pointer, InteractionBounds, _dpiScale, _horizontalRange,
            Horizontal && _sidebarVisible && !_disposed && !blocked);
        if (velocity < 0 && _scrollMotion.Position <= _horizontalRange.Minimum ||
            velocity > 0 && _scrollMotion.Position >= _horizontalRange.Maximum) _horizontalEdgeScroll.Stop();
        if (_horizontalEdgeScroll.Velocity != 0)
        {
            if (!wasActive) _scrollMotion.FreezeAtCurrent();
            if (!_scrollTimer.Enabled) { _lastScrollTick = System.Diagnostics.Stopwatch.GetTimestamp(); _scrollTimer.Start(); }
        }
        else if (wasActive) { _scrollMotion.FreezeAtCurrent(); _scrollTimer.Stop(); }
    }

    private void DetachHorizontalChildren()
    {
        foreach (var card in _horizontalCards.Values) card.DetachChildren();
    }
    private void ClearHorizontalCards()
    {
        SetTopHoveredCard(null);
        DetachHorizontalChildren();
        foreach (var card in _horizontalCards.Values) card.Dispose();
        _horizontalCards.Clear();
        _toolbarLayoutKey = new(-1);
    }

    private void LayoutHorizontalCards()
    {
        ClearTopDecorations();
        DetachHorizontalChildren();
        _layoutMotionTimer.Stop();
        if (_deferredCapture is { } deferred)
        {
            _deferredCapture = null;
            deferred.Card.MarkCaptureFailed(deferred.Revision);
            deferred.Frame?.Dispose();
        }
        _hitTargets.Clear(); _passivePolygons.Clear(); _stageScrollBounds.Clear();
        TopPanelBounds = Rectangle.Empty;
        _fixedCamera.IsVisible = false;
        _topHeaders.Clear(); _topVisibleGroups.Clear();
        _expandAllButton.SetVisible(false);
        _explorerButton.SetVisible(false);
        foreach (var button in _quickLaunchButtons) button.SetVisible(false);
        _desktopButton.SetVisible(false); _desktopIconsButton.SetVisible(false); _collapseButton.SetVisible(false);
        var used = new HashSet<string>();
        var center = new Vector2(_viewportWidth / 2, _viewportHeight / 2);
        var x = 12 * _dpiScale;
        var stride = CardSize.X + Gap;
        string T(string en, string zh) => UiText.Get(_topLanguage, en, zh);

        HorizontalContainer Container(string key, ContainerVisual parent, Vector3 local, Vector3 global, string title, int rows, bool light)
        {
            used.Add(key);
            if (!_horizontalCards.TryGetValue(key, out var container))
                _horizontalCards[key] = container = new HorizontalContainer(_compositor, _graphics);
            container.Layout(parent, local, CardSize, _dpiScale, title, rows, light);
            _passivePolygons.Add(Polygon(global, new RectangleF(0, 0, CardSize.X, CardSize.Y)));
            return container;
        }
        Vector2[] Polygon(Vector3 origin, RectangleF row) => Card3DGeometry.ProjectCardOverlay(
            origin, 1, Vector3.Zero, Vector3.One, -4, CardSize, new(CardSize.X * .88f, CardSize.Y / 2),
            new(row.X, row.Y, 0), new(row.Width, row.Height), center, PerspectiveDistance * _dpiScale);

        var shortcuts = new List<HorizontalTool>();
        if (_explorerButtonEnabled) shortcuts.Add(new(_explorerButton.Root, T("Files", "文件"),
            h => _explorerButton.SetLayout(_dpiScale, CardSize.X - 8 * _dpiScale, h),
            new("__explorer__", null, Array.Empty<Vector2>(), int.MaxValue - 2, IsExplorerButton: true)));
        foreach (var button in _quickLaunchButtons)
            shortcuts.Add(new(button.Root, button.DisplayName,
                h => button.SetLayout(_dpiScale, CardSize.X - 8 * _dpiScale, h),
                new("__quick_" + button.Definition.Id, null, Array.Empty<Vector2>(), int.MaxValue - 2, QuickLaunchApp: button.Definition)));

        void Tools(string prefix, List<HorizontalTool> tools)
        {
            for (var page = 0; page < HorizontalCardLayout.PageCount(tools.Count); page++)
            {
                var items = tools.Skip(page * 3).Take(3).ToArray();
                var position = DockPosition(x, CardSize);
                var container = Container(prefix + page, _mainCamera, position, position, "", items.Length, false);
                var rows = HorizontalCardLayout.Rows(CardSize, items.Length, _dpiScale, false);
                for (var i = 0; i < items.Length; i++)
                {
                    var item = items[i]; var row = rows[i];
                    item.Layout(Math.Max(36 * _dpiScale, row.Height - 16 * _dpiScale));
                    container.Attach(item.Root, _mainCamera);
                    item.Root.Offset = new(row.X, row.Y, 0);
                    item.Root.RotationAngleInDegrees = 0; item.Root.Scale = Vector3.One; item.Root.IsVisible = true;
                    container.Label(i, item.Title, new(row.X, row.Bottom - 16 * _dpiScale, row.Width, 16 * _dpiScale), _dpiScale);
                    _hitTargets.Add(item.Target with { Polygon = Polygon(position, row) });
                }
                x += stride;
            }
        }
        Tools("shortcut:", shortcuts);
        _aiUsageCard.SetLayout(_dpiScale, CardSize.X, CardSize.Y);
        if (_aiUsageCardEnabled) { LayoutAiUsageCard(x); x += stride; } else _aiUsageCard.SetVisible(false);
        foreach (var snapshot in _snapshots)
        {
            if (!_stages.TryGetValue(snapshot.Key, out var stage)) continue;
            stage.DetachTopPanel(); stage.SetPaginationVisible(false); stage.HideExpandedConnector(); stage.PinButton.SetVisible(false); stage.HideGroupCard();
            var start = DockPosition(x, CardSize);
            stage.Motion.Set(start, Vector3.One, 0, false);
            stage.Root.IsVisible = true;
            var pages = HorizontalCardLayout.PageCount(stage.Windows.Count);
            for (var page = 0; page < pages; page++)
            {
                var children = stage.Windows.Skip(page * 3).Take(3).ToArray();
                var local = new Vector3(page * stride, 0, 0);
                var global = start + local;
                var name = children[0].Window.ProcessName;
                var title = pages > 1 ? $"{name} {page + 1}/{pages}" : $"{name} · {children.Length}";
                var container = Container("window:" + stage.Key + ":" + page, stage.Root, local, global, title, children.Length, true);
                var rows = HorizontalCardLayout.Rows(CardSize, children.Length, _dpiScale, true);
                for (var i = 0; i < children.Length; i++)
                {
                    var child = children[i]; var row = rows[i];
                    child.ConfigureHorizontalRow(new(row.Width, row.Height), _dpiScale);
                    container.Attach(child.Root, stage.Root);
                    child.SetVisible(true); child.DesiredBadge = null;
                    child.SetTransform(new(row.X, row.Y, 0), Vector3.One, 0, false);
                    _hitTargets.Add(new(stage.Key, child.Window, Polygon(global, row), 1000 + page * 3 + i));
                }
            }
            _stageScrollBounds.Add((stage, x, Math.Max(0, pages * stride - Gap)));
            x += pages * stride;
        }
        _notificationAreaCard.SetLayout(_dpiScale, CardSize.X, CardSize.Y);
        if (_notificationAreaCardEnabled) { LayoutNotificationAreaCard(x); x += stride; } else _notificationAreaCard.SetVisible(false);
        var actions = new List<HorizontalTool>();
        if (_desktopButtonEnabled) actions.Add(new(_desktopButton.Root, T("Desktop", "显示桌面"),
            h => _desktopButton.SetLayout(_dpiScale, CardSize.X - 8 * _dpiScale, h), new("__desktop__", null, Array.Empty<Vector2>(), int.MaxValue - 2, IsDesktopButton: true)));
        if (_desktopIconsButtonEnabled) actions.Add(new(_desktopIconsButton.Root, T("Desktop icons", "桌面图标"),
            h => _desktopIconsButton.SetLayout(_dpiScale, CardSize.X - 8 * _dpiScale, h), new("__desktop_icons__", null, Array.Empty<Vector2>(), int.MaxValue - 2, IsDesktopIconsButton: true)));
        if (_collapseButtonEnabled) actions.Add(new(_collapseButton.Root, T("Hide", "收起"),
            h => _collapseButton.SetLayout(_dpiScale, CardSize.X - 8 * _dpiScale, h), new("__collapse__", null, Array.Empty<Vector2>(), int.MaxValue - 2, IsSidebarCollapseButton: true)));
        _collapseButton.SetDirection(_dockEdge);
        Tools("actions:", actions);
        _sidebarPinButton.SetLayout(_dpiScale, -4, _sidebarPinned, CardSize.X, CardSize.Y);
        if (_sidebarPinButtonEnabled) { LayoutSidebarPinButton(x); x += stride; } else _sidebarPinButton.SetVisible(false);
        foreach (var key in _horizontalCards.Keys.Where(k => !used.Contains(k)).ToArray())
        { _horizontalCards[key].Dispose(); _horizontalCards.Remove(key); }
        _horizontalRange = new(0, Math.Max(0, x - Gap + 12 * _dpiScale - _viewportWidth));
        _scrollMotion.SetRange(_horizontalRange, _dpiScale);
        _toolbarLayoutKey = new(-1); // Vertical controls must be restored on edge changes.
        _hitTargets.Sort((a, b) => b.ZOrder.CompareTo(a.ZOrder));
        ApplyScrollTranslation(); TrimWarmPreviews(); LayoutRevision++;
    }

    private sealed record HorizontalTool(ContainerVisual Root, string Title, Action<float> Layout, CardHitTarget Target);

    private sealed class HorizontalContainer : IDisposable
    {
        private readonly Compositor _compositor;
        private readonly D3DCompositionDevice _graphics;
        private readonly SpriteVisual _background;
        private readonly CompositionColorBrush _brush;
        private readonly CompositionRoundedRectangleGeometry _geometry;
        private readonly CompositionGeometricClip _clip;
        private readonly TopCardChrome _chrome;
        private readonly TopPanelLabel _header;
        private readonly List<TopPanelLabel> _labels = new();
        private readonly List<SpriteVisual> _lines = new();
        private readonly List<(ContainerVisual Visual, ContainerVisual Home)> _children = new();
        internal ContainerVisual Root { get; }
        internal HorizontalContainer(Compositor compositor, D3DCompositionDevice graphics)
        {
            _compositor = compositor; _graphics = graphics;
            Root = compositor.CreateContainerVisual();
            _geometry = compositor.CreateRoundedRectangleGeometry(); _clip = compositor.CreateGeometricClip(_geometry);
            _brush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(210, 25, 32, 44));
            _background = compositor.CreateSpriteVisual(); _background.Brush = _brush; _background.Clip = _clip;
            Root.Children.InsertAtBottom(_background);
            _chrome = new TopCardChrome(compositor, Root);
            _header = new TopPanelLabel(compositor, graphics); _header.Root.Clip = _clip; Root.Children.InsertAtTop(_header.Root);
        }
        internal void Layout(ContainerVisual parent, Vector3 position, Vector2 size, float dpi, string title, int count, bool light)
        {
            if (Root.Parent != parent) { Root.Parent?.Children.Remove(Root); parent.Children.InsertAtTop(Root); }
            Root.Size = size; Root.Offset = position; Root.CenterPoint = new(size.X * .88f, size.Y / 2, 0);
            Root.RotationAxis = Vector3.UnitY; Root.RotationAngleInDegrees = -4;
            _background.Size = size; _geometry.Size = size; _geometry.CornerRadius = new(8 * dpi);
            _brush.Color = light ? Windows.UI.Color.FromArgb(242, 226, 232, 240) : Windows.UI.Color.FromArgb(175, 25, 32, 44);
            _chrome.Layout(dpi);
            _header.Root.IsVisible = title.Length > 0;
            if (title.Length > 0) { _header.Set(title, new(size.X, 22 * dpi), dpi, true, lightSurface: light); _header.Root.Offset = Vector3.Zero; }
            foreach (var label in _labels) label.Root.IsVisible = false;
            var rows = HorizontalCardLayout.Rows(size, count, dpi, title.Length > 0);
            while (_lines.Count < count - 1)
            {
                var line = _compositor.CreateSpriteVisual(); line.Brush = _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(60, 130, 146, 165));
                Root.Children.InsertAtTop(line); _lines.Add(line);
            }
            for (var i = 0; i < _lines.Count; i++)
            {
                var line = _lines[i]; line.IsVisible = i < count - 1;
                if (line.IsVisible) { line.Size = new(size.X - 8 * dpi, Math.Max(1, dpi)); line.Offset = new(4 * dpi, rows[i].Bottom + dpi, 0); }
            }
        }
        internal void Label(int index, string title, RectangleF bounds, float dpi)
        {
            while (_labels.Count <= index) { var label = new TopPanelLabel(_compositor, _graphics); _labels.Add(label); Root.Children.InsertAtTop(label.Root); }
            var item = _labels[index]; item.Set(title, new(bounds.Width, bounds.Height), dpi, true);
            item.Root.Offset = new(bounds.X, bounds.Y, 0); item.Root.IsVisible = true;
        }
        internal void Attach(ContainerVisual visual, ContainerVisual home)
        { visual.Parent?.Children.Remove(visual); Root.Children.InsertAtTop(visual); _children.Add((visual, home)); }
        internal void DetachChildren()
        {
            foreach (var (visual, home) in _children) { visual.Parent?.Children.Remove(visual); home.Children.InsertAtTop(visual); }
            _children.Clear();
            // Stage disposal also closes its descendant visuals. Detach the reusable page first.
            Root.Parent?.Children.Remove(Root);
        }
        public void Dispose()
        {
            DetachChildren(); Root.Parent?.Children.Remove(Root);
            foreach (var line in _lines) { line.Brush?.Dispose(); line.Dispose(); }
            foreach (var label in _labels) label.Dispose();
            _chrome.Dispose(); _header.Dispose(); _background.Dispose(); _brush.Dispose(); _clip.Dispose(); _geometry.Dispose(); Root.Dispose();
        }
    }
}
