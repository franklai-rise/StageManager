using System.Numerics;
using StageManager.Settings;
using Windows.UI.Composition;

namespace StageManager.Card3DPrototype;

internal sealed partial class CompositionStageRenderer
{
    private readonly Dictionary<string, long> _topPinOrder = new(StringComparer.OrdinalIgnoreCase);
    private long _topPinSequence;
    private float _topPanelScroll, _topPanelScrollMax;
    private readonly HashSet<string> _topVisibleGroups = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _topLastInside = DateTime.UtcNow;
    private readonly List<IReadOnlyList<Vector2>> _topHeaders = new();
    private readonly Dictionary<ContainerVisual,TopPanelLabel> _topUtilityLabels = new();
    private readonly Dictionary<ContainerVisual,TopCardChrome> _topChrome = new();
    private readonly List<SpriteVisual> _topSeparators = new();
    private UiLanguage _topLanguage;
    private WindowCardVisual? _topHoveredCard;
    private void SetTopHoveredCard(CardHitTarget? target)
    {
        WindowCardVisual? next=null;
        if(target is not null && _stages.TryGetValue(target.StageKey,out var stage))
            next=target.Window is null ? stage.GroupCard : stage.Windows.FirstOrDefault(c=>c.Window.Handle==target.Window.Handle);
        if(next==_topHoveredCard) return;
        _topHoveredCard?.SetTopHovered(false);_topHoveredCard=next;next?.SetTopHovered(true);
    }
    public Rectangle TopPanelBounds { get; private set; }
    public Vector2 TargetTranslation(CardHitTarget target) => target.IsTopPanel
        ? new Vector2(0, -_topPanelScroll) : new(ScrollTranslationX, ScrollTranslationY);
    private static Vector2[] RectanglePolygon(RectangleF r) => new[] { new Vector2(r.Left,r.Top),new Vector2(r.Right,r.Top),new Vector2(r.Right,r.Bottom),new Vector2(r.Left,r.Bottom) };

    private void LayoutTopPrimary(StageCardVisual stage, Vector3 position, Vector2 center, int index)
    {
        stage.SetPaginationVisible(false);
        stage.HideExpandedConnector();
        stage.PinButton.SetVisible(false);
        var grouped = stage.GroupCard is not null;
        foreach (var child in stage.Windows) child.SetVisible(false);
        stage.GroupCard?.SetVisible(grouped);
        var card = grouped ? stage.GroupCard! : stage.Windows.FirstOrDefault();
        if (card is null) return;
        card.SetVisible(true);
        card.SetTransform(Vector3.Zero, Vector3.One, -4, false);
        card.DesiredBadge = grouped ? stage.Windows.Count.ToString() : null;
        card.SetTopCaption(grouped ? $"{card.Window.ProcessName} · {stage.Windows.Count}" : $"{card.Window.ProcessName}\n{card.Window.Title}", _dpiScale);
        var polygon = Card3DGeometry.ProjectCard(position, 1, Vector3.Zero, Vector3.One, -4, CardSize, card.Pivot, center, PerspectiveDistance * _dpiScale);
        _hitTargets.Add(new(stage.Key, grouped ? null : card.Window, polygon, index * 20 + 20, IsPrimaryCard:true, Projection:ProjectLive(stage,card)));
    }

