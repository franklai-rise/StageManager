using StageManager.Native.Window;
using System.Numerics;
using Windows.UI.Composition;

namespace StageManager.Card3DPrototype;

internal sealed class CompositionStageRenderer : IDisposable
{
	private const float BaseCardWidth = 196f;
	private const float BaseCardHeight = 122f;
	private const float PerspectiveDistance = 1200f;
	private const int PageSize = 6;
	private readonly Control _owner;
	private readonly Compositor _compositor;
	private readonly ContainerVisual _cameraRoot;
	private readonly D3DCompositionDevice _graphics;
	private readonly WindowFrameCapture _capture = new();
	private readonly Dictionary<string, StageCardVisual> _stages = new(StringComparer.OrdinalIgnoreCase);
	private readonly System.Windows.Forms.Timer _captureTimer;
	private readonly object _captureGate = new();
	private readonly HashSet<IntPtr> _capturesInFlight = new();
	private readonly List<CardHitTarget> _hitTargets = new();
	private IReadOnlyList<PrototypeStageSnapshot> _snapshots = Array.Empty<PrototypeStageSnapshot>();
	private string? _expandedStageKey;
	private string? _hoveredStageKey;
	private IntPtr _hoveredWindowHandle;
	private DateTime _lastPointerInsideUtc;
	private float _dpiScale = 1f;
	private float _viewportWidth;
	private float _viewportHeight;
	private float _scrollOffset;
	private float _maximumScroll;
	private int _expandedPage;
	private bool _disposeCaptureWhenIdle;
	private bool _disposed;
	private bool _animationsEnabled;
	private bool _sidebarVisible = true;
	private float _preferenceScale;

	public CompositionStageRenderer(Control owner, Compositor compositor, ContainerVisual cameraRoot, double cardScale, bool animationsEnabled)
	{
		_owner = owner;
		_compositor = compositor;
		_cameraRoot = cameraRoot;
		_preferenceScale = NormalizeCardScale(cardScale);
		_animationsEnabled = animationsEnabled;
		_graphics = new D3DCompositionDevice();
		_captureTimer = new System.Windows.Forms.Timer { Interval = 125 };
		_captureTimer.Tick += (_, _) => ScheduleCaptures();
		_captureTimer.Start();
	}

	public bool HasExpandedStage => _expandedStageKey is not null;
	public double CardScale => _preferenceScale;
	public bool SidebarVisible => _sidebarVisible;
	public float SidebarInteractionWidth => CardSize.X + 48f * _dpiScale;
	public TimeSpan SidebarAnimationDuration => TimeSpan.FromMilliseconds(220);

	public IReadOnlyList<PointF[]> GetInteractivePolygons()
	{
		return _hitTargets
			.Select(target => target.Polygon.Select(point => new PointF(point.X, point.Y)).ToArray())
			.ToArray();
	}

	public void SetAnimationsEnabled(bool enabled) => _animationsEnabled = enabled;

