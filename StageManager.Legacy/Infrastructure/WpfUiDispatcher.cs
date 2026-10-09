using StageManager.Threading;
using System;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace StageManager.Infrastructure;

internal sealed class WpfUiDispatcher : IUiDispatcher
{
	private readonly Dispatcher _dispatcher;

	public WpfUiDispatcher(Dispatcher dispatcher) =>
		_dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

	public bool CheckAccess() => _dispatcher.CheckAccess();

	public async Task InvokeAsync(Func<Task> action)
	{
		var operation = _dispatcher.InvokeAsync(action, DispatcherPriority.Background);
		await (await operation.Task.ConfigureAwait(false)).ConfigureAwait(false);
	}
}
