using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Net.NetworkInformation;
using System.Security;
using System.Runtime.InteropServices;
using System.ServiceProcess;

// OS-backed read-only queries intentionally run under the authenticated MCP
// frontend's Windows token. They never impersonate SYSTEM, launch PowerShell,
// create a secondary service or treat an inaccessible field as a valid zero.
internal static class SystemSnapshot
{
    internal static bool Valid(SystemQueryKind kind,int? pid,string? serviceName,int pageSize,int offset,
        SystemEventLogKind? eventLog=null,int? eventId=null,SystemRegistryHive? registryHive=null,
        string? registryPath=null,SystemRegistryView registryView=SystemRegistryView.DEFAULT,
        bool includeValue=false,string? taskFolder=null,SystemEventProviderFilter? eventProvider=null) =>
        Enum.IsDefined(kind) && pageSize is >=1 and <=50 && offset is >=0 and <=100000 &&
        (kind switch
        {
            SystemQueryKind.PROCESSES => NoExtra(eventLog,eventId,registryHive,registryPath,registryView,
                includeValue,taskFolder,eventProvider) && (pid is null or >0) && serviceName is null &&
                (pid is null || offset==0),
            SystemQueryKind.SERVICES => NoExtra(eventLog,eventId,registryHive,registryPath,registryView,
                includeValue,taskFolder,eventProvider) && pid is null && (serviceName is null ||
                (serviceName.Length is >=1 and <=256 && !string.IsNullOrWhiteSpace(serviceName))) &&
                (serviceName is null || offset==0),
            SystemQueryKind.NETWORK_INTERFACES => NoExtra(eventLog,eventId,registryHive,registryPath,registryView,
                includeValue,taskFolder,eventProvider) && pid is null && serviceName is null,
            SystemQueryKind.TCP_CONNECTIONS => NoExtra(eventLog,eventId,registryHive,registryPath,registryView,
                includeValue,taskFolder,eventProvider) && (pid is null or >0) && serviceName is null,
            SystemQueryKind.EVENT_LOG => pid is null && serviceName is null && eventLog is not null &&
                Enum.IsDefined(eventLog.Value) && (eventId is null or >=0 and <=65535) &&
                registryHive is null && registryPath is null && registryView==SystemRegistryView.DEFAULT &&
                !includeValue && taskFolder is null && offset<=200 &&
                (eventProvider is null || Enum.IsDefined(eventProvider.Value)),
            SystemQueryKind.REGISTRY => pid is null && serviceName is null && eventLog is null &&
                eventId is null && registryHive is not null && Enum.IsDefined(registryHive.Value) &&
                registryPath is { Length: >=1 and <=512 } && !registryPath.Contains('\0') &&
                Enum.IsDefined(registryView) && taskFolder is null && eventProvider is null && offset<=1000,
            SystemQueryKind.SCHEDULED_TASKS => pid is null && serviceName is null && eventLog is null &&
                eventId is null && registryHive is null && registryPath is null &&
                registryView==SystemRegistryView.DEFAULT && !includeValue &&
                eventProvider is null && (taskFolder is null || SystemExtraQueries.ValidTaskFolder(taskFolder)) && offset<=1000,
            _ => false
        });

    // Validity for existing query variants must not accidentally depend on
    // optional parameters belonging to a different subject domain.
    // Kept in a separate helper to make the old variants' gate legible.
    private static bool NoExtra(SystemEventLogKind? eventLog,int? eventId,
        SystemRegistryHive? hive,string? path,SystemRegistryView view,bool includeValue,string? taskFolder,
        SystemEventProviderFilter? eventProvider) =>
        eventLog is null && eventId is null && hive is null && path is null &&
        view==SystemRegistryView.DEFAULT && !includeValue && taskFolder is null && eventProvider is null;