	public void SetSidebarVisible(bool visible, bool animate)
	{
		if (_sidebarVisible == visible && (!visible || Math.Abs(_cameraRoot.Offset.X) < 0.1f))
			return;
		var previous = _cameraRoot.Offset;
		var target = new Vector3(visible ? 0 : HiddenOffsetX, 0, 0);
		_sidebarVisible = visible;
		_cameraRoot.StopAnimation(nameof(Visual.Offset));
		_cameraRoot.Offset = target;
		if (!animate || !_animationsEnabled)
			return;

		using var easing = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 0f), new Vector2(0f, 1f));
		using var animation = _compositor.CreateVector3KeyFrameAnimation();
		animation.Duration = SidebarAnimationDuration;
		animation.InsertKeyFrame(0, previous);
		animation.InsertKeyFrame(1, target, easing);
		_cameraRoot.StartAnimation(nameof(Visual.Offset), animation);
	}

	public void SetCardScale(double cardScale)
	{
		var normalized = NormalizeCardScale(cardScale);
		if (Math.Abs(_preferenceScale - normalized) < 0.001f)
			return;
		_preferenceScale = normalized;
		_hitTargets.Clear();
		foreach (var stage in _stages.Values)
		{
			_cameraRoot.Children.Remove(stage.Root);
			stage.Dispose();
		}
		_stages.Clear();
		Synchronize(_snapshots);
		if (!_sidebarVisible)
			_cameraRoot.Offset = new Vector3(HiddenOffsetX, 0, 0);
	}

	public void Resize(float width, float height, float dpiScale)
	{
		_viewportWidth = Math.Max(1, width);
		_viewportHeight = Math.Max(1, height);
		_dpiScale = Math.Max(0.75f, dpiScale);
		_cameraRoot.Size = new Vector2(_viewportWidth, _viewportHeight);
		_cameraRoot.CenterPoint = new Vector3(_viewportWidth / 2f, _viewportHeight / 2f, 0);
		var perspective = Matrix4x4.Identity;
		perspective.M34 = -1f / (PerspectiveDistance * _dpiScale);
		_cameraRoot.TransformMatrix = perspective;
		LayoutStages(false);
		if (!_sidebarVisible)
			_cameraRoot.Offset = new Vector3(HiddenOffsetX, 0, 0);
	}

	public void Synchronize(IReadOnlyList<PrototypeStageSnapshot> snapshots)
	{
		if (_disposed)
			return;
		_snapshots = snapshots;
		var liveKeys = snapshots.Select(stage => stage.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (var stale in _stages.Keys.Where(key => !liveKeys.Contains(key)).ToArray())
		{
			var stage = _stages[stale];
			_cameraRoot.Children.Remove(stage.Root);
			stage.Dispose();
			_stages.Remove(stale);
		}

		foreach (var snapshot in snapshots)
		{
			if (!_stages.TryGetValue(snapshot.Key, out var stage))
			{
				stage = new StageCardVisual(snapshot.Key, _compositor, _graphics, CardPixelWidth, CardPixelHeight, CardSize);
				_stages[snapshot.Key] = stage;
				_cameraRoot.Children.InsertAtTop(stage.Root);
			}
			stage.Synchronize(snapshot);
		}

		if (_expandedStageKey is not null && !liveKeys.Contains(_expandedStageKey))
		{
			_expandedStageKey = null;
			_hoveredWindowHandle = IntPtr.Zero;
			_expandedPage = 0;
		}
		else if (_expandedStageKey is not null &&
			_stages.TryGetValue(_expandedStageKey, out var expandedStage) &&
			expandedStage.Windows.Count <= 1)
		{
			_expandedStageKey = null;
			_hoveredWindowHandle = IntPtr.Zero;
			_expandedPage = 0;
		}
		if (_hoveredStageKey is not null && !liveKeys.Contains(_hoveredStageKey))
			_hoveredStageKey = null;
		LayoutStages(true);
	}

	public CardHitTarget? HitTest(Point clientPoint)
	{
		var point = new Vector2(clientPoint.X, clientPoint.Y);
		foreach (var target in _hitTargets.OrderByDescending(target => target.ZOrder))
		{
			if (Card3DGeometry.Contains(target.Polygon, point))
				return target;
		}
		return null;
	}

	public void UpdatePointer(Point clientPoint)
	{
		var hit = HitTest(clientPoint);
		if (hit is null)
			return;
		_lastPointerInsideUtc = DateTime.UtcNow;
		if (_expandedStageKey is null ||
			!string.Equals(_expandedStageKey, hit.StageKey, StringComparison.OrdinalIgnoreCase))
		{
			if (!string.Equals(_hoveredStageKey, hit.StageKey, StringComparison.OrdinalIgnoreCase))
			{
				_hoveredStageKey = hit.StageKey;
				LayoutStages(true);
			}
			return;
		}
		_hoveredStageKey = hit.StageKey;
		if (hit.Window is not null && _hoveredWindowHandle != hit.Window.Handle)
		{
			_hoveredWindowHandle = hit.Window.Handle;
			LayoutStages(true);
		}
	}

	public void PollPointer(Point clientPoint)
	{
		if (HitTest(clientPoint) is not null)
		{
			_lastPointerInsideUtc = DateTime.UtcNow;
			return;
		}
		if (DateTime.UtcNow - _lastPointerInsideUtc < TimeSpan.FromMilliseconds(500))
			return;
		if (_expandedStageKey is null)
		{
			if (_hoveredStageKey is not null)
			{
				_hoveredStageKey = null;
				LayoutStages(true);
			}
			return;
		}
		CollapseExpandedStage();
	}

	public IWindow? ActivateAt(Point clientPoint)
	{
		var hit = HitTest(clientPoint);
		if (hit is null)
			return null;
		if (hit.PageDelta != 0)
		{
			ChangeExpandedPage(hit.PageDelta);
			return null;
		}
		if (hit.Window is null)
			return null;
		var isExpandedStage = string.Equals(_expandedStageKey, hit.StageKey, StringComparison.OrdinalIgnoreCase);
		if (_stages.TryGetValue(hit.StageKey, out var stage) &&
			MultiWindowCardInteraction.Decide(stage.Windows.Count, isExpandedStage) == MultiWindowCardClickAction.Expand)
		{
			_expandedStageKey = hit.StageKey;
			_hoveredStageKey = hit.StageKey;
			_expandedPage = 0;
			_hoveredWindowHandle = hit.Window.Handle;
			_lastPointerInsideUtc = DateTime.UtcNow;
			LayoutStages(true);
			return null;
		}
		CollapseExpandedStage();
		return hit.Window;
	}

	public void Scroll(int wheelDelta)
	{
		var stride = CardSize.Y + Gap;
		_scrollOffset = Math.Clamp(_scrollOffset - Math.Sign(wheelDelta) * stride * 2f, 0, _maximumScroll);
		LayoutStages(true);
	}

	public void CollapseExpandedStage()
	{
		if (_expandedStageKey is null)
			return;
		_expandedStageKey = null;
		_hoveredStageKey = null;
		_hoveredWindowHandle = IntPtr.Zero;
		_expandedPage = 0;
		LayoutStages(true);
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_captureTimer.Stop();
		_captureTimer.Dispose();
		foreach (var stage in _stages.Values)
			stage.Dispose();
		_stages.Clear();
		lock (_captureGate)
		{
			if (_capturesInFlight.Count == 0)
				_capture.Dispose();
			else
				_disposeCaptureWhenIdle = true;
		}
		_graphics.Dispose();
	}

	private Vector2 CardSize => new(BaseCardWidth * _preferenceScale * _dpiScale, BaseCardHeight * _preferenceScale * _dpiScale);
	private float Gap => 14f * _dpiScale;
	private float HiddenOffsetX => -(CardSize.X + 48f * _dpiScale);
	private int CardPixelWidth => Math.Max(128, (int)Math.Ceiling(CardSize.X * 2f));
	private int CardPixelHeight => Math.Max(80, (int)Math.Ceiling(CardSize.Y * 2f));

	private static float NormalizeCardScale(double cardScale) => (float)Math.Clamp(cardScale, 0.55, 1.25);

	private void LayoutStages(bool animate)
	{
		if (_disposed || _viewportHeight <= 0)
			return;
		animate &= _animationsEnabled;
		_hitTargets.Clear();
		var cardSize = CardSize;
		var stride = cardSize.Y + Gap;
		var baseTotalHeight = Math.Max(0, _snapshots.Count * stride - Gap);
		var expandedExtraHeight = 0f;
		if (_expandedStageKey is not null && _stages.TryGetValue(_expandedStageKey, out var expandedStage))
			expandedExtraHeight = GetExpandedExtraHeight(expandedStage);
		var totalHeight = baseTotalHeight + expandedExtraHeight;
		var naturalStartY = baseTotalHeight <= _viewportHeight - 24 * _dpiScale
			? (_viewportHeight - baseTotalHeight) / 2f
			: 12 * _dpiScale;
		_maximumScroll = Math.Max(0, naturalStartY + totalHeight - (_viewportHeight - 12 * _dpiScale));
		_scrollOffset = Math.Clamp(_scrollOffset, 0, _maximumScroll);
		var startY = naturalStartY - _scrollOffset;
		var cameraCenter = new Vector2(_viewportWidth / 2f, _viewportHeight / 2f);
		var currentY = startY;

		for (var stageIndex = 0; stageIndex < _snapshots.Count; stageIndex++)
		{
			var snapshot = _snapshots[stageIndex];
			if (!_stages.TryGetValue(snapshot.Key, out var stage))
				continue;
			var isExpanded = string.Equals(snapshot.Key, _expandedStageKey, StringComparison.OrdinalIgnoreCase);
			var stageScale = isExpanded ? 1.008f : 1f;
			var stageOffset = new Vector3(12 * _dpiScale, currentY, isExpanded ? 8 * _dpiScale : 0);
			stage.Root.Offset = stageOffset;
			stage.Root.Scale = new Vector3(stageScale, stageScale, 1);
			var stageVisualHeight = cardSize.Y + (isExpanded ? expandedExtraHeight : 0);
			stage.Root.IsVisible = stageOffset.Y + stageVisualHeight >= -40 && stageOffset.Y <= _viewportHeight + 40;
			if (!stage.Root.IsVisible)
			{
				stage.HideAll();
				currentY += stride + (isExpanded ? expandedExtraHeight : 0);
				continue;
			}

			if (isExpanded)
				LayoutExpandedStage(stage, stageOffset, stageScale, cameraCenter, animate);
			else
				LayoutCollapsedStage(stage, stageOffset, stageScale, cameraCenter, animate, stageIndex);
			currentY += stride + (isExpanded ? expandedExtraHeight : 0);
		}
	}

	private float GetExpandedExtraHeight(StageCardVisual stage)
	{
		var visibleCount = Math.Min(PageSize, stage.Windows.Count);
		if (visibleCount <= 1)
			return 0;
		var cardSize = CardSize;
		var stride = Card3DGeometry.CalculateExpandedListStride(cardSize.Y, _dpiScale);
		var paginationHeight = stage.Windows.Count > PageSize
			? Math.Max(32f, cardSize.Y * 0.46f) + 12f * _dpiScale
			: 0;
		return (visibleCount - 1) * stride + paginationHeight;
	}

	private void LayoutCollapsedStage(StageCardVisual stage, Vector3 stageOffset, float stageScale, Vector2 cameraCenter, bool animate, int stageIndex)
	{
		var cardSize = CardSize;
		stage.SetPaginationVisible(false);
		stage.HideExpandedConnector();
		stage.ShowCollapsed(3);
		stage.ArrangeCollapsedZOrder();
		for (var index = stage.Windows.Count - 1; index >= 0; index--)
		{
			var card = stage.Windows[index];
			if (index >= 3)
			{
				card.SetVisible(false);
				continue;
			}
			var hovered = index == 0 && string.Equals(stage.Key, _hoveredStageKey, StringComparison.OrdinalIgnoreCase);
			var transform = Card3DGeometry.CreateCollapsedStackTransform(index, hovered, _dpiScale);
			card.SetVisible(true);
			card.SetTransform(transform.Offset, transform.Scale, transform.Angle, animate);
			card.DesiredBadge = index == 0 && stage.Windows.Count > 3 ? $"+{stage.Windows.Count - 3}" : null;
			var polygon = Card3DGeometry.ProjectCard(
				stageOffset,
				stageScale,
				transform.Offset,
				transform.Scale,
				transform.Angle,
				cardSize,
				card.Pivot,
				cameraCenter,
				PerspectiveDistance * _dpiScale);
			_hitTargets.Add(new CardHitTarget(stage.Key, card.Window, polygon, stageIndex * 20 + (3 - index) + (hovered ? 10 : 0)));
		}
	}

	private void LayoutExpandedStage(StageCardVisual stage, Vector3 stageOffset, float stageScale, Vector2 cameraCenter, bool animate)
	{
		var cardSize = CardSize;
		var pageCount = Math.Max(1, (int)Math.Ceiling(stage.Windows.Count / (double)PageSize));
		_expandedPage = Math.Clamp(_expandedPage, 0, pageCount - 1);
		var page = stage.Windows.Skip(_expandedPage * PageSize).Take(PageSize).ToArray();
		var pageHandles = page.Select(card => card.Window.Handle).ToHashSet();
		foreach (var card in stage.Windows)
			card.SetVisible(pageHandles.Contains(card.Window.Handle));

		var stride = Card3DGeometry.CalculateExpandedListStride(cardSize.Y, _dpiScale);
		var childIndent = 18f * _dpiScale;
		var hoveredIndex = Array.FindIndex(page, card => card.Window.Handle == _hoveredWindowHandle);
		stage.SetExpandedConnectorLayout(page.Length, cardSize.Y, stride, childIndent, _dpiScale);
		stage.ArrangeExpandedZOrder(page, _hoveredWindowHandle);
		for (var index = 0; index < page.Length; index++)
		{
			var card = page[index];
			var hovered = card.Window.Handle == _hoveredWindowHandle;
			var transform = Card3DGeometry.CreateExpandedListTransform(
				index,
				hoveredIndex,
				_dpiScale,
				stride,
				childIndent);
			card.SetTransform(transform.Offset, transform.Scale, transform.Angle, animate);
			card.DesiredBadge = index == page.Length - 1 && pageCount > 1 ? $"{_expandedPage + 1}/{pageCount}" : null;
			var polygon = Card3DGeometry.ProjectCard(
				stageOffset,
				stageScale,
				transform.Offset,
				transform.Scale,
				transform.Angle,
				cardSize,
				card.Pivot,
				cameraCenter,
				PerspectiveDistance * _dpiScale);
			_hitTargets.Add(new CardHitTarget(stage.Key, card.Window, polygon, 10000 + index + (hovered ? 100 : 0)));
		}

		stage.SetPaginationVisible(pageCount > 1);
		if (pageCount > 1)
		{
			var buttonsY = cardSize.Y + Math.Max(0, page.Length - 1) * stride + 7 * _dpiScale;
			AddPaginationButton(stage, stage.PreviousButton, stageOffset, stageScale, cameraCenter, new Vector3(12 * _dpiScale, buttonsY, 80 * _dpiScale), -1);
			AddPaginationButton(stage, stage.NextButton, stageOffset, stageScale, cameraCenter, new Vector3(46 * _dpiScale, buttonsY, 80 * _dpiScale), 1);
		}
	}

	private void AddPaginationButton(
		StageCardVisual stage,
		PageButtonVisual button,
		Vector3 stageOffset,
		float stageScale,
		Vector2 cameraCenter,
		Vector3 offset,
		int direction)
	{
		button.SetOffset(offset);
		var polygon = Card3DGeometry.ProjectCard(
			stageOffset,
			stageScale,
			offset,
			Vector3.One,
			0,
			button.Size,
			Vector2.Zero,
			cameraCenter,
			PerspectiveDistance * _dpiScale);
		_hitTargets.Add(new CardHitTarget(stage.Key, null, polygon, 20000 + direction, direction));
	}

	private void ChangeExpandedPage(int delta)
	{
		if (_expandedStageKey is null || !_stages.TryGetValue(_expandedStageKey, out var expanded))
			return;
		var pageCount = Math.Max(1, (int)Math.Ceiling(expanded.Windows.Count / (double)PageSize));
		if (pageCount <= 1)
			return;
		_expandedPage = (_expandedPage + delta + pageCount) % pageCount;
		_hoveredWindowHandle = IntPtr.Zero;
		LayoutStages(true);
	}

	private void ScheduleCaptures()
	{
		if (_disposed)
			return;
		var now = DateTime.UtcNow;
		var due = _stages.Values
			.SelectMany(stage => stage.Windows.Select(card => (stage, card)))
			.Where(tuple => tuple.card.IsVisible)
			.Where(tuple => now - tuple.card.LastCaptureUtc >= (string.Equals(tuple.stage.Key, _expandedStageKey, StringComparison.OrdinalIgnoreCase)
				? TimeSpan.FromMilliseconds(125)
				: TimeSpan.FromMilliseconds(500)))
			.OrderByDescending(tuple => tuple.card.Window.Handle == _hoveredWindowHandle)
			.Take(2)
			.ToArray();
		foreach (var item in due)
			StartCapture(item.card);
	}

	private void StartCapture(WindowCardVisual card)
	{
		lock (_captureGate)
		{
			if (!_capturesInFlight.Add(card.Window.Handle))
				return;
		}
		var window = card.Window;
		var badge = card.DesiredBadge;
		var width = card.SurfaceWidth;
		var height = card.SurfaceHeight;
		card.MarkCaptureStarted();
		_ = Task.Run(() => _capture.Capture(window, width, height, badge)).ContinueWith(task =>
		{
			var disposeCapture = false;
			lock (_captureGate)
			{
				_capturesInFlight.Remove(window.Handle);
				disposeCapture = _disposeCaptureWhenIdle && _capturesInFlight.Count == 0;
			}
			if (disposeCapture)
				_capture.Dispose();
			if (_disposed || task.IsFaulted || task.IsCanceled || _owner.IsDisposed)
				return;
			try
			{
				_owner.BeginInvoke(new Action(() =>
				{
					if (!_disposed && card.Window.Handle == task.Result.Handle)
						card.Upload(task.Result);
				}));
			}
			catch (InvalidOperationException)
			{
			}
		}, TaskScheduler.Default);
	}
}

