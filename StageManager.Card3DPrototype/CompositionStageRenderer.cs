using StageManager.Native.Window;
using StageManager.Settings;
using StageManager.Card3DPrototype.NotificationArea;
using StageManager.Card3DPrototype.QuickLaunch;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows.UI.Composition;

namespace StageManager.Card3DPrototype;

internal sealed class CompositionStageRenderer : IDisposable
{
	private const float BaseCardWidth = 196f;
	private const float BaseCardHeight = 122f;
	private const float PerspectiveDistance = 1200f;
	private const int PageSize = 6;
	private const int DefaultSidebarVerticalOffset = -80;
	private const int MinSidebarVerticalOffset = -400;
	private const int MaxSidebarVerticalOffset = 400;
	private readonly Control _owner;
	private readonly Compositor _compositor;
	private readonly ContainerVisual _cameraRoot;
	private readonly ContainerVisual _mainCamera;
	private readonly ContainerVisual _fixedCamera;
	private readonly SidebarScrollMotion _scrollMotion = new();
	private readonly System.Windows.Forms.Timer _scrollTimer = new() { Interval = 16 };
	private readonly System.Windows.Forms.Timer _layoutMotionTimer = new() { Interval = 160 };
	private Vector2 _toolbarLayoutKey = new(-1, -1);
	private (WindowCardVisual Card, CapturedCardFrame Frame)? _deferredCapture;
	private readonly List<(StageCardVisual Stage, float Top, float Height)> _stageScrollBounds = new();
	private long _lastScrollTick;
	private readonly D3DCompositionDevice _graphics;
	private readonly WindowFrameCapture _capture = new();
	private readonly SidebarExpandAllButtonVisual _expandAllButton;
	private readonly SidebarExplorerButtonVisual _explorerButton;
	private readonly SidebarCollapseButtonVisual _collapseButton;
	private readonly SidebarDesktopButtonVisual _desktopButton;
	private readonly SidebarDesktopIconsButtonVisual _desktopIconsButton;
	private readonly SidebarQuickLaunchVisual _chromeButton;
	private readonly SidebarQuickLaunchVisual _edgeButton;
	private readonly NotificationTrayCardVisual _notificationAreaCard;
	private readonly Dictionary<string, StageCardVisual> _stages = new(StringComparer.OrdinalIgnoreCase);
	private readonly System.Windows.Forms.Timer _captureTimer;
	private readonly object _captureGate = new();
	private readonly HashSet<IntPtr> _capturesInFlight = new();
	private readonly List<CardHitTarget> _hitTargets = new();
	private readonly List<IReadOnlyList<Vector2>> _passivePolygons = new();
	private IReadOnlyList<PrototypeStageSnapshot> _snapshots = Array.Empty<PrototypeStageSnapshot>();
	private string? _expandedStageKey;
	private bool _expandedByHover;
	private bool _expandedPinned;
	private WindowCardVisual? _feedbackCard;
	private string? _hoveredStageKey;
	private IntPtr _hoveredWindowHandle;
	private bool _hoveredGroupCard;
	private DateTime _lastPointerInsideUtc;
	private float _dpiScale = 1f;
	private float _viewportWidth;
	private float _viewportHeight;
	private int _expandedPage;
	private bool _disposeCaptureWhenIdle;
	private bool _disposed;
	private bool _animationsEnabled;
	private bool _sidebarVisible = true;
	private bool _expandAllStages;
	private bool _expandAllButtonHovered;
	private bool _explorerButtonEnabled = true;
	private bool _explorerButtonHovered;
	private bool _explorerRequested;
	private bool _chromeQuickLaunchEnabled;
	private bool _edgeQuickLaunchEnabled;
	private QuickLaunchApp? _quickLaunchRequested;
	private bool _pinButtonEnabled;
	private bool _pinButtonHovered;
	private bool _collapseButtonEnabled = true;
	private bool _collapseButtonHovered;
	private bool _collapseRequested;
	private bool _desktopButtonEnabled = true;
	private bool _desktopButtonHovered;
	private bool _desktopToggleRequested;
	private bool _desktopIconsButtonEnabled = true;
	private bool _desktopIconsButtonHovered;
	private bool _desktopIconsToggleRequested;
	// Start disabled and let persisted settings explicitly attach the footer.
	// This avoids relying on the constructor/first-resize ordering of the
	// DesktopWindowTarget visual tree after a cold launch.
	private bool _notificationAreaCardEnabled;
	private bool _notificationAreaOpenRequested;
	private NotificationIconActivation? _notificationIconActivationRequest;
	private float _preferenceScale;
	private int _sidebarVerticalOffset = DefaultSidebarVerticalOffset;
	private int _notificationAreaVerticalOffset;
	private int _previewRefreshMinutes = WindowCapturePolicy.DefaultRefreshMinutes;
	private bool _pausePreviewRefreshWhenHidden = true;
	private bool _manualRefreshPending;

	public CompositionStageRenderer(Control owner, Compositor compositor, ContainerVisual cameraRoot, double cardScale, bool animationsEnabled, bool lowMemoryRendering)
	{
		_owner = owner;
		_compositor = compositor;
		_cameraRoot = cameraRoot;
		_mainCamera = compositor.CreateContainerVisual();
		_fixedCamera = compositor.CreateContainerVisual();
		_cameraRoot.Children.InsertAtTop(_mainCamera);
		_cameraRoot.Children.InsertAtTop(_fixedCamera);
		_scrollTimer.Tick += (_, _) => AdvanceScroll();
		_layoutMotionTimer.Tick += (_, _) => FinishLayoutMotion();
		_preferenceScale = NormalizeCardScale(cardScale);
		_animationsEnabled = animationsEnabled;
		_graphics = new D3DCompositionDevice(lowMemoryRendering);
		_expandAllButton = new SidebarExpandAllButtonVisual(_compositor);
		_explorerButton = new SidebarExplorerButtonVisual(_compositor);
		_collapseButton = new SidebarCollapseButtonVisual(_compositor);
		_desktopButton = new SidebarDesktopButtonVisual(_compositor);
		_desktopIconsButton = new SidebarDesktopIconsButtonVisual(_compositor);
		_chromeButton = new SidebarQuickLaunchVisual(_compositor, _graphics, QuickLaunchApp.Chrome);
		_edgeButton = new SidebarQuickLaunchVisual(_compositor, _graphics, QuickLaunchApp.Edge);
		_notificationAreaCard = new NotificationTrayCardVisual(_compositor, _graphics);
		_mainCamera.Children.InsertAtTop(_expandAllButton.Root);
		_mainCamera.Children.InsertAtTop(_explorerButton.Root);
		_mainCamera.Children.InsertAtTop(_collapseButton.Root);
		_mainCamera.Children.InsertAtTop(_desktopButton.Root);
		_mainCamera.Children.InsertAtTop(_desktopIconsButton.Root);
		_mainCamera.Children.InsertAtTop(_chromeButton.Root);
		_mainCamera.Children.InsertAtTop(_edgeButton.Root);
		_mainCamera.Children.InsertAtTop(_notificationAreaCard.Root);
		_captureTimer = new System.Windows.Forms.Timer { Interval = 350 };
		_captureTimer.Tick += (_, _) => ScheduleCaptures();
		_captureTimer.Start();
	}

	public bool HasExpandedStage => _expandAllStages || _expandedStageKey is not null;
	public double CardScale => _preferenceScale;
	public int SidebarVerticalOffset => _sidebarVerticalOffset;
	public bool SidebarVisible => _sidebarVisible;
	public bool IsExpandedStagePinned => _expandedPinned;
	public bool AreAllStagesExpanded => _expandAllStages;
	public bool IsStageExpanded(string key) => _expandAllStages || string.Equals(key, _expandedStageKey, StringComparison.OrdinalIgnoreCase);
	public bool IsStagePinned(string key) => _expandedPinned && string.Equals(key, _expandedStageKey, StringComparison.OrdinalIgnoreCase);
	public PrototypeStageSnapshot? GetStageSnapshot(string key) => _snapshots.FirstOrDefault(stage => string.Equals(stage.Key, key, StringComparison.OrdinalIgnoreCase));

	public void SetCardFeedback(CardHitTarget? target, CardFeedback feedback)
	{
		WindowCardVisual? card = null;
		if (target is not null && _stages.TryGetValue(target.StageKey, out var stage))
			card = target.Window is { } window
				? stage.Windows.FirstOrDefault(candidate => candidate.Window.Handle == window.Handle)
				: stage.GroupCard;
		var animate = _animationsEnabled && NativeMethods.ClientAreaAnimationsEnabled;
		if (_feedbackCard is not null && _feedbackCard != card)
			_feedbackCard.SetFeedback(CardFeedback.None, animate);
		_feedbackCard = card;
		card?.SetFeedback(feedback, animate);
	}
	public long LayoutRevision { get; private set; }
	public event EventHandler? ScrollPositionChanged;
	public float ScrollTranslationY => -_scrollMotion.Position;
	public bool IsScrolling => _scrollTimer.Enabled;
	public float SidebarInteractionWidth => CardSize.X + 48f * _dpiScale;
	public TimeSpan SidebarAnimationDuration => TimeSpan.FromMilliseconds(220);

	public bool CanScrollAt(Point clientPoint)
	{
		if (!_sidebarVisible || clientPoint.X < 0 || clientPoint.Y < 0 ||
			clientPoint.X > SidebarInteractionWidth || clientPoint.Y > _viewportHeight)
			return false;
		return true;
	}

	public void ReleasePointerPress()
	{
		_expandAllButton.SetPressed(false);
		_explorerButton.SetPressed(false);
		_collapseButton.SetPressed(false);
		_desktopButton.SetPressed(false);
		_desktopIconsButton.SetPressed(false);
		_chromeButton.SetPressed(false);
		_edgeButton.SetPressed(false);
		_notificationAreaCard.SetControlPressed(false, false);
		_notificationAreaCard.SetPressed(false);
		if (_expandedStageKey is not null && _stages.TryGetValue(_expandedStageKey, out var stage))
			stage.PinButton.SetPressed(false);
	}

	public NotificationIconActivation? ConsumeNotificationIconActivationRequest()
	{
		var request = _notificationIconActivationRequest;
		_notificationIconActivationRequest = null;
		_notificationAreaCard.SetPressed(false);
		return request;
	}

	public bool ConsumeNotificationAreaOpenRequest()
	{
		var requested = _notificationAreaOpenRequested;
		_notificationAreaOpenRequested = false;
		_notificationAreaCard.SetPressed(false);
		return requested;
	}

	public bool ConsumeExplorerLaunchRequest()
	{
		if (!_explorerButtonEnabled)
		{
			_explorerRequested = false;
			return false;
		}
		var requested = _explorerRequested;
		_explorerRequested = false;
		return requested;
	}

	public QuickLaunchApp? ConsumeQuickLaunchRequest()
	{
		var requested = _quickLaunchRequested;
		_quickLaunchRequested = null;
		return requested;
	}

	public bool ConsumeSidebarCollapseRequest()
	{
		if (!_collapseButtonEnabled)
		{
			_collapseRequested = false;
			return false;
		}
		var requested = _collapseRequested;
		_collapseRequested = false;
		return requested;
	}

	public bool ConsumeDesktopToggleRequest()
	{
		if (!_desktopButtonEnabled)
		{
			_desktopToggleRequested = false;
			return false;
		}
		var requested = _desktopToggleRequested;
		_desktopToggleRequested = false;
		return requested;
	}

	public void SetDesktopShown(bool shown) => _desktopButton.SetActive(shown);

	public bool ConsumeDesktopIconsToggleRequest()
	{
		if (!_desktopIconsButtonEnabled)
		{
			_desktopIconsToggleRequested = false;
			return false;
		}
		var requested = _desktopIconsToggleRequested;
		_desktopIconsToggleRequested = false;
		return requested;
	}

	public void SetDesktopIconsVisible(bool visible) => _desktopIconsButton.SetIconsVisible(visible);

	public IReadOnlyList<PointF[]> GetInteractivePolygons(bool fixedLayer)
	{
		var polygons = _hitTargets
			.Where(_ => !fixedLayer)
			.Select(target =>
			{
				if (target.Projection?.IsMoving != true)
					return target.Polygon.Select(point => new PointF(point.X, point.Y)).ToArray();
				var points = target.Polygon.Concat(target.Projection.CurrentPolygon()).ToArray();
				var left = points.Min(point => point.X);
				var right = points.Max(point => point.X);
				var top = points.Min(point => point.Y);
				var bottom = points.Max(point => point.Y);
				return new[] { new PointF(left, top), new PointF(right, top),
					new PointF(right, bottom), new PointF(left, bottom) };
			})
			.ToList();
		if (!fixedLayer)
			polygons.AddRange(_passivePolygons.Select(polygon => polygon.Select(point => new PointF(point.X, point.Y)).ToArray()));
		return polygons;
	}

	public void SetAnimationsEnabled(bool enabled)
	{
		_animationsEnabled = enabled;
		if (!enabled)
		{
			FinishScroll();
			LayoutStages(false);
		}
	}

	public void SetNotificationAreaDragPressed(bool pressed) => _notificationAreaCard.SetControlPressed(pressed, false);
	public void SetNotificationAreaRefreshPressed(bool pressed) => _notificationAreaCard.SetControlPressed(false, pressed);

	public bool SetNotificationAreaVerticalOffset(int verticalOffset)
	{
		// Kept for settings migration. The hidden-icons card now follows the
		// scrolling card column and no longer has an independent vertical offset.
		_notificationAreaVerticalOffset = 0;
		return verticalOffset != 0;
	}

	public bool PreviewNotificationAreaVerticalOffset(int verticalOffset)
	{
		return false;
	}

	public void CommitNotificationAreaVerticalOffset() => LayoutStages(false);

	public int NotificationAreaVerticalOffset => _notificationAreaVerticalOffset;

	public bool SetExplorerButtonEnabled(bool enabled)
	{
		if (_explorerButtonEnabled == enabled)
			return false;

		_explorerButtonEnabled = enabled;
		_explorerRequested = false;
		_explorerButton.SetPressed(false);
		SetExplorerButtonHovered(false);
		_chromeButton.SetPressed(false);
		_chromeButton.SetHovered(false);
		_edgeButton.SetPressed(false);
		_edgeButton.SetHovered(false);
		_explorerButton.SetVisible(false);
		LayoutStages(true);
		return true;
	}

