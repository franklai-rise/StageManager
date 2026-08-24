using StageManager.Settings;

namespace StageManager.Card3DPrototype;

internal static class SidebarHintFormatter
{
	public static string Format(bool autoHideEnabled, int idleSeconds, UiLanguage language = UiLanguage.English)
	{
		if (!autoHideEnabled)
			return UiText.Get(language,
				"Always visible  ·  Click the arrow to hide",
				"始终显示  ·  点击箭头即可隐藏");

		var seconds = Math.Clamp(idleSeconds, 15, 600);
		if (UiText.IsChinese(language))
		{
			var chineseDelay = seconds % 60 == 0 ? $"{seconds / 60} 分钟" : $"{seconds} 秒";
			return $"{chineseDelay}后自动隐藏  ·  移到左侧边缘即可显示";
		}
		var delay = seconds % 60 == 0 ? $"{seconds / 60} min" : $"{seconds} sec";
		return $"Auto-hides after {delay}  ·  Move to the left edge to show";
	}
}