internal sealed record CardHitTarget(string StageKey, IWindow? Window, IReadOnlyList<Vector2> Polygon, int ZOrder, int PageDelta = 0);

internal sealed class StageCardVisual : IDisposable
{
	private readonly Compositor _compositor;
	private readonly D3DCompositionDevice _graphics;
	private readonly int _surfaceWidth;
	private readonly int _surfaceHeight;
	private readonly Vector2 _cardSize;

	public StageCardVisual(string key, Compositor compositor, D3DCompositionDevice graphics, int surfaceWidth, int surfaceHeight, Vector2 cardSize)
	{
		Key = key;
		_compositor = compositor;
		_graphics = graphics;
		_surfaceWidth = surfaceWidth;
		_surfaceHeight = surfaceHeight;
		_cardSize = cardSize;
		Root = compositor.CreateContainerVisual();
		Root.Size = cardSize;
		PreviousButton = new PageButtonVisual(compositor, false, cardSize.Y);
		NextButton = new PageButtonVisual(compositor, true, cardSize.Y);
		ExpandedConnector = new ExpandedConnectorVisual(compositor, 5);
		Root.Children.InsertAtBottom(ExpandedConnector.Root);
		Root.Children.InsertAtTop(PreviousButton.Root);
		Root.Children.InsertAtTop(NextButton.Root);
	}

