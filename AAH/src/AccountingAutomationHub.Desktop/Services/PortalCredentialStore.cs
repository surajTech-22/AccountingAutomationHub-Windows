using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace AccountingAutomationHub.Desktop.Services;

public enum PortalLoginKind
{
    Gst,
    IncomeTax
}

public sealed record PortalCredential(string UserId, string Password);

/// <summary>
/// Windows Credential Manager-backed portal credential storage. Credentials are scoped to
/// the exact Client/FY and portal. The linked Automation Folder exposes only a shortcut into
/// AAH's secure entry prompt; this store remains the application's protected runtime authority.
/// </summary>
public static class PortalCredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint PersistLocalMachine = 2;

    public static void Save(long clientId, string financialYear, PortalLoginKind portal, string userId, string password)
    {
        if (clientId <= 0) throw new ArgumentOutOfRangeException(nameof(clientId));
        var target = Target(clientId, financialYear, portal);
        var payload = (userId ?? string.Empty).Trim() + "\n" + (password ?? string.Empty);
        var bytes = Encoding.Unicode.GetBytes(payload);
        var blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = (userId ?? string.Empty).Trim(),
                AttributeCount = 0,
                Attributes = IntPtr.Zero,
                Comment = "Accounting Automation Hub portal login",
                TargetAlias = string.Empty
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public static PortalCredential Load(long clientId, string financialYear, PortalLoginKind portal)
    {
        if (clientId <= 0) return new PortalCredential(string.Empty, string.Empty);
        if (!CredRead(Target(clientId, financialYear, portal), CredTypeGeneric, 0, out var pointer))
            return new PortalCredential(string.Empty, string.Empty);
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                return new PortalCredential(string.Empty, string.Empty);
            var payload = Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2) ?? string.Empty;
            var split = payload.Split('\n', 2);
            return split.Length == 2
                ? new PortalCredential(split[0].Trim(), split[1])
                : new PortalCredential(split.FirstOrDefault()?.Trim() ?? string.Empty, string.Empty);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public static void Delete(long clientId, string financialYear, PortalLoginKind portal)
    {
        try { CredDelete(Target(clientId, financialYear, portal), CredTypeGeneric, 0); }
        catch { }
    }

    private static string Target(long clientId, string financialYear, PortalLoginKind portal)
    {
        var fy = new string((financialYear ?? string.Empty).Trim().Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        return $"AccountingAutomationHub.Portal.{portal}.Client{clientId}.FY{fy}";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credentialPtr);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
