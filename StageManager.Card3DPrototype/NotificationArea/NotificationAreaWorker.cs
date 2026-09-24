using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

namespace StageManager.Card3DPrototype.NotificationArea;

internal static class NotificationAreaWorker
{
	private const string OverflowWindowClass = "TopLevelWindowForOverflowXamlIsland";
	private const string NotifyItemAutomationId = "NotifyItemIcon";
	private static string? _outputPath;

	public static int Run(string[] args)
	{
		try
		{
			var outputIndex = Array.FindIndex(args, value => string.Equals(value, "--output", StringComparison.Ordinal));
			if (outputIndex >= 0 && outputIndex + 1 < args.Length)
			{
				_outputPath = args[outputIndex + 1];
				args = args.Where((_, index) => index != outputIndex && index != outputIndex + 1).ToArray();
			}
			var command = args.Length > 0 ? args[0] : "snapshot";
			return command switch
			{
				"snapshot" => WriteSnapshot(),
				"invoke" => InvokeItem(args),
				"activate-app" => ActivateApplication(args),
				"show" => ShowOverflow(),
				_ => 2
			};
		}
		catch (Exception exception)
		{
			WriteResult(new NotificationAreaWorkerResult(false, [], exception.Message));
			return 1;
		}
	}

	private static int WriteSnapshot()
	{
		var button = FindOverflowButton();
		if (button is null)
			return WriteFailure("The Windows hidden-icons button was not found.");

		var overflow = FindOverflowWindow();
		var openedByWorker = overflow is null;
		try
		{
			if (openedByWorker)
			{
				Invoke(button);
				overflow = WaitForOverflowWindow();
			}
			if (overflow is null)
				return WriteFailure("The Windows hidden-icons panel did not open.");

			Thread.Sleep(120);
			var dpi = GetDpiForWindow((IntPtr)overflow.Current.NativeWindowHandle);
			var iconPixels = Math.Clamp((int)Math.Round(20d * Math.Max(96u, dpi) / 96d), 16, 48);
			var elements = GetNotifyItems(overflow);
			var icons = new List<NotificationAreaWorkerIcon>(Math.Min(elements.Count, 16));
			for (var index = 0; index < elements.Count && icons.Count < 16; index++)
			{
				var element = elements[index];
				var bounds = element.Current.BoundingRectangle;
				if (bounds.Width < iconPixels || bounds.Height < iconPixels || element.Current.IsOffscreen)
					continue;
				var left = (int)Math.Round(bounds.X + (bounds.Width - iconPixels) / 2d);
				var top = (int)Math.Round(bounds.Y + (bounds.Height - iconPixels) / 2d);
				using var bitmap = new Bitmap(iconPixels, iconPixels, PixelFormat.Format32bppArgb);
				using (var graphics = Graphics.FromImage(bitmap))
					graphics.CopyFromScreen(left, top, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
				RemovePanelBackground(bitmap);
				using var stream = new MemoryStream();
				bitmap.Save(stream, ImageFormat.Png);
				icons.Add(new NotificationAreaWorkerIcon(
					index,
					NormalizeName(element.Current.Name),
					Convert.ToBase64String(stream.ToArray())));
			}

			WriteResult(new NotificationAreaWorkerResult(true, icons.ToArray()));
			return 0;
		}
		finally
		{
			if (openedByWorker && FindOverflowWindow() is not null)
				TryInvoke(button);
		}
	}

	private static int InvokeItem(string[] args)
	{
		if (args.Length < 3 || !int.TryParse(args[1], out var ordinal))
			return WriteFailure("Invalid notification icon identity.");
		var expectedName = NormalizeName(Encoding.UTF8.GetString(Convert.FromBase64String(args[2])));
		if (string.IsNullOrWhiteSpace(expectedName))
			return WriteFailure("Unnamed notification icons cannot be invoked safely.");

		var button = FindOverflowButton();
		if (button is null)
			return WriteFailure("The Windows hidden-icons button was not found.");
		var overflow = FindOverflowWindow();
		if (overflow is null)
		{
			Invoke(button);
			overflow = WaitForOverflowWindow();
		}
		if (overflow is null)
			return WriteFailure("The Windows hidden-icons panel did not open.");

		var items = GetNotifyItems(overflow);
		AutomationElement? target = null;
		if (ordinal >= 0 && ordinal < items.Count &&
			string.Equals(NormalizeName(items[ordinal].Current.Name), expectedName, StringComparison.Ordinal))
		{
			target = items[ordinal];
		}
		target ??= items.FirstOrDefault(item =>
			string.Equals(NormalizeName(item.Current.Name), expectedName, StringComparison.Ordinal));
		if (target is null)
		{
			TryInvoke(button);
			return WriteFailure("The notification icon changed before it could be opened.");
		}

		Invoke(target);
		WriteResult(new NotificationAreaWorkerResult(true, []));
		return 0;
	}

	private static int ActivateApplication(string[] args)
	{
		if (args.Length != 2) return WriteFailure("Invalid application identity.");
		var name = Encoding.UTF8.GetString(Convert.FromBase64String(args[1]));
		var button = FindOverflowButton();
		if (button is null) return WriteFailure("The Windows hidden-icons button was not found.");
		var overflow = FindOverflowWindow();
		var opened = overflow is null || overflow.Current.IsOffscreen;
		var invoked = false;
		try
		{
			if (opened) { Invoke(button); overflow = WaitForOverflowWindow(); }
			if (overflow is null) return WriteFailure("The Windows hidden-icons panel did not open.");
			var items = GetNotifyItems(overflow);
			var match = NotificationIconMatcher.FindBest(items.Select((item, index) =>
				new NotificationIconActivation(index, NormalizeName(item.Current.Name))), [name]);
			if (match is null) return WriteFailure("The application's notification icon was not found.");
			Invoke(items[match.Value.Ordinal]);
			invoked = true;
			WriteResult(new NotificationAreaWorkerResult(true, []));
			return 0;
		}
		finally
		{
			// After invoking an application Explorer owns dismissal. Toggling the
			// overflow button here can steal foreground back from the restored app.
			if (!invoked && opened && FindOverflowWindow() is { } remaining && !remaining.Current.IsOffscreen)
				TryInvoke(button);
		}
	}

	private static int ShowOverflow()
	{
		if (FindOverflowWindow() is not null)
			return 0;
		var button = FindOverflowButton();
		if (button is null)
			return 1;
		Invoke(button);
		WriteResult(new NotificationAreaWorkerResult(true, []));
		return 0;
	}

	private static int WriteFailure(string message)
	{
		WriteResult(new NotificationAreaWorkerResult(false, [], message));
		return 1;
	}

	private static void WriteResult(NotificationAreaWorkerResult result)
	{
		var json = JsonSerializer.Serialize(result);
		if (string.IsNullOrWhiteSpace(_outputPath))
			Console.Out.Write(json);
		else
			File.WriteAllText(_outputPath, json);
	}

	private static AutomationElement? FindOverflowButton()
	{
		var taskbar = AutomationElement.RootElement.FindFirst(
			TreeScope.Children,
			new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd"));
		if (taskbar is null)
			return null;
		var button = FindOverflowButton(taskbar);
		if (button is not null)
			return button;
		if (!RevealAutoHiddenTaskbar(taskbar))
			return null;
		return FindOverflowButton(taskbar);
	}

	private static AutomationElement? FindOverflowButton(AutomationElement taskbar)
	{
		var descendants = taskbar.FindAll(TreeScope.Descendants, Condition.TrueCondition);
		for (var index = 0; index < descendants.Count; index++)
		{
			var element = descendants[index];
			var name = NormalizeName(element.Current.Name);
			if (element.Current.AutomationId == "SystemTrayIcon" &&
				(name.Contains("隐藏", StringComparison.OrdinalIgnoreCase) ||
				 name.Contains("hidden icon", StringComparison.OrdinalIgnoreCase)))
			{
				return element;
			}
		}
		return null;
	}

	private static bool RevealAutoHiddenTaskbar(AutomationElement taskbar)
	{
		if (!GetCursorPos(out var original))
			return false;
		try
		{
			var automationBounds = taskbar.Current.BoundingRectangle;
			var bounds = Rectangle.FromLTRB(
				(int)Math.Floor(automationBounds.Left),
				(int)Math.Floor(automationBounds.Top),
				(int)Math.Ceiling(automationBounds.Right),
				(int)Math.Ceiling(automationBounds.Bottom));
			if (bounds.Width <= 0 || bounds.Height <= 0)
				return false;
			var screen = Screen.FromRectangle(bounds).Bounds;
			int x;
			int y;
			if (bounds.Width >= bounds.Height)
			{
				x = Math.Clamp(bounds.Right - 90, screen.Left + 1, screen.Right - 2);
				y = Math.Abs(bounds.Top - screen.Top) < Math.Abs(bounds.Bottom - screen.Bottom)
					? screen.Top + 1
					: screen.Bottom - 2;
			}
			else
			{
				x = Math.Abs(bounds.Left - screen.Left) < Math.Abs(bounds.Right - screen.Right)
					? screen.Left + 1
					: screen.Right - 2;
				y = Math.Clamp(bounds.Bottom - 90, screen.Top + 1, screen.Bottom - 2);
			}
			if (!SetCursorPos(x, y))
				return false;
			Thread.Sleep(550);
			return true;
		}
		finally
		{
			SetCursorPos(original.X, original.Y);
		}
	}

	private static AutomationElement? FindOverflowWindow() =>
		AutomationElement.RootElement.FindFirst(
			TreeScope.Children,
			new PropertyCondition(AutomationElement.ClassNameProperty, OverflowWindowClass));

	private static AutomationElement? WaitForOverflowWindow()
	{
		for (var attempt = 0; attempt < 16; attempt++)
		{
			Thread.Sleep(50);
			var window = FindOverflowWindow();
			if (window is not null && !window.Current.IsOffscreen)
				return window;
		}
		return null;
	}

	private static List<AutomationElement> GetNotifyItems(AutomationElement overflow)
	{
		var descendants = overflow.FindAll(TreeScope.Descendants, Condition.TrueCondition);
		var items = new List<AutomationElement>();
		for (var index = 0; index < descendants.Count; index++)
		{
			var element = descendants[index];
			if (element.Current.AutomationId == NotifyItemAutomationId)
				items.Add(element);
		}
		return items;
	}

	private static void Invoke(AutomationElement element) =>
		((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

	private static void TryInvoke(AutomationElement element)
	{
		try { Invoke(element); }
		catch { }
	}

	private static string NormalizeName(string? value) =>
		string.Join(' ', (value ?? string.Empty)
			.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

	private static void RemovePanelBackground(Bitmap bitmap)
	{
		if (bitmap.Width < 3 || bitmap.Height < 3)
			return;
		var corners = new[]
		{
			bitmap.GetPixel(0, 0),
			bitmap.GetPixel(bitmap.Width - 1, 0),
			bitmap.GetPixel(0, bitmap.Height - 1),
			bitmap.GetPixel(bitmap.Width - 1, bitmap.Height - 1)
		};
		var background = Color.FromArgb(
			corners.Sum(color => color.R) / corners.Length,
			corners.Sum(color => color.G) / corners.Length,
			corners.Sum(color => color.B) / corners.Length);
		var visited = new bool[bitmap.Width, bitmap.Height];
		var queue = new Queue<Point>();
		for (var x = 0; x < bitmap.Width; x++)
		{
			queue.Enqueue(new Point(x, 0));
			queue.Enqueue(new Point(x, bitmap.Height - 1));
		}
		for (var y = 1; y < bitmap.Height - 1; y++)
		{
			queue.Enqueue(new Point(0, y));
			queue.Enqueue(new Point(bitmap.Width - 1, y));
		}
		while (queue.Count > 0)
		{
			var point = queue.Dequeue();
			if (point.X < 0 || point.Y < 0 || point.X >= bitmap.Width || point.Y >= bitmap.Height || visited[point.X, point.Y])
				continue;
			visited[point.X, point.Y] = true;
			var color = bitmap.GetPixel(point.X, point.Y);
			var red = color.R - background.R;
			var green = color.G - background.G;
			var blue = color.B - background.B;
			if (red * red + green * green + blue * blue > 42 * 42 * 3)
				continue;
			bitmap.SetPixel(point.X, point.Y, Color.Transparent);
			queue.Enqueue(new Point(point.X - 1, point.Y));
			queue.Enqueue(new Point(point.X + 1, point.Y));
			queue.Enqueue(new Point(point.X, point.Y - 1));
			queue.Enqueue(new Point(point.X, point.Y + 1));
		}
	}

	[DllImport("user32.dll")]
	private static extern uint GetDpiForWindow(IntPtr windowHandle);

	[DllImport("user32.dll")]
	private static extern bool GetCursorPos(out NativePoint point);

	[DllImport("user32.dll")]
	private static extern bool SetCursorPos(int x, int y);

	[StructLayout(LayoutKind.Sequential)]
	private readonly struct NativePoint
	{
		public readonly int X;
		public readonly int Y;
	}
}