	public bool SetQuickLaunchButtonsEnabled(bool chromeEnabled, bool edgeEnabled)
	{
		chromeEnabled &= _chromeButton.IsAvailable;
		edgeEnabled &= _edgeButton.IsAvailable;
		if (_chromeQuickLaunchEnabled == chromeEnabled && _edgeQuickLaunchEnabled == edgeEnabled)
			return false;
		_chromeQuickLaunchEnabled = chromeEnabled;
		_edgeQuickLaunchEnabled = edgeEnabled;
		_quickLaunchRequested = null;
		_chromeButton.SetVisible(false);
		_edgeButton.SetVisible(false);
		LayoutStages(true);
		return true;
	}

	public bool SetCollapseButtonEnabled(bool enabled)
	{
		if (_collapseButtonEnabled == enabled)
			return false;

		_collapseButtonEnabled = enabled;
		_collapseRequested = false;
		_collapseButton.SetPressed(false);
		SetCollapseButtonHovered(false);
		_collapseButton.SetVisible(false);
		LayoutStages(true);
		return true;
	}

	public bool SetNotificationAreaCardEnabled(bool enabled)
	{
		if (_notificationAreaCardEnabled == enabled)
			return false;
		_notificationAreaCardEnabled = enabled;
		_notificationAreaOpenRequested = false;
		_notificationIconActivationRequest = null;
		_notificationAreaCard.SetPressed(false);
		_notificationAreaCard.SetHovered(null);
		_notificationAreaCard.SetVisible(false);
		LayoutStages(true);
		return true;
	}

	public bool SetDesktopButtonEnabled(bool enabled)
	{
		if (_desktopButtonEnabled == enabled)
			return false;
		_desktopButtonEnabled = enabled;
		_desktopToggleRequested = false;
		_desktopButton.SetPressed(false);
		SetDesktopButtonHovered(false);
		_desktopButton.SetVisible(false);
		LayoutStages(true);
		return true;
	}

	public bool SetDesktopIconsButtonEnabled(bool enabled)
	{
		if (_desktopIconsButtonEnabled == enabled)
			return false;
		_desktopIconsButtonEnabled = enabled;
		_desktopIconsToggleRequested = false;
		_desktopIconsButton.SetPressed(false);
		SetDesktopIconsButtonHovered(false);
		_desktopIconsButton.SetVisible(false);
		LayoutStages(true);
		return true;
	}

	public void UpdateNotificationAreaIcons(IReadOnlyList<NotificationIconSnapshot> icons)
	{
		_notificationAreaCard.UpdateIcons(icons);
		LayoutStages(true);
	}

	public bool SetPinButtonEnabled(bool enabled)
	{
		if (_pinButtonEnabled == enabled)
			return false;

		_pinButtonEnabled = enabled;
		SetPinButtonHovered(false);
		if (!enabled)
		{
			_expandedPinned = false;
			_expandedByHover = false;
		}
		LayoutStages(true);
		return true;
	}

	public void SetPreviewPolicy(int refreshMinutes, bool pauseWhenHidden)
	{
		_previewRefreshMinutes = Math.Clamp(refreshMinutes, 1, 60);
		_pausePreviewRefreshWhenHidden = pauseWhenHidden;
		if (_sidebarVisible)
			ScheduleCaptures();
	}

	public void RefreshAllPreviews()
	{
		foreach (var stage in _stages.Values)
		{
			stage.GroupCard?.InvalidateCapture();
			foreach (var card in stage.Windows)
				card.InvalidateCapture();
		}
		_manualRefreshPending = true;
		ScheduleCaptures();
	}

	public void RefreshStagePreviews(string stageKey)
	{
		if (!_stages.TryGetValue(stageKey, out var stage))
			return;
		stage.GroupCard?.InvalidateCapture();
		foreach (var card in stage.Windows)
			card.InvalidateCapture();
		_manualRefreshPending = true;
		ScheduleCaptures();
	}

	public void ReleasePreviewSurfaces()
	{
		foreach (var stage in _stages.Values)
			stage.ReleasePreviewSurfaces();
		_graphics.Trim();
	}

