using StageManager.Native.Window;
using System;

namespace StageManager;

/// <summary>
/// Compatibility name retained for integrations built against the original prototype.
/// New code uses <see cref="Stage"/>.
/// </summary>
public sealed class Scene : Stage
{
	public Scene(string key, params IWindow[] windows) : base(key, windows)
	{
	}

	public string Key => InitialAppKey;
}
