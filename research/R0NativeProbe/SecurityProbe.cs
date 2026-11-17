using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

internal static class SecurityProbe
{
    internal static void RestrictedClient(string name, string database)
    {
        using var current = WindowsIdentity.GetCurrent();
        var admin = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        byte[] bytes = new byte[admin.BinaryLength];
        admin.GetBinaryForm(bytes, 0);
        IntPtr sid = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, sid, bytes.Length);
            var disabled = new SidAndAttributes { Sid = sid };
            if (!CreateRestrictedToken(current.AccessToken, 1, 1, ref disabled,
                0, IntPtr.Zero, 0, IntPtr.Zero, out var token))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            using (token)
            WindowsIdentity.RunImpersonated(token, () =>
            {
                using var id = WindowsIdentity.GetCurrent();
                if (id.IsSystem || new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new InvalidOperationException("Restricted token is still privileged");
                Console.WriteLine("PASS security.restricted_token_not_admin");
                RequireAccessDenied(() =>
                {
                    using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut);
                    client.Connect(2000);
                }, "security.nonadmin_pipe_denied");
                RequireAccessDenied(() => { using var file = File.OpenRead(database); },
                    "security.nonadmin_database_denied");
            });
        }
        finally { Marshal.FreeHGlobal(sid); }
    }

    private static void RequireAccessDenied(Action action, string test)
    {
        try { action(); }
        catch (Exception e) when (e is UnauthorizedAccessException || (e.HResult & 0xffff) == 5)
        {
            Console.WriteLine("PASS " + test);
            return;
        }
        throw new InvalidOperationException("FAIL " + test + ": no access denied");
    }

    internal static async Task RejectFakeServer()
    {
        string name = "R0Fake_" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task connected = server.WaitForConnectionAsync(deadline.Token);
        try
        {
            await PipeWire.RoundTripAsync(name, "{\"action\":\"ping\"}", uint.MaxValue, deadline.Token);
            throw new InvalidOperationException("Fake server accepted");
        }
        catch (UnauthorizedAccessException)
        {
            await connected;
            byte[] buffer = new byte[1];
            if (await server.ReadAsync(buffer, deadline.Token) != 0)
                throw new InvalidOperationException("Payload sent to mismatched server");
            Console.WriteLine("PASS security.wrong_server_process_rejected_before_payload");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateRestrictedToken(SafeAccessTokenHandle existingToken, uint flags,
        uint disableSidCount, ref SidAndAttributes sidsToDisable, uint deletePrivilegeCount,
        IntPtr privilegesToDelete, uint restrictedSidCount, IntPtr restrictedSids,
        out SafeAccessTokenHandle newToken);
}
