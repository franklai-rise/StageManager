using System.Text;

namespace StageManager.Card3DPrototype.NotificationArea;

internal static class NotificationIconMatcher
{
	public static NotificationIconActivation? FindBest(
		IEnumerable<NotificationIconActivation> icons,
		IEnumerable<string?> candidateNames)
	{
		var candidates = candidateNames
			.SelectMany(CreateVariants)
			.Where(value => value.Length >= 3)
			.Distinct(StringComparer.Ordinal)
			.ToArray();
		if (candidates.Length == 0) return null;

		NotificationIconActivation? best = null;
		var bestScore = 0;
		foreach (var icon in icons)
		{
			var normalizedIcon = Normalize(icon.Name);
			if (normalizedIcon.Length < 3) continue;
			foreach (var candidate in candidates)
			{
				var score = normalizedIcon == candidate
					? 10_000 + candidate.Length
					: normalizedIcon.Contains(candidate, StringComparison.Ordinal) ||
					  candidate.Contains(normalizedIcon, StringComparison.Ordinal)
						? candidate.Length
						: 0;
				if (score <= bestScore) continue;
				bestScore = score;
				best = icon;
			}
		}
		return bestScore > 0 ? best : null;
	}

	private static IEnumerable<string> CreateVariants(string? value)
	{
		var normalized = Normalize(value);
		if (normalized.Length > 0) yield return normalized;
		if (string.IsNullOrWhiteSpace(value)) yield break;
		var separator = value.IndexOfAny(['·', '|', '—', '–']);
		if (separator <= 0) yield break;
		var stableTitle = Normalize(value[..separator]);
		if (stableTitle.Length > 0) yield return stableTitle;
	}

	private static string Normalize(string? value)
	{
		if (string.IsNullOrWhiteSpace(value)) return string.Empty;
		var result = new StringBuilder(value.Length);
		foreach (var character in value.ToLowerInvariant())
			if (char.IsLetterOrDigit(character)) result.Append(character);
		return result.ToString();
	}
}