	public string Key { get; }
	public ContainerVisual Root { get; }
	public List<WindowCardVisual> Windows { get; } = new();
	public PageButtonVisual PreviousButton { get; }
	public PageButtonVisual NextButton { get; }
	public ExpandedConnectorVisual ExpandedConnector { get; }

	public void Synchronize(PrototypeStageSnapshot snapshot)
	{
		var handles = snapshot.Windows.Select(window => window.Handle).ToHashSet();
		for (var index = Windows.Count - 1; index >= 0; index--)
		{
			if (handles.Contains(Windows[index].Window.Handle))
				continue;
			Root.Children.Remove(Windows[index].Root);
			Windows[index].Dispose();
			Windows.RemoveAt(index);
		}

		for (var index = 0; index < snapshot.Windows.Count; index++)
		{
			var window = snapshot.Windows[index];
			var existing = Windows.FirstOrDefault(card => card.Window.Handle == window.Handle);
			if (existing is null)
			{
				existing = new WindowCardVisual(_compositor, _graphics, window, _surfaceWidth, _surfaceHeight, _cardSize);
				Windows.Insert(Math.Min(index, Windows.Count), existing);
				Root.Children.InsertAtTop(existing.Root);
			}
			else
			{
				existing.Window = window;
				var oldIndex = Windows.IndexOf(existing);
				if (oldIndex != index)
				{
					Windows.RemoveAt(oldIndex);
					Windows.Insert(index, existing);
				}
			}
		}
	}