	public void SetSidebarVisible(bool visible, bool animate)
	{
		if (_sidebarVisible == visible && (!visible || Math.Abs(_cameraRoot.Offset.X) < 0.1f))
			return;
		var previous = _cameraRoot.Offset;
		var target = new Vector3(visible ? 0 : HiddenOffsetX, 0, 0);
		_sidebarVisible = visible;
		if (!visible)
			FinishScroll();
		_expandAllButton.SetPressed(false);
		SetExpandAllButtonHovered(false);
		_explorerButton.SetPressed(false);
		SetExplorerButtonHovered(false);
		_collapseButton.SetPressed(false);
		SetCollapseButtonHovered(false);
		_desktopButton.SetPressed(false);
		SetDesktopButtonHovered(false);
		_desktopIconsButton.SetPressed(false);
		SetDesktopIconsButtonHovered(false);
		_notificationAreaCard.SetControlPressed(false, false);
		_notificationAreaCard.SetControlHovered(false, false);
		_notificationAreaCard.SetPressed(false);
		_notificationAreaCard.SetHovered(null);
		SetPinButtonHovered(false);
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
			_mainCamera.Children.Remove(stage.Root);
			stage.Dispose();
		}
		_stages.Clear();
		Synchronize(_snapshots);
		if (!_sidebarVisible)
			_cameraRoot.Offset = new Vector3(HiddenOffsetX, 0, 0);
	}

	public bool SetSidebarVerticalOffset(int verticalOffset)
	{
		var normalized = Math.Clamp(verticalOffset, MinSidebarVerticalOffset, MaxSidebarVerticalOffset);
		if (_sidebarVerticalOffset == normalized)
			return false;

		_sidebarVerticalOffset = normalized;
		LayoutStages(true);
		return true;
	}

	public void Resize(float width, float height, float dpiScale)
	{
		_viewportWidth = Math.Max(1, width);
		_viewportHeight = Math.Max(1, height);
		_dpiScale = Math.Max(0.75f, dpiScale);
		_cameraRoot.Size = new Vector2(_viewportWidth, _viewportHeight);
		var perspective = Matrix4x4.Identity;
		perspective.M34 = -1f / (PerspectiveDistance * _dpiScale);
		foreach (var camera in new[] { _mainCamera, _fixedCamera })
		{
			camera.Size = _cameraRoot.Size;
			camera.CenterPoint = new Vector3(_viewportWidth / 2f, _viewportHeight / 2f, 0);
			camera.TransformMatrix = perspective;
		}
		LayoutStages(false);
		if (!_sidebarVisible)
			_cameraRoot.Offset = new Vector3(HiddenOffsetX, 0, 0);
	}

	public void Synchronize(IReadOnlyList<PrototypeStageSnapshot> snapshots)
	{
		if (_disposed)
			return;
		var layoutChanged = !HasSameLayout(_snapshots, snapshots);
		_snapshots = snapshots;
		var liveKeys = snapshots.Select(stage => stage.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (var stale in _stages.Keys.Where(key => !liveKeys.Contains(key)).ToArray())
		{
			layoutChanged = true;
			var stage = _stages[stale];
			_mainCamera.Children.Remove(stage.Root);
			stage.Dispose();
			_stages.Remove(stale);
		}

		foreach (var snapshot in snapshots)
		{
			if (!_stages.TryGetValue(snapshot.Key, out var stage))
			{
				layoutChanged = true;
				stage = new StageCardVisual(snapshot.Key, _compositor, _graphics, CardPixelWidth, CardPixelHeight, CardSize);
				_stages[snapshot.Key] = stage;
				_mainCamera.Children.InsertAtTop(stage.Root);
			}
			stage.Synchronize(snapshot);
			stage.SynchronizeGroupCard(snapshot.Windows.Count > 1 ? snapshot.Windows[0] : null);
		}

		if (_expandedStageKey is not null && !liveKeys.Contains(_expandedStageKey))
		{
			layoutChanged = true;
			_expandedStageKey = null;
			_expandedByHover = false;
			_expandedPinned = false;
			_hoveredWindowHandle = IntPtr.Zero;
			_hoveredGroupCard = false;
			_expandedPage = 0;
		}
		else if (_expandedStageKey is not null &&
			_stages.TryGetValue(_expandedStageKey, out var expandedStage) &&
			expandedStage.Windows.Count <= 1)
		{
			layoutChanged = true;
			_expandedStageKey = null;
			_expandedByHover = false;
			_expandedPinned = false;
			_hoveredWindowHandle = IntPtr.Zero;
			_hoveredGroupCard = false;
			_expandedPage = 0;
		}
		if (_hoveredStageKey is not null && !liveKeys.Contains(_hoveredStageKey))
			_hoveredStageKey = null;
		if (layoutChanged)
			LayoutStages(true);
	}

	private static bool HasSameLayout(
		IReadOnlyList<PrototypeStageSnapshot> previous,
		IReadOnlyList<PrototypeStageSnapshot> current)
	{
		if (previous.Count != current.Count)
			return false;
		for (var stageIndex = 0; stageIndex < previous.Count; stageIndex++)
		{
			var oldStage = previous[stageIndex];
			var newStage = current[stageIndex];
			if (!string.Equals(oldStage.Key, newStage.Key, StringComparison.OrdinalIgnoreCase) ||
				oldStage.Windows.Count != newStage.Windows.Count)
				return false;
			for (var windowIndex = 0; windowIndex < oldStage.Windows.Count; windowIndex++)
			{
				if (oldStage.Windows[windowIndex].Handle != newStage.Windows[windowIndex].Handle)
					return false;
			}
		}
		return true;
	}

	public CardHitTarget? HitTest(Point clientPoint)
	{
		var point = new Vector2(clientPoint.X, clientPoint.Y - ScrollTranslationY);
		foreach (var target in _hitTargets)
		{
			var polygon = target.Projection?.IsMoving == true ? target.Projection.CurrentPolygon() : target.Polygon;
			if (Card3DGeometry.Contains(polygon, point))
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
		SetExplorerButtonHovered(hit.IsExplorerButton);
		SetQuickLaunchHovered(hit.QuickLaunchApp);
		SetExpandAllButtonHovered(hit.IsExpandAllButton);
		SetCollapseButtonHovered(hit.IsSidebarCollapseButton);
		SetDesktopButtonHovered(hit.IsDesktopButton);
		SetDesktopIconsButtonHovered(hit.IsDesktopIconsButton);
		SetNotificationAreaControlHovered(hit);
		SetPinButtonHovered(hit.IsPinButton);
		SetNotificationAreaHovered(hit);
		if (hit.IsSidebarCollapseButton || hit.IsDesktopButton || hit.IsDesktopIconsButton || hit.IsExpandAllButton || hit.IsNotificationAreaCard || hit.QuickLaunchApp is not null)
			return;
		if (!_expandAllStages && _expandedStageKey is not null &&
			!_expandedPinned &&
			!string.Equals(_expandedStageKey, hit.StageKey, StringComparison.OrdinalIgnoreCase))
		{
			CollapseExpandedStage();
			hit = HitTest(clientPoint);
			if (hit is null)
				return;
			_lastPointerInsideUtc = DateTime.UtcNow;
			SetExplorerButtonHovered(hit.IsExplorerButton);
			SetQuickLaunchHovered(hit.QuickLaunchApp);
			SetExpandAllButtonHovered(hit.IsExpandAllButton);
			SetCollapseButtonHovered(hit.IsSidebarCollapseButton);
			SetDesktopButtonHovered(hit.IsDesktopButton);
			SetDesktopIconsButtonHovered(hit.IsDesktopIconsButton);
			SetNotificationAreaControlHovered(hit);
			SetPinButtonHovered(hit.IsPinButton);
			SetNotificationAreaHovered(hit);
		}
		if (hit.IsExplorerButton || hit.IsDesktopButton || hit.IsDesktopIconsButton || hit.IsExpandAllButton || hit.IsPinButton || hit.IsNotificationAreaCard || hit.QuickLaunchApp is not null)
			return;
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
		var hoveredHandle = hit.Window?.Handle ?? IntPtr.Zero;
		if (_hoveredWindowHandle != hoveredHandle || _hoveredGroupCard != hit.IsPrimaryCard)
		{
			_hoveredWindowHandle = hoveredHandle;
			_hoveredGroupCard = hit.IsPrimaryCard;
			LayoutStages(true);
		}
	}

	public void PollPointer(Point clientPoint)
	{
		var hit = HitTest(clientPoint);
		if (hit is not null)
		{
			_lastPointerInsideUtc = DateTime.UtcNow;
			SetExplorerButtonHovered(hit.IsExplorerButton);
			SetQuickLaunchHovered(hit.QuickLaunchApp);
			SetExpandAllButtonHovered(hit.IsExpandAllButton);
			SetCollapseButtonHovered(hit.IsSidebarCollapseButton);
			SetDesktopButtonHovered(hit.IsDesktopButton);
			SetDesktopIconsButtonHovered(hit.IsDesktopIconsButton);
			SetNotificationAreaControlHovered(hit);
			SetPinButtonHovered(hit.IsPinButton);
			SetNotificationAreaHovered(hit);
			if (!_expandAllStages && _expandedStageKey is not null &&
				!_expandedPinned &&
				!string.Equals(_expandedStageKey, hit.StageKey, StringComparison.OrdinalIgnoreCase))
			{
				CollapseExpandedStage();
			}
			return;
		}
		SetExplorerButtonHovered(false);
		SetQuickLaunchHovered(null);
		SetExpandAllButtonHovered(false);
		SetCollapseButtonHovered(false);
		SetDesktopButtonHovered(false);
		SetDesktopIconsButtonHovered(false);
		_notificationAreaCard.SetControlHovered(false, false);
		SetPinButtonHovered(false);
		_notificationAreaCard.SetHovered(null);
		var elapsedSinceCard = DateTime.UtcNow - _lastPointerInsideUtc;
		if (elapsedSinceCard < TimeSpan.FromMilliseconds(500))
			return;
		if (_expandedStageKey is null || _expandAllStages)
		{
			if (_hoveredStageKey is not null)
			{
				_hoveredStageKey = null;
				LayoutStages(true);
			}
			return;
		}
		if (MultiWindowCardInteraction.ShouldCollapseOnPointerLeave(_expandedPinned, elapsedSinceCard))
		{
			CollapseExpandedStage();
			return;
		}
		if (_hoveredWindowHandle != IntPtr.Zero || _hoveredGroupCard)
		{
			_hoveredWindowHandle = IntPtr.Zero;
			_hoveredGroupCard = false;
			LayoutStages(true);
		}
	}

	public bool CanExpandOnHover(CardHitTarget target)
	{
		if (_disposed ||
			HasExpandedStage ||
			target.IsExpandAllButton ||
			target.IsExplorerButton ||
			target.QuickLaunchApp is not null ||
			target.IsPinButton ||
			target.IsNotificationAreaCard ||
			target.IsNotificationAreaDragHandle ||
			target.IsNotificationAreaRefreshButton ||
			target.IsSidebarCollapseButton ||
			target.IsDesktopButton ||
			target.IsDesktopIconsButton ||
			target.PageDelta != 0 ||
			!_stages.TryGetValue(target.StageKey, out var stage))
			return false;

		return MultiWindowCardInteraction.ShouldExpandOnHover(
			stage.Windows.Count,
			isExpandedStage: false,
			target.IsPrimaryCard);
	}

	public bool TryExpandHoveredPrimaryCard(Point clientPoint, string expectedStageKey)
	{
		if (_disposed || HasExpandedStage)
			return false;

		var hit = HitTest(clientPoint);
		if (hit is null ||
			!string.Equals(hit.StageKey, expectedStageKey, StringComparison.OrdinalIgnoreCase) ||
			!CanExpandOnHover(hit))
			return false;

		ExpandStage(hit.StageKey, expandedByHover: true);
		return true;
	}

	public IWindow? ActivateAt(Point clientPoint)
	{
		return Activate(HitTest(clientPoint));
	}

	public IWindow? Activate(CardHitTarget? hit)
	{
		if (hit is null)
			return null;
		if (hit.IsExplorerButton)
		{
			_explorerRequested = true;
			_explorerButton.SetPressed(true);
			return null;
		}
		if (hit.QuickLaunchApp is { } quickLaunchApp)
		{
			_quickLaunchRequested = quickLaunchApp;
			(quickLaunchApp == QuickLaunchApp.Chrome ? _chromeButton : _edgeButton).SetPressed(true);
			return null;
		}
		if (hit.IsNotificationAreaDragHandle)
		{
			_notificationAreaCard.SetControlPressed(true, false);
			return null;
		}
		if (hit.IsNotificationAreaRefreshButton)
		{
			_notificationAreaCard.SetControlPressed(false, true);
			return null;
		}
		if (hit.IsExpandAllButton)
		{
			_expandAllButton.SetPressed(true);
			ToggleExpandAllStages();
			return null;
		}
		if (hit.IsSidebarCollapseButton)
		{
			_collapseRequested = true;
			_collapseButton.SetPressed(true);
			return null;
		}
		if (hit.IsDesktopButton)
		{
			_desktopToggleRequested = true;
			_desktopButton.SetPressed(true);
			return null;
		}
		if (hit.IsDesktopIconsButton)
		{
			_desktopIconsToggleRequested = true;
			_desktopIconsButton.SetPressed(true);
			return null;
		}
		if (hit.IsNotificationAreaCard)
		{
			_notificationAreaCard.SetPressed(true);
			if (hit.NotificationIcon is { } activation)
				_notificationIconActivationRequest = activation;
			else
				_notificationAreaOpenRequested = true;
			return null;
		}
		if (hit.PageDelta != 0)
		{
			_expandedByHover = false;
			ChangeExpandedPage(hit.PageDelta);
			return null;
		}
		if (!_stages.TryGetValue(hit.StageKey, out var stage))
			return null;
		if (hit.IsPinButton)
		{
			if (_pinButtonEnabled &&
				_expandedStageKey is not null &&
				string.Equals(_expandedStageKey, hit.StageKey, StringComparison.OrdinalIgnoreCase))
			{
				stage.PinButton.SetPressed(true);
				_expandedPinned = !_expandedPinned;
				_expandedByHover = !_expandedPinned;
				LayoutStages(true);
			}
			return null;
		}
		if (_expandAllStages)
			return hit.Window;
		var isExpandedStage = string.Equals(_expandedStageKey, hit.StageKey, StringComparison.OrdinalIgnoreCase);
		var action = MultiWindowCardInteraction.Decide(
			stage.Windows.Count,
			isExpandedStage,
			hit.IsPrimaryCard,
			_expandedPinned);
		if (action == MultiWindowCardClickAction.Expand)
		{
			ExpandStage(hit.StageKey, expandedByHover: false);
			return null;
		}
		if (action == MultiWindowCardClickAction.KeepExpanded)
			return null;
		if (action == MultiWindowCardClickAction.Collapse)
		{
			CollapseExpandedStage();
			return null;
		}
		if (hit.Window is not null)
			_expandedByHover = false;
		return hit.Window;
	}

	private void ExpandStage(string stageKey, bool expandedByHover)
	{
		SetPinButtonHovered(false);
		_expandedStageKey = stageKey;
		_expandedByHover = expandedByHover;
		_expandedPinned = false;
		_hoveredStageKey = stageKey;
		_expandedPage = 0;
		_hoveredWindowHandle = IntPtr.Zero;
		_hoveredGroupCard = true;
		_lastPointerInsideUtc = DateTime.UtcNow;
		LayoutStages(true);
	}

	public void Scroll(int wheelDelta)
	{
		if (_disposed || !_sidebarVisible || wheelDelta == 0)
			return;
		var animated = _animationsEnabled && NativeMethods.ClientAreaAnimationsEnabled;
		_scrollMotion.AddWheel(wheelDelta, (CardSize.Y + Gap) * 0.5f, animated);
		if (!animated)
		{
			ApplyScrollTranslation();
			ScrollPositionChanged?.Invoke(this, EventArgs.Empty);
			return;
		}
		if (!_scrollTimer.Enabled && _scrollMotion.IsMoving)
		{
			_lastScrollTick = Stopwatch.GetTimestamp();
			_scrollTimer.Start();
		}
	}

	private void AdvanceScroll()
	{
		var now = Stopwatch.GetTimestamp();
		_scrollMotion.Advance(Stopwatch.GetElapsedTime(_lastScrollTick, now).TotalSeconds);
		_lastScrollTick = now;
		ApplyScrollTranslation();
		ScrollPositionChanged?.Invoke(this, EventArgs.Empty);
		if (!_scrollMotion.IsMoving)
			_scrollTimer.Stop();
	}

	public void FinishScroll()
	{
		_scrollTimer.Stop();
		_scrollMotion.SnapToTarget();
		ApplyScrollTranslation();
	}

	private void ApplyScrollTranslation()
	{
		_mainCamera.Offset = new Vector3(0, ScrollTranslationY, 0);
		foreach (var (stage, top, height) in _stageScrollBounds)
		{
			var movingTop = stage.Motion.Current.Offset.Y;
			stage.Root.IsVisible = Math.Max(top, movingTop) + height + ScrollTranslationY >= -80f * _dpiScale &&
				Math.Min(top, movingTop) + ScrollTranslationY <= _viewportHeight + 80f * _dpiScale;
		}
	}

	public void CollapseExpandedStage(bool force = false)
	{
		if (_expandedStageKey is null || !MultiWindowCardInteraction.CanCollapseExpandedStage(_expandedPinned, force))
			return;
		SetPinButtonHovered(false);
		_expandedStageKey = null;
		_expandedByHover = false;
		_expandedPinned = false;
		_hoveredStageKey = null;
		_hoveredWindowHandle = IntPtr.Zero;
		_hoveredGroupCard = false;
		_expandedPage = 0;
		LayoutStages(true);
	}

	private void ToggleExpandAllStages()
	{
		_expandAllStages = !_expandAllStages;
		if (_expandAllStages && !_expandedPinned)
		{
			_expandedStageKey = null;
			_expandedByHover = false;
			_expandedPage = 0;
		}
		_hoveredWindowHandle = IntPtr.Zero;
		_hoveredGroupCard = false;
		_expandAllButton.SetActive(_expandAllStages);
		LayoutStages(true);
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_scrollTimer.Stop();
		_scrollTimer.Dispose();
		_layoutMotionTimer.Stop();
		_layoutMotionTimer.Dispose();
		if (_deferredCapture is { } deferred) deferred.Frame.Dispose();
		_deferredCapture = null;
		ScrollPositionChanged = null;
		_captureTimer.Stop();
		_captureTimer.Dispose();
		foreach (var stage in _stages.Values)
			stage.Dispose();
		_stages.Clear();
		_expandAllButton.Dispose();
		_explorerButton.Dispose();
		_collapseButton.Dispose();
		_desktopButton.Dispose();
		_desktopIconsButton.Dispose();
		_chromeButton.Dispose();
		_edgeButton.Dispose();
		_notificationAreaCard.Dispose();
		_cameraRoot.Children.Remove(_mainCamera);
		_cameraRoot.Children.Remove(_fixedCamera);
		_mainCamera.Dispose();
		_fixedCamera.Dispose();
		_stageScrollBounds.Clear();
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
	// The old 2x backing textures consumed four times the pixel memory needed by
	// the on-screen cards. 1.25x remains crisp after the perspective transform.
	private int CardPixelWidth => Math.Max(128, (int)Math.Ceiling(CardSize.X * 1.25f));
	private int CardPixelHeight => Math.Max(80, (int)Math.Ceiling(CardSize.Y * 1.25f));

	private static float NormalizeCardScale(double cardScale) => (float)Math.Clamp(cardScale, 0.55, 1.25);

	private void LayoutStages(bool animate)
	{
		if (_disposed || _viewportHeight <= 0)
			return;
		animate &= _animationsEnabled && NativeMethods.ClientAreaAnimationsEnabled;
		_hitTargets.Clear();
		_passivePolygons.Clear();
		_stageScrollBounds.Clear();
		var toolbarKey = new Vector2(_dpiScale, CardSize.X);
		if (_toolbarLayoutKey != toolbarKey)
		{
			_toolbarLayoutKey = toolbarKey;
			_expandAllButton.SetLayout(_dpiScale, CardSize.X);
			_explorerButton.SetLayout(_dpiScale, CardSize.X);
			_chromeButton.SetLayout(_dpiScale, CardSize.X);
			_edgeButton.SetLayout(_dpiScale, CardSize.X);
			_collapseButton.SetLayout(_dpiScale, CardSize.X);
			_desktopButton.SetLayout(_dpiScale, CardSize.X);
			_desktopIconsButton.SetLayout(_dpiScale, CardSize.X);
			_notificationAreaCard.SetLayout(_dpiScale, CardSize.X);
		}
		var headerCardGap = 12f * _dpiScale;
		var headerHeight = _expandAllButton.Size.Y + headerCardGap +
			(_explorerButtonEnabled ? _explorerButton.Size.Y + headerCardGap : 0f) +
			(_chromeQuickLaunchEnabled ? _chromeButton.Size.Y + headerCardGap : 0f) +
			(_edgeQuickLaunchEnabled ? _edgeButton.Size.Y + headerCardGap : 0f);
		var trailingGap = 12f * _dpiScale;
		var trailingHeight =
			(_notificationAreaCardEnabled ? trailingGap + _notificationAreaCard.Size.Y : 0f) +
			(_desktopButtonEnabled ? trailingGap + _desktopButton.Size.Y : 0f) +
			(_desktopIconsButtonEnabled ? trailingGap + _desktopIconsButton.Size.Y : 0f) +
			(_collapseButtonEnabled ? trailingGap + _collapseButton.Size.Y : 0f);
		var cardViewportHeight = _viewportHeight;
		var cardSize = CardSize;
		var stride = cardSize.Y + Gap;
		var cardsHeight = Math.Max(0, _snapshots.Count * stride - Gap);
		var baseTotalHeight = headerHeight + cardsHeight;
		var expandedExtraHeight = _expandAllStages
			? _stages.Values.Where(stage => stage.Windows.Count > 1).Sum(stage => GetExpandedExtraHeight(stage, true))
			: _expandedStageKey is not null && _stages.TryGetValue(_expandedStageKey, out var expandedStage)
				? GetExpandedExtraHeight(expandedStage, false)
				: 0f;
		var columnLayout = SidebarExpansionLayout.Calculate(baseTotalHeight + trailingHeight,
			expandedExtraHeight, cardViewportHeight, _dpiScale, _sidebarVerticalOffset * _dpiScale);
		_scrollMotion.SetRange(columnLayout.ScrollRange, _dpiScale);
		var startY = columnLayout.StartY;
		var cameraCenter = new Vector2(_viewportWidth / 2f, _viewportHeight / 2f);
		var currentY = startY;
		var lowestVisibleCardBottom = float.NaN;
		lowestVisibleCardBottom = LayoutExpandAllButton(currentY);
		currentY += _expandAllButton.Size.Y + headerCardGap;
		if (_explorerButtonEnabled)
		{
			lowestVisibleCardBottom = LayoutExplorerButton(currentY);
			currentY += _explorerButton.Size.Y + headerCardGap;
		}
		else
		{
			_explorerButton.SetVisible(false);
		}
		if (_chromeQuickLaunchEnabled)
		{
			lowestVisibleCardBottom = LayoutQuickLaunchButton(_chromeButton, currentY);
			currentY += _chromeButton.Size.Y + headerCardGap;
		}
		else
		{
			_chromeButton.SetVisible(false);
		}
		if (_edgeQuickLaunchEnabled)
		{
			lowestVisibleCardBottom = LayoutQuickLaunchButton(_edgeButton, currentY);
			currentY += _edgeButton.Size.Y + headerCardGap;
		}
		else
		{
			_edgeButton.SetVisible(false);
		}

		for (var stageIndex = 0; stageIndex < _snapshots.Count; stageIndex++)
		{
			var snapshot = _snapshots[stageIndex];
			if (!_stages.TryGetValue(snapshot.Key, out var stage))
				continue;
			var isExpanded = stage.Windows.Count > 1 && (_expandAllStages || string.Equals(snapshot.Key, _expandedStageKey, StringComparison.OrdinalIgnoreCase));
			var stageExpandedExtraHeight = isExpanded ? GetExpandedExtraHeight(stage, _expandAllStages) : 0f;
			const float stageScale = 1f;
			var stageOffset = new Vector3(12 * _dpiScale, currentY, 0);
			stage.Motion.Set(stageOffset, Vector3.One, 0, animate);
			var stageVisualHeight = cardSize.Y + stageExpandedExtraHeight;
			_stageScrollBounds.Add((stage, stageOffset.Y, stageVisualHeight * stageScale));

			if (isExpanded)
				LayoutExpandedStage(stage, stageOffset, stageScale, cameraCenter, animate, _expandAllStages);
			else
				LayoutCollapsedStage(stage, stageOffset, stageScale, cameraCenter, animate, stageIndex);
			lowestVisibleCardBottom = float.IsNaN(lowestVisibleCardBottom)
				? stageOffset.Y + stageVisualHeight * stageScale
				: Math.Max(lowestVisibleCardBottom, stageOffset.Y + stageVisualHeight * stageScale);
			currentY += stride + stageExpandedExtraHeight;
		}
		if (_notificationAreaCardEnabled)
		{
			LayoutNotificationAreaCard(currentY);
			currentY += _notificationAreaCard.Size.Y + trailingGap;
		}
		else
			_notificationAreaCard.SetVisible(false);
		if (_desktopButtonEnabled)
		{
			LayoutDesktopButton(currentY);
			currentY += _desktopButton.Size.Y + trailingGap;
		}
		else
			_desktopButton.SetVisible(false);
		if (_desktopIconsButtonEnabled)
		{
			LayoutDesktopIconsButton(currentY);
			currentY += _desktopIconsButton.Size.Y + trailingGap;
		}
		else
			_desktopIconsButton.SetVisible(false);
		if (_collapseButtonEnabled)
			LayoutCollapseButton(currentY);
		else
			_collapseButton.SetVisible(false);
		ApplyScrollTranslation();
		_hitTargets.Sort((left, right) => right.ZOrder.CompareTo(left.ZOrder));
		TrimWarmPreviews();
		if (HasLayoutMotion) _layoutMotionTimer.Start();
		LayoutRevision++;
	}

	private bool HasLayoutMotion => _stages.Values.Any(stage => stage.Motion.IsMoving ||
		stage.GroupCard?.Motion.IsMoving == true || stage.Windows.Any(card => card.IsVisible && card.Motion.IsMoving));

	private void FinishLayoutMotion()
	{
		if (_disposed || HasLayoutMotion) return;
		_layoutMotionTimer.Stop();
		ApplyScrollTranslation();
		LayoutRevision++;
		ScrollPositionChanged?.Invoke(this, EventArgs.Empty);
		if (_deferredCapture is { } capture)
		{
			_deferredCapture = null;
			UploadCapturedFrame(capture.Card, capture.Frame);
		}
		ScheduleCaptures();
	}

	private void TrimWarmPreviews()
	{
		var now = DateTime.UtcNow;
		long budget = 4L * 1024 * 1024;
		var slots = 8;
		foreach (var card in _stages.Values.SelectMany(stage => stage.Windows)
			.Where(card => !card.IsVisible && card.HasSurface).OrderByDescending(card => card.HiddenSinceUtc))
		{
			var bytes = card.EstimatedSurfaceBytes;
			if (now - card.HiddenSinceUtc >= TimeSpan.FromSeconds(8) || slots <= 0 || bytes > budget)
				card.ReleaseSurface();
			else { budget -= bytes; slots--; }
		}
	}

	private CardHitProjection ProjectLive(StageCardVisual stage, WindowCardVisual card,
		Vector3? overlayOffset = null, Vector2? overlaySize = null) =>
		new(stage.Motion, card.Motion, CardSize, card.Pivot,
			new Vector2(_viewportWidth / 2f, _viewportHeight / 2f), PerspectiveDistance * _dpiScale, overlayOffset, overlaySize);

	private float LayoutExpandAllButton(float y)
	{
		_mainCamera.Children.Remove(_expandAllButton.Root);
		_mainCamera.Children.InsertAtTop(_expandAllButton.Root);
		var x = 12f * _dpiScale;
		_expandAllButton.SetOffset(new Vector3(x, y, 0));
		_expandAllButton.SetVisible(true);
		var polygon = Card3DGeometry.ProjectCard(
			new Vector3(x, y, 0),
			1f,
			Vector3.Zero,
			Vector3.One,
			_expandAllButton.Angle,
			_expandAllButton.Size,
			_expandAllButton.Pivot,
			new Vector2(_viewportWidth / 2f, _viewportHeight / 2f),
			PerspectiveDistance * _dpiScale);
		_hitTargets.Add(new CardHitTarget(
			"__expand_all__",
			null,
			polygon,
			int.MaxValue - 2,
			IsExpandAllButton: true));
		return y + _expandAllButton.Size.Y;
	}

	private float LayoutExplorerButton(float y)
	{
		if (!_explorerButtonEnabled)
		{
			_explorerButton.SetVisible(false);
			return float.NaN;
		}

		_mainCamera.Children.Remove(_explorerButton.Root);
		_mainCamera.Children.InsertAtTop(_explorerButton.Root);
		var x = 12f * _dpiScale;
		_explorerButton.SetOffset(new Vector3(x, y, 0));
		_explorerButton.SetVisible(true);
		var polygon = Card3DGeometry.ProjectCard(
			new Vector3(x, y, 0),
			1f,
			Vector3.Zero,
			Vector3.One,
			_explorerButton.Angle,
			_explorerButton.Size,
			_explorerButton.Pivot,
			new Vector2(_viewportWidth / 2f, _viewportHeight / 2f),
			PerspectiveDistance * _dpiScale);
		_hitTargets.Add(new CardHitTarget(
			"__open_explorer__",
			null,
			polygon,
			int.MaxValue - 1,
			IsExplorerButton: true));
		return y + _explorerButton.Size.Y;
	}

	private float LayoutQuickLaunchButton(SidebarQuickLaunchVisual button, float y)
	{
		_mainCamera.Children.Remove(button.Root);
		_mainCamera.Children.InsertAtTop(button.Root);
		var x = 12f * _dpiScale;
		button.SetOffset(new Vector3(x, y, 0));
		button.SetVisible(true);
		var polygon = Card3DGeometry.ProjectCard(
			new Vector3(x, y, 0),
			1f,
			Vector3.Zero,
			Vector3.One,
			button.Angle,
			button.Size,
			button.Pivot,
			new Vector2(_viewportWidth / 2f, _viewportHeight / 2f),
			PerspectiveDistance * _dpiScale);
		_hitTargets.Add(new CardHitTarget(
			$"__quick_launch_{button.App}__",
			null,
			polygon,
			int.MaxValue - 1,
			QuickLaunchApp: button.App));
		return y + button.Size.Y;
	}

	private float LayoutNotificationAreaCard(float y)
	{
		_mainCamera.Children.Remove(_notificationAreaCard.Root);
		_mainCamera.Children.InsertAtTop(_notificationAreaCard.Root);
		var x = 12f * _dpiScale;
		_notificationAreaCard.SetOffset(new Vector3(x, y, 0));
		_notificationAreaCard.SetVisible(true);
		var cameraCenter = new Vector2(_viewportWidth / 2f, _viewportHeight / 2f);
		var polygon = Card3DGeometry.ProjectCard(
			new Vector3(x, y, 0),
			1f,
			Vector3.Zero,
			Vector3.One,
			_notificationAreaCard.Angle,
			_notificationAreaCard.Size,
			_notificationAreaCard.Pivot,
			cameraCenter,
			PerspectiveDistance * _dpiScale);
		_hitTargets.Add(new CardHitTarget(
			"__notification_area__",
			null,
			polygon,
			int.MaxValue - 4,
			IsNotificationAreaCard: true));
		foreach (var slot in _notificationAreaCard.Slots)
		{
			var bounds = slot.Bounds;
			var iconPolygon = new[]
			{
				new Vector2(x + bounds.Left, y + bounds.Top),
				new Vector2(x + bounds.Right, y + bounds.Top),
				new Vector2(x + bounds.Right, y + bounds.Bottom),
				new Vector2(x + bounds.Left, y + bounds.Bottom)
			};
			_hitTargets.Add(new CardHitTarget(
				"__notification_area__",
				null,
				iconPolygon,
				int.MaxValue - 3,
				IsNotificationAreaCard: true,
				NotificationIcon: slot.Activation,
				NotificationIconName: slot.Name));
		}
		return y + _notificationAreaCard.Size.Y;
	}

	private void LayoutDesktopButton(float y)
	{
		_mainCamera.Children.Remove(_desktopButton.Root);
		_mainCamera.Children.InsertAtTop(_desktopButton.Root);
		var x = 12f * _dpiScale;
		_desktopButton.SetOffset(new Vector3(x, y, 0));
		_desktopButton.SetVisible(true);
		var polygon = Card3DGeometry.ProjectCard(
			new Vector3(x, y, 0), 1f, Vector3.Zero, Vector3.One,
			_desktopButton.Angle, _desktopButton.Size, _desktopButton.Pivot,
			new Vector2(_viewportWidth / 2f, _viewportHeight / 2f),
			PerspectiveDistance * _dpiScale);
		_hitTargets.Add(new CardHitTarget(
			"__show_desktop__", null, polygon, int.MaxValue - 4,
			IsDesktopButton: true));
	}

	private void LayoutDesktopIconsButton(float y)
	{
		_mainCamera.Children.Remove(_desktopIconsButton.Root);
		_mainCamera.Children.InsertAtTop(_desktopIconsButton.Root);
		var x = 12f * _dpiScale;
		_desktopIconsButton.SetOffset(new Vector3(x, y, 0));
		_desktopIconsButton.SetVisible(true);
		var polygon = Card3DGeometry.ProjectCard(
			new Vector3(x, y, 0), 1f, Vector3.Zero, Vector3.One,
			_desktopIconsButton.Angle, _desktopIconsButton.Size, _desktopIconsButton.Pivot,
			new Vector2(_viewportWidth / 2f, _viewportHeight / 2f),
			PerspectiveDistance * _dpiScale);
		_hitTargets.Add(new CardHitTarget(
			"__desktop_icons__", null, polygon, int.MaxValue - 4,
			IsDesktopIconsButton: true));
	}

	private void LayoutCollapseButton(float y)
	{
		if (!_collapseButtonEnabled)
		{
			_collapseButton.SetVisible(false);
			return;
		}
		_mainCamera.Children.Remove(_collapseButton.Root);
		_mainCamera.Children.InsertAtTop(_collapseButton.Root);
		var x = 12f * _dpiScale;
		_collapseButton.SetOffset(new Vector3(x, y, 0));
		_collapseButton.SetVisible(true);
		var polygon = Card3DGeometry.ProjectCard(
			new Vector3(x, y, 0),
			1f,
			Vector3.Zero,
			Vector3.One,
			_collapseButton.Angle,
			_collapseButton.Size,
			_collapseButton.Pivot,
			new Vector2(_viewportWidth / 2f, _viewportHeight / 2f),
			PerspectiveDistance * _dpiScale);
		_hitTargets.Add(new CardHitTarget(
			"__sidebar_collapse__",
			null,
			polygon,
			int.MaxValue,
			IsSidebarCollapseButton: true));
	}

	private void SetCollapseButtonHovered(bool hovered)
	{
		if (!_collapseButtonEnabled)
			hovered = false;
		if (_collapseButtonHovered == hovered)
			return;
		_collapseButtonHovered = hovered;
		_collapseButton.SetHovered(hovered);
	}

	private void SetDesktopButtonHovered(bool hovered)
	{
		if (!_desktopButtonEnabled)
			hovered = false;
		if (_desktopButtonHovered == hovered)
			return;
		_desktopButtonHovered = hovered;
		_desktopButton.SetHovered(hovered);
	}

	private void SetDesktopIconsButtonHovered(bool hovered)
	{
		if (!_desktopIconsButtonEnabled)
			hovered = false;
		if (_desktopIconsButtonHovered == hovered)
			return;
		_desktopIconsButtonHovered = hovered;
		_desktopIconsButton.SetHovered(hovered);
	}

	private void SetExplorerButtonHovered(bool hovered)
	{
		if (!_explorerButtonEnabled)
			hovered = false;
		if (_explorerButtonHovered == hovered)
			return;
		_explorerButtonHovered = hovered;
		_explorerButton.SetHovered(hovered);
	}

	private void SetQuickLaunchHovered(QuickLaunchApp? app)
	{
		_chromeButton.SetHovered(_chromeQuickLaunchEnabled && app == QuickLaunchApp.Chrome);
		_edgeButton.SetHovered(_edgeQuickLaunchEnabled && app == QuickLaunchApp.Edge);
	}

	private void SetExpandAllButtonHovered(bool hovered)
	{
		if (_expandAllButtonHovered == hovered)
			return;
		_expandAllButtonHovered = hovered;
		_expandAllButton.SetHovered(hovered);
	}

	private void SetNotificationAreaHovered(CardHitTarget? target)
	{
		var activation = target is { IsNotificationAreaCard: true }
			? target.NotificationIcon
			: null;
		_notificationAreaCard.SetHovered(activation);
	}

	private void SetNotificationAreaControlHovered(CardHitTarget? target) =>
		_notificationAreaCard.SetControlHovered(
			target?.IsNotificationAreaDragHandle == true,
			target?.IsNotificationAreaRefreshButton == true);

	private void SetPinButtonHovered(bool hovered)
	{
		if (_pinButtonHovered == hovered)
			return;
		_pinButtonHovered = hovered;
		if (_expandedStageKey is not null && _stages.TryGetValue(_expandedStageKey, out var stage))
			stage.PinButton.SetHovered(hovered);
	}

	private float GetExpandedExtraHeight(StageCardVisual stage, bool expandAll)
	{
		var visibleCount = 1 + (expandAll ? stage.Windows.Count : Math.Min(PageSize - 1, stage.Windows.Count));
		if (visibleCount <= 1)
			return 0;
		var cardSize = CardSize;
		var stride = Card3DGeometry.CalculateExpandedListStride(cardSize.Y, _dpiScale);
		var paginationHeight = !expandAll && stage.Windows.Count > PageSize - 1
			? Math.Max(32f, cardSize.Y * 0.46f) + 12f * _dpiScale
			: 0;
		return (visibleCount - 1) * stride + paginationHeight;
	}

	private void LayoutCollapsedStage(StageCardVisual stage, Vector3 stageOffset, float stageScale, Vector2 cameraCenter, bool animate, int stageIndex)
	{
		var cardSize = CardSize;
		stage.PinButton.SetVisible(false);
		stage.SetPaginationVisible(false);
		stage.HideExpandedConnector();
		if (stage.Windows.Count > 1 && stage.GroupCard is not null)
		{
			stage.ShowCollapsedGroup(2);
			stage.ArrangeCollapsedGroupZOrder();
			for (var index = stage.Windows.Count - 1; index >= 0; index--)
			{
				var card = stage.Windows[index];
				if (index >= 2)
				{
					card.SetVisible(false);
					continue;
				}
				var transform = Card3DGeometry.CreateCollapsedStackTransform(index + 1, false, _dpiScale);
				card.SetVisible(true);
				card.SetTransform(transform.Offset, transform.Scale, transform.Angle, animate);
				card.DesiredBadge = null;
			}

			var hovered = string.Equals(stage.Key, _hoveredStageKey, StringComparison.OrdinalIgnoreCase);
			var groupTransform = Card3DGeometry.CreateCollapsedStackTransform(0, hovered, _dpiScale);
			stage.GroupCard.SetVisible(true);
			stage.GroupCard.SetTransform(groupTransform.Offset, groupTransform.Scale, groupTransform.Angle, animate);
			var groupPolygon = Card3DGeometry.ProjectCard(
				stageOffset,
				stageScale,
				groupTransform.Offset,
				groupTransform.Scale,
				groupTransform.Angle,
				cardSize,
				stage.GroupCard.Pivot,
				cameraCenter,
				PerspectiveDistance * _dpiScale);
			_hitTargets.Add(new CardHitTarget(
				stage.Key,
				null,
				groupPolygon,
				stageIndex * 20 + 20 + (hovered ? 10 : 0),
				IsPrimaryCard: true, Projection: ProjectLive(stage, stage.GroupCard)));
			return;
		}

		stage.HideGroupCard();
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
			_hitTargets.Add(new CardHitTarget(
				stage.Key,
				card.Window,
				polygon,
				stageIndex * 20 + (3 - index) + (hovered ? 10 : 0),
				IsPrimaryCard: index == 0, Projection: ProjectLive(stage, card)));
		}
	}

	private void LayoutExpandedStage(StageCardVisual stage, Vector3 stageOffset, float stageScale, Vector2 cameraCenter, bool animate, bool expandAll)
	{
		var cardSize = CardSize;
		var expandedPage = expandAll
			? null
			: MultiWindowCardInteraction.CreateExpandedChildPage(stage.Windows, _expandedPage, PageSize - 1);
		if (expandedPage is not null)
			_expandedPage = expandedPage.PageIndex;
		var pageCount = expandedPage?.PageCount ?? 1;
		var page = expandAll ? stage.Windows.ToArray() : expandedPage!.VisibleChildren.ToArray();
		var pageHandles = page.Select(card => card.Window.Handle).ToHashSet();
		foreach (var card in stage.Windows)
			card.SetVisible(pageHandles.Contains(card.Window.Handle));
		if (stage.GroupCard is null)
			return;
		stage.GroupCard.SetVisible(true);

		var stride = Card3DGeometry.CalculateExpandedListStride(cardSize.Y, _dpiScale);
		var childIndent = 18f * _dpiScale;
		var hoveredChildIndex = Array.FindIndex(page, card => card.Window.Handle == _hoveredWindowHandle);
		var hoveredIndex = hoveredChildIndex < 0 ? -1 : hoveredChildIndex + 1;
		stage.SetExpandedConnectorLayout(page.Length + 1, cardSize.Y, stride, childIndent, _dpiScale);
		stage.ArrangeExpandedZOrder(page, _hoveredWindowHandle);

		var groupTransform = Card3DGeometry.CreateCollapsedStackTransform(0,
			_hoveredGroupCard && string.Equals(stage.Key, _hoveredStageKey, StringComparison.OrdinalIgnoreCase), _dpiScale);
		stage.GroupCard.SetTransform(groupTransform.Offset, groupTransform.Scale, groupTransform.Angle, animate);
		var groupPolygon = Card3DGeometry.ProjectCard(
			stageOffset,
			stageScale,
			groupTransform.Offset,
			groupTransform.Scale,
			groupTransform.Angle,
			cardSize,
			stage.GroupCard.Pivot,
			cameraCenter,
			PerspectiveDistance * _dpiScale);
		_hitTargets.Add(new CardHitTarget(stage.Key, null, groupPolygon, 10100, IsPrimaryCard: true, Projection: ProjectLive(stage, stage.GroupCard)));

		if (_pinButtonEnabled && !expandAll)
		{
			// The pin is a child of the primary card, so it inherits the exact same
			// pivot, scale and Y-axis rotation rather than approximating the card's
			// projected center from the camera root.
			stage.PinButton.SetLayout(_dpiScale, 0f, _expandedPinned);
			var pinOffset = Card3DGeometry.CreateExpandedPinOffset(cardSize, stage.PinButton.Size, _dpiScale);
			stage.PinButton.SetOffset(pinOffset);
			stage.PinButton.SetVisible(true);
			stage.PinButton.SetHovered(_pinButtonHovered);
			var pinPolygon = Card3DGeometry.ProjectCardOverlay(
				stageOffset,
				stageScale,
				groupTransform.Offset,
				groupTransform.Scale,
				groupTransform.Angle,
				cardSize,
				stage.GroupCard.Pivot,
				pinOffset,
				stage.PinButton.Size,
				cameraCenter,
				PerspectiveDistance * _dpiScale);
			var hitPadding = 8f * _dpiScale;
			var hitSize = stage.PinButton.Size + new Vector2(hitPadding * 2f);
			var hitOffset = pinOffset - new Vector3(hitPadding, hitPadding, 0);
			var hitPolygon = Card3DGeometry.ProjectCardOverlay(
				stageOffset,
				stageScale,
				groupTransform.Offset,
				groupTransform.Scale,
				groupTransform.Angle,
				cardSize,
				stage.GroupCard.Pivot,
				hitOffset,
				hitSize,
				cameraCenter,
				PerspectiveDistance * _dpiScale);
			_hitTargets.Add(new CardHitTarget(
				stage.Key,
				null,
				hitPolygon,
				25000,
				IsPinButton: true, Projection: ProjectLive(stage, stage.GroupCard, hitOffset, hitSize)));
			_passivePolygons.Add(pinPolygon);
		}
		else
		{
			stage.PinButton.SetVisible(false);
		}

		for (var index = 0; index < page.Length; index++)
		{
			var card = page[index];
			var hovered = card.Window.Handle == _hoveredWindowHandle;
			var transform = Card3DGeometry.CreateExpandedListTransform(
				index + 1,
				hoveredIndex,
				_dpiScale,
				stride,
				childIndent);
			card.SetTransform(transform.Offset, transform.Scale, transform.Angle, animate);
			card.DesiredBadge = !expandAll && index == page.Length - 1 && pageCount > 1 ? $"{_expandedPage + 1}/{pageCount}" : null;
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
			_hitTargets.Add(new CardHitTarget(
				stage.Key,
				card.Window,
				polygon,
				10000 + index + (hovered ? 100 : 0), Projection: ProjectLive(stage, card)));
		}

		stage.SetPaginationVisible(!expandAll && pageCount > 1);
		if (!expandAll && pageCount > 1)
		{
			var buttonsY = cardSize.Y + page.Length * stride + 7 * _dpiScale;
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
		var pageCount = Math.Max(1, (int)Math.Ceiling(expanded.Windows.Count / (double)(PageSize - 1)));
		if (pageCount <= 1)
			return;
		_expandedPage = (_expandedPage + delta + pageCount) % pageCount;
		_hoveredWindowHandle = IntPtr.Zero;
		LayoutStages(true);
	}

	private void ScheduleCaptures()
	{
		if (_disposed) return;
		TrimWarmPreviews();
		if (_layoutMotionTimer.Enabled || _deferredCapture is not null) return;
		if (_disposed || (_pausePreviewRefreshWhenHidden && !_sidebarVisible && !_manualRefreshPending))
			return;
		lock (_captureGate)
		{
			if (_capturesInFlight.Count > 0)
				return;
		}
		var nowUtc = DateTime.UtcNow;
		var due = _stages.Values
			.Where(stage => stage.Root.IsVisible)
			.SelectMany(stage => stage.Windows
				.Concat(stage.GroupCard is { } groupCard ? new[] { groupCard } : Array.Empty<WindowCardVisual>())
				.Select(card => (stage, card)))
			.Where(tuple => tuple.card.IsVisible)
			.Where(tuple => tuple.card.NeedsCapture(nowUtc, _previewRefreshMinutes))
			.OrderByDescending(tuple => tuple.card.Window.Handle == _hoveredWindowHandle)
			.Take(1)
			.ToArray();
		if (due.Length == 0)
			_manualRefreshPending = false;
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
		_ = Task.Run(() => card.IsApplicationGroupCard
			? _capture.CaptureApplicationCard(window, width, height)
			: _capture.Capture(window, width, height, badge)).ContinueWith(task =>
		{
			var disposeCapture = false;
			lock (_captureGate)
			{
				_capturesInFlight.Remove(window.Handle);
				disposeCapture = _disposeCaptureWhenIdle && _capturesInFlight.Count == 0;
			}
			if (disposeCapture)
				_capture.Dispose();
			if (task.IsFaulted || task.IsCanceled)
				return;
			var frame = task.Result;
			if (_disposed || _owner.IsDisposed)
			{
				frame.Dispose();
				return;
			}
			try
			{
				_owner.BeginInvoke(new Action(() =>
				{
					if (!_disposed && _layoutMotionTimer.Enabled)
					{
						if (_deferredCapture is { } previous)
						{
							previous.Card.InvalidateCapture();
							previous.Frame.Dispose();
						}
						_deferredCapture = (card, frame);
					}
					else
						UploadCapturedFrame(card, frame);
				}));
			}
			catch (InvalidOperationException)
			{
				frame.Dispose();
			}
		}, TaskScheduler.Default);
	}
	private void UploadCapturedFrame(WindowCardVisual card, CapturedCardFrame frame)
	{
		using (frame)
		{
			if (!_disposed && _sidebarVisible && card.IsVisible && card.Window.Handle == frame.Handle)
				card.Upload(frame);
			else if (!_disposed && !card.HasSurface)
				card.InvalidateCapture();
		}
	}
}

internal sealed record CardHitTarget(
	string StageKey,
	IWindow? Window,
	IReadOnlyList<Vector2> Polygon,
	int ZOrder,
	int PageDelta = 0,
	bool IsPrimaryCard = false,
	bool IsSidebarCollapseButton = false,
	bool IsExplorerButton = false,
	bool IsExpandAllButton = false,
	bool IsPinButton = false,
	QuickLaunchApp? QuickLaunchApp = null,
	bool IsNotificationAreaCard = false,
	bool IsNotificationAreaDragHandle = false,
	bool IsNotificationAreaRefreshButton = false,
	NotificationIconActivation? NotificationIcon = null,
	string? NotificationIconName = null,
	bool IsDesktopButton = false,
	bool IsDesktopIconsButton = false,
	CardHitProjection? Projection = null);

internal sealed class StageCardVisual : IDisposable
{
	private readonly Compositor _compositor;
	private readonly D3DCompositionDevice _graphics;
	private readonly int _surfaceWidth;
	private readonly int _surfaceHeight;
	private readonly Vector2 _cardSize;
	private string? _groupCardIdentity;
	private Visual[] _arrangedVisuals = Array.Empty<Visual>();

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
		Root.IsVisible = false;
		Motion = new CardVisualMotion(compositor, Root);
		PreviousButton = new PageButtonVisual(compositor, false, cardSize.Y);
		NextButton = new PageButtonVisual(compositor, true, cardSize.Y);
		PinButton = new PinButtonVisual(compositor);
		ExpandedConnector = new ExpandedConnectorVisual(compositor, 5);
		Root.Children.InsertAtBottom(ExpandedConnector.Root);
		Root.Children.InsertAtTop(PreviousButton.Root);
		Root.Children.InsertAtTop(NextButton.Root);
	}

	public string Key { get; }
	public ContainerVisual Root { get; }
	public CardVisualMotion Motion { get; }
	public List<WindowCardVisual> Windows { get; } = new();
	public WindowCardVisual? GroupCard { get; private set; }
	public PageButtonVisual PreviousButton { get; }
	public PageButtonVisual NextButton { get; }
	public PinButtonVisual PinButton { get; }
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
				existing = new WindowCardVisual(_compositor, _graphics, window, _surfaceWidth, _surfaceHeight, _cardSize, false);
				Windows.Insert(Math.Min(index, Windows.Count), existing);
				Root.Children.InsertAtTop(existing.Root);
			}
			else
			{
				existing.UpdateWindow(window);
				var oldIndex = Windows.IndexOf(existing);
				if (oldIndex != index)
				{
					Windows.RemoveAt(oldIndex);
					Windows.Insert(index, existing);
				}
			}
		}
	}

	public void SynchronizeGroupCard(IWindow? representative)
	{
		if (representative is null)
		{
			if (GroupCard is null)
				return;
			GroupCard.Root.Children.Remove(PinButton.Root);
			PinButton.SetVisible(false);
			Root.Children.Remove(GroupCard.Root);
			GroupCard.Dispose();
			GroupCard = null;
			_groupCardIdentity = null;
			return;
		}

		var identity = string.IsNullOrWhiteSpace(representative.ProcessExecutable)
			? representative.ProcessName
			: representative.ProcessExecutable;
		if (GroupCard is null)
		{
			GroupCard = new WindowCardVisual(_compositor, _graphics, representative, _surfaceWidth, _surfaceHeight, _cardSize, true);
			Root.Children.InsertAtTop(GroupCard.Root);
			GroupCard.Root.Children.InsertAtTop(PinButton.Root);
			GroupCard.SetVisible(false);
		}
		else
		{
			GroupCard.UpdateWindow(representative);
		}

		if (!string.Equals(_groupCardIdentity, identity, StringComparison.OrdinalIgnoreCase))
		{
			GroupCard.InvalidateCapture();
			_groupCardIdentity = identity;
		}
	}

	public void ShowCollapsed(int count)
	{
		for (var index = 0; index < Windows.Count; index++)
			Windows[index].SetVisible(index < count);
	}

	public void ShowCollapsedGroup(int previewCount)
	{
		GroupCard?.SetVisible(true);
		for (var index = 0; index < Windows.Count; index++)
			Windows[index].SetVisible(index < previewCount);
	}

	public void HideGroupCard() => GroupCard?.SetVisible(false);

	private void Arrange(Visual[] order)
	{
		if (_arrangedVisuals.SequenceEqual(order)) return;
		foreach (var visual in order)
		{
			Root.Children.Remove(visual);
			Root.Children.InsertAtTop(visual);
		}
		_arrangedVisuals = order;
	}

	public void ArrangeCollapsedZOrder() => Arrange(Windows.AsEnumerable().Reverse().Select(card => (Visual)card.Root).ToArray());

	public void ArrangeCollapsedGroupZOrder() => Arrange(Windows.AsEnumerable().Reverse()
		.Select(card => (Visual)card.Root).Concat(GroupCard is { } group ? new Visual[] { group.Root } : Array.Empty<Visual>()).ToArray());

	public void ArrangeExpandedZOrder(IReadOnlyList<WindowCardVisual> page, IntPtr hoveredHandle)
	{
		var group = GroupCard is { } card ? new Visual[] { card.Root } : Array.Empty<Visual>();
		Arrange(group.Concat(page.OrderBy(card => card.Window.Handle == hoveredHandle).Select(card => (Visual)card.Root))
			.Concat(new Visual[] { PreviousButton.Root, NextButton.Root }).ToArray());
	}

	public void HideAll()
	{
		SetPaginationVisible(false);
		PinButton.SetVisible(false);
		HideExpandedConnector();
		HideGroupCard();
		foreach (var card in Windows)
			card.SetVisible(false);
	}

	public void ReleasePreviewSurfaces()
	{
		GroupCard?.ReleaseSurface();
		foreach (var card in Windows)
			card.ReleaseSurface();
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
		GroupCard?.Root.Children.Remove(PinButton.Root);
		PinButton.Dispose();
		GroupCard?.Dispose();
		GroupCard = null;
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

internal sealed class PinButtonVisual : IDisposable
{
	private readonly Compositor _compositor;
	private readonly SpriteVisual _background;
	private readonly CompositionColorBrush _backgroundBrush;
	private readonly CompositionRoundedRectangleGeometry _geometry;
	private readonly CompositionGeometricClip _clip;
	private readonly SpriteVisual _pinHead;
	private readonly SpriteVisual _pinStem;
	private readonly SpriteVisual _pinPoint;
	private readonly CompositionColorBrush _pinBrush;
	private readonly SpriteVisual _statusDot;
	private readonly SpriteVisual _activeBar;
	private readonly CompositionColorBrush _activeBrush;
	private readonly CompositionEllipseGeometry _statusGeometry;
	private readonly CompositionGeometricClip _statusClip;
	private readonly ContainerVisual _fixLabel;
	private readonly ContainerVisual _edLabel;
	private readonly CompositionColorBrush _labelBrush;
	private readonly List<SpriteVisual> _labelStrokes = new();
	private bool _hovered;
	private bool _pinned;
	private bool _pressed;
	private bool _visible;
	private bool _disposed;

	public PinButtonVisual(Compositor compositor)
	{
		_compositor = compositor;
		Root = compositor.CreateContainerVisual();
		Root.IsVisible = false;
		Root.Opacity = 0f;
		_backgroundBrush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(220, 25, 31, 42));
		_background = compositor.CreateSpriteVisual();
		_background.Brush = _backgroundBrush;
		_geometry = compositor.CreateRoundedRectangleGeometry();
		_clip = compositor.CreateGeometricClip(_geometry);
		_background.Clip = _clip;
		_pinBrush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(226, 229, 235, 244));
		_pinHead = compositor.CreateSpriteVisual();
		_pinHead.Brush = _pinBrush;
		_pinStem = compositor.CreateSpriteVisual();
		_pinStem.Brush = _pinBrush;
		_pinPoint = compositor.CreateSpriteVisual();
		_pinPoint.Brush = _pinBrush;
		_activeBrush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, 255, 201, 76));
		_statusDot = compositor.CreateSpriteVisual();
		_statusDot.Brush = _activeBrush;
		_statusGeometry = compositor.CreateEllipseGeometry();
		_statusClip = compositor.CreateGeometricClip(_statusGeometry);
		_statusDot.Clip = _statusClip;
		_activeBar = compositor.CreateSpriteVisual();
		_activeBar.Brush = _activeBrush;
		_labelBrush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, 241, 245, 252));
		_fixLabel = CreateLabel(compositor, "FIX", _labelBrush);
		_edLabel = CreateLabel(compositor, "ED", _activeBrush);
		_edLabel.IsVisible = false;
		_edLabel.Opacity = 0f;
		Root.Children.InsertAtTop(_background);
		Root.Children.InsertAtTop(_pinStem);
		Root.Children.InsertAtTop(_pinPoint);
		Root.Children.InsertAtTop(_pinHead);
		Root.Children.InsertAtTop(_statusDot);
		Root.Children.InsertAtTop(_activeBar);
		Root.Children.InsertAtTop(_fixLabel);
		Root.Children.InsertAtTop(_edLabel);
		SetLayout(1f, 0f, false);
	}

	public ContainerVisual Root { get; }
	public Vector2 Size { get; private set; }
	public Vector2 Pivot { get; private set; }

	public void SetLayout(float dpiScale, float angle, bool pinned)
	{
		var scale = Math.Max(0.75f, dpiScale);
		Size = new Vector2(104f * scale, 34f * scale);
		Root.Size = Size;
		Root.CenterPoint = new Vector3(Size.X / 2f, Size.Y / 2f, 0);
		Root.RotationAxis = Vector3.UnitY;
		Root.RotationAngleInDegrees = angle;
		Pivot = new Vector2(Root.CenterPoint.X, Root.CenterPoint.Y);
		_background.Size = Size;
		_geometry.Size = Size;
		_geometry.CornerRadius = new Vector2(12f * scale, 12f * scale);
		_pinHead.Size = new Vector2(11f * scale, 6f * scale);
		_pinHead.Offset = new Vector3(46.5f * scale, 7f * scale, 0);
		_pinStem.Size = new Vector2(2.4f * scale, 9f * scale);
		_pinStem.Offset = new Vector3(50.8f * scale, 12f * scale, 0);
		_pinPoint.Size = new Vector2(5f * scale, 5f * scale);
		_pinPoint.Offset = new Vector3(49.5f * scale, 19f * scale, 0);
		_pinPoint.CenterPoint = new Vector3(2.5f * scale, 2.5f * scale, 0);
		_pinPoint.RotationAngleInDegrees = 45f;
		_statusDot.Size = new Vector2(6f * scale, 6f * scale);
		_statusDot.Offset = new Vector3(94f * scale, 4f * scale, 0);
		_statusGeometry.Center = new Vector2(3f * scale, 3f * scale);
		_statusGeometry.Radius = new Vector2(3f * scale, 3f * scale);
		_activeBar.Size = new Vector2(30f * scale, 3f * scale);
		_activeBar.Offset = new Vector3(37f * scale, 29f * scale, 0);
		_fixLabel.Offset = new Vector3(8f * scale, 10f * scale, 0);
		_fixLabel.Scale = new Vector3(scale, scale, 1f);
		_edLabel.Offset = new Vector3(77f * scale, 10f * scale, 0);
		_edLabel.Scale = new Vector3(scale, scale, 1f);
		var pinnedChanged = _pinned != pinned;
		_pinned = pinned;
		ApplyState();
		if (pinnedChanged && _visible && _pinned)
			AnimateEdReveal();
	}

	public void SetOffset(Vector3 offset) => Root.Offset = offset;

	public void SetVisible(bool visible)
	{
		if (_visible == visible)
			return;
		_visible = visible;
		Root.StopAnimation(nameof(Visual.Opacity));
		if (!visible)
		{
			_pressed = false;
			Root.Opacity = 0f;
			Root.IsVisible = false;
			ApplyState();
			return;
		}

		Root.IsVisible = true;
		Root.Opacity = 0f;
		using var animation = _compositor.CreateScalarKeyFrameAnimation();
		animation.Duration = TimeSpan.FromMilliseconds(150);
		animation.InsertKeyFrame(0f, 0f);
		animation.InsertKeyFrame(1f, 1f);
		Root.StartAnimation(nameof(Visual.Opacity), animation);
	}

	public void SetHovered(bool hovered)
	{
		if (_hovered == hovered)
			return;
		_hovered = hovered;
		ApplyState();
	}

	public void SetPressed(bool pressed)
	{
		if (_pressed == pressed)
			return;
		_pressed = pressed;
		ApplyState();
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		Root.Dispose();
		foreach (var stroke in _labelStrokes)
			stroke.Dispose();
		_labelStrokes.Clear();
		_edLabel.Dispose();
		_fixLabel.Dispose();
		_activeBar.Dispose();
		_statusDot.Dispose();
		_statusClip.Dispose();
		_statusGeometry.Dispose();
		_pinPoint.Dispose();
		_pinStem.Dispose();
		_pinHead.Dispose();
		_clip.Dispose();
		_geometry.Dispose();
		_background.Dispose();
		_pinBrush.Dispose();
		_activeBrush.Dispose();
		_labelBrush.Dispose();
		_backgroundBrush.Dispose();
	}

	private void ApplyState()
	{
		if (_disposed)
			return;
		_backgroundBrush.Color = _pinned
			? Windows.UI.Color.FromArgb((byte)(_pressed ? 255 : 242), 30, 91, 157)
			: Windows.UI.Color.FromArgb((byte)(_pressed ? 252 : _hovered ? 238 : 220), 25, 31, 42);
		_pinBrush.Color = _pinned
			? Windows.UI.Color.FromArgb(255, 255, 201, 76)
			: _hovered
				? Windows.UI.Color.FromArgb(255, 255, 214, 112)
				: Windows.UI.Color.FromArgb(255, 241, 245, 252);
		_statusDot.IsVisible = _pinned;
		_activeBar.IsVisible = _pinned;
		_edLabel.IsVisible = _pinned;
		if (!_pinned)
			_edLabel.Opacity = 0f;
		var scale = _pressed ? 0.92f : _hovered ? 1.045f : 1f;
		Root.Scale = new Vector3(scale, scale, 1f);
	}

	private ContainerVisual CreateLabel(Compositor compositor, string text, CompositionBrush brush)
	{
		var root = compositor.CreateContainerVisual();
		var x = 0f;
		foreach (var character in text)
		{
			CreateLetter(compositor, root, character, x, brush);
			x += 9f;
		}
		root.Size = new Vector2(Math.Max(0, x - 2f), 12f);
		return root;
	}

	private void CreateLetter(Compositor compositor, ContainerVisual root, char character, float x, CompositionBrush brush)
	{
		void Stroke(float left, float top, float width, float height, float angle = 0f)
		{
			var visual = compositor.CreateSpriteVisual();
			visual.Size = new Vector2(width, height);
			visual.Offset = new Vector3(x + left, top, 0);
			visual.CenterPoint = new Vector3(width / 2f, height / 2f, 0);
			visual.RotationAngleInDegrees = angle;
			visual.Brush = brush;
			root.Children.InsertAtTop(visual);
			_labelStrokes.Add(visual);
		}

		const float thickness = 1.5f;
		switch (character)
		{
			case 'F':
				Stroke(0, 0, thickness, 11);
				Stroke(0, 0, 7, thickness);
				Stroke(0, 5, 5.5f, thickness);
				break;
			case 'I':
				Stroke(0, 0, 7, thickness);
				Stroke(2.75f, 0, thickness, 11);
				Stroke(0, 9.5f, 7, thickness);
				break;
			case 'X':
				Stroke(2.7f, -0.2f, thickness, 11.6f, -32f);
				Stroke(2.7f, -0.2f, thickness, 11.6f, 32f);
				break;
			case 'E':
				Stroke(0, 0, thickness, 11);
				Stroke(0, 0, 7, thickness);
				Stroke(0, 5, 5.5f, thickness);
				Stroke(0, 9.5f, 7, thickness);
				break;
			case 'D':
				Stroke(0, 0, thickness, 11);
				Stroke(0, 0, 5.5f, thickness);
				Stroke(0, 9.5f, 5.5f, thickness);
				Stroke(5.5f, 1f, thickness, 9f);
				break;
		}
	}

	private void AnimateEdReveal()
	{
		_edLabel.StopAnimation(nameof(Visual.Opacity));
		_edLabel.IsVisible = true;
		_edLabel.Opacity = 0f;
		using var animation = _compositor.CreateScalarKeyFrameAnimation();
		animation.Duration = TimeSpan.FromMilliseconds(140);
		animation.InsertKeyFrame(0f, 0f);
		animation.InsertKeyFrame(1f, 1f);
		_edLabel.StartAnimation(nameof(Visual.Opacity), animation);
	}
}

