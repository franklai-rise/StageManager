namespace StageManager.Desktop;

internal sealed class StableStageOrder
{
	private readonly Dictionary<string, int> _slots = new(StringComparer.OrdinalIgnoreCase);
	private int _nextSlot;

	public IReadOnlyList<T> Apply<T>(
		IReadOnlyCollection<T> items,
		Func<T, string> getKey,
		Func<T, DateTime> getInitialPriority)
	{
		foreach (var item in items.OrderByDescending(getInitialPriority))
		{
			var key = getKey(item);
			if (!_slots.ContainsKey(key))
				_slots[key] = _nextSlot++;
		}

		var liveKeys = items.Select(getKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (var stale in _slots.Keys.Where(key => !liveKeys.Contains(key)).ToArray())
			_slots.Remove(stale);

		return items.OrderBy(item => _slots[getKey(item)]).ToArray();
	}
}
