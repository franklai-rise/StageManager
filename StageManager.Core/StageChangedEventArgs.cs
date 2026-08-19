using StageManager.Native.Window;
using System;

namespace StageManager;

public sealed class StageChangedEventArgs : EventArgs
{
	public StageChangedEventArgs(Stage stage, IWindow? window, ChangeType change)
	{
		Stage = stage;
		Window = window;
		Change = change;
	}

	public Stage Stage { get; }
	public IWindow? Window { get; }
	public ChangeType Change { get; }
}

public sealed class CurrentStageSelectionChangedEventArgs : EventArgs
{
	public CurrentStageSelectionChangedEventArgs(Stage? prior, Stage? current)
	{
		Prior = prior;
		Current = current;
	}

	public Stage? Prior { get; }
	public Stage? Current { get; }
}