internal sealed class SidebarExpandAllButtonVisual : IDisposable
{
	private readonly Compositor _compositor;
	private readonly SpriteVisual _background;
	private readonly CompositionColorBrush _backgroundBrush;
	private readonly CompositionRoundedRectangleGeometry _geometry;
	private readonly CompositionGeometricClip _clip;
	private readonly CompositionColorBrush _iconBrush;
	private readonly SpriteVisual[] _cards;
	private readonly ContainerVisual _fixLabel;
	private readonly ContainerVisual _edLabel;
	private readonly CompositionColorBrush _labelBrush;
	private readonly CompositionColorBrush _activeBrush;
	private readonly List<SpriteVisual> _labelStrokes = new();
	private bool _hovered;
	private bool _pressed;
	private bool _active;
	private bool _disposed;

	public SidebarExpandAllButtonVisual(Compositor compositor)
	{
		_compositor = compositor;
		Root = compositor.CreateContainerVisual();
		_backgroundBrush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(112, 22, 27, 36));
		_background = compositor.CreateSpriteVisual();
		_background.Brush = _backgroundBrush;
		_geometry = compositor.CreateRoundedRectangleGeometry();
		_clip = compositor.CreateGeometricClip(_geometry);
		_background.Clip = _clip;
		_iconBrush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(225, 205, 219, 242));
		_labelBrush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(245, 241, 245, 252));
		_activeBrush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, 112, 190, 255));
		_fixLabel = CreateLabel(compositor, "FIX", _labelBrush);
		_edLabel = CreateLabel(compositor, "ED", _activeBrush);
		_edLabel.IsVisible = false;
		_edLabel.Opacity = 0f;
		_cards = Enumerable.Range(0, 3).Select(_ =>
		{
			var card = compositor.CreateSpriteVisual();
			card.Brush = _iconBrush;
			Root.Children.InsertAtTop(card);
			return card;
		}).ToArray();
		Root.Children.InsertAtBottom(_background);
		Root.Children.InsertAtTop(_fixLabel);
		Root.Children.InsertAtTop(_edLabel);
		SetLayout(1f, 64f);
	}

	public ContainerVisual Root { get; }
	public Vector2 Size { get; private set; }
	public Vector2 Pivot { get; private set; }
	public float Angle => -7.5f;

	public void SetLayout(float dpiScale, float cardWidth)
	{
		var scale = Math.Max(0.75f, dpiScale);
		Size = new Vector2(Math.Max(64f * scale, cardWidth), 36f * scale);
		Root.Size = Size;
		Root.CenterPoint = new Vector3(Size.X * 0.88f, Size.Y / 2f, 0);
		Root.RotationAxis = Vector3.UnitY;
		Root.RotationAngleInDegrees = Angle;
		Pivot = new Vector2(Root.CenterPoint.X, Root.CenterPoint.Y);
		_background.Size = Size;
		_geometry.Size = Size;
		_geometry.CornerRadius = new Vector2(8f * scale);
		var width = 34f * scale;
		var height = 4f * scale;
		var x = (Size.X - width) / 2f;
		var firstY = (Size.Y - (height * 3f + 4f * scale * 2f)) / 2f;
		for (var index = 0; index < _cards.Length; index++)
		{
			_cards[index].Size = new Vector2(width - index * 4f * scale, height);
			_cards[index].Offset = new Vector3(x + index * 2f * scale, firstY + index * (height + 4f * scale), 0);
		}
		_fixLabel.Offset = new Vector3(13f * scale, 12f * scale, 0);
		_fixLabel.Scale = new Vector3(scale, scale, 1f);
		_edLabel.Offset = new Vector3(Size.X - 31f * scale, 12f * scale, 0);
		_edLabel.Scale = new Vector3(scale, scale, 1f);
		ApplyState();
	}

	public void SetOffset(Vector3 offset) => Root.Offset = offset;
	public void SetVisible(bool visible) => Root.IsVisible = visible;
	public void SetHovered(bool hovered) { _hovered = hovered; ApplyState(); }
	public void SetPressed(bool pressed) { _pressed = pressed; ApplyState(); }
	public void SetActive(bool active)
	{
		if (_active == active)
			return;
		_active = active;
		ApplyState();
		if (_active)
			AnimateEdReveal();
	}

	private void ApplyState()
	{
		if (_disposed)
			return;
		var backgroundAlpha = _pressed ? 205 : _active ? 175 : _hovered ? 160 : 112;
		_backgroundBrush.Color = Windows.UI.Color.FromArgb((byte)backgroundAlpha, 22, 27, 36);
		_iconBrush.Color = _active
			? Windows.UI.Color.FromArgb(255, 112, 190, 255)
			: Windows.UI.Color.FromArgb((byte)(_hovered ? 255 : 225), 205, 219, 242);
		_edLabel.IsVisible = _active;
		if (!_active)
			_edLabel.Opacity = 0f;
		var visualScale = _pressed ? 0.95f : _hovered ? 1.025f : 1f;
		Root.Scale = new Vector3(visualScale, visualScale, 1f);
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		foreach (var card in _cards)
			card.Dispose();
		foreach (var stroke in _labelStrokes)
			stroke.Dispose();
		_labelStrokes.Clear();
		_edLabel.Dispose();
		_fixLabel.Dispose();
		Root.Dispose();
		_activeBrush.Dispose();
		_labelBrush.Dispose();
		_iconBrush.Dispose();
		_clip.Dispose();
		_geometry.Dispose();
		_background.Dispose();
		_backgroundBrush.Dispose();
	}

	private ContainerVisual CreateLabel(Compositor compositor, string text, CompositionBrush brush)
	{
		var root = compositor.CreateContainerVisual();
		var x = 0f;
		foreach (var character in text)
		{
			CreateLetter(compositor, root, character, x, brush);
			x += 9f;
		}
		root.Size = new Vector2(Math.Max(0, x - 2f), 12f);
		return root;
	}

	private void CreateLetter(Compositor compositor, ContainerVisual root, char character, float x, CompositionBrush brush)
	{
		void Stroke(float left, float top, float width, float height, float angle = 0f)
		{
			var visual = compositor.CreateSpriteVisual();
			visual.Size = new Vector2(width, height);
			visual.Offset = new Vector3(x + left, top, 0);
			visual.CenterPoint = new Vector3(width / 2f, height / 2f, 0);
			visual.RotationAngleInDegrees = angle;
			visual.Brush = brush;
			root.Children.InsertAtTop(visual);
			_labelStrokes.Add(visual);
		}

		const float thickness = 1.5f;
		switch (character)
		{
			case 'F':
				Stroke(0, 0, thickness, 11); Stroke(0, 0, 7, thickness); Stroke(0, 5, 5.5f, thickness); break;
			case 'I':
				Stroke(0, 0, 7, thickness); Stroke(2.75f, 0, thickness, 11); Stroke(0, 9.5f, 7, thickness); break;
			case 'X':
				Stroke(2.7f, -0.2f, thickness, 11.6f, -32f); Stroke(2.7f, -0.2f, thickness, 11.6f, 32f); break;
			case 'E':
				Stroke(0, 0, thickness, 11); Stroke(0, 0, 7, thickness); Stroke(0, 5, 5.5f, thickness); Stroke(0, 9.5f, 7, thickness); break;
			case 'D':
				Stroke(0, 0, thickness, 11); Stroke(0, 0, 5.5f, thickness); Stroke(0, 9.5f, 5.5f, thickness); Stroke(5.5f, 1f, thickness, 9f); break;
		}
	}

	private void AnimateEdReveal()
	{
		_edLabel.StopAnimation(nameof(Visual.Opacity));
		_edLabel.IsVisible = true;
		_edLabel.Opacity = 0f;
		using var animation = _compositor.CreateScalarKeyFrameAnimation();
		animation.Duration = TimeSpan.FromMilliseconds(140);
		animation.InsertKeyFrame(0f, 0f);
		animation.InsertKeyFrame(1f, 1f);
		_edLabel.StartAnimation(nameof(Visual.Opacity), animation);
	}
}