    private void LayoutTopDecorations()
    {
        string T(string en,string zh) => UiText.Get(_topLanguage,en,zh);
        void Caption(ContainerVisual visual,string text)
        {
            if(!_topChrome.TryGetValue(visual,out var chrome)) { chrome=new TopCardChrome(_compositor,visual);_topChrome[visual]=chrome; }
            chrome.Layout(_dpiScale);
            if (!_topUtilityLabels.TryGetValue(visual,out var label))
            { label=new TopPanelLabel(_compositor,_graphics);_topUtilityLabels[visual]=label;visual.Children.InsertAtTop(label.Root); }
            var height=22*_dpiScale;
            label.Set(text,new(visual.Size.X,height),_dpiScale,true);
            label.Root.Offset=new(0,Math.Min(visual.Size.Y-height-8*_dpiScale,visual.Size.Y/2+20*_dpiScale),1);
        }
        Caption(_explorerButton.Root,T("Files","文件"));
        Caption(_expandAllButton.Root,T("Expand all","全部展开"));
        foreach(var button in _quickLaunchButtons) Caption(button.Root,button.DisplayName);
        Caption(_desktopButton.Root,T("Desktop","显示桌面"));
        Caption(_desktopIconsButton.Root,T("Desktop icons","桌面图标"));
        Caption(_sidebarPinButton.Root,T("Keep visible","保持显示"));
        Caption(_collapseButton.Root,T("Hide","收起"));
        while(_topSeparators.Count<2)
        {
            var line=_compositor.CreateSpriteVisual();line.Brush=_compositor.CreateColorBrush(Windows.UI.Color.FromArgb(65,225,235,250));
            _mainCamera.Children.InsertAtTop(line);_topSeparators.Add(line);
        }
        var mains=_hitTargets.Where(t=>t.IsPrimaryCard).ToArray();
        for(var i=0;i<2;i++)
        {
            var line=_topSeparators[i];line.IsVisible=mains.Length>0;
            if(mains.Length==0) continue;
            var boundary=i==0?mains.Min(t=>t.Polygon.Min(p=>p.X))-13*_dpiScale:mains.Max(t=>t.Polygon.Max(p=>p.X))+13*_dpiScale;
            line.Size=new(Math.Max(1,_dpiScale),CardSize.Y*.65f);
            line.Offset=new(boundary,12*_dpiScale+CardSize.Y*.175f,0);
            _passivePolygons.Add(RectanglePolygon(new(boundary,line.Offset.Y,line.Size.X,line.Size.Y)));
        }
    }
    private void ClearTopDecorations()
    {
        foreach(var chrome in _topChrome.Values) chrome.Dispose();_topChrome.Clear();
        foreach(var pair in _topUtilityLabels) { pair.Key.Children.Remove(pair.Value.Root);pair.Value.Dispose(); }
        _topUtilityLabels.Clear();
        foreach(var line in _topSeparators) { _mainCamera.Children.Remove(line);line.Brush?.Dispose();line.Dispose(); }
        _topSeparators.Clear();
    }

    private void LayoutTopPanel(bool animate, Vector2 center)
    {
        _topHeaders.Clear();
        var groups = _snapshots.Where(s => _stages.TryGetValue(s.Key,out var g) && g.Windows.Count > 0 &&
            (g.Windows.Count > 1 || IsStagePinned(s.Key)) && IsStageExpanded(s.Key))
            .OrderBy(s => IsStagePinned(s.Key) ? 0 : 1)
            .ThenBy(s => IsStagePinned(s.Key) ? _topPinOrder.GetValueOrDefault(s.Key) : long.MaxValue).ToArray();
        foreach (var key in _topPinOrder.Keys.Where(k => !IsStagePinned(k)).ToArray()) _topPinOrder.Remove(key);
        if (groups.Length == 0)
        {
            _topVisibleGroups.Clear(); _topPanelScroll = _topPanelScrollMax = 0;
            _fixedCamera.IsVisible = false; return;
        }
        var layout = TopPanelLayout.Calculate(new(_viewportWidth,_viewportHeight),CardSize,_dpiScale,0);
        var contentHeight = groups.Sum(s => layout.GroupHeight(_stages[s.Key].Windows.Count,CardSize,_dpiScale));
        layout = TopPanelLayout.Calculate(new(_viewportWidth,_viewportHeight),CardSize,_dpiScale,contentHeight);
        TopPanelBounds = Rectangle.Ceiling(new RectangleF(layout.Left,layout.Top,layout.Width,layout.Height));
        _topPanelScrollMax = Math.Max(0,contentHeight-layout.Height);
        _topPanelScroll = Math.Clamp(_topPanelScroll,0,_topPanelScrollMax);
        var groupTop = layout.Top;
        foreach (var snapshot in groups)
        {
            var stage = _stages[snapshot.Key];
            var reveal = !_topVisibleGroups.Contains(stage.Key);
            stage.AttachTopPanel(_fixedCamera);
            stage.TopPanelMotion!.Set(Vector3.Zero,Vector3.One,0,false);
            stage.TopLabel!.Set($"{stage.Windows[0].Window.ProcessName}   ·   {stage.Windows.Count}", new(layout.Width,32*_dpiScale),_dpiScale);
            stage.TopLabel.Root.Offset = new(layout.Left,groupTop,0);
            _topHeaders.Add(RectanglePolygon(new(layout.Left,groupTop,layout.Width,32*_dpiScale)));
            stage.PinButton.SetLayout(_dpiScale,0,IsStagePinned(stage.Key));
            var pinOffset = new Vector3(layout.Left+layout.Width-stage.PinButton.Size.X,groupTop,0);
            stage.PinButton.SetOffset(pinOffset);
            stage.PinButton.SetHeaderVisible(_pinButtonEnabled);
            if (_pinButtonEnabled)
                _hitTargets.Add(new(stage.Key,null,RectanglePolygon(new(pinOffset.X,pinOffset.Y,stage.PinButton.Size.X,stage.PinButton.Size.Y)),50000,IsPinButton:true,IsTopPanel:true));
            for (var i=0;i<stage.Windows.Count;i++)
            {
                var card = stage.Windows[i];
                if (reveal) card.SetVisible(false);
                card.SetVisible(true);
                card.SetTopCaption($"{card.Window.ProcessName}\n{card.Window.Title}",_dpiScale);
                var offset = layout.Child(i,groupTop,_dpiScale);
                card.SetTransform(offset,Vector3.One,-4,animate && reveal,
                    TimeSpan.FromMilliseconds(Math.Min(i*25,100)),TimeSpan.FromMilliseconds(220),offset-new Vector3(0,8*_dpiScale,0),smoothMotion:true);
                card.DesiredBadge = null;
                var projection = new CardHitProjection(stage.TopPanelMotion!,card.Motion,CardSize,card.Pivot,center,PerspectiveDistance*_dpiScale);
                var polygon = Card3DGeometry.ProjectCard(Vector3.Zero,1,offset,Vector3.One,-4,CardSize,card.Pivot,center,PerspectiveDistance*_dpiScale);
                _hitTargets.Add(new(stage.Key,card.Window,polygon,30000+i,Projection:projection,IsTopPanel:true));
            }
            groupTop += layout.GroupHeight(stage.Windows.Count,CardSize,_dpiScale);
        }
        _topVisibleGroups.Clear(); foreach (var group in groups) _topVisibleGroups.Add(group.Key);
        ApplyTopPanelScroll();
    }

