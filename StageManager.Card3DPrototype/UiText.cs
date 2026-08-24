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
			["Card size"] = "卡片大小",
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
			["Keyboard shortcuts"] = "键盘快捷键",
			["Enable global shortcuts"] = "启用全局快捷键",
			["Show / hide sidebar"] = "显示/隐藏侧栏",
			["Previous card"] = "上一张卡片",
			["Next card"] = "下一张卡片",
			["Ignored applications"] = "已忽略的应用",
			["Check a running app to hide it; no .exe name is required."] = "勾选正在运行的应用即可隐藏，无需填写 .exe 文件名。",
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