    internal static SystemQueryResult Observe(SystemQueryKind kind,int? pid,string? serviceName,
        int pageSize,int offset,CancellationToken ct,SystemEventLogKind? eventLog=null,int? eventId=null,
        SystemRegistryHive? registryHive=null,string? registryPath=null,
        SystemRegistryView registryView=SystemRegistryView.DEFAULT,bool includeValue=false,
        string? taskFolder=null,SystemEventProviderFilter? eventProvider=null)
    {
        var watch=Stopwatch.StartNew();
        var at=DateTimeOffset.UtcNow;
        SystemQueryResult Result(SystemQueryCode code,int? nativeError=null) =>
            new(code,kind,at,NativeError:nativeError,ElapsedMs:watch.ElapsedMilliseconds);
        if(!Valid(kind,pid,serviceName,pageSize,offset,eventLog,eventId,registryHive,registryPath,
            registryView,includeValue,taskFolder,eventProvider))return Result(SystemQueryCode.INVALID_ARGUMENT);
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
                        var ancestry=SystemProcessQueries.Read(ct);
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
                            var lineage=ancestry is not null && ancestry.TryGetValue(process.Id,out var observed)
                                ? observed : null;
                            entries.Add(new(process.Id,name,session,memory,handles,start,path,cpu,
                                partial || lineage is null, lineage?.ParentPid,lineage?.ThreadCount));
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
                            SystemServiceStartMode? startMode=null;
                            string[]? dependencies=null;
                            bool truncated=false;
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
                            if(serviceName is not null)
                            {
                                try
                                {
                                    startMode=service.StartType switch
                                    {
                                        ServiceStartMode.Boot=>SystemServiceStartMode.BOOT,
                                        ServiceStartMode.System=>SystemServiceStartMode.SYSTEM,
                                        ServiceStartMode.Automatic=>SystemServiceStartMode.AUTOMATIC,
                                        ServiceStartMode.Manual=>SystemServiceStartMode.MANUAL,
                                        ServiceStartMode.Disabled=>SystemServiceStartMode.DISABLED,
                                        _=>SystemServiceStartMode.UNKNOWN
                                    };
                                    var dependsOn=service.ServicesDependedOn;
                                    try
                                    {
                                        truncated=dependsOn.Length>64;
                                        dependencies=dependsOn.Take(64).Select(x=>x.ServiceName)
                                            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
                                    }
                                    finally {foreach(var dependency in dependsOn)dependency.Dispose();}
                                }
                                catch(Exception e) when(e is Win32Exception or InvalidOperationException)
                                {partial=true;}
                            }
                            entries.Add(new(service.ServiceName,service.DisplayName,status,type,stop,pause,
                                partial,startMode,dependencies,truncated));
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
                case SystemQueryKind.EVENT_LOG:
                    return SystemExtraQueries.Events(kind,at,watch,eventLog!.Value,eventId,
                        eventProvider,pageSize,offset,ct);
                case SystemQueryKind.REGISTRY:
                    return SystemExtraQueries.Registry(kind,at,watch,registryHive!.Value,registryPath!,
                        registryView,includeValue,pageSize,offset,ct);
                case SystemQueryKind.SCHEDULED_TASKS:
                    return SystemExtraQueries.Tasks(kind,at,watch,taskFolder??"\\",pageSize,offset,ct);
                case SystemQueryKind.TCP_CONNECTIONS:
                    return SystemTcpQueries.Connections(kind,at,watch,pid,pageSize,offset,ct);
                default:return Result(SystemQueryCode.INVALID_ARGUMENT);
            }
        }
        catch(Win32Exception e) {return Result(SystemQueryCode.QUERY_FAILED,e.NativeErrorCode);}
        catch(EventLogException e) {return Result(e.HResult==unchecked((int)0x80070005)?
            SystemQueryCode.ACCESS_DENIED:SystemQueryCode.QUERY_FAILED,e.HResult);}
        catch(COMException e) {return Result(e.HResult==unchecked((int)0x80070005)?
            SystemQueryCode.ACCESS_DENIED:SystemQueryCode.QUERY_FAILED,e.HResult);}
        catch(System.TimeoutException) {return Result(SystemQueryCode.TIMEOUT);}
        catch(UnauthorizedAccessException) {return Result(SystemQueryCode.ACCESS_DENIED);}
        catch(SecurityException) {return Result(SystemQueryCode.ACCESS_DENIED);}
        catch(Exception e) when(e is InvalidOperationException or NetworkInformationException or IOException or ArgumentException)
        {return Result(SystemQueryCode.QUERY_FAILED);}
    }
}
