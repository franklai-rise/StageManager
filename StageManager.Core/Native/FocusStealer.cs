using StageManager.Native.PInvoke;
using System;
using System.Collections.Generic;
using System.Threading;

namespace StageManager.Native;

/// <summary>
/// A point-in-time description of a window considered by the activation policy.
/// This intentionally contains no native calls so target selection can be tested
/// without changing the real foreground window.
/// </summary>
public readonly record struct WindowActivationCandidate(
	IntPtr Handle,
	IntPtr RootOwner,
	bool Exists,
	bool IsEnabled,
	bool IsVisible,
	bool CanActivate)
{
	public bool IsUsableFor(IntPtr expectedRootOwner)
	{
		return Handle != IntPtr.Zero &&
			Exists &&
			IsEnabled &&
			IsVisible &&
			CanActivate &&
			expectedRootOwner != IntPtr.Zero &&
			(Handle == expectedRootOwner || RootOwner == expectedRootOwner);
	}
}

/// <summary>
/// Native observations used to choose the real window to activate for a click.
/// </summary>
public readonly record struct WindowActivationFacts(
	WindowActivationCandidate Clicked,
	WindowActivationCandidate RootOwner,
	WindowActivationCandidate LastActivePopup,
	WindowActivationCandidate EnabledPopup)
{
	public IntPtr ExpectedRootOwner => RootOwner.Exists && RootOwner.Handle != IntPtr.Zero
		? RootOwner.Handle
		: Clicked.RootOwner;
}

/// <summary>
/// Pure activation decisions shared by the native implementation and unit tests.
/// </summary>
public static class WindowActivationPolicy
{
	public static IntPtr SelectTarget(WindowActivationFacts facts)
	{
		var expectedRootOwner = facts.ExpectedRootOwner;
		if (facts.LastActivePopup.IsUsableFor(expectedRootOwner))
			return facts.LastActivePopup.Handle;
		if (facts.EnabledPopup.IsUsableFor(expectedRootOwner))
			return facts.EnabledPopup.Handle;
		if (facts.Clicked.IsUsableFor(expectedRootOwner))
			return facts.Clicked.Handle;
		if (facts.RootOwner.IsUsableFor(expectedRootOwner))
			return facts.RootOwner.Handle;
		return IntPtr.Zero;
	}

	public static bool IsExpectedForeground(
		IntPtr foregroundWindow,
		IntPtr requestedTarget,
		IntPtr latestResolvedTarget,
		IntPtr expectedRootOwner,
		IntPtr foregroundRootOwner)
	{
		if (foregroundWindow == IntPtr.Zero || expectedRootOwner == IntPtr.Zero)
			return false;
		if (foregroundRootOwner != expectedRootOwner)
			return false;
		return foregroundWindow == requestedTarget || foregroundWindow == latestResolvedTarget;
	}

	public static IntPtr SelectFlashTarget(WindowActivationFacts facts, IntPtr activationTarget)
	{
		if (facts.RootOwner.Exists && facts.RootOwner.IsVisible && facts.RootOwner.Handle != IntPtr.Zero)
			return facts.RootOwner.Handle;
		if (facts.Clicked.Exists && facts.Clicked.IsVisible && facts.Clicked.Handle != IntPtr.Zero)
			return facts.Clicked.Handle;
		return activationTarget;
	}
}

public static class FocusStealer
{
	private const int VerificationAttempts = 6;
	private const int VerificationDelayMilliseconds = 15;

	public static bool Steal(IntPtr clickedWindow)
	{
		var facts = Inspect(clickedWindow);
		var target = WindowActivationPolicy.SelectTarget(facts);
		if (target == IntPtr.Zero)
			return false;
		var expectedRootOwner = facts.ExpectedRootOwner;
		var rootStamp = CaptureStamp(expectedRootOwner);
		if (!rootStamp.IsValid)
			return false;

		if (IsExpectedForeground(target, facts, expectedRootOwner, rootStamp))
			return true;

		RestoreIfMinimized(facts.RootOwner.Handle);
		if (target != facts.RootOwner.Handle)
			RestoreIfMinimized(target);

		facts = Inspect(clickedWindow);
		if (!MatchesStamp(rootStamp) || facts.ExpectedRootOwner != expectedRootOwner)
			return false;
		target = WindowActivationPolicy.SelectTarget(facts);
		if (target == IntPtr.Zero)
			return false;

		var currentThread = Win32.GetCurrentThreadId();
		var foregroundBeforeRequest = Win32.GetForegroundWindow();
		var foregroundThread = GetWindowThread(foregroundBeforeRequest);
		var targetThread = GetWindowThread(target);

		using (var attachments = new InputAttachmentScope(currentThread))
		{
			attachments.AttachTo(foregroundThread);
			attachments.AttachTo(targetThread);

			Win32.BringWindowToTop(target);
			Win32.SetForegroundWindow(target);
			Win32.SetActiveWindow(target);
			Win32.SetFocus(target);
		}

		for (var attempt = 0; attempt < VerificationAttempts; attempt++)
		{
			if (IsExpectedForeground(target, Inspect(clickedWindow), expectedRootOwner, rootStamp))
				return true;
			if (attempt + 1 < VerificationAttempts)
				Thread.Sleep(VerificationDelayMilliseconds);
		}

		var latestFacts = Inspect(clickedWindow);
		if (MatchesStamp(rootStamp) && latestFacts.ExpectedRootOwner == expectedRootOwner)
			FlashTaskbar(WindowActivationPolicy.SelectFlashTarget(latestFacts, target));
		return false;
	}

