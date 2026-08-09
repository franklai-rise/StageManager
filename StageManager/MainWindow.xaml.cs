using SharpHook;
using StageManager.Infrastructure;
using StageManager.Model;
using StageManager.Native;
using StageManager.Native.PInvoke;
using StageManager.Native.Window;
using StageManager.Services;
using StageManager.Settings;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace StageManager;

public partial class MainWindow : Window, INotifyPropertyChanged
{
	private const int TimerIntervalMilliseconds = 500;
	private const double BaseCardWidth = 196;
	private const double BaseCardHeight = 122;
	private const double BasePreviewWidth = 188;
	private const double BasePreviewHeight = 92;
	private const double BaseIconHostHeight = 22;
	private const double BaseIconSize = 20;
	private const double BaseGap = 8;
	private const double SceneListVerticalPadding = 16;

	private readonly SettingsService _settings = AppServices.Settings;
	private readonly DisplayTopologyService _displays = AppServices.Displays;
	private IntPtr _thisHandle;
	private TaskPoolGlobalHook? _hook;
	private HotkeyManager? _hotkeys;
	private System.Threading.Timer? _overlapCheckTimer;
	private WindowMode _mode = WindowMode.OnScreen;
	private Point _mouse;
	private Point _mouseDownPoint;
	private SceneModel? _mouseDownStage;
	private int _lastNativeWidth;
	private int _overlapCheckRunning;
	private bool _manualSidebarHidden;
	private bool _sidebarForcedVisible;
	private bool _exclusiveFullScreen;
	private bool _closing;
	private bool _isSafeMode = AppServices.SafeMode;
	private double _sceneCardWidth = BaseCardWidth;
	private double _sceneCardHeight = BaseCardHeight;
	private Thickness _sceneCardMargin = new(0, 0, 0, BaseGap);
	private double _scenePreviewWidth = BasePreviewWidth;
	private double _scenePreviewHeight = BasePreviewHeight;
	private double _sceneIconHostHeight = BaseIconHostHeight;
	private double _sceneIconSize = BaseIconSize;
	private double _sceneListMaxHeight = 450;

