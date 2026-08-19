using StageManager.Infrastructure;
using StageManager.Native.Window;

namespace StageManager.Card3DPrototype.Commands;

internal enum AppCommandKind
{
	ToggleSidebar,
	ShowSidebar,
	HideSidebar,
	OpenSettings,
	RefreshAllPreviews,
	RefreshStagePreviews,
	ActivateWindow,
	PreviousStage,
	NextStage,
	ExportDiagnostics,
	Exit
}

internal readonly record struct AppCommandRequest(
	AppCommandKind Kind,
	IWindow? Window = null,
	string? StageKey = null,
	bool AllowMinimize = false);

internal interface IAppCommandDispatcher
{
	void Register(AppCommandKind kind, Action<AppCommandRequest> handler);
	bool Execute(AppCommandRequest request);
}

internal sealed class AppCommandDispatcher : IAppCommandDispatcher, IDisposable
{
	private readonly Control _owner;
	private readonly Dictionary<AppCommandKind, Action<AppCommandRequest>> _handlers = new();
	private bool _disposed;

	public AppCommandDispatcher(Control owner) =>
		_owner = owner ?? throw new ArgumentNullException(nameof(owner));

	public void Register(AppCommandKind kind, Action<AppCommandRequest> handler)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		_handlers[kind] = handler ?? throw new ArgumentNullException(nameof(handler));
	}

	public bool Execute(AppCommandRequest request)
	{
		if (_disposed || _owner.IsDisposed || !_handlers.TryGetValue(request.Kind, out var handler))
			return false;
		if (_owner.InvokeRequired)
		{
			try
			{
				_owner.BeginInvoke(new Action(() => ExecuteCore(request, handler)));
				return true;
			}
			catch (InvalidOperationException)
			{
				return false;
			}
		}

		ExecuteCore(request, handler);
		return true;
	}

	public void Dispose()
	{
		_disposed = true;
		_handlers.Clear();
	}

	private static void ExecuteCore(AppCommandRequest request, Action<AppCommandRequest> handler)
	{
		try
		{
			handler(request);
		}
		catch (Exception exception)
		{
			AppLogger.Error($"Command '{request.Kind}' failed.", exception);
		}
	}
}
