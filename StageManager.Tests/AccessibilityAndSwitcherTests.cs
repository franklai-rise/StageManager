using Microsoft.VisualStudio.TestTools.UnitTesting;
using StageManager.Desktop;
using System.Runtime.InteropServices;

[TestClass]
public sealed class AccessibilityAndSwitcherTests
{
	[TestMethod]
	public void ReducedMotionAndHighContrastDisableAnimations()
	{
		Assert.IsTrue(AccessibilityPreferences.ShouldAnimate(true, false, true));
		Assert.IsFalse(AccessibilityPreferences.ShouldAnimate(false, false, true));
		Assert.IsFalse(AccessibilityPreferences.ShouldAnimate(true, true, true));
		Assert.IsFalse(AccessibilityPreferences.ShouldAnimate(true, false, false));
	}

	[TestMethod]
	public void SwitcherSearchMatchesApplicationTitleStageAndState()
	{
		var wechat = new FakeWindow(34001, "WeChat", "wechat.exe");
		var zotero = new FakeWindow(34002, "Zotero", "zotero.exe");
		var entries = new[]
		{
			new WindowSwitcherEntry("one", "Research", zotero, "on DISPLAY2"),
			new WindowSwitcherEntry("two", "Chat", wechat, "minimized")
		};

		Assert.AreEqual(1, WindowSwitcherSearch.Filter(entries, "zotero research").Count);
		Assert.AreSame(zotero, WindowSwitcherSearch.Filter(entries, "display2")[0].Window);
		Assert.AreSame(wechat, WindowSwitcherSearch.Filter(entries, "chat minimized")[0].Window);
		Assert.AreEqual(0, WindowSwitcherSearch.Filter(entries, "powerpoint").Count);
	}

	[TestMethod]
	public void GraphicsRecoveryRecognizesDeviceRemovalResetAndHung()
	{
		foreach (var hresult in new[]
		{
			unchecked((int)0x887A0005),
			unchecked((int)0x887A0006),
			unchecked((int)0x887A0007)
		})
		{
			Assert.IsTrue(GraphicsDeviceRecoveryPolicy.IsDeviceLoss(new COMException("device", hresult)));
		}
		Assert.IsTrue(GraphicsDeviceRecoveryPolicy.IsDeviceLoss(
			new InvalidOperationException("outer", new COMException("device", unchecked((int)0x887A0007)))));
		Assert.IsFalse(GraphicsDeviceRecoveryPolicy.IsDeviceLoss(new InvalidOperationException("ordinary")));
	}
}
