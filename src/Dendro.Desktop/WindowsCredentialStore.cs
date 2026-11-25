using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Dendro.Desktop;

internal sealed record CopernicusOAuthCredentials(string ClientId, string ClientSecret);
internal sealed record DlbPasswordReminder(string Path, DateTimeOffset ExpiresAtUtc, string Password);

internal static class WindowsCredentialStore
{
    private const int CryptProtectUiForbidden = 0x1;
    private static readonly string KeyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DENDRO",
        "noaa-image-service-key.bin");
    private static readonly string CopernicusPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DENDRO",
        "copernicus-oauth.bin");
    private static readonly string DlbPasswordsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DENDRO",
        "dlb-passwords");

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description,
        IntPtr optionalEntropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description,
        IntPtr optionalEntropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    internal static string ReadNoaaToken()
    {
        if (!File.Exists(KeyPath)) return "";
        byte[] encrypted = File.ReadAllBytes(KeyPath);
        return Encoding.UTF8.GetString(Transform(encrypted, protect: false));
    }

    internal static void SaveNoaaToken(string value)
    {
        value = value.Trim();
        if (value.Length == 0) throw new InvalidOperationException("Informe um token ou use Remover chave.");
        byte[] encrypted = Transform(Encoding.UTF8.GetBytes(value), protect: true);
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        string temporary = KeyPath + ".tmp";
        File.WriteAllBytes(temporary, encrypted);
        File.Move(temporary, KeyPath, overwrite: true);
    }

    internal static void RemoveNoaaToken()
    {
        if (File.Exists(KeyPath)) File.Delete(KeyPath);
    }

    internal static CopernicusOAuthCredentials? ReadCopernicusCredentials()
    {
        if (!File.Exists(CopernicusPath)) return null;
        byte[] encrypted = File.ReadAllBytes(CopernicusPath);
        return JsonSerializer.Deserialize<CopernicusOAuthCredentials>(Transform(encrypted, protect: false));
    }

    internal static void SaveCopernicusCredentials(string clientId, string clientSecret)
    {
        clientId = clientId.Trim();
        clientSecret = clientSecret.Trim();
        if (clientId.Length == 0 || clientSecret.Length == 0)
            throw new InvalidOperationException("Informe o Client ID e o Client Secret do Copernicus.");
        byte[] encrypted = Transform(JsonSerializer.SerializeToUtf8Bytes(new CopernicusOAuthCredentials(clientId, clientSecret)), protect: true);
        Directory.CreateDirectory(Path.GetDirectoryName(CopernicusPath)!);
        string temporary = CopernicusPath + ".tmp";
        File.WriteAllBytes(temporary, encrypted);
        File.Move(temporary, CopernicusPath, overwrite: true);
    }

    internal static void RemoveCopernicusCredentials()
    {
        if (File.Exists(CopernicusPath)) File.Delete(CopernicusPath);
    }

    internal static string? ReadDlbPassword(string path)
    {
        string normalizedPath = Path.GetFullPath(path);
        string credentialPath = DlbCredentialPath(normalizedPath);
        if (!File.Exists(credentialPath)) return null;
        try
        {
            var reminder = JsonSerializer.Deserialize<DlbPasswordReminder>(
                Transform(File.ReadAllBytes(credentialPath), protect: false));
            if (reminder == null
                || !string.Equals(reminder.Path, normalizedPath, StringComparison.OrdinalIgnoreCase)
                || reminder.ExpiresAtUtc <= DateTimeOffset.UtcNow
                || string.IsNullOrEmpty(reminder.Password))
            {
                File.Delete(credentialPath);
                return null;
            }
            return reminder.Password;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException
            or Win32Exception)
        {
            try { File.Delete(credentialPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }
    }

    internal static void SaveDlbPassword(string path, string password)
    {
        string normalizedPath = Path.GetFullPath(path);
        var reminder = new DlbPasswordReminder(normalizedPath, DateTimeOffset.UtcNow.AddDays(7), password);
        byte[] encrypted = Transform(JsonSerializer.SerializeToUtf8Bytes(reminder), protect: true);
        string credentialPath = DlbCredentialPath(normalizedPath);
        Directory.CreateDirectory(DlbPasswordsFolder);
        string temporary = credentialPath + ".tmp";
        File.WriteAllBytes(temporary, encrypted);
        File.Move(temporary, credentialPath, overwrite: true);
    }

    internal static void RemoveDlbPassword(string path)
    {
        string credentialPath = DlbCredentialPath(Path.GetFullPath(path));
        try { if (File.Exists(credentialPath)) File.Delete(credentialPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string DlbCredentialPath(string path)
    {
        string identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
        return Path.Combine(DlbPasswordsFolder, identity + ".bin");
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("A proteção da chave requer Windows.");
        IntPtr inputMemory = Marshal.AllocHGlobal(input.Length);
        Marshal.Copy(input, 0, inputMemory, input.Length);
        var inputBlob = new DataBlob { Size = input.Length, Data = inputMemory };
        DataBlob outputBlob = default;
        try
        {
            bool success = protect
                ? CryptProtectData(ref inputBlob, "DendroLab NOAA Image Service", IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, CryptProtectUiForbidden, out outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, CryptProtectUiForbidden, out outputBlob);
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), "Não foi possível proteger a chave da NOAA neste usuário do Windows.");
            byte[] result = new byte[outputBlob.Size];
            Marshal.Copy(outputBlob.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(inputMemory);
            if (outputBlob.Data != IntPtr.Zero) _ = LocalFree(outputBlob.Data);
        }
    }
}
