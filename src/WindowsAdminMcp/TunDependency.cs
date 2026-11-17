using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;

// Local startup prerequisite, not proof of Internet reachability or service authorization.
// INTERNET and Windows remain the owners of networking. No CLI subprocess, route changes,
// driver changes, external requests, config parsing or new persistent watchdog.
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<TunReadiness>))]
internal enum TunReadiness
{
    READY, ADAPTER_MISSING, ADAPTER_AMBIGUOUS, ADAPTER_DOWN,
    DATAPLANE_NOT_RUNNING, DATAPLANE_AMBIGUOUS, READBACK_UNKNOWN
}

internal static class TunDependency
{
    internal const string AdapterName = "internet-tun";
    internal const string DataplanePath = @"C:\internet\sing-box.exe";

    internal static TunReadiness Classify(OperationalStatus[] adapters,int matchingProcesses)
    {
        if(adapters.Length==0)return TunReadiness.ADAPTER_MISSING;
        if(adapters.Length!=1)return TunReadiness.ADAPTER_AMBIGUOUS;
        if(adapters[0]!=OperationalStatus.Up)return TunReadiness.ADAPTER_DOWN;
        return matchingProcesses switch
        {
            0 => TunReadiness.DATAPLANE_NOT_RUNNING,
            1 => TunReadiness.READY,
            _ => TunReadiness.DATAPLANE_AMBIGUOUS
        };
    }

    internal static TunReadiness Observe()
    {
        try
        {
            var adapters=NetworkInterface.GetAllNetworkInterfaces()
                .Where(n=>string.Equals(n.Name,AdapterName,StringComparison.OrdinalIgnoreCase))
                .Select(n=>n.OperationalStatus).ToArray();
            // Do not mistake an unrelated sing-box installation for this dataplane.
            var processes=Process.GetProcessesByName("sing-box");
            try
            {
                int matching=0;
                foreach(var process in processes)
                {
                    if(!process.HasExited && string.Equals(process.MainModule?.FileName,DataplanePath,
                        StringComparison.OrdinalIgnoreCase))matching++;
                }
                return Classify(adapters,matching);
            }
            finally{foreach(var process in processes)process.Dispose();}
        }
        catch(Exception e) when(e is NetworkInformationException or Win32Exception or
            InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException)
        { return TunReadiness.READBACK_UNKNOWN; }
    }
}
