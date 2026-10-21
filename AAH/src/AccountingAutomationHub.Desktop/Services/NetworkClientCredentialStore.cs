using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AccountingAutomationHub.Desktop.Services;

public static class NetworkClientCredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint PersistLocalMachine = 2;
    private const string DeviceTarget = "AccountingAutomationHub.FullUiNetwork.Device";
    private const string LoginTarget = "AccountingAutomationHub.FullUiNetwork.Login";
    private const string SessionTarget = "AccountingAutomationHub.FullUiNetwork.Session";

    public static void SaveDevice(string userName,string server,string deviceId,string token) => Save(DeviceTarget,userName,$"{server}\n{deviceId}\n{token}");
    public static void DeleteDevice()=>Delete(DeviceTarget);
    public static (string Server,string DeviceId,string Token) LoadDevice()
    {
        var value=Load(DeviceTarget).Split('\n',3);return value.Length==3?(value[0].Trim(),value[1].Trim(),value[2].Trim()):(string.Empty,string.Empty,string.Empty);
    }
    public static void SaveLogin(string userName,string password) => Save(LoginTarget,userName,$"{userName}\n{password}");
    public static (string User,string Password) LoadLogin(){var value=Load(LoginTarget).Split('\n',2);return value.Length==2?(value[0].Trim(),value[1]):(string.Empty,string.Empty);}
    public static void DeleteLogin()=>Delete(LoginTarget);

    public static void SaveSession(NetworkSavedSession session)
    {
        var payload = JsonSerializer.Serialize(session);
        Save(SessionTarget, session.UserName, payload);
    }

    public static NetworkSavedSession? LoadSession()
    {
        var value = Load(SessionTarget);
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return JsonSerializer.Deserialize<NetworkSavedSession>(value); }
        catch { DeleteSession(); return null; }
    }

    public static void DeleteSession()=>Delete(SessionTarget);

    private static void Delete(string target){try{CredDelete(target,CredTypeGeneric,0);}catch{}}

    private static void Save(string target,string userName,string secret)
    {
        var bytes=Encoding.Unicode.GetBytes(secret??string.Empty);var blob=Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes,0,blob,bytes.Length);var credential=new NativeCredential{Type=CredTypeGeneric,TargetName=target,CredentialBlobSize=(uint)bytes.Length,CredentialBlob=blob,Persist=PersistLocalMachine,UserName=userName??string.Empty,AttributeCount=0,Attributes=IntPtr.Zero,Comment=string.Empty,TargetAlias=string.Empty};
            if(!CredWrite(ref credential,0))throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally{Marshal.FreeCoTaskMem(blob);}
    }
    private static string Load(string target)
    {
        if(!CredRead(target,CredTypeGeneric,0,out var ptr))return string.Empty;
        try{var credential=Marshal.PtrToStructure<NativeCredential>(ptr);return credential.CredentialBlob==IntPtr.Zero||credential.CredentialBlobSize==0?string.Empty:Marshal.PtrToStringUni(credential.CredentialBlob,(int)credential.CredentialBlobSize/2)??string.Empty;}
        finally{CredFree(ptr);}
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct NativeCredential{public uint Flags;public uint Type;public string TargetName;public string Comment;public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;public uint CredentialBlobSize;public IntPtr CredentialBlob;public uint Persist;public uint AttributeCount;public IntPtr Attributes;public string TargetAlias;public string UserName;}
    [DllImport("advapi32.dll",EntryPoint="CredWriteW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool CredWrite(ref NativeCredential credential,uint flags);
    [DllImport("advapi32.dll",EntryPoint="CredReadW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool CredRead(string target,uint type,uint flags,out IntPtr credentialPtr);
    [DllImport("advapi32.dll",EntryPoint="CredDeleteW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool CredDelete(string target,uint type,uint flags);
    [DllImport("advapi32.dll")]private static extern void CredFree(IntPtr buffer);
}

public sealed record NetworkSavedSession(
    string Server,
    string DeviceId,
    string UserName,
    string DisplayName,
    string RoleName,
    string SessionToken,
    DateTime ExpiresUtc,
    string[] Permissions);
