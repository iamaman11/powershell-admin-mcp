using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security;
using System.ServiceProcess;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

[JsonConverter(typeof(JsonStringEnumConverter<SystemQueryKind>))]
public enum SystemQueryKind { PROCESSES, SERVICES, NETWORK_INTERFACES }

[JsonConverter(typeof(JsonStringEnumConverter<SystemQueryCode>))]
public enum SystemQueryCode { COMPLETED, INVALID_ARGUMENT, NOT_FOUND, ACCESS_DENIED, QUERY_FAILED, UNSUPPORTED_PLATFORM }

public sealed record SystemProcessInfo(
    [property:JsonPropertyName("pid")] int Pid,
    [property:JsonPropertyName("name")] string Name,
    [property:JsonPropertyName("session_id")] int? SessionId,
    [property:JsonPropertyName("working_set_bytes")] long? WorkingSetBytes,
    [property:JsonPropertyName("handle_count")] int? HandleCount,
    [property:JsonPropertyName("start_utc"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? StartUtc = null,
    [property:JsonPropertyName("executable_path"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? ExecutablePath = null,
    [property:JsonPropertyName("cpu_total_ms"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] long? CpuTotalMs = null,
    [property:JsonPropertyName("details_unavailable")] bool DetailsUnavailable = false);

public sealed record SystemServiceInfo(
    [property:JsonPropertyName("service_name")] string ServiceName,
    [property:JsonPropertyName("display_name")] string DisplayName,
    [property:JsonPropertyName("status")] string Status,
    [property:JsonPropertyName("service_type")] string ServiceType,
    [property:JsonPropertyName("can_stop")] bool? CanStop,
    [property:JsonPropertyName("can_pause")] bool? CanPause,
    [property:JsonPropertyName("details_unavailable")] bool DetailsUnavailable = false);

public sealed record SystemNetworkInterfaceInfo(
    [property:JsonPropertyName("id")] string Id,
    [property:JsonPropertyName("name")] string Name,
    [property:JsonPropertyName("kind")] string Kind,
    [property:JsonPropertyName("status")] string Status,
    [property:JsonPropertyName("speed_bits_per_second")] long SpeedBitsPerSecond,
    [property:JsonPropertyName("unicast_addresses")] string[] UnicastAddresses,
    [property:JsonPropertyName("dns_servers")] string[] DnsServers,
    [property:JsonPropertyName("details_unavailable")] bool DetailsUnavailable = false);

public sealed record SystemQueryResult(
    [property:JsonPropertyName("status")] SystemQueryCode Status,
    [property:JsonPropertyName("kind")] SystemQueryKind Kind,
    [property:JsonPropertyName("observed_utc")] DateTimeOffset ObservedUtc,
    [property:JsonPropertyName("processes"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] SystemProcessInfo[]? Processes = null,
    [property:JsonPropertyName("services"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] SystemServiceInfo[]? Services = null,
    [property:JsonPropertyName("network_interfaces"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] SystemNetworkInterfaceInfo[]? NetworkInterfaces = null,
    [property:JsonPropertyName("next_offset"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] int? NextOffset = null,
    [property:JsonPropertyName("native_error"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] int? NativeError = null,
    [property:JsonPropertyName("elapsed_ms")] long ElapsedMs = 0);

// OS-backed read-only queries intentionally run under the authenticated MCP
// frontend's Windows token. They never impersonate SYSTEM, launch PowerShell,
// create a secondary service or treat an inaccessible field as a valid zero.
internal static class SystemSnapshot
{
    internal static bool Valid(SystemQueryKind kind,int? pid,string? serviceName,int pageSize,int offset) =>
        Enum.IsDefined(kind) && pageSize is >=1 and <=50 && offset is >=0 and <=100000 &&
        (kind switch
        {
            SystemQueryKind.PROCESSES => (pid is null or >0) && serviceName is null &&
                (pid is null || offset==0),
            SystemQueryKind.SERVICES => pid is null && (serviceName is null ||
                (serviceName.Length is >=1 and <=256 && !string.IsNullOrWhiteSpace(serviceName))) &&
                (serviceName is null || offset==0),
            SystemQueryKind.NETWORK_INTERFACES => pid is null && serviceName is null,
            _ => false
        });

    internal static SystemQueryResult Observe(SystemQueryKind kind,int? pid,string? serviceName,
        int pageSize,int offset,CancellationToken ct)
    {
        var watch=Stopwatch.StartNew();
        var at=DateTimeOffset.UtcNow;
        SystemQueryResult Result(SystemQueryCode code,int? nativeError=null) =>
            new(code,kind,at,NativeError:nativeError,ElapsedMs:watch.ElapsedMilliseconds);
        if(!Valid(kind,pid,serviceName,pageSize,offset))return Result(SystemQueryCode.INVALID_ARGUMENT);
        if(!OperatingSystem.IsWindows())return Result(SystemQueryCode.UNSUPPORTED_PLATFORM);
        ct.ThrowIfCancellationRequested();
        try
        {
            switch(kind)
            {
                case SystemQueryKind.PROCESSES:
                {
                    // Page PID/name first; potentially protected details are read
                    // only for the explicit PID to avoid slow global module scans.
                    var processes=Process.GetProcesses();
                    try
                    {
                        var ordered=processes.OrderBy(p=>p.Id).ToArray();
                        if(pid is not null)ordered=ordered.Where(p=>p.Id==pid).ToArray();
                        if(ordered.Length==0)return Result(SystemQueryCode.NOT_FOUND);
                        var page=ordered.Skip(offset).Take(pageSize).ToArray();
                        var entries=new List<SystemProcessInfo>(page.Length);
                        foreach(var process in page)
                        {
                            ct.ThrowIfCancellationRequested();
                            string name="";
                            int? session=null;
                            long? memory=null;
                            int? handles=null;
                            DateTimeOffset? start=null;
                            string? path=null;
                            long? cpu=null;
                            bool partial=false;
                            try { name=process.ProcessName; } catch(Exception e) when(e is Win32Exception or InvalidOperationException) { partial=true; }
                            try { session=process.SessionId; } catch(Exception e) when(e is Win32Exception or InvalidOperationException) { partial=true; }
                            try { memory=process.WorkingSet64; } catch(Exception e) when(e is Win32Exception or InvalidOperationException) { partial=true; }
                            try { handles=process.HandleCount; } catch(Exception e) when(e is Win32Exception or InvalidOperationException) { partial=true; }
                            if(pid is not null)
                            {
                                try { start=process.StartTime.ToUniversalTime(); } catch(Exception e) when(e is Win32Exception or InvalidOperationException) { partial=true; }
                                try { path=process.MainModule?.FileName; } catch(Exception e) when(e is Win32Exception or InvalidOperationException) { partial=true; }
                                try { cpu=(long)process.TotalProcessorTime.TotalMilliseconds; } catch(Exception e) when(e is Win32Exception or InvalidOperationException) { partial=true; }
                            }
                            entries.Add(new(process.Id,name,session,memory,handles,start,path,cpu,partial));
                        }
                        int? next=offset+page.Length<ordered.Length?offset+page.Length:null;
                        return new(SystemQueryCode.COMPLETED,kind,at,Processes:[..entries],
                            NextOffset:next,ElapsedMs:watch.ElapsedMilliseconds);
                    }
                    finally {foreach(var process in processes)process.Dispose();}
                }
                case SystemQueryKind.SERVICES:
                {
                    var services=ServiceController.GetServices();
                    try
                    {
                        var ordered=services.OrderBy(s=>s.ServiceName,StringComparer.OrdinalIgnoreCase).ToArray();
                        if(serviceName is not null)
                            ordered=ordered.Where(s=>s.ServiceName.Equals(serviceName,StringComparison.OrdinalIgnoreCase)).ToArray();
                        if(ordered.Length==0)return Result(SystemQueryCode.NOT_FOUND);
                        var page=ordered.Skip(offset).Take(pageSize).ToArray();
                        var entries=new List<SystemServiceInfo>(page.Length);
                        foreach(var service in page)
                        {
                            ct.ThrowIfCancellationRequested();
                            string status="UNKNOWN",type="UNKNOWN";
                            bool? stop=null,pause=null;
                            bool partial=false;
                            try
                            {
                                status=service.Status.ToString();
                                type=service.ServiceType.ToString();
                                stop=service.CanStop;
                                pause=service.CanPauseAndContinue;
                            }
                            catch(Exception e) when(e is Win32Exception or InvalidOperationException)
                            { partial=true; }
                            entries.Add(new(service.ServiceName,service.DisplayName,status,type,stop,pause,partial));
                        }
                        int? next=offset+page.Length<ordered.Length?offset+page.Length:null;
                        return new(SystemQueryCode.COMPLETED,kind,at,Services:[..entries],
                            NextOffset:next,ElapsedMs:watch.ElapsedMilliseconds);
                    }
                    finally {foreach(var service in services)service.Dispose();}
                }
                case SystemQueryKind.NETWORK_INTERFACES:
                {
                    var ordered=NetworkInterface.GetAllNetworkInterfaces()
                        .OrderBy(n=>n.Name,StringComparer.OrdinalIgnoreCase).ToArray();
                    var page=ordered.Skip(offset).Take(pageSize).ToArray();
                    var entries=new List<SystemNetworkInterfaceInfo>(page.Length);
                    foreach(var nic in page)
                    {
                        ct.ThrowIfCancellationRequested();
                        string[] addresses=[],dns=[];
                        bool partial=false;
                        try
                        {
                            var properties=nic.GetIPProperties();
                            addresses=properties.UnicastAddresses.Select(x=>x.Address.ToString()).Take(32).ToArray();
                            dns=properties.DnsAddresses.Select(x=>x.ToString()).Take(16).ToArray();
                        }
                        catch(NetworkInformationException) { partial=true; }
                        entries.Add(new(nic.Id,nic.Name,nic.NetworkInterfaceType.ToString(),
                            nic.OperationalStatus.ToString(),nic.Speed,addresses,dns,partial));
                    }
                    int? next=offset+page.Length<ordered.Length?offset+page.Length:null;
                    return new(SystemQueryCode.COMPLETED,kind,at,NetworkInterfaces:[..entries],
                        NextOffset:next,ElapsedMs:watch.ElapsedMilliseconds);
                }
                default:return Result(SystemQueryCode.INVALID_ARGUMENT);
            }
        }
        catch(Win32Exception e) {return Result(SystemQueryCode.QUERY_FAILED,e.NativeErrorCode);}
        catch(UnauthorizedAccessException) {return Result(SystemQueryCode.ACCESS_DENIED);}
        catch(SecurityException) {return Result(SystemQueryCode.ACCESS_DENIED);}
        catch(Exception e) when(e is InvalidOperationException or NetworkInformationException)
        {return Result(SystemQueryCode.QUERY_FAILED);}
    }
}

[McpServerToolType]
public sealed class SystemTools
{
    [McpServerTool(Name="windows.system_query",UseStructuredContent=true),
     Description("Native bounded read-only Windows processes, SCM services and network interfaces; exact PID/service selector or paged list. No PowerShell or second service.")]
    public static Task<SystemQueryResult> Query(SystemQueryKind kind,int? pid=null,
        string? service_name=null,int page_size=50,int offset=0,
        CancellationToken cancellationToken=default) =>
        Task.FromResult(SystemSnapshot.Observe(kind,pid,service_name,page_size,offset,cancellationToken));
}
