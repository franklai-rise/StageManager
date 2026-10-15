using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace StageManager.Card3DPrototype.AiUsage;

internal static class CodexUsageClient
{
	public static async Task<AiUsageSnapshot> ReadAsync(string configuredPath, CancellationToken cancellationToken)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(15));
		var token = timeout.Token;
		var start = CreateStartInfo(configuredPath);
		if (start is null) return Empty("Codex CLI not found");
		using var process = new Process { StartInfo = start };
		if (!process.Start()) return Empty("Codex CLI could not start");
		var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
		string? confirmedScope = null;
		try
		{
			var reader = new BoundedLineReader(process.StandardOutput);
			async Task Send(string json) { await process.StandardInput.WriteLineAsync(json.AsMemory(), token).ConfigureAwait(false); await process.StandardInput.FlushAsync(token).ConfigureAwait(false); }
			await Send("""{"method":"initialize","id":1,"params":{"clientInfo":{"name":"stage_manager_lai","version":"4.6.0"}}}""");
			using var initialized = await ReadResponseAsync(reader, 1, token);
			if (initialized.RootElement.TryGetProperty("error", out _)) return Empty("Codex protocol error");
			await Send("""{"method":"initialized","params":{}}""");
			await Send("""{"method":"account/read","id":2,"params":{"refreshToken":false}}""");
			using var accountResponse = await ReadResponseAsync(reader, 2, token);
			if (!AiUsageParsers.TryProperty(accountResponse.RootElement, "result", out var result) ||
				!AiUsageParsers.TryProperty(result, "account", out var account) || account.ValueKind != JsonValueKind.Object)
				return Empty("Codex login required");
			var scope = AiUsageParsers.AccountScope(account);
			confirmedScope = scope;
			await Send("""{"method":"account/rateLimits/read","id":3}""");
			using var rates = await ReadResponseAsync(reader, 3, token);
			if (!AiUsageParsers.TryProperty(rates.RootElement, "result", out var rateResult)) return Empty("Codex quota is unavailable") with { ContinuityKey = scope };
			return AiUsageParsers.Codex(rateResult, scope, DateTimeOffset.UtcNow);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Empty("Codex query timed out") with { ContinuityKey = confirmedScope ?? "" }; }
		catch (Exception e) when (e is not OperationCanceledException) { return Empty(e.GetType().Name) with { ContinuityKey = confirmedScope ?? "" }; }
		finally
		{
			try
			{
				try { process.StandardInput.Close(); } catch (IOException) { }
				try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false); }
				catch (TimeoutException)
				{
					if (!process.HasExited) process.Kill(entireProcessTree: true);
					await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
				}
			}
			finally
			{
				timeout.Cancel();
				try { await stderr.ConfigureAwait(false); } catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException) { }
			}
		}
	}
	private static AiUsageSnapshot Empty(string status) => new("codex", "Codex", "C", "—", status, null, Array.Empty<AiUsageDetail>(), null, "", "");
	internal static ProcessStartInfo? CreateStartInfo(string configuredPath)
	{
		var path = string.IsNullOrWhiteSpace(configuredPath) ? FindCli() : configuredPath.Trim().Trim('"');
		if (path is null || !IsLocalPath(path) || !File.Exists(path)) return null;
		ProcessStartInfo info;
		switch (Path.GetExtension(path).ToLowerInvariant())
		{
			case ".exe":
				info = new ProcessStartInfo(path);
				info.ArgumentList.Add("app-server"); info.ArgumentList.Add("--stdio");
				break;
			case ".cmd":
			case ".bat":
				// cmd.exe does not understand the CRT quoting used by ArgumentList for /c.
				// Fail closed on expansion/control characters in a user-selected script path.
				if (path.IndexOfAny(new[] { '"', '%', '!', '^', '&', '|', '<', '>', '\r', '\n' }) >= 0) return null;
				info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
					{ Arguments = $"/d /v:off /s /c \"\"{path}\" app-server --stdio\"" };
				break;
			case ".ps1":
				info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"));
				foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File", path, "app-server", "--stdio" }) info.ArgumentList.Add(arg);
				break;
			default: return null;
		}
		info.UseShellExecute = false; info.CreateNoWindow = true; info.WindowStyle = ProcessWindowStyle.Hidden;
		info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = true;
		info.StandardInputEncoding = new UTF8Encoding(false);
		info.StandardOutputEncoding = info.StandardErrorEncoding = Encoding.UTF8;
		return info;
	}
	internal static string? FindCli()
	{
		var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
			.Select(d => d.Trim().Trim('"')).Where(IsLocalPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		directories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
		directories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"));
		// Prefer a native CLI anywhere on PATH, including the desktop app's supported CLI shim.
		foreach (var dir in directories)
		{
			try { var candidate = Path.Combine(dir, "codex.exe"); if (File.Exists(candidate)) return candidate; }
			catch (ArgumentException) { }
		}
		foreach (var dir in directories)
		{
			try
			{
				foreach (var name in new[] { "codex.cmd", "codex.ps1" })
				{ var candidate = Path.Combine(dir, name); if (File.Exists(candidate)) return candidate; }
			}
			catch (ArgumentException) { }
		}
		return null;
	}
	private static bool IsLocalPath(string path)
	{
		try
		{
			return Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal) &&
				new DriveInfo(Path.GetPathRoot(path)!).DriveType is DriveType.Fixed or DriveType.Removable;
		}
		catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
	}
	private static async Task<JsonDocument> ReadResponseAsync(BoundedLineReader reader, int expectedId, CancellationToken token)
	{
		while (true)
		{
			var line = await reader.ReadAsync(token).ConfigureAwait(false);
			if (line is null) throw new IOException("Codex closed");
			JsonDocument message;
			try { message = JsonDocument.Parse(line); } catch (JsonException) { continue; }
			if (AiUsageParsers.TryProperty(message.RootElement, "id", out var id) && id.ValueKind == JsonValueKind.Number &&
				id.TryGetInt32(out var value) && value == expectedId && !message.RootElement.TryGetProperty("method", out _))
				return message;
			message.Dispose();
		}
	}
	internal sealed class BoundedLineReader
	{
		private readonly StreamReader _reader;
		private readonly char[] _buffer = new char[4096];
		private readonly StringBuilder _line = new();
		private int _offset, _count;
		public BoundedLineReader(StreamReader reader) => _reader = reader;
		public async Task<string?> ReadAsync(CancellationToken token)
		{
			while (true)
			{
				if (_offset == _count)
				{
					_count = await _reader.ReadAsync(_buffer.AsMemory(), token).ConfigureAwait(false); _offset = 0;
					if (_count == 0) { if (_line.Length == 0) return null; var tail = _line.ToString(); _line.Clear(); return tail; }
				}
				var end = Array.IndexOf(_buffer, '\n', _offset, _count - _offset);
				var length = end >= 0 ? end - _offset : _count - _offset;
				if (_line.Length + length > 1024 * 1024) throw new InvalidDataException("Oversized Codex message");
				_line.Append(_buffer, _offset, length);
				_offset += length;
				if (end < 0) continue;
				_offset++;
				var line = _line.ToString().TrimEnd('\r'); _line.Clear(); return line;
			}
		}
	}
}

