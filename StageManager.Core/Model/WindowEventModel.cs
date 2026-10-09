using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace StageManager.Model;

/// <summary>
/// Identifies one lifetime of a native window. A handle can be reused by Windows,
/// so callers must not compare handles without also comparing the generation.
/// </summary>
public readonly record struct WindowInstanceId(
	IntPtr Handle,
	int ProcessId,
	DateTimeOffset? ProcessStartTimeUtc,
	long Generation)
{
	public bool HasResolvedProcess => ProcessId > 0 && ProcessStartTimeUtc.HasValue;
}

public enum WindowEventKind
{
	Create,
	Destroy,
	Show,
	Hide,
	Cloaked,
	Uncloaked,
	MinimizeStart,
	MinimizeEnd,
	Foreground,
	MoveStart,
	MoveEnd,
	LocationChanged,
	NameChanged,
	StyleChanged,
	DesktopSwitch,
}

/// <summary>
/// Immutable data captured by the WinEvent callback. Expensive process metadata
/// can be resolved later by the event pump by creating a copy with a richer
/// <see cref="InstanceId"/>.
/// </summary>
public sealed record WindowEventEnvelope(
	long Sequence,
	WindowEventKind Kind,
	WindowInstanceId InstanceId,
	int ObjectId,
	int ChildId,
	uint EventThreadId,
	uint NativeEventTime);

/// <summary>
/// One ordered event-pump delivery. Only redundant high-frequency observations
/// are coalesced; lifecycle and foreground barriers are always retained.
/// </summary>
public sealed record WindowEventBatch(
	ImmutableArray<WindowEventEnvelope> Events,
	bool RequiresReconcile)
{
	public static WindowEventBatch Empty { get; } = new([], false);

	internal static WindowEventBatch Create(
		IEnumerable<WindowEventEnvelope> events,
		bool requiresReconcile)
	{
		ArgumentNullException.ThrowIfNull(events);
		var ordered = events.OrderBy(item => item.Sequence).ToArray();
		if (ordered.Length == 0)
			return requiresReconcile ? new([], true) : Empty;

		var output = ImmutableArray.CreateBuilder<WindowEventEnvelope>(ordered.Length);
		var pending = new Dictionary<CoalesceKey, WindowEventEnvelope>();

		foreach (var item in ordered)
		{
			if (IsCoalescible(item.Kind))
			{
				pending[new CoalesceKey(item.InstanceId.Handle, item.InstanceId.Generation, item.Kind)] = item;
				continue;
			}

			FlushPending(pending, output);
			output.Add(item);
		}

		FlushPending(pending, output);
		return new WindowEventBatch(output.ToImmutable(), requiresReconcile);
	}

	private static bool IsCoalescible(WindowEventKind kind) =>
		kind is WindowEventKind.LocationChanged or WindowEventKind.NameChanged or WindowEventKind.StyleChanged;

	private static void FlushPending(
		Dictionary<CoalesceKey, WindowEventEnvelope> pending,
		ImmutableArray<WindowEventEnvelope>.Builder output)
	{
		if (pending.Count == 0)
			return;

		foreach (var item in pending.Values.OrderBy(item => item.Sequence))
			output.Add(item);
		pending.Clear();
	}

	private readonly record struct CoalesceKey(IntPtr Handle, long Generation, WindowEventKind Kind);
}
