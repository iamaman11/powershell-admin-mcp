using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

internal static class TokenLogon
{
    // WindowsIdentity.Groups filters out SE_GROUP_LOGON_ID. Read the authenticated
    // token directly; never accept a caller-supplied logon identity.
    internal static string? Read(WindowsIdentity identity)
    {
        const int TokenLogonSid = 28;
        var token = identity.AccessToken;
        if (GetTokenInformation(token, TokenLogonSid, IntPtr.Zero, 0, out uint needed) ||
            Marshal.GetLastWin32Error() != 122 || needed is 0 or > 65536)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        IntPtr buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!GetTokenInformation(token, TokenLogonSid, buffer, needed, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var groups = Marshal.PtrToStructure<LogonGroups>(buffer);
            if (groups.Count == 0) return null;
            if (groups.Count != 1 || groups.Group.Sid == IntPtr.Zero ||
                (groups.Group.Attributes & 0xc0000000u) != 0xc0000000u)
                throw new InvalidOperationException("Invalid token logon group.");
            string sid = new SecurityIdentifier(groups.Group.Sid).Value;
            if (!sid.StartsWith("S-1-5-5-", StringComparison.Ordinal))
                throw new InvalidOperationException("Invalid token logon SID.");
            return sid;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct LogonGroups { public uint Count; public SidAndAttributes Group; }
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, IntPtr buffer, uint size, out uint needed);
}
