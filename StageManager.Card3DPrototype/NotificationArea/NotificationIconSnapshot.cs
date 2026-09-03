using System.Drawing;

namespace StageManager.Card3DPrototype.NotificationArea;

internal sealed record NotificationIconSnapshot(int Ordinal, string Name, Bitmap Icon) : IDisposable
{
	public void Dispose() => Icon.Dispose();
}

internal readonly record struct NotificationIconActivation(int Ordinal, string Name);

internal sealed record NotificationAreaWorkerIcon(int Ordinal, string Name, string PngBase64);

internal sealed record NotificationAreaWorkerResult(bool Success, NotificationAreaWorkerIcon[] Icons, string? Error = null);