	public void ShowCollapsed(int count)
	{
		for (var index = 0; index < Windows.Count; index++)
			Windows[index].SetVisible(index < count);
	}

	public void ArrangeCollapsedZOrder()
	{
		foreach (var card in Windows.AsEnumerable().Reverse())
		{
			Root.Children.Remove(card.Root);
			Root.Children.InsertAtTop(card.Root);
		}
	}

	public void ArrangeExpandedZOrder(IReadOnlyList<WindowCardVisual> page, IntPtr hoveredHandle)
	{
		foreach (var card in page.Where(card => card.Window.Handle != hoveredHandle))
		{
			Root.Children.Remove(card.Root);
			Root.Children.InsertAtTop(card.Root);
		}
		var hovered = page.FirstOrDefault(card => card.Window.Handle == hoveredHandle);
		if (hovered is not null)
		{
			Root.Children.Remove(hovered.Root);
			Root.Children.InsertAtTop(hovered.Root);
		}
		Root.Children.Remove(PreviousButton.Root);
		Root.Children.InsertAtTop(PreviousButton.Root);
		Root.Children.Remove(NextButton.Root);
		Root.Children.InsertAtTop(NextButton.Root);
	}

	public void HideAll()
	{
		SetPaginationVisible(false);
		HideExpandedConnector();
		foreach (var card in Windows)
			card.SetVisible(false);
	}

