using StageManager;
using StageManager.Model;
using StageManager.Native.Window;
using StageManager.Services;
using StageManager.Settings;
using System.Drawing;
using System.IO;

return TestRunner.Run();

internal static class TestRunner
{
	private static int _failures;

	public static int Run()
	{
		RunTest("Stage groups multiple applications and rejects duplicate handles", StageGrouping);
		RunTest("Stage window cycling is deterministic", StageCycling);
		RunTest("Composite preview exposes three thumbnails and overflow count", CompositePreview);
		RunTest("Adaptive cards fit or scroll at 2, 6, 10, and 20 stages", AdaptiveCards);
		RunTest("Settings are normalized and persisted atomically", SettingsPersistence);
		RunTest("Default and custom hotkeys parse", HotkeyParsing);
		Console.WriteLine(_failures == 0 ? "All Stage_Manager_Lai tests passed." : $"{_failures} test(s) failed.");
		return _failures == 0 ? 0 : 1;
	}

	private static void StageGrouping()
	{
		var wechat = new FakeWindow(1, "WeChat", "wechat.exe");
		var codex = new FakeWindow(2, "Codex", "Codex.exe");
		var stage = new Stage(Stage.GetAppKey(wechat), wechat);
		stage.Add(codex);
		stage.Add(codex);
		Assert(stage.WindowCount == 2, "Duplicate handles were added.");
		Assert(stage.ContainsApp(Stage.GetAppKey(wechat)), "WeChat app key was lost.");
		Assert(stage.ContainsApp(Stage.GetAppKey(codex)), "Codex app key was lost.");
		stage.Remove(wechat);
		Assert(stage.WindowCount == 1 && stage.Windows[0].Handle == codex.Handle, "Removing a window damaged the stage.");
	}

	private static void StageCycling()
	{
		var first = new FakeWindow(10, "A", "a.exe");
		var second = new FakeWindow(11, "B", "b.exe");
		var stage = new Stage("cycle", first, second);
		Assert(stage.GetNextWindow()?.Handle == first.Handle, "Cycle did not start with the first window.");
		Assert(stage.GetNextWindow()?.Handle == second.Handle, "Cycle did not advance.");
		Assert(stage.GetNextWindow()?.Handle == first.Handle, "Cycle did not wrap.");
	}

	private static void CompositePreview()
	{
		var windows = Enumerable.Range(1, 5).Select(index => (IWindow)new FakeWindow(index, $"App{index}", $"app{index}.exe")).ToArray();
		var model = SceneModel.FromStage(new Stage("preview", windows));
		Assert(model.PreviewWindows.Count == 3, "Preview count is not capped at three.");
		Assert(model.ExtraWindowCount == 2 && model.ExtraWindowLabel == "+2", "Overflow badge is incorrect.");
	}

	private static void AdaptiveCards()
	{
		foreach (var count in new[] { 2, 6, 10, 20 })
		{
			var layout = CardLayoutCalculator.Calculate(900, count, 1);
			Assert(layout.Scale >= 0.55 && layout.Scale <= 1, $"Scale is outside bounds for {count} stages.");
			Assert(layout.CardWidth > 0 && layout.CardHeight > 0 && layout.Gap > 0, $"Invalid geometry for {count} stages.");
			var stride = layout.CardHeight + layout.Gap;
			for (var index = 1; index < count; index++)
			{
				var previousBottom = (index - 1) * stride + layout.CardHeight;
				var nextTop = index * stride;
				Assert(previousBottom < nextTop, $"Card slots overlap for {count} stages at index {index}.");
			}
			if (!layout.RequiresScrolling)
				Assert(count * stride <= 900.5, $"Non-scrolling layout overflows for {count} stages.");
		}
		Assert(!CardLayoutCalculator.Calculate(900, 2, 1).RequiresScrolling, "Two stages should fit without scrolling.");
		Assert(CardLayoutCalculator.Calculate(900, 20, 1).RequiresScrolling, "Twenty stages should scroll after reaching minimum size.");
	}

