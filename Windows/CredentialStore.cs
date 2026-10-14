using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PipelineBar;

/// Bewaart het Personal Access Token in Windows Referentiebeheer (Credential Manager), per organisatie.
public static class CredentialStore
{
    private const string Prefix = "PipelineBar:";
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private static readonly Dictionary<string, string> Cache = [];

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    private static string Target(string account) => Prefix + account.Trim().ToLowerInvariant();

    public static string? Read(string account)
    {
        if (Cache.TryGetValue(account, out var cached)) return cached;
        if (!CredRead(Target(account), CredTypeGeneric, 0, out var ptr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<Credential>(ptr);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0) return null;
            var token = Marshal.PtrToStringUni(cred.CredentialBlob, cred.CredentialBlobSize / 2);
            Cache[account] = token;
            return token;
        }
        finally { CredFree(ptr); }
    }

    public static bool Save(string token, string account)
    {
        var bytes = Encoding.Unicode.GetBytes(token);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var cred = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = Target(account),
                Comment = "PipelineBar (Azure DevOps)",
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = account.Trim(),
            };
            if (!CredWrite(ref cred, 0)) return false;
            Cache[account] = token;
            return true;
        }
        finally { Marshal.FreeHGlobal(blob); }
    }

    public static void Delete(string account)
    {
        Cache.Remove(account);
        CredDelete(Target(account), CredTypeGeneric, 0);
    }
}