	private static WindowActivationFacts Inspect(IntPtr clickedWindow)
	{
		if (clickedWindow == IntPtr.Zero || !Win32.IsWindow(clickedWindow))
			return default;

		var rootOwner = NormalizeRootOwner(clickedWindow);
		var lastActivePopup = Win32.GetLastActivePopup(rootOwner);
		var enabledPopup = Win32.GetWindow(rootOwner, Win32.GW.GW_ENABLEDPOPUP);

		return new WindowActivationFacts(
			Describe(clickedWindow),
			Describe(rootOwner),
			Describe(lastActivePopup),
			Describe(enabledPopup));
	}

	private static WindowActivationCandidate Describe(IntPtr window)
	{
		if (window == IntPtr.Zero || !Win32.IsWindow(window))
			return new WindowActivationCandidate(window, IntPtr.Zero, false, false, false, false);

		var style = Win32.GetWindowStyleLongPtr(window);
		var extendedStyle = Win32.GetWindowExStyleLongPtr(window);

		return new WindowActivationCandidate(
			window,
			NormalizeRootOwner(window),
			true,
			Win32.IsWindowEnabled(window),
			Win32.IsWindowVisible(window),
			!style.HasFlag(Win32.WS.WS_CHILD) && !extendedStyle.HasFlag(Win32.WS_EX.WS_EX_NOACTIVATE));
	}

	private static IntPtr NormalizeRootOwner(IntPtr window)
	{
		if (window == IntPtr.Zero)
			return IntPtr.Zero;
		var rootOwner = Win32.GetAncestor(window, Win32.GA.GA_ROOTOWNER);
		return rootOwner == IntPtr.Zero ? window : rootOwner;
	}

	private static uint GetWindowThread(IntPtr window)
	{
		return window == IntPtr.Zero ? 0 : Win32.GetWindowThreadProcessId(window, out _);
	}

	private static void RestoreIfMinimized(IntPtr window)
	{
		if (window != IntPtr.Zero && Win32.IsWindow(window) && Win32.IsIconic(window))
			Win32.ShowWindowAsync(window, Win32.SW.SW_RESTORE);
	}

	private static bool IsExpectedForeground(
		IntPtr requestedTarget,
		WindowActivationFacts latestFacts,
		IntPtr expectedRootOwner,
		NativeWindowStamp rootStamp)
	{
		if (!MatchesStamp(rootStamp) || latestFacts.ExpectedRootOwner != expectedRootOwner)
			return false;

		var foreground = Win32.GetForegroundWindow();
		if (foreground == IntPtr.Zero || !Win32.IsWindow(foreground))
			return false;

		var latestResolvedTarget = WindowActivationPolicy.SelectTarget(latestFacts);
		return WindowActivationPolicy.IsExpectedForeground(
			foreground,
			requestedTarget,
			latestResolvedTarget,
			expectedRootOwner,
			NormalizeRootOwner(foreground));
	}

	private static NativeWindowStamp CaptureStamp(IntPtr window)
	{
		if (window == IntPtr.Zero || !Win32.IsWindow(window))
			return default;
		var threadId = Win32.GetWindowThreadProcessId(window, out var processId);
		return threadId == 0 || processId == 0
			? default
			: new NativeWindowStamp(window, threadId, processId);
	}

	private static bool MatchesStamp(NativeWindowStamp stamp)
	{
		if (!stamp.IsValid || !Win32.IsWindow(stamp.Handle))
			return false;
		var threadId = Win32.GetWindowThreadProcessId(stamp.Handle, out var processId);
		return threadId == stamp.ThreadId && processId == stamp.ProcessId;
	}

	private static void FlashTaskbar(IntPtr window)
	{
		if (window == IntPtr.Zero || !Win32.IsWindow(window))
			return;

		var flash = new Win32.FLASHWINFO
		{
			cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.FLASHWINFO>(),
			hwnd = window,
			dwFlags = Win32.FlashWindowFlags.Tray,
			uCount = 3,
			dwTimeout = 0,
		};
		Win32.FlashWindowEx(ref flash);
	}

	private sealed class InputAttachmentScope : IDisposable
	{
		private readonly uint _currentThread;
		private readonly List<uint> _attachedThreads = new(2);

		public InputAttachmentScope(uint currentThread)
		{
			_currentThread = currentThread;
		}

		public void AttachTo(uint thread)
		{
			if (thread == 0 || thread == _currentThread || _attachedThreads.Contains(thread))
				return;
			if (Win32.AttachThreadInput(_currentThread, thread, true))
				_attachedThreads.Add(thread);
		}

		public void Dispose()
		{
			for (var index = _attachedThreads.Count - 1; index >= 0; index--)
				Win32.AttachThreadInput(_currentThread, _attachedThreads[index], false);
			_attachedThreads.Clear();
		}
	}

	private readonly record struct NativeWindowStamp(IntPtr Handle, uint ThreadId, uint ProcessId)
	{
		public bool IsValid => Handle != IntPtr.Zero && ThreadId != 0 && ProcessId != 0;
	}
}