internal sealed class SidebarExplorerButtonVisual : IDisposable
{
	private readonly SpriteVisual _background;
	private readonly CompositionColorBrush _backgroundBrush;
	private readonly CompositionRoundedRectangleGeometry _geometry;
	private readonly CompositionGeometricClip _clip;
	private readonly SpriteVisual _folderTab;
	private readonly SpriteVisual _folderBody;
	private readonly SpriteVisual _folderLine;
	private readonly CompositionColorBrush _folderBrush;
	private readonly CompositionColorBrush _folderLineBrush;
	private bool _hovered;
	private bool _pressed;
	private bool _disposed;

	public SidebarExplorerButtonVisual(Compositor compositor)
	{
		Root = compositor.CreateContainerVisual();
		_backgroundBrush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(112, 22, 27, 36));
		_background = compositor.CreateSpriteVisual();
		_background.Brush = _backgroundBrush;
		_geometry = compositor.CreateRoundedRectangleGeometry();
		_clip = compositor.CreateGeometricClip(_geometry);
		_background.Clip = _clip;
		_folderBrush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(238, 242, 185, 67));
		_folderLineBrush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(190, 128, 84, 18));
		_folderTab = compositor.CreateSpriteVisual();
		_folderTab.Brush = _folderBrush;
		_folderBody = compositor.CreateSpriteVisual();
		_folderBody.Brush = _folderBrush;
		_folderLine = compositor.CreateSpriteVisual();
		_folderLine.Brush = _folderLineBrush;
		Root.Children.InsertAtTop(_background);
		Root.Children.InsertAtTop(_folderTab);
		Root.Children.InsertAtTop(_folderBody);
		Root.Children.InsertAtTop(_folderLine);
		SetLayout(1f, 64f);
	}

	public ContainerVisual Root { get; }
	public Vector2 Size { get; private set; }
	public Vector2 Pivot { get; private set; }
	public float Angle => -7.5f;

	public void SetLayout(float dpiScale, float cardWidth)
	{
		var scale = Math.Max(0.75f, dpiScale);
		Size = new Vector2(Math.Max(64f * scale, cardWidth), 36f * scale);
		Root.Size = Size;
		Root.CenterPoint = new Vector3(Size.X * 0.88f, Size.Y / 2f, 0);
		Root.RotationAxis = Vector3.UnitY;
		Root.RotationAngleInDegrees = Angle;
		Pivot = new Vector2(Root.CenterPoint.X, Root.CenterPoint.Y);
		_background.Size = Size;
		_geometry.Size = Size;
		_geometry.CornerRadius = new Vector2(8f * scale, 8f * scale);

		var folderWidth = 30f * scale;
		var folderHeight = 17f * scale;
		var folderX = (Size.X - folderWidth) / 2f + 2f * scale;
		var folderY = (Size.Y - folderHeight) / 2f + 2f * scale;
		_folderTab.Size = new Vector2(14f * scale, 7f * scale);
		_folderTab.Offset = new Vector3(folderX + 2f * scale, folderY - 5f * scale, 0);
		_folderBody.Size = new Vector2(folderWidth, folderHeight);
		_folderBody.Offset = new Vector3(folderX, folderY, 0);
		_folderLine.Size = new Vector2(22f * scale, Math.Max(1f, 1.4f * scale));
		_folderLine.Offset = new Vector3(folderX + 4f * scale, folderY + 5f * scale, 0);
		ApplyState();
	}

	public void SetOffset(Vector3 offset) => Root.Offset = offset;

	public void SetVisible(bool visible) => Root.IsVisible = visible;

	public void SetHovered(bool hovered)
	{
		if (_hovered == hovered)
			return;
		_hovered = hovered;
		ApplyState();
	}

	public void SetPressed(bool pressed)
	{
		if (_pressed == pressed)
			return;
		_pressed = pressed;
		ApplyState();
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		Root.Dispose();
		_folderLine.Dispose();
		_folderBody.Dispose();
		_folderTab.Dispose();
		_clip.Dispose();
		_geometry.Dispose();
		_background.Dispose();
		_folderLineBrush.Dispose();
		_folderBrush.Dispose();
		_backgroundBrush.Dispose();
	}

	private void ApplyState()
	{
		if (_disposed)
			return;
		var backgroundAlpha = _pressed ? 190 : _hovered ? 160 : 112;
		var folderAlpha = _pressed ? 255 : _hovered ? 255 : 238;
		_backgroundBrush.Color = Windows.UI.Color.FromArgb((byte)backgroundAlpha, 22, 27, 36);
		_folderBrush.Color = Windows.UI.Color.FromArgb((byte)folderAlpha, 242, 185, 67);
		var visualScale = _pressed ? 0.95f : _hovered ? 1.025f : 1f;
		Root.Scale = new Vector3(visualScale, visualScale, 1f);
	}
}