	public void SetPaginationVisible(bool visible)
	{
		PreviousButton.Root.IsVisible = visible;
		NextButton.Root.IsVisible = visible;
	}

	public void SetExpandedConnectorLayout(int visibleCount, float cardHeight, float stride, float childIndent, float dpiScale)
	{
		ExpandedConnector.SetLayout(visibleCount, cardHeight, stride, childIndent, dpiScale);
	}

	public void HideExpandedConnector() => ExpandedConnector.Root.IsVisible = false;

	public void Dispose()
	{
		foreach (var window in Windows)
			window.Dispose();
		Windows.Clear();
		PreviousButton.Dispose();
		NextButton.Dispose();
		ExpandedConnector.Dispose();
		Root.Dispose();
	}
}

internal sealed class ExpandedConnectorVisual : IDisposable
{
	private readonly CompositionColorBrush _brush;
	private readonly SpriteVisual _verticalLine;
	private readonly List<SpriteVisual> _branches = new();

	public ExpandedConnectorVisual(Compositor compositor, int maximumChildren)
	{
		Root = compositor.CreateContainerVisual();
		Root.IsVisible = false;
		Root.Offset = new Vector3(0, 0, -20);
		_brush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(205, 126, 151, 190));
		_verticalLine = compositor.CreateSpriteVisual();
		_verticalLine.Brush = _brush;
		Root.Children.InsertAtTop(_verticalLine);
		for (var index = 0; index < maximumChildren; index++)
		{
			var branch = compositor.CreateSpriteVisual();
			branch.Brush = _brush;
			branch.IsVisible = false;
			_branches.Add(branch);
			Root.Children.InsertAtTop(branch);
		}
	}

	public ContainerVisual Root { get; }

	public void SetLayout(int visibleCount, float cardHeight, float stride, float childIndent, float dpiScale)
	{
		var childCount = Math.Max(0, visibleCount - 1);
		Root.IsVisible = childCount > 0;
		if (childCount == 0)
			return;
		var safeScale = Math.Max(0.75f, dpiScale);
		var thickness = Math.Max(2f, 2.4f * safeScale);
		var lineX = 7f * safeScale;
		var startY = cardHeight + (stride - cardHeight) * 0.42f;
		var lastCenterY = childCount * stride + cardHeight * 0.5f;
		_verticalLine.Offset = new Vector3(lineX, startY, 0);
		_verticalLine.Size = new Vector2(thickness, Math.Max(thickness, lastCenterY - startY));
		Root.Size = new Vector2(childIndent + cardHeight, lastCenterY + cardHeight * 0.5f);

		for (var index = 0; index < _branches.Count; index++)
		{
			var branch = _branches[index];
			branch.IsVisible = index < childCount;
			if (!branch.IsVisible)
				continue;
			var childCenterY = (index + 1) * stride + cardHeight * 0.5f;
			branch.Offset = new Vector3(lineX, childCenterY - thickness * 0.5f, 0);
			branch.Size = new Vector2(Math.Max(thickness, childIndent - lineX + 3f * safeScale), thickness);
		}
	}

	public void Dispose()
	{
		foreach (var branch in _branches)
			branch.Dispose();
		_branches.Clear();
		_verticalLine.Dispose();
		_brush.Dispose();
		Root.Dispose();
	}
}

