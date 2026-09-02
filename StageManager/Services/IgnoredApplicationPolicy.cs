using System;
using System.Collections.Generic;

namespace StageManager.Services;

public static class IgnoredApplicationPolicy
{
	public static bool ShouldShowCard(string processName, IEnumerable<string> ignoredProcesses)
	{
		if (string.IsNullOrWhiteSpace(processName))
			return false;

		foreach (var ignored in ignoredProcesses)
		{
			if (string.Equals(ignored, processName, StringComparison.OrdinalIgnoreCase))
				return false;
		}
		return true;
	}
}