internal sealed class SidebarCollapseButtonVisual : IDisposable
{
	private readonly SpriteVisual _background;
	private readonly CompositionColorBrush _backgroundBrush;
	private readonly CompositionRoundedRectangleGeometry _geometry;
	private readonly CompositionGeometricClip _clip;
	private readonly SpriteVisual _upperStroke;
	private readonly SpriteVisual _lowerStroke;
	private readonly CompositionColorBrush _strokeBrush;
	private bool _hovered;
	private bool _pressed;
	private bool _disposed;

	public SidebarCollapseButtonVisual(Compositor compositor)
	{
		Root = compositor.CreateContainerVisual();
		_backgroundBrush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(38, 246, 248, 252));
		_background = compositor.CreateSpriteVisual();
		_background.Brush = _backgroundBrush;
		_geometry = compositor.CreateRoundedRectangleGeometry();
		_clip = compositor.CreateGeometricClip(_geometry);
		_background.Clip = _clip;
		_strokeBrush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(150, 40, 46, 59));
		_upperStroke = CreateStroke(compositor, 45);
		_lowerStroke = CreateStroke(compositor, -45);
		_upperStroke.Brush = _strokeBrush;
		_lowerStroke.Brush = _strokeBrush;
		Root.Children.InsertAtTop(_background);
		Root.Children.InsertAtTop(_upperStroke);
		Root.Children.InsertAtTop(_lowerStroke);
		SetLayout(1f, 64f);
	}

	public ContainerVisual Root { get; }
	public Vector2 Size { get; private set; }
	public Vector2 Pivot { get; private set; }
	public float Angle => -7.5f;

	public void SetLayout(float dpiScale, float cardWidth)
	{
		var scale = Math.Max(0.75f, dpiScale);
		Size = new Vector2(Math.Max(64f * scale, cardWidth), 30f * scale);
		Root.Size = Size;
		Root.CenterPoint = new Vector3(Size.X * 0.88f, Size.Y / 2f, 0);
		Root.RotationAxis = Vector3.UnitY;
		Root.RotationAngleInDegrees = Angle;
		Pivot = new Vector2(Root.CenterPoint.X, Root.CenterPoint.Y);
		_background.Size = Size;
		_geometry.Size = Size;
		_geometry.CornerRadius = new Vector2(8f * scale, 8f * scale);
		_upperStroke.Size = new Vector2(Math.Max(1.6f, 2f * scale), 10f * scale);
		_lowerStroke.Size = _upperStroke.Size;
		_upperStroke.CenterPoint = new Vector3(_upperStroke.Size.X / 2f, _upperStroke.Size.Y / 2f, 0);
		_lowerStroke.CenterPoint = _upperStroke.CenterPoint;
		var centerX = Size.X / 2f + 2f * scale;
		_upperStroke.Offset = new Vector3(centerX, Size.Y / 2f - 7f * scale, 0);
		_lowerStroke.Offset = new Vector3(centerX, Size.Y / 2f, 0);
		ApplyState();
	}

	public void SetOffset(Vector3 offset) => Root.Offset = offset;

	public void SetVisible(bool visible) => Root.IsVisible = visible;

	public void SetHovered(bool hovered)
	{
		if (_hovered == hovered)
			return;
		_hovered = hovered;
		ApplyState();
	}

	public void SetPressed(bool pressed)
	{
		if (_pressed == pressed)
			return;
		_pressed = pressed;
		ApplyState();
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		Root.Dispose();
		_upperStroke.Dispose();
		_lowerStroke.Dispose();
		_clip.Dispose();
		_geometry.Dispose();
		_background.Dispose();
		_strokeBrush.Dispose();
		_backgroundBrush.Dispose();
	}

	private void ApplyState()
	{
		if (_disposed)
			return;
		var backgroundAlpha = _pressed ? 145 : _hovered ? 108 : 72;
		var strokeAlpha = _pressed ? 255 : _hovered ? 255 : 245;
		_backgroundBrush.Color = Windows.UI.Color.FromArgb((byte)backgroundAlpha, 246, 248, 252);
		_strokeBrush.Color = Windows.UI.Color.FromArgb((byte)strokeAlpha, 0, 0, 0);
		var visualScale = _pressed ? 0.93f : _hovered ? 1.04f : 1f;
		Root.Scale = new Vector3(visualScale, visualScale, 1f);
	}

	private static SpriteVisual CreateStroke(Compositor compositor, float angle)
	{
		var stroke = compositor.CreateSpriteVisual();
		stroke.Size = new Vector2(2, 10);
		stroke.CenterPoint = new Vector3(1, 5, 0);
		stroke.RotationAngleInDegrees = angle;
		return stroke;
	}
}

