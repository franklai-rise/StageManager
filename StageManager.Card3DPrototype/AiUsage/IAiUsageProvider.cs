using StageManager.Settings;

namespace StageManager.Card3DPrototype.AiUsage;

internal interface IAiUsageProvider
{
	string ProviderId { get; }
	bool IsEnabled(AppSettings settings);
	Task<AiUsageSnapshot> RefreshAsync(AppSettings settings, CancellationToken cancellationToken);
}
