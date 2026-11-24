using System.Diagnostics;
using System.Text.Json;

internal static class ExpertDiagnosticsAcceptance
{
    internal static async Task RunAsync()
    {
        static void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAIL " + name);
            Console.WriteLine("PASS " + name);
        }

        Check(NativeFileAcl.Valid(@"C:\Windows", 1, 0) &&
              !NativeFileAcl.Valid(@"\\server\share", 1, 0) &&
              !NativeFileAcl.Valid(@"\\?\C:\Windows", 1, 0) &&
              !NativeFileAcl.Valid(@"Windows", 1, 0) &&
              !NativeFileAcl.Valid(@"C:\Windows", 65, 0) &&
              !NativeFileAcl.Valid(@"C:\Windows", 32, -1),
              "expert_acl.strict_local_path_and_bounded_ace_page");
        Check(NativeProcessWaitChain.Valid(Environment.ProcessId, 8, 0) &&
              !NativeProcessWaitChain.Valid(0, 1, 0) &&
              !NativeProcessWaitChain.Valid(Environment.ProcessId, 9, 0) &&
              !NativeProcessWaitChain.Valid(Environment.ProcessId, 1, -1),
              "expert_wct.strict_pid_and_bounded_thread_page");

        var badAcl = await ExpertDiagnosticsTools.FileAcl(@"relative.txt");
        var badWct = await ExpertDiagnosticsTools.ProcessWaitChain(-1);
        Check(badAcl.Status == FileAclCode.INVALID_ARGUMENT &&
              badWct.Status == WaitChainCode.INVALID_ARGUMENT,
              "expert_diagnostics.invalid_input_is_pure_no_effect");

        if (!OperatingSystem.IsWindows()) return;
        string temp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var acl = await ExpertDiagnosticsTools.FileAcl(temp, page_size: 8);
        Check(acl.Status == FileAclCode.COMPLETED &&
              !string.IsNullOrWhiteSpace(acl.OwnerSid) &&
              acl.DaclState is not null &&
              acl.AceCount is >= 0 and <= 4096 &&
              acl.Aces is { Length: <= 8 } &&
              (acl.NextOffset is null || acl.NextOffset > 0),
              "expert_acl.real_windows_directory_owner_and_dacl");
        var serialized = JsonSerializer.SerializeToElement(acl, PipeWire.Json);
        Check(serialized.GetProperty("status").GetString() == "COMPLETED" &&
              serialized.GetProperty("owner_sid").GetString()!.StartsWith("S-1-", StringComparison.Ordinal),
              "expert_acl.typed_json_contract");

        var wait = await ExpertDiagnosticsTools.ProcessWaitChain(Environment.ProcessId,
            page_size: 1);
        Check(wait.Status is WaitChainCode.COMPLETED or WaitChainCode.ACCESS_DENIED &&
              wait.ElapsedMs < 5000 &&
              (wait.Threads is null || wait.Threads.Length <= 1),
              "expert_wct.own_pid_bounded_native_snapshot");
    }
}