internal sealed class PageButtonVisual : IDisposable
{
	private readonly SpriteVisual _background;
	private readonly CompositionColorBrush _backgroundBrush;
	private readonly CompositionRoundedRectangleGeometry _geometry;
	private readonly CompositionGeometricClip _clip;
	private readonly SpriteVisual _upperStroke;
	private readonly SpriteVisual _lowerStroke;
	private readonly CompositionColorBrush _strokeBrush;

	public PageButtonVisual(Compositor compositor, bool pointsRight, float cardHeight)
	{
		Size = new Vector2(Math.Max(24, cardHeight * 0.30f), Math.Max(32, cardHeight * 0.46f));
		Root = compositor.CreateContainerVisual();
		Root.Size = Size;
		_backgroundBrush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(92, 246, 248, 252));
		_background = compositor.CreateSpriteVisual();
		_background.Size = Size;
		_background.Brush = _backgroundBrush;
		_geometry = compositor.CreateRoundedRectangleGeometry();
		_geometry.Size = Size;
		_geometry.CornerRadius = new Vector2(Size.X * 0.42f);
		_clip = compositor.CreateGeometricClip(_geometry);
		_background.Clip = _clip;
		_strokeBrush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(225, 38, 43, 55));
		_upperStroke = CreateStroke(compositor, pointsRight ? 45 : -45);
		_lowerStroke = CreateStroke(compositor, pointsRight ? -45 : 45);
		var centerX = Size.X * 0.5f - 1;
		_upperStroke.Offset = new Vector3(centerX, Size.Y * 0.5f - 8, 0);
		_lowerStroke.Offset = new Vector3(centerX, Size.Y * 0.5f, 0);
		Root.Children.InsertAtTop(_background);
		Root.Children.InsertAtTop(_upperStroke);
		Root.Children.InsertAtTop(_lowerStroke);
	}

	public ContainerVisual Root { get; }
	public Vector2 Size { get; }

	public void SetOffset(Vector3 offset) => Root.Offset = offset;

	public void Dispose()
	{
		Root.Dispose();
		_upperStroke.Dispose();
		_lowerStroke.Dispose();
		_clip.Dispose();
		_geometry.Dispose();
		_background.Dispose();
		_strokeBrush.Dispose();
		_backgroundBrush.Dispose();
	}

	private SpriteVisual CreateStroke(Compositor compositor, float angle)
	{
		var stroke = compositor.CreateSpriteVisual();
		stroke.Size = new Vector2(2.2f, 11);
		stroke.CenterPoint = new Vector3(1.1f, 5.5f, 0);
		stroke.RotationAngleInDegrees = angle;
		stroke.Brush = _strokeBrush;
		return stroke;
	}
}

internal sealed class WindowCardVisual : IDisposable
{
	private readonly Compositor _compositor;
	private readonly CardSwapChain _surface;
	private readonly CompositionSurfaceBrush _surfaceBrush;
	private readonly SpriteVisual _content;
	private readonly CompositionRoundedRectangleGeometry _clipGeometry;
	private readonly CompositionGeometricClip _clip;
	private readonly DropShadow _shadow;
	private Vector3 _lastOffset;
	private Vector3 _lastScale;
	private float _lastAngle;
	private bool _hasTransform;
	private bool _disposed;