	public MainWindow()
	{
		InitializeComponent();
		DataContext = this;
		_settings.SettingsChanged += Settings_SettingsChanged;

		try
		{
			AutoStart.SetStartup(AutoStart.DefaultAppName, _settings.Current.StartWithWindows);
		}
		catch (Exception ex)
		{
			AppLogger.Error("Unable to synchronize the startup setting.", ex);
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public ObservableCollection<SceneModel> Scenes { get; } = new();
	public SceneManager? SceneManager { get; private set; }
	public IntPtr Handle => _thisHandle;
	public bool IsSafeMode
	{
		get => _isSafeMode;
		private set
		{
			if (!SetLayoutValue(ref _isSafeMode, value))
				return;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SafeModeVisibility)));
		}
	}
	public Visibility SafeModeVisibility => IsSafeMode ? Visibility.Visible : Visibility.Collapsed;
	public double SceneCardWidth { get => _sceneCardWidth; private set => SetLayoutValue(ref _sceneCardWidth, value); }
	public double SceneCardHeight { get => _sceneCardHeight; private set => SetLayoutValue(ref _sceneCardHeight, value); }
	public Thickness SceneCardMargin { get => _sceneCardMargin; private set => SetLayoutValue(ref _sceneCardMargin, value); }
	public double ScenePreviewWidth { get => _scenePreviewWidth; private set => SetLayoutValue(ref _scenePreviewWidth, value); }
	public double ScenePreviewHeight { get => _scenePreviewHeight; private set => SetLayoutValue(ref _scenePreviewHeight, value); }
	public double SceneIconHostHeight { get => _sceneIconHostHeight; private set => SetLayoutValue(ref _sceneIconHostHeight, value); }
	public double SceneIconSize { get => _sceneIconSize; private set => SetLayoutValue(ref _sceneIconSize, value); }
	public double SceneListMaxHeight { get => _sceneListMaxHeight; private set => SetLayoutValue(ref _sceneListMaxHeight, value); }

	public WindowMode Mode
	{
		get => _mode;
		private set
		{
			if (_mode == value)
				return;
			_mode = value;
			Topmost = value == WindowMode.Flyover;
			ApplyWindowMode();
		}
	}

	protected override void OnSourceInitialized(EventArgs e)
	{
		base.OnSourceInitialized(e);
		_thisHandle = new WindowInteropHelper(this).Handle;
		_hotkeys = new HotkeyManager(_thisHandle);
		RegisterHotkeys();
		ApplyWindowMode();
	}

	protected override async void OnContentRendered(EventArgs e)
	{
		base.OnContentRendered(e);
		if (!IsSafeMode)
			await StartWindowManagementAsync();
		UpdateSceneCardLayout();
	}

	protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
	{
		base.OnRenderSizeChanged(sizeInfo);
		ApplyWindowMode();
		UpdateSceneCardLayout();
	}

	protected override void OnClosed(EventArgs e)
	{
		_closing = true;
		_settings.SettingsChanged -= Settings_SettingsChanged;
		_overlapCheckTimer?.Dispose();
		_overlapCheckTimer = null;
		StopHook();
		_hotkeys?.Dispose();
		_hotkeys = null;
		trayIcon.Dispose();
		SceneManager?.Dispose();
		SceneManager = null;
		base.OnClosed(e);
	}

	private async Task StartWindowManagementAsync()
	{
		if (SceneManager is not null || _closing)
			return;

		IsSafeMode = false;
		var windowsManager = new WindowsManager(AppServices.WindowClassifier, AppServices.VirtualDesktops);
		SceneManager = new SceneManager(windowsManager, _settings, AppServices.VirtualDesktops, _displays, Dispatcher);
		SceneManager.StageChanged += SceneManager_StageChanged;
		SceneManager.CurrentStageSelectionChanged += SceneManager_CurrentStageSelectionChanged;
		SceneManager.StagesReset += SceneManager_StagesReset;
		await SceneManager.Start().ConfigureAwait(true);
		RefreshStageModels();
		StartHook();
		_overlapCheckTimer = new System.Threading.Timer(OverlapCheck, null, 700, TimerIntervalMilliseconds);

		var foreground = Win32.GetForegroundWindow();
		var foregroundStage = SceneManager.FindStageForWindow(foreground);
		if (foregroundStage is not null)
			await SceneManager.SwitchTo(foregroundStage).ConfigureAwait(true);
		AppLogger.Info("Window management is active.");
	}

	private void SceneManager_StageChanged(object? sender, StageChangedEventArgs e) => Dispatcher.BeginInvoke(RefreshStageModels);
	private void SceneManager_CurrentStageSelectionChanged(object? sender, CurrentStageSelectionChangedEventArgs e) => Dispatcher.BeginInvoke(RefreshStageModels);
	private void SceneManager_StagesReset(object? sender, EventArgs e) => Dispatcher.BeginInvoke(RefreshStageModels);

	private void RefreshStageModels()
	{
		if (SceneManager is null)
			return;

		var currentId = SceneManager.GetCurrentStage()?.Id;
		var desired = SceneManager.GetStages().Where(stage => stage.Id != currentId).ToArray();
		for (var index = Scenes.Count - 1; index >= 0; index--)
		{
			if (!desired.Any(stage => stage.Id == Scenes[index].Id))
				Scenes.RemoveAt(index);
		}

		for (var index = 0; index < desired.Length; index++)
		{
			var model = Scenes.FirstOrDefault(candidate => candidate.Id == desired[index].Id);
			if (model is null)
			{
				model = SceneModel.FromStage(desired[index]);
				Scenes.Insert(Math.Min(index, Scenes.Count), model);
			}
			else
			{
				model.UpdateFromStage(desired[index]);
				var oldIndex = Scenes.IndexOf(model);
				if (oldIndex != index)
					Scenes.Move(oldIndex, index);
			}
			model.IsVisible = true;
		}
		UpdateSceneCardLayout();
	}

	private void UpdateSceneCardLayout()
	{
		var preferenceScale = _settings.Current.CardScale;
		var maximumWidth = BaseCardWidth * preferenceScale;
		var visibleCount = Scenes.Count(scene => scene.IsVisible);
		var availableHeight = Math.Max(1, (ActualHeight > 0 ? ActualHeight : Height) - SceneListVerticalPadding - (IsSafeMode ? 74 : 0));
		var layout = CardLayoutCalculator.Calculate(availableHeight, visibleCount, preferenceScale);

		SceneCardWidth = layout.CardWidth;
		SceneCardHeight = layout.CardHeight;
		SceneCardMargin = new Thickness(0, 0, 0, layout.Gap);
		ScenePreviewWidth = layout.PreviewWidth;
		ScenePreviewHeight = layout.PreviewHeight;
		SceneIconHostHeight = layout.IconHostHeight;
		SceneIconSize = layout.IconSize;
		SceneListMaxHeight = availableHeight;

		var desiredWidth = Math.Ceiling(maximumWidth + 28);
		if (Math.Abs(Width - desiredWidth) > 0.5)
			Width = desiredWidth;
	}

	private void StartHook()
	{
		if (_hook is not null || IsSafeMode || _closing)
			return;
		_hook = new TaskPoolGlobalHook();
		_hook.MousePressed += OnMousePressed;
		_hook.MouseReleased += OnMouseReleased;
		_hook.MouseMoved += OnMouseMoved;
		_ = Task.Run(_hook.Run);
	}

	private void StopHook()
	{
		if (_hook is null)
			return;
		_hook.MousePressed -= OnMousePressed;
		_hook.MouseReleased -= OnMouseReleased;
		_hook.MouseMoved -= OnMouseMoved;
		try
		{
			_hook.Dispose();
		}
		catch (HookException ex)
		{
			AppLogger.Warn($"Mouse hook disposal reported: {ex.Message}");
		}
		_hook = null;
	}

	private void OnMousePressed(object? sender, MouseHookEventArgs e)
	{
		_overlapCheckTimer?.Change(Timeout.Infinite, Timeout.Infinite);
		if (Win32.GetForegroundWindow() != _thisHandle)
			return;
		_mouseDownPoint = new Point(e.Data.X, e.Data.Y);
		Dispatcher.BeginInvoke(() => _mouseDownStage = FindStageByPoint(_mouseDownPoint));
	}

	private void OnMouseReleased(object? sender, MouseHookEventArgs e)
	{
		_overlapCheckTimer?.Change(0, TimerIntervalMilliseconds);
		if (SceneManager is null)
			return;

		var screenPoint = new Point(e.Data.X, e.Data.Y);
		var foreground = Win32.GetForegroundWindow();
		if (foreground != _thisHandle)
		{
			Dispatcher.BeginInvoke(() =>
			{
				var target = FindStageByPoint(screenPoint);
				if (target is not null)
					_ = SceneManager.MoveWindow(foreground, target.Stage);
			});
		}

		var targetScreen = _displays.LeftmostDisplay;
		var moved = Math.Abs(screenPoint.X - _mouseDownPoint.X) + Math.Abs(screenPoint.Y - _mouseDownPoint.Y) > 24;
		if (_mouseDownStage is not null && moved && screenPoint.X > targetScreen.WorkingArea.Left + _lastNativeWidth)
		{
			var source = _mouseDownStage;
			Dispatcher.BeginInvoke(() => _ = SceneManager.MergeStageIntoCurrent(source.Stage));
		}
		_mouseDownStage = null;
	}

	private void OnMouseMoved(object? sender, MouseHookEventArgs e)
	{
		_mouse = new Point(e.Data.X, e.Data.Y);
		var targetLeft = _displays.LeftmostDisplay.WorkingArea.Left;
		if (Mode == WindowMode.OffScreen && !_manualSidebarHidden && !_exclusiveFullScreen && e.Data.X >= targetLeft && e.Data.X <= targetLeft + 7)
			Dispatcher.BeginInvoke(() => Mode = WindowMode.Flyover);
	}

	private SceneModel? FindStageByPoint(Point point)
	{
		if (_thisHandle == IntPtr.Zero)
			return null;
		var sidebarBounds = new Win32.Rect();
		Win32.GetWindowRect(_thisHandle, ref sidebarBounds);
		var pointOnWindow = new Point(point.X - sidebarBounds.Left, point.Y - sidebarBounds.Top);
		var dpi = VisualTreeHelper.GetDpi(this);
		pointOnWindow.X /= dpi.DpiScaleX;
		pointOnWindow.Y /= dpi.DpiScaleY;
		var element = VisualTreeHelper.HitTest(this, pointOnWindow)?.VisualHit;
		while (element is not null)
		{
			if (element is FrameworkElement { DataContext: SceneModel model })
				return model;
			element = element.GetParentObject();
		}
		return null;
	}

	private void OverlapCheck(object? state)
	{
		if (Interlocked.Exchange(ref _overlapCheckRunning, 1) != 0)
			return;
		try
		{
			if (SceneManager is null || _closing)
				return;
			var target = _displays.LeftmostDisplay;
			var foreground = Win32.GetForegroundWindow();
			_exclusiveFullScreen = FullScreenService.IsExclusiveFullScreenOn(foreground, target);
			if (_manualSidebarHidden || _exclusiveFullScreen)
			{
				Dispatcher.BeginInvoke(() => Mode = WindowMode.OffScreen);
				return;
			}
			if (_sidebarForcedVisible)
			{
				Dispatcher.BeginInvoke(() => Mode = WindowMode.OnScreen);
				return;
			}
			if (!_settings.Current.AutoHideSidebar)
			{
				Dispatcher.BeginInvoke(() => Mode = WindowMode.OnScreen);
				return;
			}
			UpdateModeByWindows(SceneManager.GetCurrentWindows().ToArray(), target);
		}
		catch (Exception ex)
		{
			AppLogger.Error("Sidebar overlap check failed.", ex);
		}
		finally
		{
			Interlocked.Exchange(ref _overlapCheckRunning, 0);
		}
	}

	private void UpdateModeByWindows(IEnumerable<IWindow> windows, System.Windows.Forms.Screen target)
	{
		var area = target.WorkingArea;
		bool OnTarget(IWindowLocation location) => location.X < area.Right && location.X + location.Width > area.Left && location.Y < area.Bottom && location.Y + location.Height > area.Top;
		bool Overlaps(IWindowLocation location) => OnTarget(location) && (location.State == Native.Window.WindowState.Maximized ||
			(location.State == Native.Window.WindowState.Normal && location.X < area.Left + _lastNativeWidth));
		var anyOverlap = windows.Any(window => Overlaps(window.Location));
		var containsMouse = _mouse.X >= area.Left && _mouse.X <= area.Left + _lastNativeWidth;
		if (!containsMouse || Mode != WindowMode.Flyover)
			Dispatcher.BeginInvoke(() => Mode = anyOverlap ? WindowMode.OffScreen : WindowMode.OnScreen);
	}

	private void ApplyWindowMode()
	{
		if (_thisHandle == IntPtr.Zero)
			return;
		var target = _displays.LeftmostDisplay;
		var dpi = VisualTreeHelper.GetDpi(this);
		_lastNativeWidth = Math.Max(1, (int)Math.Ceiling(Width * dpi.DpiScaleX));
		var hidden = Mode == WindowMode.OffScreen || _manualSidebarHidden || _exclusiveFullScreen;
		var nativeLeft = hidden ? target.WorkingArea.Left - _lastNativeWidth + 2 : target.WorkingArea.Left;
		Win32.SetWindowPos(_thisHandle, IntPtr.Zero, nativeLeft, target.WorkingArea.Top, _lastNativeWidth, target.WorkingArea.Height,
			Win32.SetWindowPosFlags.IgnoreZOrder | Win32.SetWindowPosFlags.DoNotActivate | Win32.SetWindowPosFlags.ShowWindow);
		Opacity = _settings.Current.SidebarOpacity;
		if (!hidden && _settings.Current.AnimationsEnabled && SystemParameters.ClientAreaAnimation)
		{
			var animation = new DoubleAnimation(Math.Max(0.55, Opacity - 0.2), Opacity, TimeSpan.FromMilliseconds(150));
			BeginAnimation(OpacityProperty, animation);
		}
	}

	private void RegisterHotkeys()
	{
		_hotkeys?.Clear();
		if (_hotkeys is null || !_settings.Current.HotkeysEnabled)
			return;
		_hotkeys.Register(_settings.Current.ToggleSidebarHotkey, () => Dispatcher.BeginInvoke(ToggleSidebar));
		_hotkeys.Register(_settings.Current.PreviousStageHotkey, () => Dispatcher.BeginInvoke(() => _ = SceneManager?.SwitchRelative(-1)));
		_hotkeys.Register(_settings.Current.NextStageHotkey, () => Dispatcher.BeginInvoke(() => _ = SceneManager?.SwitchRelative(1)));
		if (!_hotkeys.Register(_settings.Current.ToggleWindowInStageHotkey, () => Dispatcher.BeginInvoke(() => _ = SceneManager?.ToggleForegroundWindowInCurrentStage())))
			_hotkeys.Register("Ctrl+Alt+Shift+G", () => Dispatcher.BeginInvoke(() => _ = SceneManager?.ToggleForegroundWindowInCurrentStage()));
	}

	private void ToggleSidebar()
	{
		var shouldShow = _manualSidebarHidden || Mode == WindowMode.OffScreen;
		_manualSidebarHidden = !shouldShow;
		_sidebarForcedVisible = shouldShow;
		Mode = shouldShow ? WindowMode.OnScreen : WindowMode.OffScreen;
		ApplyWindowMode();
	}

	private void Settings_SettingsChanged(object? sender, EventArgs e)
	{
		Dispatcher.BeginInvoke(() =>
		{
			RegisterHotkeys();
			UpdateSceneCardLayout();
			if (!_settings.Current.AutoHideSidebar && !_manualSidebarHidden && !_exclusiveFullScreen)
				Mode = WindowMode.OnScreen;
			ApplyWindowMode();
		});
	}

	private static SceneModel? GetStageModel(object sender) => (sender as FrameworkElement)?.DataContext as SceneModel;

	private async void StageCard_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
	{
		if (GetStageModel(sender) is { } model && SceneManager is not null)
			await SceneManager.SwitchTo(model.Stage);
	}

	private void MenuItem_MergeStage_Click(object sender, RoutedEventArgs e)
	{
		if (GetStageModel(sender) is { } model && SceneManager is not null)
			_ = SceneManager.MergeStageIntoCurrent(model.Stage);
	}

	private void MenuItem_ExtractWindow_Click(object sender, RoutedEventArgs e)
	{
		if (GetStageModel(sender) is { } model && SceneManager is not null)
			_ = SceneManager.ExtractLastWindow(model.Stage);
	}

	private void MenuItem_MoveDisplay_Click(object sender, RoutedEventArgs e)
	{
		if (GetStageModel(sender) is { } model && SceneManager is not null)
			_ = SceneManager.MoveStageToNextDisplay(model.Stage);
	}

	private void MenuItem_TwoColumns_Click(object sender, RoutedEventArgs e)
	{
		if (GetStageModel(sender) is { } model && SceneManager is not null)
			_ = SceneManager.ArrangeStage(model.Stage, StageLayout.TwoColumns);
	}

	private void MenuItem_ThreeColumns_Click(object sender, RoutedEventArgs e)
	{
		if (GetStageModel(sender) is { } model && SceneManager is not null)
			_ = SceneManager.ArrangeStage(model.Stage, StageLayout.ThreeColumns);
	}

	private void MenuItem_RebuildPreviews_Click(object sender, RoutedEventArgs e) => GetStageModel(sender)?.RebuildPreviews();

	private void MenuItem_IgnoreStage_Click(object sender, RoutedEventArgs e)
	{
		if (GetStageModel(sender) is not { } model)
			return;
		_settings.AddIgnoredProcesses(model.Stage.Windows.Select(window => window.ProcessName));
	}

	private void MenuItem_CloseWindow_Click(object sender, RoutedEventArgs e)
	{
		if (GetStageModel(sender) is { } model && SceneManager is not null)
			_ = SceneManager.CloseLastWindow(model.Stage);
	}

	private void MenuItem_CoexistMode_Click(object sender, RoutedEventArgs e)
	{
		var settings = _settings.CloneCurrent();
		settings.StageMode = StageMode.Coexist;
		_settings.Apply(settings);
	}

	private void MenuItem_FocusMode_Click(object sender, RoutedEventArgs e)
	{
		var settings = _settings.CloneCurrent();
		settings.StageMode = StageMode.Focus;
		_settings.Apply(settings);
	}

	private void MenuItem_AutoHide_Click(object sender, RoutedEventArgs e)
	{
		var settings = _settings.CloneCurrent();
		settings.AutoHideSidebar = !settings.AutoHideSidebar;
		_settings.Apply(settings);
	}

	private void MenuItem_Settings_Click(object sender, RoutedEventArgs e)
	{
		var settingsWindow = new SettingsWindow(_settings) { Owner = this };
		settingsWindow.ShowDialog();
	}

	private async void MenuItem_ResumeSafeMode_Click(object sender, RoutedEventArgs e)
	{
		if (!IsSafeMode)
			return;
		AppServices.DisableSafeMode();
		await StartWindowManagementAsync();
	}

	private void MenuItem_OpenLogs_Click(object sender, RoutedEventArgs e)
	{
		var directory = Path.GetDirectoryName(AppLogger.CurrentLogPath)!;
		Directory.CreateDirectory(directory);
		Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
	}

	private void MenuItem_ProjectPage_Click(object sender, RoutedEventArgs e) =>
		Process.Start(new ProcessStartInfo("https://github.com/franklai-rise/StageManager") { UseShellExecute = true });

	private void MenuItem_Quit_Click(object sender, RoutedEventArgs e) => Close();

	private void ContextMenu_Opened(object sender, RoutedEventArgs e)
	{
		StopHook();
		coexistModeMenu.IsChecked = _settings.Current.StageMode == StageMode.Coexist;
		focusModeMenu.IsChecked = _settings.Current.StageMode == StageMode.Focus;
		autoHideMenu.IsChecked = _settings.Current.AutoHideSidebar;
		resumeSafeModeMenu.Visibility = IsSafeMode ? Visibility.Visible : Visibility.Collapsed;
	}

	private void ContextMenu_Closed(object sender, RoutedEventArgs e)
	{
		if (SceneManager is not null)
			StartHook();
	}

	private bool SetLayoutValue<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
	{
		if (EqualityComparer<T>.Default.Equals(field, value))
			return false;
		field = value;
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		return true;
	}
}

public enum WindowMode
{
	OnScreen,
	OffScreen,
	Flyover
}
