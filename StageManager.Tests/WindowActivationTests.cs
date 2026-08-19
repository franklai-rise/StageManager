using Microsoft.VisualStudio.TestTools.UnitTesting;
using StageManager.Native;
using System;

[TestClass]
public sealed class WindowActivationTests
{
	private static readonly IntPtr Root = new(100);

	[TestMethod]
	public void LastActiveEnabledPopupWins()
	{
		var facts = Facts(
			clicked: Candidate(101),
			lastActivePopup: Candidate(102),
			enabledPopup: Candidate(103));

		Assert.AreEqual(new IntPtr(102), WindowActivationPolicy.SelectTarget(facts));
	}

	[TestMethod]
	public void DisabledLastActivePopupFallsBackToEnabledPopup()
	{
		var facts = Facts(
			clicked: Candidate(101),
			lastActivePopup: Candidate(102, enabled: false),
			enabledPopup: Candidate(103));

		Assert.AreEqual(new IntPtr(103), WindowActivationPolicy.SelectTarget(facts));
	}

	[TestMethod]
	public void InvisibleLastActivePopupFallsBackToEnabledPopup()
	{
		var facts = Facts(
			clicked: Candidate(101),
			lastActivePopup: Candidate(102, visible: false),
			enabledPopup: Candidate(103));

		Assert.AreEqual(new IntPtr(103), WindowActivationPolicy.SelectTarget(facts));
	}

	[TestMethod]
	public void NoActivatePopupFallsBackToEnabledPopup()
	{
		var facts = Facts(
			clicked: Candidate(101),
			lastActivePopup: Candidate(102, canActivate: false),
			enabledPopup: Candidate(103));

		Assert.AreEqual(new IntPtr(103), WindowActivationPolicy.SelectTarget(facts));
	}

	[TestMethod]
	public void PopupFromAnotherOwnerFamilyIsNeverSelected()
	{
		var facts = Facts(
			clicked: Candidate(101),
			lastActivePopup: Candidate(202, rootOwner: new IntPtr(200)),
			enabledPopup: Candidate(103));

		Assert.AreEqual(new IntPtr(103), WindowActivationPolicy.SelectTarget(facts));
	}

	[TestMethod]
	public void ClickedWindowIsFallbackWhenNoPopupIsUsable()
	{
		var facts = Facts(
			clicked: Candidate(101),
			lastActivePopup: Missing(102),
			enabledPopup: Missing(103));

		Assert.AreEqual(new IntPtr(101), WindowActivationPolicy.SelectTarget(facts));
	}

	[TestMethod]
	public void ForegroundVerificationAcceptsOnlyResolvedWindowInExpectedFamily()
	{
		Assert.IsTrue(WindowActivationPolicy.IsExpectedForeground(
			foregroundWindow: new IntPtr(102),
			requestedTarget: new IntPtr(102),
			latestResolvedTarget: new IntPtr(102),
			expectedRootOwner: Root,
			foregroundRootOwner: Root));

		Assert.IsTrue(WindowActivationPolicy.IsExpectedForeground(
			foregroundWindow: new IntPtr(103),
			requestedTarget: new IntPtr(102),
			latestResolvedTarget: new IntPtr(103),
			expectedRootOwner: Root,
			foregroundRootOwner: Root));

		Assert.IsFalse(WindowActivationPolicy.IsExpectedForeground(
			foregroundWindow: new IntPtr(202),
			requestedTarget: new IntPtr(102),
			latestResolvedTarget: new IntPtr(202),
			expectedRootOwner: Root,
			foregroundRootOwner: new IntPtr(200)));
	}

	[TestMethod]
	public void FlashTargetsTaskbarOwningRoot()
	{
		var facts = Facts(
			clicked: Candidate(101),
			lastActivePopup: Candidate(102),
			enabledPopup: Candidate(103));

		Assert.AreEqual(Root, WindowActivationPolicy.SelectFlashTarget(facts, new IntPtr(102)));
	}

	private static WindowActivationFacts Facts(
		WindowActivationCandidate clicked,
		WindowActivationCandidate lastActivePopup,
		WindowActivationCandidate enabledPopup)
	{
		return new WindowActivationFacts(
			clicked,
			Candidate(Root.ToInt32()),
			lastActivePopup,
			enabledPopup);
	}

	private static WindowActivationCandidate Candidate(
		int handle,
		bool enabled = true,
		bool visible = true,
		IntPtr? rootOwner = null,
		bool canActivate = true)
	{
		return new WindowActivationCandidate(
			new IntPtr(handle),
			rootOwner ?? Root,
			Exists: true,
			IsEnabled: enabled,
			IsVisible: visible,
			CanActivate: canActivate);
	}

	private static WindowActivationCandidate Missing(int handle)
	{
		return new WindowActivationCandidate(new IntPtr(handle), Root, false, false, false, false);
	}
}
