namespace StageManager.Card3DPrototype.AiUsage;

internal sealed record AiUsageDetail(string Label, string Value);

internal sealed record AiUsageSnapshot(
	string ProviderId,
	string Name,
	string Badge,
	string Summary,
	string Status,
	DateTimeOffset? UpdatedUtc,
	IReadOnlyList<AiUsageDetail> Details,
	decimal? TrendValue,
	string TrendUnit,
	string ContinuityKey,
	IReadOnlyList<AiQuotaWindow>? Windows = null)
{
	public bool HasData => TrendValue.HasValue || Details.Count > 0;
	public bool NeedsConnection => Status is "Waiting for API key" or "Codex CLI not found" or "Codex login required" or "API key rejected";
}

internal sealed record AiQuotaWindow(string LimitId, string Label, decimal RemainingPercent,
	int? DurationMinutes, DateTimeOffset? ResetsAt);

internal sealed record AiUsagePoint(string ProviderId, DateTimeOffset At, decimal Value, string Unit, string ContinuityKey, bool BreakBefore);

internal sealed record AiUsageHistory(IReadOnlyList<AiUsagePoint> Points, string Unit);
