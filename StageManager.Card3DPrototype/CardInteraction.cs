using System.Globalization;
using System.Text;
using StageManager.Settings;

namespace StageManager.Card3DPrototype;

internal enum CardFeedback { None, Pressed }

internal static class CardInteraction
{
	public static bool IsWindowCard(CardHitTarget? target) => target is not null &&
		!target.IsExplorerButton && target.QuickLaunchApp is null && !target.IsExpandAllButton &&
		!target.IsPinButton && !target.IsSidebarCollapseButton && !target.IsDesktopButton &&
		!target.IsDesktopIconsButton &&
		!target.IsNotificationAreaCard && target.PageDelta == 0;

	public static string Key(CardHitTarget target) =>
		$"{target.StageKey}:{target.Window?.Handle}:{target.IsPrimaryCard}:{target.IsPinButton}:{target.PageDelta}:" +
		$"{target.IsDesktopButton}:{target.IsDesktopIconsButton}:{target.IsNotificationAreaDragHandle}:{target.IsNotificationAreaRefreshButton}:" +
		$"{target.NotificationIcon?.Ordinal}:{target.NotificationIconName}";

	public static bool SameTarget(CardHitTarget? first, CardHitTarget? second) =>
		first is not null && second is not null && Key(first) == Key(second);
}

internal static class CardHoverText
{
	public static string Window(UiLanguage language, string? title, string fallback,
		bool minimized, bool foreground, bool maximized)
	{
		string L(string en, string zh) => UiText.Get(language, en, zh);
		var heading = CompactTitle(string.IsNullOrWhiteSpace(title) ? fallback : title);
		var state = minimized ? L(" · Minimized", " · 已最小化") :
			maximized ? L(" · Maximized", " · 已最大化") : string.Empty;
		var action = minimized ? L("Click to restore", "单击恢复窗口") : foreground
			? L("Click to minimize · Click again to restore", "单击最小化 · 再点恢复")
			: L("Click to bring forward · Keep window size", "单击置于前台 · 保持窗口大小");
		return $"{heading}{state}\n{action}\n{L("Right-click for window actions", "右键：最大化、居中等操作")}";
	}

	public static string Group(UiLanguage language, string title, int count, bool expanded,
		bool pinned, bool allExpanded, bool canExpand, bool pinAvailable = true)
	{
		string L(string en, string zh) => UiText.Get(language, en, zh);
		var action = allExpanded ? L("All groups fixed open · Use FIXED above to collapse", "全部固定展开 · 点击顶部 FIXED 收起") :
			pinned ? L("Fixed open · Click FIXED to release", "已固定展开 · 点击 FIXED 解除") :
			expanded ? (pinAvailable ? L("Click to collapse · FIX keeps this group open", "单击收起 · FIX 可保持展开") : L("Click to collapse", "单击收起")) :
			canExpand ? L("Hover or click to show windows", "悬停或单击展开窗口") :
			L("Release the other fixed group to expand this one", "请先解除另一组的 FIXED，再展开此组");
		return $"{CompactTitle(title)} · {count} {L(count == 1 ? "window" : "windows", "个窗口")}\n{action}\n{L("Right-click for group actions", "右键：此应用的窗口操作")}";
	}

	public static string CompactTitle(string? text, int columns = 44)
	{
		var result = new StringBuilder();
		var elements = StringInfo.GetTextElementEnumerator(text ?? string.Empty);
		var used = 0;
		var whitespace = false;
		while (elements.MoveNext())
		{
			var element = elements.GetTextElement();
			if (char.IsControl(element[0]) || char.IsWhiteSpace(element[0]))
			{
				whitespace = result.Length > 0;
				continue;
			}
			var width = element[0] > 0x7f ? 2 : 1;
			if (used + width + (whitespace ? 1 : 0) > columns)
			{
				result.Append('…');
				break;
			}
			if (whitespace) { result.Append(' '); used++; }
			result.Append(element);
			used += width;
			whitespace = false;
		}
		return result.ToString();
	}
}
