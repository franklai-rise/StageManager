using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StageManager.Card3DPrototype.AiUsage;

internal static class AiUsageParsers
{
	public static AiUsageSnapshot Codex(JsonElement result, string accountScope, DateTimeOffset now)
	{
		var windows = new List<AiQuotaWindow>();
		if (TryProperty(result, "rateLimitsByLimitId", out var byId) && byId.ValueKind == JsonValueKind.Object)
		{
			foreach (var bucket in byId.EnumerateObject().OrderBy(p => p.Name == "codex" ? 0 : 1).ThenBy(p => p.Name, StringComparer.Ordinal))
				AddWindows(bucket.Value, bucket.Name, windows);
		}
		if (windows.Count == 0 && TryProperty(result, "rateLimits", out var legacy))
			AddWindows(legacy, "codex", windows);
		if (windows.Count == 0) throw new InvalidDataException("No quota windows");
		var first = windows[0];
		var rows = windows.SelectMany(w =>
		{
			var label = $"{w.Label} {AiUsagePresentation.Duration(w.DurationMinutes)}";
			var list = new List<AiUsageDetail> { new(label + " remaining", $"{w.RemainingPercent:0}%") };
			if (w.ResetsAt is { } at) list.Add(new(label + " resets", at.ToLocalTime().ToString("g")));
			return list;
		}).ToArray();
		return new("codex", "Codex", "C", $"{first.RemainingPercent:0}%", "Connected", now, rows,
			first.RemainingPercent, "percent",
			$"{accountScope}|{first.LimitId}|{first.DurationMinutes}|{first.ResetsAt?.ToUnixTimeSeconds()}", windows);
	}
	private static void AddWindows(JsonElement bucket, string id, List<AiQuotaWindow> windows)
	{
		if (bucket.ValueKind != JsonValueKind.Object) return;
		var name = Text(bucket, "limitName") ?? (id == "codex" ? "Codex" : id.Replace('_', ' '));
		foreach (var slot in new[] { "primary", "secondary" })
		{
			if (!TryProperty(bucket, slot, out var w) || w.ValueKind != JsonValueKind.Object || Decimal(w, "usedPercent") is not { } used) continue;
			int? duration = TryProperty(w, "windowDurationMins", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var n) && n > 0 ? n : null;
			DateTimeOffset? reset = null;
			if (TryProperty(w, "resetsAt", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var unix)
				&& unix >= -62135596800 && unix <= 253402300799)
				reset = DateTimeOffset.FromUnixTimeSeconds(unix);
			windows.Add(new($"{id}:{slot}", name, Math.Clamp(100 - used, 0, 100), duration, reset));
		}
	}
	public static AiUsageSnapshot DeepSeek(JsonElement root, string scope, DateTimeOffset now)
	{
		if (!TryProperty(root, "balance_infos", out var array) || array.ValueKind != JsonValueKind.Array) throw new InvalidDataException();
		var balances = array.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object && Decimal(x, "total_balance").HasValue)
			.OrderBy(x => Text(x, "currency") == "CNY" ? 0 : 1).ThenBy(x => Text(x, "currency"), StringComparer.Ordinal).ToArray();
		if (balances.Length == 0) throw new InvalidDataException();
		var rows = new List<AiUsageDetail>();
		foreach (var b in balances)
		{
			var unit = Text(b, "currency") ?? "";
			foreach (var (field, label) in new[] { ("total_balance", $"Total {unit}"), ("topped_up_balance", "Topped up"), ("granted_balance", "Granted") })
				if (Decimal(b, field) is { } value) rows.Add(new(label, Money(value, unit)));
		}
		var first = balances[0]; var currency = Text(first, "currency") ?? "";
		var total = Decimal(first, "total_balance")!.Value;
		return new("deepseek", "DeepSeek", "DS", Money(total, currency), "Connected", now, rows, total, currency, $"{scope}|total|{currency}");
	}
	public static AiUsageSnapshot Kimi(JsonElement root, bool international, string scope, DateTimeOffset now)
	{
		if (TryProperty(root, "code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var c) && c != 0) throw new InvalidDataException();
		var data = TryProperty(root, "data", out var payload) && payload.ValueKind == JsonValueKind.Object ? payload : root;
		var currency = Text(data, "currency") ?? (international ? "USD" : "CNY");
		var available = Decimal(data, "available_balance") ?? Decimal(data, "balance");
		if (available is null) throw new InvalidDataException(); // Never mistake voucher/cash for total available balance.
		var rows = new List<AiUsageDetail> { new("Available", Money(available.Value, currency)) };
		if (Decimal(data, "cash_balance") is { } cash) rows.Add(new("Cash", Money(cash, currency)));
		if (Decimal(data, "voucher_balance") is { } voucher) rows.Add(new("Voucher", Money(voucher, currency)));
		return new("kimi", "Kimi", "K", Money(available.Value, currency), "Connected", now, rows, available, currency, $"{scope}|available|{currency}");
	}
	public static string Money(decimal value, string currency) =>
		(currency == "CNY" ? "¥" : currency == "USD" ? "$" : "") + value.ToString("N2", CultureInfo.CurrentCulture) +
		(currency is "" or "CNY" or "USD" ? "" : $" {currency}");
	public static bool TryProperty(JsonElement root, string key, out JsonElement value)
	{
		if (root.ValueKind == JsonValueKind.Object)
			foreach (var p in root.EnumerateObject())
				if (p.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
		value = default; return false;
	}
	public static decimal? Decimal(JsonElement root, string key)
	{
		if (!TryProperty(root, key, out var v)) return null;
		if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n)) return n;
		return v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out n) ? n : null;
	}
	public static string? Text(JsonElement root, string key) =>
		TryProperty(root, key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
	public static string Fingerprint(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..24];
	public static string AccountScope(JsonElement account)
	{
		var identity = string.Join("|", new[] { "id", "accountId", "userId", "email" }
			.Select(k => TryProperty(account, k, out var v) ? v.ToString() : ""));
		// Without an identity, do not join samples from potentially different accounts.
		return identity.Trim('|').Length == 0 ? "codex:unidentified:" + Guid.NewGuid().ToString("N") : "codex:" + Fingerprint(identity);
	}
}