internal sealed class SidebarHintVisual : IDisposable
{
	private const int SurfaceWidth = 640;
	private const int SurfaceHeight = 64;
	private readonly CardSwapChain _surface;
	private readonly CompositionSurfaceBrush _surfaceBrush;
	private readonly SpriteVisual _content;
	private string _text;
	private bool _disposed;

	public SidebarHintVisual(D3DCompositionDevice graphics, Compositor compositor)
	{
		Root = compositor.CreateContainerVisual();
		_surface = graphics.CreateSurface(compositor, SurfaceWidth, SurfaceHeight);
		_text = SidebarHintFormatter.Format(true, 60);
		_surface.Upload(CreatePixels(_text));
		_surfaceBrush = compositor.CreateSurfaceBrush(_surface.CompositionSurface);
		_surfaceBrush.Stretch = CompositionStretch.Fill;
		_content = compositor.CreateSpriteVisual();
		_content.Brush = _surfaceBrush;
		Root.Children.InsertAtTop(_content);
		SetDpiScale(1f);
	}

	public ContainerVisual Root { get; }
	public Vector2 Size { get; private set; }

	public void SetDpiScale(float dpiScale)
	{
		var scale = Math.Max(0.75f, dpiScale);
		Size = new Vector2(280f * scale, 28f * scale);
		Root.Size = Size;
		_content.Size = Size;
	}

