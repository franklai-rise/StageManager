namespace StageManager.Card3DPrototype;

/// <summary>
/// Keeps child-window cards in the slot assigned when each window is first
/// observed. Focus, minimize, title and foreground changes must not move a
/// card out from underneath the pointer.
/// </summary>
internal sealed class StableWindowOrder
{
	private readonly Dictionary<string, StageSlots> _stageSlots = new(StringComparer.OrdinalIgnoreCase);

	public T[] Apply<T>(
		string stageKey,
		IReadOnlyCollection<T> windows,
		Func<T, IntPtr> getHandle)
	{
		if (!_stageSlots.TryGetValue(stageKey, out var state))
		{
			state = new StageSlots();
			_stageSlots[stageKey] = state;
		}

		foreach (var window in windows)
		{
			var handle = getHandle(window);
			if (!state.Slots.ContainsKey(handle))
				state.Slots[handle] = state.NextSlot++;
		}

		var liveHandles = windows.Select(getHandle).ToHashSet();
		foreach (var staleHandle in state.Slots.Keys.Where(handle => !liveHandles.Contains(handle)).ToArray())
			state.Slots.Remove(staleHandle);

		return windows
			.OrderBy(window => state.Slots[getHandle(window)])
			.ToArray();
	}

	public void RetainStages(IEnumerable<string> stageKeys)
	{
		var liveKeys = stageKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (var staleKey in _stageSlots.Keys.Where(key => !liveKeys.Contains(key)).ToArray())
			_stageSlots.Remove(staleKey);
	}

	private sealed class StageSlots
	{
		public Dictionary<IntPtr, long> Slots { get; } = new();
		public long NextSlot { get; set; }
	}
}
