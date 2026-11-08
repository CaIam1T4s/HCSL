using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HCSL.Infrastructure.Player;

public interface IPlayerSecretStore
{
    Task<string> ReadAsync(Guid id, CancellationToken ct);
    Task WriteAsync(Guid id, string password, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
}

/// <summary>Linux Secret Service (secret-tool) or Windows user-bound DPAPI. No plaintext fallback.</summary>
public sealed class PlayerSecretStore(string root) : IPlayerSecretStore
{
    private string PathFor(Guid id) => Path.Combine(root, id.ToString("N") + ".secret");

    public async Task<string> ReadAsync(Guid id, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return await SecretTool(["lookup", "application", "hcsl-player", "server", id.ToString("N")], null, ct);
        var data = await File.ReadAllBytesAsync(PathFor(id), ct);
        return Encoding.UTF8.GetString(Protect(data, false));
    }

    public async Task WriteAsync(Guid id, string password, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            await SecretTool(["store", "--label=HCSL player server", "application", "hcsl-player", "server", id.ToString("N")], password, ct);
            return;
        }
        Directory.CreateDirectory(root);
        var path = PathFor(id);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temp, Protect(Encoding.UTF8.GetBytes(password), true), ct);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows()) File.Delete(PathFor(id));
        else await SecretTool(["clear", "application", "hcsl-player", "server", id.ToString("N")], null, ct, allowMissing: true);
    }

    private static async Task<string> SecretTool(string[] arguments, string? input, CancellationToken ct, bool allowMissing = false)
    {
        if (!OperatingSystem.IsLinux()) throw new IOException("HCSL-CLIENT-SECRET: Only Windows and Linux are supported.");
        var start = new ProcessStartInfo("secret-tool") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(start) ?? throw new IOException();
            using var cancel = ct.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), ct);
            process.StandardInput.Close();
            await process.WaitForExitAsync(ct);
            var result = await output;
            await error; // Never expose keyring command output in diagnostics.
            if (process.ExitCode != 0 && !(allowMissing && process.ExitCode == 1)) throw new IOException();
            return result.TrimEnd('\r', '\n');
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
        {
            throw new IOException("HCSL-CLIENT-SECRET: Password storage unavailable. Linux requires secret-tool and an unlocked Secret Service keyring; no plaintext fallback.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

    private static byte[] Protect(byte[] bytes, bool encrypt)
    {
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            Blob output;
            var ok = encrypt ? CryptProtectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                             : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new IOException("HCSL-CLIENT-SECRET: Windows user-bound credential protection failed.");
            try { var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
    }
}
