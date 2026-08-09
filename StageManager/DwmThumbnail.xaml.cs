using StageManager.Infrastructure;
using StageManager.Native.Interop;
using StageManager.Native.PInvoke;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace StageManager;

public partial class DwmThumbnail : UserControl
{
	private IntPtr _dwmThumbnail;
	private Window? _window;
	private Point? _dpiScaleFactor;
	private RECT? _lastDestination;
	private bool _updateQueued;
	private bool _layoutSuspended;

	public DwmThumbnail()
	{
		InitializeComponent();
		Loaded += OnLoaded;
		Unloaded += OnUnloaded;
		IsVisibleChanged += OnIsVisibleChanged;
		SizeChanged += (_, _) => QueueUpdate();
		LayoutUpdated += (_, _) => QueueUpdate();
	}

	public static readonly DependencyProperty PreviewHandleProperty = DependencyProperty.Register(
		nameof(PreviewHandle),
		typeof(IntPtr),
		typeof(DwmThumbnail),
		new PropertyMetadata(IntPtr.Zero, OnPreviewHandleChanged));

	public IntPtr PreviewHandle
	{
		get => (IntPtr)GetValue(PreviewHandleProperty);
		set => SetValue(PreviewHandleProperty, value);
	}

	public void SuspendForLayout()
	{
		_layoutSuspended = true;
		SetNativeVisibility(false);
	}

	public void ResumeAfterLayout()
	{
		_layoutSuspended = false;
		_lastDestination = null;
		if (IsLoaded && IsVisible)
		{
			RegisterThumbnail();
			QueueUpdate();
		}
	}

	protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
	{
		_dpiScaleFactor = null;
		_lastDestination = null;
		base.OnDpiChanged(oldDpi, newDpi);
		QueueUpdate();
	}

	private static void OnPreviewHandleChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
	{
		var control = (DwmThumbnail)dependencyObject;
		control.UnregisterThumbnail();
		control._lastDestination = null;
		if (control.IsLoaded && control.IsVisible && !control._layoutSuspended)
			control.RegisterThumbnail();
	}

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		_window = Window.GetWindow(this);
		if (!_layoutSuspended)
		{
			RegisterThumbnail();
			QueueUpdate();
		}
	}

	private void OnUnloaded(object sender, RoutedEventArgs e)
	{
		UnregisterThumbnail();
		_window = null;
		_dpiScaleFactor = null;
		_lastDestination = null;
	}

	private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
	{
		if ((bool)e.NewValue && !_layoutSuspended)
		{
			RegisterThumbnail();
			QueueUpdate();
		}
		else
			UnregisterThumbnail();
	}

	private void RegisterThumbnail()
	{
		if (_dwmThumbnail != IntPtr.Zero || PreviewHandle == IntPtr.Zero || !Win32.IsWindow(PreviewHandle))
			return;

		_window ??= Window.GetWindow(this);
		if (_window is null)
			return;

		var destinationHandle = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
		if (destinationHandle == IntPtr.Zero)
			return;

		var result = NativeMethods.DwmRegisterThumbnail(destinationHandle, PreviewHandle, out _dwmThumbnail);
		if (result != 0)
		{
			_dwmThumbnail = IntPtr.Zero;
			AppLogger.Warn($"DwmRegisterThumbnail failed with HRESULT 0x{result:X8} for {PreviewHandle}.");
		}
	}

	private void UnregisterThumbnail()
	{
		if (_dwmThumbnail == IntPtr.Zero)
			return;
		NativeMethods.DwmUnregisterThumbnail(_dwmThumbnail);
		_dwmThumbnail = IntPtr.Zero;
	}

	private void QueueUpdate()
	{
		if (_updateQueued || _layoutSuspended || !IsLoaded || !IsVisible)
			return;
		_updateQueued = true;
		Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
		{
			_updateQueued = false;
			UpdateThumbnailProperties();
		}));
	}

	private void UpdateThumbnailProperties()
	{
		if (_layoutSuspended)
			return;

		if (_dwmThumbnail == IntPtr.Zero)
		{
			RegisterThumbnail();
			if (_dwmThumbnail == IntPtr.Zero)
				return;
		}

		try
		{
			_window ??= Window.GetWindow(this);
			if (_window is null || ActualWidth <= 0 || ActualHeight <= 0)
				return;

			var dpi = GetDpiScaleFactor();
			var origin = TransformToVisual(_window).Transform(new Point(0, 0));
			var destination = new RECT
			{
				left = (int)Math.Round(origin.X * dpi.X),
				top = (int)Math.Round(origin.Y * dpi.Y),
				right = (int)Math.Round((origin.X + ActualWidth) * dpi.X),
				bottom = (int)Math.Round((origin.Y + ActualHeight) * dpi.Y)
			};

			if (_lastDestination is RECT previous && RectEquals(previous, destination))
				return;

			var properties = new DWM_THUMBNAIL_PROPERTIES
			{
				fVisible = true,
				dwFlags = (int)(DWM_TNP.DWM_TNP_VISIBLE | DWM_TNP.DWM_TNP_OPACITY | DWM_TNP.DWM_TNP_RECTDESTINATION | DWM_TNP.DWM_TNP_SOURCECLIENTAREAONLY),
				opacity = 255,
				rcDestination = destination,
				fSourceClientAreaOnly = true
			};

			if (NativeMethods.DwmUpdateThumbnailProperties(_dwmThumbnail, ref properties) == 0)
				_lastDestination = destination;
		}
		catch (InvalidOperationException)
		{
			// The visual was detached between the layout event and the render callback.
		}
	}

	private void SetNativeVisibility(bool visible)
	{
		if (_dwmThumbnail == IntPtr.Zero)
			return;
		var properties = new DWM_THUMBNAIL_PROPERTIES
		{
			fVisible = visible,
			dwFlags = (int)DWM_TNP.DWM_TNP_VISIBLE
		};
		NativeMethods.DwmUpdateThumbnailProperties(_dwmThumbnail, ref properties);
	}

	private Point GetDpiScaleFactor()
	{
		if (_dpiScaleFactor is not null)
			return _dpiScaleFactor.Value;
		var source = PresentationSource.FromVisual(this);
		_dpiScaleFactor = source?.CompositionTarget is not null
			? new Point(source.CompositionTarget.TransformToDevice.M11, source.CompositionTarget.TransformToDevice.M22)
			: new Point(1, 1);
		return _dpiScaleFactor.Value;
	}

	private static bool RectEquals(RECT left, RECT right) =>
		left.left == right.left && left.top == right.top && left.right == right.right && left.bottom == right.bottom;
}
