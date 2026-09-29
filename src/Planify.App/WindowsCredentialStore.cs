using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Planify.Core;

namespace Planify.App;

// Win32 Credential Manager supports unpackaged desktop applications. Credentials
// persist for the current Windows user on this computer, independently of the EXE path.
public sealed class WindowsCredentialStore : ICredentialStore
{
    private const uint Generic = 1, LocalMachine = 2;
    private const int NotFound = 1168;
    public bool Contains(LoginAccount account)
    {
        if (!TryRead(account, out var pointer)) return false;
        Native.CredFree(pointer); return true;
    }
    public string? Read(LoginAccount account)
    {
        if (!TryRead(account, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlobSize % 2 != 0) throw new InvalidOperationException("Windows 凭据格式无效，请重新保存密码。");
            return Marshal.PtrToStringUni(credential.CredentialBlob, checked((int)credential.CredentialBlobSize / 2));
        }
        finally { Native.CredFree(pointer); }
    }
    private static bool TryRead(LoginAccount account, out IntPtr pointer)
    {
        if (Native.CredRead(account.CredentialKey, Generic, 0, out pointer)) return true;
        int error = Marshal.GetLastWin32Error();
        if (error == NotFound) return false;
        throw new Win32Exception(error);
    }
    public void Save(LoginAccount account, string password)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("密码不能为空。");
        int size = Encoding.Unicode.GetByteCount(password);
        if (size > 2560) throw new ArgumentException("密码超出 Windows 凭据存储长度限制。");
        string key = account.CredentialKey;
        IntPtr blob = Marshal.StringToCoTaskMemUni(password);
        try
        {
            var credential = new Credential { Type = Generic, TargetName = key, UserName = account.User.Trim(), Comment = "Planify Windows · Nextcloud application password", CredentialBlobSize = (uint)size, CredentialBlob = blob, Persist = LocalMachine };
            if (!Native.CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(blob); }
    }
    public void Remove(LoginAccount account)
    {
        if (Native.CredDelete(account.CredentialKey, Generic, 0)) return;
        int error = Marshal.GetLastWin32Error();
        if (error != NotFound) throw new Win32Exception(error);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string? TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }
    private static class Native
    {
        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredWrite(ref Credential credential, uint flags);
        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredDelete(string target, uint type, uint flags);
        [DllImport("advapi32.dll")]
        internal static extern void CredFree(IntPtr credential);
    }
}
