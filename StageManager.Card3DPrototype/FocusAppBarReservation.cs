using System.Runtime.InteropServices;

namespace StageManager.Card3DPrototype;

internal sealed class FocusAppBarReservation : IDisposable
{
	public const int CallbackMessage = 0x8000 + 0x4C1;
	private const uint AbmNew = 0x00000000;
	private const uint AbmRemove = 0x00000001;
	private const uint AbmQueryPos = 0x00000002;
	private const uint AbmSetPos = 0x00000003;
	private const uint AbnPosChanged = 0x00000001;
	private const uint AbeLeft = 0;
	private bool _registered;
	private IntPtr _windowHandle;
	private string _displayDeviceName = string.Empty;
	private Rectangle _displayBounds;
	private int _reservedWidth;

	public bool IsRegistered => _registered;
	public string DisplayDeviceName => _displayDeviceName;
	public Rectangle ReservedBounds { get; private set; }

	public bool SetReservation(IntPtr windowHandle, Screen display, int reservedWidth, bool force = false)
	{
		ArgumentNullException.ThrowIfNull(display);
		if (windowHandle == IntPtr.Zero || reservedWidth <= 0)
		{
			Remove();
			return false;
		}

		var displayChanged = !string.Equals(
			_displayDeviceName,
			display.DeviceName,
			StringComparison.OrdinalIgnoreCase);
		if (_registered && (_windowHandle != windowHandle || displayChanged))
			Remove();

		_windowHandle = windowHandle;
		_displayDeviceName = display.DeviceName;
		_displayBounds = display.Bounds;
		if (!_registered)
		{
			var registration = CreateData();
			registration.CallbackMessage = CallbackMessage;
			if (SHAppBarMessage(AbmNew, ref registration) == UIntPtr.Zero)
			{
				ClearState();
				return false;
			}
			_registered = true;
			force = true;
		}

		if (!force && _reservedWidth == reservedWidth && ReservedBounds.Width > 0)
			return true;

		_reservedWidth = reservedWidth;
		ApplyPosition();
		return ReservedBounds.Width > 0;
	}

	public bool IsPositionChangedMessage(int message, IntPtr wParam)
	{
		return _registered && message == CallbackMessage && unchecked((uint)wParam.ToInt64()) == AbnPosChanged;
	}

	public void Reapply()
	{
		if (_registered)
			ApplyPosition();
	}

	public void Remove()
	{
		if (_registered && _windowHandle != IntPtr.Zero)
		{
			var data = CreateData();
			SHAppBarMessage(AbmRemove, ref data);
		}
		ClearState();
	}

	public void Dispose() => Remove();

	private void ApplyPosition()
	{
		if (!_registered || _windowHandle == IntPtr.Zero || _reservedWidth <= 0)
			return;

		var data = CreateData();
		data.Edge = AbeLeft;
		data.Bounds = AppBarRect.FromRectangle(_displayBounds);
		SHAppBarMessage(AbmQueryPos, ref data);
		data.Bounds.Right = data.Bounds.Left + _reservedWidth;
		SHAppBarMessage(AbmSetPos, ref data);
		ReservedBounds = data.Bounds.ToRectangle();
	}

	private AppBarData CreateData()
	{
		return new AppBarData
		{
			Size = Marshal.SizeOf<AppBarData>(),
			WindowHandle = _windowHandle
		};
	}

	private void ClearState()
	{
		_registered = false;
		_windowHandle = IntPtr.Zero;
		_displayDeviceName = string.Empty;
		_displayBounds = Rectangle.Empty;
		_reservedWidth = 0;
		ReservedBounds = Rectangle.Empty;
	}

	[DllImport("shell32.dll", CallingConvention = CallingConvention.StdCall)]
	private static extern UIntPtr SHAppBarMessage(uint message, ref AppBarData data);

	[StructLayout(LayoutKind.Sequential)]
	private struct AppBarData
	{
		public int Size;
		public IntPtr WindowHandle;
		public uint CallbackMessage;
		public uint Edge;
		public AppBarRect Bounds;
		public IntPtr Parameter;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct AppBarRect
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;

		public static AppBarRect FromRectangle(Rectangle rectangle)
		{
			return new AppBarRect
			{
				Left = rectangle.Left,
				Top = rectangle.Top,
				Right = rectangle.Right,
				Bottom = rectangle.Bottom
			};
		}

		public readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
	}
}