    private void ApplyTopPanelScroll()
    {
        _fixedCamera.Size = new(_viewportWidth,_viewportHeight+_topPanelScrollMax);
        _fixedCamera.Offset = new(0,-_topPanelScroll,0);
        var previous = _fixedCamera.Clip;
        _fixedCamera.Clip = _compositor.CreateInsetClip(TopPanelBounds.Left,TopPanelBounds.Top+_topPanelScroll,
            Math.Max(0,_viewportWidth-TopPanelBounds.Right),Math.Max(0,_fixedCamera.Size.Y-TopPanelBounds.Bottom-_topPanelScroll));
        previous?.Dispose();
    }

    private bool KeepTopPanelOpen(Point point)
    {
        if (TopPanelBounds.IsEmpty) return false;
        var hit = HitTest(point);
        var corridor = new Rectangle(TopPanelBounds.Left,(int)(12*_dpiScale+CardSize.Y),TopPanelBounds.Width,(int)(12*_dpiScale));
        if (TopPanelBounds.Contains(point) || corridor.Contains(point) || hit?.StageKey == _expandedStageKey)
        { _topLastInside = DateTime.UtcNow; return true; }
        if (_expandedStageKey is not null && !_expandedPinned && !_expandAllStages && DateTime.UtcNow-_topLastInside > TimeSpan.FromMilliseconds(750))
            CollapseExpandedStage();
        return false;
    }

    private IReadOnlyList<PointF[]> TopPanelPolygons()
    {
        // Clip transformed polygons to the same viewport as the compositor. Gaps remain click-through.
        var result = new List<PointF[]>();
        IReadOnlyList<Vector2> MotionEnvelope(CardHitTarget target)
        {
            if (target.Projection?.IsMoving != true) return target.Polygon;
            var all = target.Polygon.Concat(target.Projection.CurrentPolygon()).ToArray();
            return RectanglePolygon(RectangleF.FromLTRB(all.Min(p=>p.X),all.Min(p=>p.Y),all.Max(p=>p.X),all.Max(p=>p.Y)));
        }
        foreach (var polygon in _hitTargets.Where(t => t.IsTopPanel).Select(MotionEnvelope).Concat(_topHeaders))
        {
            var points = polygon.Select(p => new Vector2(p.X,p.Y-_topPanelScroll)).ToList();
            for (var edge=0;edge<4 && points.Count>0;edge++)
            {
                var output = new List<Vector2>();
                float Distance(Vector2 p) => edge switch { 0 => p.X-TopPanelBounds.Left, 1 => TopPanelBounds.Right-p.X, 2 => p.Y-TopPanelBounds.Top, _ => TopPanelBounds.Bottom-p.Y };
                var previous = points[^1]; var before=Distance(previous);
                foreach (var current in points)
                {
                    var after=Distance(current);
                    if ((before>=0)!=(after>=0)) output.Add(Vector2.Lerp(previous,current,before/(before-after)));
                    if (after>=0) output.Add(current);
                    previous=current; before=after;
                }
                points=output;
            }
            if(points.Count>=3) result.Add(points.Select(p=>new PointF(p.X,p.Y)).ToArray());
        }
        return result;
    }
}
