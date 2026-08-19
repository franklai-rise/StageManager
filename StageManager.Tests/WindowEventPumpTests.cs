using Microsoft.VisualStudio.TestTools.UnitTesting;
using StageManager.Model;
using StageManager.Native;
using System;
using System.Linq;

[TestClass]
public sealed class WindowEventPumpTests
{
	[TestMethod]
	public void BatchRetainsLifecycleAndForegroundOrder()
	{
		var firstLifetime = Instance(handle: 42, generation: 1);
		var secondLifetime = Instance(handle: 42, generation: 2);
		var batch = WindowEventBatch.Create(
		[
			Event(1, WindowEventKind.Create, firstLifetime),
			Event(2, WindowEventKind.LocationChanged, firstLifetime),
			Event(3, WindowEventKind.LocationChanged, firstLifetime),
			Event(4, WindowEventKind.Foreground, firstLifetime),
			Event(5, WindowEventKind.Foreground, firstLifetime),
			Event(6, WindowEventKind.Destroy, firstLifetime),
			Event(7, WindowEventKind.Create, secondLifetime),
			Event(8, WindowEventKind.Foreground, secondLifetime),
		],
			requiresReconcile: false);

		CollectionAssert.AreEqual(
			new[]
			{
				WindowEventKind.Create,
				WindowEventKind.LocationChanged,
				WindowEventKind.Foreground,
				WindowEventKind.Foreground,
				WindowEventKind.Destroy,
				WindowEventKind.Create,
				WindowEventKind.Foreground,
			},
			batch.Events.Select(item => item.Kind).ToArray());
		Assert.AreEqual(3L, batch.Events[1].Sequence, "The newest location observation was not retained.");
		Assert.AreEqual(1L, batch.Events[4].InstanceId.Generation);
		Assert.AreEqual(2L, batch.Events[5].InstanceId.Generation);
	}

	[TestMethod]
	public void BatchRestoresSequenceOrderFromConcurrentWriters()
	{
		var instance = Instance(73, 1);
		var batch = WindowEventBatch.Create(
		[
			Event(3, WindowEventKind.Destroy, instance),
			Event(1, WindowEventKind.Create, instance),
			Event(2, WindowEventKind.Foreground, instance),
		],
			requiresReconcile: false);

		CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, batch.Events.Select(item => item.Sequence).ToArray());
	}

	[TestMethod]
	public void BatchNeverMergesDifferentHandlesOrGenerations()
	{
		var batch = WindowEventBatch.Create(
		[
			Event(1, WindowEventKind.LocationChanged, Instance(7, 1)),
			Event(2, WindowEventKind.LocationChanged, Instance(8, 1)),
			Event(3, WindowEventKind.LocationChanged, Instance(7, 2)),
		],
			requiresReconcile: false);

		Assert.AreEqual(3, batch.Events.Length);
		CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, batch.Events.Select(item => item.Sequence).ToArray());
	}

	[TestMethod]
	public void BatchUsesNonCoalescibleEventsAsOrderingBarriers()
	{
		var instance = Instance(99, 3);
		var batch = WindowEventBatch.Create(
		[
			Event(1, WindowEventKind.LocationChanged, instance),
			Event(2, WindowEventKind.Foreground, instance),
			Event(3, WindowEventKind.LocationChanged, instance),
		],
			requiresReconcile: false);

		CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, batch.Events.Select(item => item.Sequence).ToArray());
	}

	[TestMethod]
	public void BoundedInboxRequestsOneShotReconcileOnOverflow()
	{
		Assert.AreEqual(2048, WindowEventInbox.DefaultCapacity);
		var inbox = new WindowEventInbox(capacity: 2);
		var instance = Instance(12, 1);

		Assert.IsTrue(inbox.TryWrite(Event(1, WindowEventKind.Show, instance)));
		Assert.IsTrue(inbox.TryWrite(Event(2, WindowEventKind.Foreground, instance)));
		Assert.IsFalse(inbox.TryWrite(Event(3, WindowEventKind.Destroy, instance)));

		var overflowBatch = inbox.DrainBatch();
		Assert.IsTrue(overflowBatch.RequiresReconcile);
		Assert.AreEqual(2, overflowBatch.Events.Length);

		var nextBatch = inbox.DrainBatch();
		Assert.IsFalse(nextBatch.RequiresReconcile, "The overflow signal was not consumed atomically.");
		Assert.AreEqual(0, nextBatch.Events.Length);
	}

	private static WindowInstanceId Instance(long handle, long generation) =>
		new(new IntPtr(handle), 1234, DateTimeOffset.UnixEpoch, generation);

	private static WindowEventEnvelope Event(long sequence, WindowEventKind kind, WindowInstanceId instance) =>
		new(sequence, kind, instance, 0, 0, 0, 0);
}
