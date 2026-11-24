using System.ComponentModel;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class SystemTools
{
    [McpServerTool(Name="windows.system_query",UseStructuredContent=true),
     Description("Native bounded read-only Windows process ancestry, SCM service configuration, network interfaces, TCP connections by PID, Event Log (including WER provider filter), registry and Task Scheduler queries. Exact target or paged list; no PowerShell or second service.")]
    public static Task<SystemQueryResult> Query(SystemQueryKind kind,int? pid=null,
        string? service_name=null,int page_size=50,int offset=0,
        SystemEventLogKind? event_log=null,int? event_id=null,
        SystemRegistryHive? registry_hive=null,string? registry_path=null,
        SystemRegistryView registry_view=SystemRegistryView.DEFAULT,bool include_value=false,
        string? task_folder=null,SystemEventProviderFilter? event_provider=null,
        CancellationToken cancellationToken=default) =>
        Task.FromResult(SystemSnapshot.Observe(kind,pid,service_name,page_size,offset,cancellationToken,
            event_log,event_id,registry_hive,registry_path,registry_view,include_value,task_folder,event_provider));
}