	private static void SettingsPersistence()
	{
		var directory = Path.Combine(Path.GetTempPath(), "StageManagerTests", Guid.NewGuid().ToString("N"));
		var path = Path.Combine(directory, "settings.json");
		try
		{
			var service = new SettingsService(path);
			Assert(!service.Current.AutoHideSidebar, "Sidebar auto-hide should be disabled by default.");
			Assert(Math.Abs(service.Current.CardScale - 0.60) < 0.001, "Default card scale should be 60%.");
			var settings = service.CloneCurrent();
			settings.CardScale = 99;
			settings.SidebarOpacity = 0;
			settings.StageMode = StageMode.Focus;
			settings.IgnoredProcesses = new List<string> { "yuanbao", "YuanBao", "  explorer  " };
			service.Apply(settings);
			Assert(service.Current.CardScale == 1.25, "Maximum card scale was not clamped.");
			settings = service.CloneCurrent();
			settings.CardScale = 0;
			service.Apply(settings);
			Assert(service.Current.CardScale == 0.55, "Minimum card scale was not clamped.");
			Assert(service.Current.SidebarOpacity == 0.65, "Opacity was not clamped.");
			Assert(service.Current.IgnoredProcesses.Count == 2, "Ignored process names were not normalized.");
			var reloaded = new SettingsService(path);
			Assert(reloaded.Current.StageMode == StageMode.Focus, "Enum setting did not persist.");
			Assert(File.Exists(path) && !File.Exists(path + ".tmp"), "Atomic settings replacement left an invalid temporary file.");
		}
		finally
		{
			if (Directory.Exists(directory))
				Directory.Delete(directory, true);
		}
	}

	private static void HotkeyParsing()
	{
		foreach (var gesture in new[] { "Win+Alt+S", "Win+Alt+[", "Win+Alt+]", "Ctrl+Shift+F12" })
			Assert(HotkeyManager.TryParse(gesture, out _, out _), $"Valid hotkey '{gesture}' was rejected.");
		Assert(!HotkeyManager.TryParse("S", out _, out _), "Modifier-free hotkey should be rejected.");
		Assert(!HotkeyManager.TryParse("Win+Magic+S", out _, out _), "Unknown modifier should be rejected.");
	}

	private static void RunTest(string name, Action test)
	{
		try
		{
			test();
			Console.WriteLine($"PASS: {name}");
		}
		catch (Exception ex)
		{
			_failures++;
			Console.WriteLine($"FAIL: {name}{Environment.NewLine}{ex}");
		}
	}

	private static void Assert(bool condition, string message)
	{
		if (!condition)
			throw new InvalidOperationException(message);
	}
}

internal sealed class FakeWindow : IWindow
{
	public FakeWindow(long handle, string processName, string executable)
	{
		Handle = new IntPtr(handle);
		ProcessName = processName;
		ProcessFileName = executable;
		ProcessExecutable = executable;
		Title = processName;
	}

	public event IWindowDelegate? WindowClosed;
	public event IWindowDelegate? WindowUpdated;
	public event IWindowDelegate? WindowFocused;
	public IntPtr Handle { get; }
	public string Title { get; }
	public string Class => "TestWindow";
	public IWindowLocation Location => new WindowLocation(100, 100, 800, 600, WindowState.Normal);
	public Rectangle Offset => Rectangle.Empty;
	public int ProcessId => Handle.ToInt32();
	public string ProcessFileName { get; }
	public string ProcessName { get; }
	public string ProcessExecutable { get; }
	public string? AppUserModelId => null;
	public bool CanLayout => true;
	public bool IsFocused { get; private set; }
	public bool IsMinimized { get; private set; }
	public bool IsMaximized => false;
	public bool IsMouseMoving => false;
	public void Focus() { IsFocused = true; WindowFocused?.Invoke(this); }
	public void ShowNormal() { IsMinimized = false; WindowUpdated?.Invoke(this); }
	public void ShowMaximized() { IsMinimized = false; WindowUpdated?.Invoke(this); }
	public void ShowMinimized() { IsMinimized = true; WindowUpdated?.Invoke(this); }
	public void ShowInCurrentState() { IsMinimized = false; WindowUpdated?.Invoke(this); }
	public void BringToTop() => WindowUpdated?.Invoke(this);
	public void Close() => WindowClosed?.Invoke(this);
	public void NotifyUpdated() => WindowUpdated?.Invoke(this);
}
