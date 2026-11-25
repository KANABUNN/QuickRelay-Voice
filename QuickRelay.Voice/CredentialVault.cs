using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace QuickRelay.Voice;

internal static class CredentialVault
{
    private const uint Generic = 1;
    private const int NotFound = 1168;

    public static string? Read(string scope)
    {
        if (string.IsNullOrEmpty(scope))
            return null;
        if (!CredRead("QuickRelay Voice/" + scope, Generic, 0, out var pointer))
        {
            if (Marshal.GetLastPInvokeError() == NotFound)
                return null;
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows 資格情報を読み込めません。");
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            return Marshal.PtrToStringUTF8(credential.Blob, (int)credential.BlobSize);
        }
        finally { CredFree(pointer); }
    }

    public static void Write(string scope, string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var credential = new NativeCredential
            {
                Type = Generic,
                Target = "QuickRelay Voice/" + scope,
                BlobSize = (uint)bytes.Length,
                Blob = pointer,
                Persist = 2,
                UserName = "QuickRelay Voice"
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows 資格情報を保存できません。");
        }
        finally
        {
            Array.Clear(bytes);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            Marshal.FreeHGlobal(pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags, Type;
        public string? Target, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? Alias, UserName;
    }

    [DllImport("advapi32", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr pointer);

    [DllImport("advapi32", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32")]
    private static extern void CredFree(IntPtr pointer);
}
