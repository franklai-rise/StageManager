using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace StageManager.Card3DPrototype.AiUsage;

internal sealed class AiUsageCredentialStore
{
	private readonly string _path;
	private readonly object _gate = new();

	public AiUsageCredentialStore(string? directory = null)
	{
		directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stage_Manager_Lai");
		_path = Path.Combine(directory, "ai-usage-credentials.dpapi");
	}

	public string? Get(string providerId)
	{
		lock (_gate)
		{
			var values = ReadAll();
			return values.TryGetValue(providerId, out var value) ? value : null;
		}
	}

	public void Save(string deepSeekKey, string kimiKey)
	{
		lock (_gate)
		{
			// Refuse to overwrite an unreadable vault, including on a settings-only save.
			_ = ReadAll();
			var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			if (!string.IsNullOrWhiteSpace(deepSeekKey)) values["deepseek"] = deepSeekKey.Trim();
			if (!string.IsNullOrWhiteSpace(kimiKey)) values["kimi"] = kimiKey.Trim();
			if (values.Count == 0)
			{
				if (File.Exists(_path)) File.Delete(_path);
				return;
			}

			var plaintext = JsonSerializer.SerializeToUtf8Bytes(values);
			try
			{
				var protectedBytes = Protect(plaintext);
				Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
				var temporary = _path + ".tmp";
				File.WriteAllBytes(temporary, protectedBytes);
				File.Move(temporary, _path, true);
				CryptographicOperations.ZeroMemory(protectedBytes);
			}
			finally
			{
				CryptographicOperations.ZeroMemory(plaintext);
			}
		}
	}

	private Dictionary<string, string> ReadAll()
	{
		if (!File.Exists(_path)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		byte[]? plaintext = null;
		try
		{
			if (new FileInfo(_path).Length > 64 * 1024) throw new CryptographicException("Credential store is invalid.");
			plaintext = Unprotect(File.ReadAllBytes(_path));
			return JsonSerializer.Deserialize<Dictionary<string, string>>(plaintext)
				?? throw new CryptographicException("Credential store is invalid.");
		}
		finally
		{
			if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
		}
	}

	private static byte[] Protect(byte[] data) => Transform(data, protect: true);
	private static byte[] Unprotect(byte[] data) => Transform(data, protect: false);

	private static byte[] Transform(byte[] data, bool protect)
	{
		var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
		var input = new DataBlob { Size = data.Length, Data = pinned.AddrOfPinnedObject() };
		DataBlob output = default;
		try
		{
			var success = protect
				? CryptProtectData(ref input, "Stage_Manager_Lai AI usage credentials", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
				: CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
			if (!success) throw new CryptographicException(Marshal.GetLastWin32Error());
			var result = new byte[output.Size];
			Marshal.Copy(output.Data, result, 0, result.Length);
			return result;
		}
		finally
		{
			pinned.Free();
			if (output.Data != IntPtr.Zero)
			{
				if (!protect && output.Size > 0) Marshal.Copy(new byte[output.Size], 0, output.Data, output.Size);
				LocalFree(output.Data);
			}
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct DataBlob
	{
		public int Size;
		public IntPtr Data;
	}

	[DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

	[DllImport("crypt32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

	[DllImport("kernel32.dll")]
	private static extern IntPtr LocalFree(IntPtr memory);
}
