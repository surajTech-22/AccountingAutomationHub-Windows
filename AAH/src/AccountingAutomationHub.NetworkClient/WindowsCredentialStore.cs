using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace AccountingAutomationHub.NetworkClient;

internal static class WindowsCredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint PersistLocalMachine = 2;
    private const string Target = "AccountingAutomationHub.NetworkClient.DeviceToken";
    private static string FolderTarget(string linkId) => "AccountingAutomationHub.NetworkClient.Folder." + linkId;

    public static void Save(string userName, string secret) => SaveNamed(Target, userName, secret);
    public static void SaveFolderToken(string linkId, string secret) => SaveNamed(FolderTarget(linkId), Environment.UserName, secret);
    public static string LoadFolderToken(string linkId) => LoadNamed(FolderTarget(linkId));

    private static void SaveNamed(string target, string userName, string secret)
    {
        var bytes = Encoding.Unicode.GetBytes(secret ?? string.Empty);
        var blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = CredTypeGeneric, TargetName = target, CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob, Persist = PersistLocalMachine, UserName = userName ?? string.Empty,
                AttributeCount = 0, Attributes = IntPtr.Zero, Comment = string.Empty, TargetAlias = string.Empty
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeCoTaskMem(blob); }
    }

    public static string Load() => LoadNamed(Target);

    private static string LoadNamed(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var ptr)) return string.Empty;
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(ptr);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return string.Empty;
            return Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2) ?? string.Empty;
        }
        finally { CredFree(ptr); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags; public uint Type; public string TargetName; public string Comment; public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize; public IntPtr CredentialBlob; public uint Persist; public uint AttributeCount; public IntPtr Attributes; public string TargetAlias; public string UserName;
    }
    [DllImport("advapi32.dll", EntryPoint="CredWriteW", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool CredWrite(ref NativeCredential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint="CredReadW", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credentialPtr);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr buffer);
}