	public void SetOffset(Vector3 offset) => Root.Offset = offset;

	public void SetVisible(bool visible) => Root.IsVisible = visible;

	public void SetIdleBehavior(bool autoHideEnabled, int idleSeconds, UiLanguage language = UiLanguage.English)
	{
		var text = SidebarHintFormatter.Format(autoHideEnabled, idleSeconds, language);
		if (string.Equals(_text, text, StringComparison.Ordinal))
			return;
		_text = text;
		_surface.Upload(CreatePixels(_text));
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		Root.Dispose();
		_content.Dispose();
		_surfaceBrush.Dispose();
		_surface.Dispose();
	}

	private static byte[] CreatePixels(string text)
	{
		using var bitmap = new Bitmap(SurfaceWidth, SurfaceHeight, PixelFormat.Format32bppPArgb);
		using var graphics = Graphics.FromImage(bitmap);
		graphics.Clear(Color.Transparent);
		graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
		using var font = new Font("Microsoft YaHei UI", 22f, FontStyle.Regular, GraphicsUnit.Pixel);
		using var brush = new SolidBrush(Color.FromArgb(132, 235, 240, 248));
		graphics.DrawString(text, font, brush, new PointF(4, 17));

		var data = bitmap.LockBits(
			new Rectangle(0, 0, SurfaceWidth, SurfaceHeight),
			ImageLockMode.ReadOnly,
			PixelFormat.Format32bppPArgb);
		try
		{
			var pixels = new byte[SurfaceWidth * SurfaceHeight * 4];
			for (var row = 0; row < SurfaceHeight; row++)
				Marshal.Copy(data.Scan0 + row * data.Stride, pixels, row * SurfaceWidth * 4, SurfaceWidth * 4);
			return pixels;
		}
		finally
		{
			bitmap.UnlockBits(data);
		}
	}
}

internal sealed class WindowCardVisual : IDisposable
{
	private readonly Compositor _compositor;
	private readonly D3DCompositionDevice _graphics;
	private CardSwapChain? _surface;
	private CompositionSurfaceBrush? _surfaceBrush;
	private readonly CompositionColorBrush _placeholderBrush;
	private readonly SpriteVisual _content;
	private readonly CompositionRoundedRectangleGeometry _clipGeometry;
	private readonly CompositionGeometricClip _clip;
	private readonly DropShadow _shadow;
	private readonly SpriteVisual _focusIndicator;
	private readonly CompositionColorBrush _focusIndicatorBrush;
	private readonly CompositionRoundedRectangleGeometry _focusIndicatorGeometry;
	private readonly CompositionGeometricClip _focusIndicatorClip;
	private bool _justRevealed;
	private bool _lastKnownMinimized;
	private bool _disposed;
	private ShapeVisual? _feedbackVisual;
	private CompositionRoundedRectangleGeometry? _feedbackGeometry;
	private CompositionSpriteShape? _feedbackShape;
	private CompositionColorBrush? _feedbackFill;
	private CompositionColorBrush? _feedbackStroke;
	private CardFeedback _feedback;
	private float _feedbackOpacity;

	public WindowCardVisual(Compositor compositor, D3DCompositionDevice graphics, IWindow window, int surfaceWidth, int surfaceHeight, Vector2 cardSize, bool isApplicationGroupCard)
	{
		_compositor = compositor;
		_graphics = graphics;
		Window = window;
		IsApplicationGroupCard = isApplicationGroupCard;
		_lastKnownMinimized = window.IsMinimized;
		SurfaceWidth = surfaceWidth;
		SurfaceHeight = surfaceHeight;
		_placeholderBrush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(238, 232, 236, 242));
		_content = compositor.CreateSpriteVisual();
		_content.Size = cardSize;
		_content.Brush = _placeholderBrush;
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
		_content.Shadow = _shadow;
		var indicatorSize = Math.Max(7f, cardSize.Y * 0.065f);
		_focusIndicatorBrush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(245, 69, 133, 255));
		_focusIndicator = compositor.CreateSpriteVisual();
		_focusIndicator.Size = new Vector2(indicatorSize, indicatorSize);
		_focusIndicator.Offset = new Vector3(
			cardSize.X - indicatorSize - Math.Max(8f, cardSize.Y * 0.08f),
			Math.Max(8f, cardSize.Y * 0.08f),
			2f);
		_focusIndicator.Brush = _focusIndicatorBrush;
		_focusIndicatorGeometry = compositor.CreateRoundedRectangleGeometry();
		_focusIndicatorGeometry.Size = _focusIndicator.Size;
		_focusIndicatorGeometry.CornerRadius = new Vector2(indicatorSize / 2f);
		_focusIndicatorClip = compositor.CreateGeometricClip(_focusIndicatorGeometry);
		_focusIndicator.Clip = _focusIndicatorClip;
		Root = compositor.CreateContainerVisual();
		Root.Size = cardSize;
		Root.CenterPoint = new Vector3(cardSize.X * 0.88f, cardSize.Y * 0.5f, 0);
		Root.RotationAxis = Vector3.UnitY;
		Root.Children.InsertAtTop(_content);
		Root.Children.InsertAtTop(_focusIndicator);
		Root.IsVisible = false;
		Motion = new CardVisualMotion(compositor, Root);
		Pivot = new Vector2(Root.CenterPoint.X, Root.CenterPoint.Y);
		UpdateFocusIndicator();
	}

	public ContainerVisual Root { get; }
	public CardFeedback Feedback => _feedback;
	public CardVisualMotion Motion { get; }
	public bool HasSurface => _surface is not null;
	public DateTime HiddenSinceUtc { get; private set; }
	public long EstimatedSurfaceBytes => (long)SurfaceWidth * SurfaceHeight * 8;

	public void SetFeedback(CardFeedback feedback, bool animate)
	{
		if (_disposed || _feedback == feedback)
			return;
		_feedback = feedback;
		if (_feedbackVisual is null)
		{
			_feedbackGeometry = _compositor.CreateRoundedRectangleGeometry();
			_feedbackGeometry.Size = Root.Size - new Vector2(2);
			_feedbackGeometry.Offset = Vector2.One;
			_feedbackGeometry.CornerRadius = _clipGeometry.CornerRadius;
			_feedbackFill = _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(36, 54, 112, 210));
			_feedbackStroke = _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(235, 98, 162, 250));
			_feedbackShape = _compositor.CreateSpriteShape(_feedbackGeometry);
			_feedbackShape.FillBrush = _feedbackFill;
			_feedbackShape.StrokeBrush = _feedbackStroke;
			_feedbackShape.StrokeThickness = Math.Max(1.5f, Root.Size.Y / 80f);
			_feedbackVisual = _compositor.CreateShapeVisual();
			_feedbackVisual.Size = Root.Size;
			_feedbackVisual.Opacity = 0;
			_feedbackVisual.Shapes.Add(_feedbackShape);
			// Inherit the card's perspective, without moving its content or hit area.
			Root.Children.InsertAtTop(_feedbackVisual);
		}
		var opacity = feedback == CardFeedback.Pressed ? 1f : 0f;
		_feedbackVisual.StopAnimation(nameof(Visual.Opacity));
		_feedbackVisual.Opacity = opacity;
		if (animate && feedback != CardFeedback.Pressed)
		{
			using var animation = _compositor.CreateScalarKeyFrameAnimation();
			animation.Duration = TimeSpan.FromMilliseconds(140);
			animation.InsertKeyFrame(0, _feedbackOpacity);
			animation.InsertKeyFrame(1, opacity);
			_feedbackVisual.StartAnimation(nameof(Visual.Opacity), animation);
		}
		_feedbackOpacity = opacity;
	}
	public IWindow Window { get; private set; }
	public Vector2 Pivot { get; }
	public int SurfaceWidth { get; }
	public int SurfaceHeight { get; }
	public bool IsVisible { get; private set; }
	public bool IsApplicationGroupCard { get; }
	public string? DesiredBadge { get; set; }
	public DateTime LastCaptureUtc { get; private set; } = DateTime.MinValue;
	public bool NeedsCapture(DateTime nowUtc, int refreshMinutes) =>
		WindowCapturePolicy.NeedsCapture(LastCaptureUtc, nowUtc, refreshMinutes);

	public void InvalidateCapture() => LastCaptureUtc = DateTime.MinValue;

	public void SetVisible(bool visible)
	{
		if (IsVisible == visible)
			return;
		IsVisible = visible;
		Root.IsVisible = visible;
		_justRevealed = visible;
		if (!visible) HiddenSinceUtc = DateTime.UtcNow;
	}

	public void UpdateWindow(IWindow window)
	{
		var minimized = window.IsMinimized;
		Window = window;
		if (minimized != _lastKnownMinimized)
			InvalidateCapture();
		_lastKnownMinimized = minimized;
		UpdateFocusIndicator();
	}

	private void UpdateFocusIndicator() =>
		_focusIndicator.IsVisible = Window.IsFocused && !Window.IsMinimized;

	public void SetTransform(Vector3 offset, Vector3 scale, float angle, bool animate)
	{
		if (_justRevealed && animate && !Motion.Initialized)
		{
			Motion.Set(offset - new Vector3(0, Math.Min(14, Root.Size.Y * 0.12f), 0), scale, angle, false);
			using var fade = _compositor.CreateScalarKeyFrameAnimation();
			fade.Duration = CardVisualMotion.Duration;
			fade.InsertKeyFrame(0, 0);
			fade.InsertKeyFrame(1, 1);
			Root.StartAnimation(nameof(Visual.Opacity), fade);
		}
		if (!animate)
		{
			Root.StopAnimation(nameof(Visual.Opacity));
			Root.Opacity = 1;
		}
		_justRevealed = false;
		Motion.Set(offset, scale, angle, animate);
	}

	public void Upload(CapturedCardFrame frame)
	{
		if (_disposed || frame.Width != SurfaceWidth || frame.Height != SurfaceHeight)
			return;
		EnsureSurface();
		_surface!.Upload(frame.Pixels);
		LastCaptureUtc = DateTime.UtcNow;
	}

	private void EnsureSurface()
	{
		if (_surface is not null)
			return;
		_surface = _graphics.CreateSurface(_compositor, SurfaceWidth, SurfaceHeight);
		_surfaceBrush = _compositor.CreateSurfaceBrush(_surface.CompositionSurface);
		_surfaceBrush.Stretch = CompositionStretch.Fill;
		_content.Brush = _surfaceBrush;
		_shadow.Mask = _surfaceBrush;
	}

	public void ReleaseSurface()
	{
		if (_surface is null)
			return;
		_shadow.Mask = null;
		_content.Brush = _placeholderBrush;
		_surfaceBrush?.Dispose();
		_surfaceBrush = null;
		_surface.Dispose();
		_surface = null;
		LastCaptureUtc = DateTime.MinValue;
	}

	public void MarkCaptureStarted() => LastCaptureUtc = DateTime.UtcNow;

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		ReleaseSurface();
		_feedbackVisual?.Dispose();
		_feedbackShape?.Dispose();
		_feedbackGeometry?.Dispose();
		_feedbackFill?.Dispose();
		_feedbackStroke?.Dispose();
		Root.Dispose();
		_shadow.Dispose();
		_focusIndicatorClip.Dispose();
		_focusIndicatorGeometry.Dispose();
		_focusIndicator.Dispose();
		_focusIndicatorBrush.Dispose();
		_clip.Dispose();
		_clipGeometry.Dispose();
		_content.Dispose();
		_placeholderBrush.Dispose();
	}
}