	public WindowCardVisual(Compositor compositor, D3DCompositionDevice graphics, IWindow window, int surfaceWidth, int surfaceHeight, Vector2 cardSize)
	{
		_compositor = compositor;
		Window = window;
		SurfaceWidth = surfaceWidth;
		SurfaceHeight = surfaceHeight;
		_surface = graphics.CreateSurface(compositor, surfaceWidth, surfaceHeight);
		_surface.Upload(new byte[surfaceWidth * surfaceHeight * 4]);
		_surfaceBrush = compositor.CreateSurfaceBrush(_surface.CompositionSurface);
		_surfaceBrush.Stretch = CompositionStretch.Fill;
		_content = compositor.CreateSpriteVisual();
		_content.Size = cardSize;
		_content.Brush = _surfaceBrush;
		_clipGeometry = compositor.CreateRoundedRectangleGeometry();
		_clipGeometry.Size = cardSize;
		_clipGeometry.CornerRadius = new Vector2(Math.Max(9, cardSize.Y * 0.105f));
		_clip = compositor.CreateGeometricClip(_clipGeometry);
		_content.Clip = _clip;
		_shadow = compositor.CreateDropShadow();
		_shadow.BlurRadius = Math.Max(16, cardSize.Y * 0.30f);
		_shadow.Opacity = 0.48f;
		_shadow.Offset = new Vector3(0, Math.Max(5, cardSize.Y * 0.08f), 0);
		_shadow.Color = Windows.UI.Color.FromArgb(220, 2, 4, 8);
		_shadow.Mask = _surfaceBrush;
		_content.Shadow = _shadow;
		Root = compositor.CreateContainerVisual();
		Root.Size = cardSize;
		Root.CenterPoint = new Vector3(cardSize.X * 0.88f, cardSize.Y * 0.5f, 0);
		Root.RotationAxis = Vector3.UnitY;
		Root.Children.InsertAtTop(_content);
		Pivot = new Vector2(Root.CenterPoint.X, Root.CenterPoint.Y);
	}

	public ContainerVisual Root { get; }
	public IWindow Window { get; set; }
	public Vector2 Pivot { get; }
	public int SurfaceWidth { get; }
	public int SurfaceHeight { get; }
	public bool IsVisible { get; private set; }
	public string? DesiredBadge { get; set; }
	public DateTime LastCaptureUtc { get; private set; } = DateTime.MinValue;

	public void SetVisible(bool visible)
	{
		IsVisible = visible;
		Root.IsVisible = visible;
	}

	public void SetTransform(Vector3 offset, Vector3 scale, float angle, bool animate)
	{
		if (_hasTransform && Vector3.DistanceSquared(_lastOffset, offset) < 0.01f &&
			Vector3.DistanceSquared(_lastScale, scale) < 0.0001f && Math.Abs(_lastAngle - angle) < 0.01f)
			return;
		var hadTransform = _hasTransform;
		_hasTransform = true;
		_lastOffset = offset;
		_lastScale = scale;
		_lastAngle = angle;
		if (!animate || !hadTransform)
		{
			Root.Offset = offset;
			Root.Scale = scale;
			Root.RotationAngleInDegrees = angle;
			return;
		}

		using var offsetAnimation = _compositor.CreateSpringVector3Animation();
		offsetAnimation.FinalValue = offset;
		offsetAnimation.DampingRatio = 1f;
		offsetAnimation.Period = TimeSpan.FromMilliseconds(110);
		Root.StartAnimation(nameof(Visual.Offset), offsetAnimation);
		using var scaleAnimation = _compositor.CreateSpringVector3Animation();
		scaleAnimation.FinalValue = scale;
		scaleAnimation.DampingRatio = 1f;
		scaleAnimation.Period = TimeSpan.FromMilliseconds(110);
		Root.StartAnimation(nameof(Visual.Scale), scaleAnimation);
		using var angleAnimation = _compositor.CreateSpringScalarAnimation();
		angleAnimation.FinalValue = angle;
		angleAnimation.DampingRatio = 1f;
		angleAnimation.Period = TimeSpan.FromMilliseconds(110);
		Root.StartAnimation(nameof(Visual.RotationAngleInDegrees), angleAnimation);
	}

	public void Upload(CapturedCardFrame frame)
	{
		if (_disposed || frame.Width != SurfaceWidth || frame.Height != SurfaceHeight)
			return;
		_surface.Upload(frame.Pixels);
		LastCaptureUtc = DateTime.UtcNow;
	}

	public void MarkCaptureStarted() => LastCaptureUtc = DateTime.UtcNow;

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		Root.Dispose();
		_shadow.Dispose();
		_clip.Dispose();
		_clipGeometry.Dispose();
		_content.Dispose();
		_surfaceBrush.Dispose();
		_surface.Dispose();
	}
}
