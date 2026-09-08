using StageManager.Settings;
using System.Runtime.CompilerServices;

namespace StageManager.Card3DPrototype;

internal static class UiText
{
	private static readonly IReadOnlyDictionary<string, string> EnglishToChinese =
		new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["Stage_Manager_Lai Settings"] = "Stage_Manager_Lai 设置",
			["Appearance"] = "外观",
			["Interface language"] = "界面语言",
			["English"] = "英文",
			["Simplified Chinese"] = "简体中文",
			["Card size"] = "卡片大小",
			["Vertical position"] = "垂直位置",
			["px (negative = up)"] = "像素（负数=向上）",
			["Show File Explorer button above cards"] = "在卡片上方显示文件资源管理器按钮",
			["Show Google Chrome quick-launch card"] = "显示 Google Chrome 快捷启动卡片",
			["Show Microsoft Edge quick-launch card"] = "显示 Microsoft Edge 快捷启动卡片",
			["Show pin button when multi-window cards are expanded"] = "多窗口卡片展开时显示固定按钮",
			["Show Windows hidden icons in a bottom card"] = "在底部卡片中显示 Windows 隐藏图标",
			["Show minimize / restore shortcut below window cards"] = "在窗口卡片下方显示一键最小化／恢复快捷卡",
			["Show desktop-icons toggle below minimize / restore"] = "在一键最小化／恢复下方显示桌面图标开关",
			["Use animations"] = "使用动画",
			["Low-memory renderer (restart required)"] = "低内存渲染器（需重启）",
			["Behavior"] = "行为",
			["Auto-hide after no pointer activity"] = "鼠标无活动后自动隐藏",
			["Idle delay"] = "空闲延迟",
			["seconds"] = "秒",
			["Start with Windows"] = "开机自动启动",
			["Preview refresh"] = "预览刷新",
			["min"] = "分钟",
			["Pause preview refresh while the sidebar is hidden"] = "侧栏隐藏时暂停预览刷新",
			["Focus enhanced mode (reserve the card column)"] = "Focus 增强模式（保留卡片栏区域）",
			["Keyboard shortcuts"] = "键盘快捷键",
			["Enable global shortcuts"] = "启用全局快捷键",
			["Show / hide sidebar"] = "显示/隐藏侧栏",
			["Previous card"] = "上一张卡片",
			["Next card"] = "下一张卡片",
			["Ignored applications"] = "已忽略的应用",
			["Check an app to hide its card only; the app remains fully usable."] = "勾选后只隐藏该应用的卡片，应用本身仍可正常打开和使用。",
			["Advanced: process names (optional)"] = "高级：进程名称（可选）",
			["Reset defaults"] = "恢复默认设置",
			["Cancel"] = "取消",
			["Save"] = "保存",
			["Show / Hide sidebar"] = "显示 / 隐藏侧栏",
			["Settings..."] = "设置...",
			["Refresh all previews now"] = "立即刷新全部预览",
			["Exit Stage_Manager_Lai"] = "退出 Stage_Manager_Lai"
		};

	private static readonly IReadOnlyDictionary<string, string> ChineseToEnglish =
		EnglishToChinese.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);
	private static readonly ConditionalWeakTable<ComboBox, ComboLanguageState> ComboStates = new();

	public static bool IsChinese(UiLanguage language) => language == UiLanguage.SimplifiedChinese;

	public static string Get(UiLanguage language, string english, string chinese) =>
		IsChinese(language) ? chinese : english;

	public static string Translate(UiLanguage language, string? text)
	{
		if (string.IsNullOrEmpty(text))
			return text ?? string.Empty;
		var translations = IsChinese(language) ? EnglishToChinese : ChineseToEnglish;
		return translations.TryGetValue(text, out var translated) ? translated : text;
	}

	public static void Apply(Control root, UiLanguage language)
	{
		if (root is ComboBox comboBox)
			ConfigureComboBox(comboBox, language);
		else
			root.Text = Translate(language, root.Text);
		if (root is DataGridView grid)
		{
			foreach (DataGridViewColumn column in grid.Columns)
				column.HeaderText = Translate(language, column.HeaderText);
		}
		foreach (Control child in root.Controls)
			Apply(child, language);
	}

	public static void Apply(ToolStripItemCollection items, UiLanguage language)
	{
		foreach (ToolStripItem item in items)
		{
			item.Text = Translate(language, item.Text);
			if (item is ToolStripDropDownItem dropDown)
				Apply(dropDown.DropDownItems, language);
		}
	}

	private static void ConfigureComboBox(ComboBox comboBox, UiLanguage language)
	{
		var state = ComboStates.GetValue(comboBox, _ => new ComboLanguageState());
		state.Language = language;
		if (!state.Hooked)
		{
			comboBox.FormattingEnabled = true;
			comboBox.Format += (_, args) =>
			{
				var current = ComboStates.GetValue(comboBox, _ => new ComboLanguageState()).Language;
				args.Value = Translate(current, Convert.ToString(args.Value) ?? string.Empty);
			};
			state.Hooked = true;
		}
		comboBox.Refresh();
	}

	private sealed class ComboLanguageState
	{
		public UiLanguage Language { get; set; }
		public bool Hooked { get; set; }
	}
}
