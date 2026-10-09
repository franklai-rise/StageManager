using System.Globalization;
using System.Windows.Forms;

namespace StageManager.Services;

public static class HotkeyGestureParser
{
	public const uint Alt = 0x0001;
	public const uint Control = 0x0002;
	public const uint Shift = 0x0004;
	public const uint Win = 0x0008;

	public static bool TryParse(string? gesture, out uint modifiers, out uint virtualKey)
	{
		modifiers = 0;
		virtualKey = 0;
		if (string.IsNullOrWhiteSpace(gesture))
			return false;

		var parts = gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length < 2)
			return false;

		foreach (var modifier in parts.Take(parts.Length - 1))
		{
			var flag = modifier.ToUpperInvariant() switch
			{
				"WIN" or "WINDOWS" => Win,
				"ALT" => Alt,
				"CTRL" or "CONTROL" => Control,
				"SHIFT" => Shift,
				_ => 0u
			};
			if (flag == 0 || (modifiers & flag) != 0)
				return false;
			modifiers |= flag;
		}

		var keyText = parts[^1];
		if (keyText == "[")
			virtualKey = (uint)Keys.OemOpenBrackets;
		else if (keyText == "]")
			virtualKey = (uint)Keys.OemCloseBrackets;
		else if (keyText.Length == 1 && char.IsLetterOrDigit(keyText[0]))
			virtualKey = char.ToUpper(keyText[0], CultureInfo.InvariantCulture);
		else if (Enum.TryParse<Keys>(keyText, true, out var key) && key != Keys.None)
			virtualKey = (uint)(key & Keys.KeyCode);

		return virtualKey != 0;
	}
}
