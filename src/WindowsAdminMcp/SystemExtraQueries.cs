using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.Win32;

[JsonConverter(typeof(JsonStringEnumConverter<SystemEventLogKind>))]
public enum SystemEventLogKind { APPLICATION, SYSTEM, SECURITY }

[JsonConverter(typeof(JsonStringEnumConverter<SystemRegistryHive>))]
public enum SystemRegistryHive { LOCAL_MACHINE, CURRENT_USER, USERS, CLASSES_ROOT, CURRENT_CONFIG }

[JsonConverter(typeof(JsonStringEnumConverter<SystemRegistryView>))]
public enum SystemRegistryView { DEFAULT, REGISTRY32, REGISTRY64 }

public sealed record SystemEventInfo(
    [property:JsonPropertyName("record_id")] long? RecordId,
    [property:JsonPropertyName("event_id")] int EventId,
    [property:JsonPropertyName("provider")] string Provider,
    [property:JsonPropertyName("level")] int? Level,
    [property:JsonPropertyName("occurred_utc")] DateTimeOffset? OccurredUtc);

public sealed record SystemRegistryEntry(
    [property:JsonPropertyName("name")] string Name,
    [property:JsonPropertyName("entry_kind")] string EntryKind,
    [property:JsonPropertyName("value_kind"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? ValueKind = null,
    [property:JsonPropertyName("data_length"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] int? DataLength = null,
    [property:JsonPropertyName("data_text"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? DataText = null,
    [property:JsonPropertyName("data_number"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] long? DataNumber = null,
    [property:JsonPropertyName("data_items"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string[]? DataItems = null,
    [property:JsonPropertyName("data_sha256"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? DataSha256 = null,
    [property:JsonPropertyName("data_truncated")] bool DataTruncated = false,
    [property:JsonPropertyName("details_unavailable")] bool DetailsUnavailable = false);

public sealed record SystemScheduledTaskInfo(
    [property:JsonPropertyName("name")] string Name,
    [property:JsonPropertyName("path")] string Path,
    [property:JsonPropertyName("state")] string State,
    [property:JsonPropertyName("enabled")] bool? Enabled,
    [property:JsonPropertyName("last_run_utc")] DateTimeOffset? LastRunUtc,
    [property:JsonPropertyName("next_run_utc")] DateTimeOffset? NextRunUtc,
    [property:JsonPropertyName("last_result")] int? LastResult,
    [property:JsonPropertyName("details_unavailable")] bool DetailsUnavailable = false);

// Each branch uses an official Windows API, returns a bounded typed snapshot
// and never launches powershell/schtasks/reg.exe or alters native state.
internal static class SystemExtraQueries
{
    internal static bool ValidTaskFolder(string path) => path.Length is >=1 and <=256 &&
        path[0]=='\\' && !path.Contains('\0') &&
        !path.Split('\\').Any(part=>part is "." or "..");

    internal static SystemQueryResult Events(SystemQueryKind kind,DateTimeOffset at,Stopwatch watch,
        SystemEventLogKind log,int? eventId,int pageSize,int offset,CancellationToken ct)
    {
        string logName=log switch {
            SystemEventLogKind.APPLICATION=>"Application",
            SystemEventLogKind.SYSTEM=>"System",
            SystemEventLogKind.SECURITY=>"Security",
            _=>throw new ArgumentOutOfRangeException(nameof(log))};
        string? xpath=eventId is null?null:$"*[System[(EventID={eventId.Value})]]";
        var query=new EventLogQuery(logName,PathType.LogName,xpath) {ReverseDirection=true};
        using var reader=new EventLogReader(query);
        var records=new List<SystemEventInfo>(pageSize);
        int position=0;
        bool ended=false;
        while(position<offset+pageSize+1)
        {
            ct.ThrowIfCancellationRequested();
            if(watch.Elapsed>TimeSpan.FromSeconds(3))
                return new(SystemQueryCode.TIMEOUT,kind,at,ElapsedMs:watch.ElapsedMilliseconds);
            using EventRecord? record=reader.ReadEvent(TimeSpan.FromMilliseconds(250));
            if(record is null){ended=true;break;}
            if(position>=offset && records.Count<pageSize)
                records.Add(new(record.RecordId,record.Id,record.ProviderName??"",
                    record.Level,record.TimeCreated is {} time ? time.ToUniversalTime() : null));
            position++;
        }
        int? next=!ended && position>=offset+pageSize+1?offset+pageSize:null;
        return new(SystemQueryCode.COMPLETED,kind,at,Events:[..records],NextOffset:next,
            ElapsedMs:watch.ElapsedMilliseconds);
    }

    internal static SystemQueryResult Registry(SystemQueryKind kind,DateTimeOffset at,Stopwatch watch,
        SystemRegistryHive hive,string path,SystemRegistryView view,bool includeValue,
        int pageSize,int offset,CancellationToken ct)
    {
        var nativeHive=hive switch {
            SystemRegistryHive.LOCAL_MACHINE=>RegistryHive.LocalMachine,
            SystemRegistryHive.CURRENT_USER=>RegistryHive.CurrentUser,
            SystemRegistryHive.USERS=>RegistryHive.Users,
            SystemRegistryHive.CLASSES_ROOT=>RegistryHive.ClassesRoot,
            SystemRegistryHive.CURRENT_CONFIG=>RegistryHive.CurrentConfig,
            _=>throw new ArgumentOutOfRangeException(nameof(hive))};
        var nativeView=view switch {
            SystemRegistryView.DEFAULT=>RegistryView.Default,
            SystemRegistryView.REGISTRY32=>RegistryView.Registry32,
            SystemRegistryView.REGISTRY64=>RegistryView.Registry64,
            _=>throw new ArgumentOutOfRangeException(nameof(view))};
        using var root=RegistryKey.OpenBaseKey(nativeHive,nativeView);
        using var key=root.OpenSubKey(path,false);
        if(key is null)
            return new(SystemQueryCode.NOT_FOUND,kind,at,ElapsedMs:watch.ElapsedMilliseconds);
        string[] subkeys=key.GetSubKeyNames().Order(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] values=key.GetValueNames().Order(StringComparer.OrdinalIgnoreCase).ToArray();
        int total=checked(subkeys.Length+values.Length);
        var entries=new List<SystemRegistryEntry>(pageSize);
        int stop=Math.Min(total,offset+pageSize);
        for(int i=offset;i<stop;i++)
        {
            ct.ThrowIfCancellationRequested();
            if(i<subkeys.Length)
            {
                entries.Add(new(subkeys[i],"SUBKEY"));
                continue;
            }
            string name=values[i-subkeys.Length];
            try
            {
                var valueKind=key.GetValueKind(name);
                if(!includeValue)
                {
                    entries.Add(new(name,"VALUE",ValueKind:valueKind.ToString()));
                    continue;
                }
                object? value=key.GetValue(name,null,RegistryValueOptions.DoNotExpandEnvironmentNames);
                entries.Add(DescribeValue(name,valueKind,value));
            }
            catch(Exception e) when(e is IOException or ArgumentException or UnauthorizedAccessException)
            {entries.Add(new(name,"VALUE",DetailsUnavailable:true));}
        }
        int? next=stop<total?stop:null;
        return new(SystemQueryCode.COMPLETED,kind,at,RegistryEntries:[..entries],
            NextOffset:next,ElapsedMs:watch.ElapsedMilliseconds);
    }

    private static SystemRegistryEntry DescribeValue(string name,RegistryValueKind kind,object? value)
    {
        string type=kind.ToString();
        if(value is null)return new(name,"VALUE",ValueKind:type,DetailsUnavailable:true);
        if(value is string str)
        {
            // Unexpanded environment variables remain literal. Explicit
            // include_value is required; even then, every value is bounded.
            return new(name,"VALUE",ValueKind:type,DataLength:str.Length,
                DataText:str[..Math.Min(4096,str.Length)],DataTruncated:str.Length>4096);
        }
        if(value is string[] lines)
        {
            var items=lines.Take(16).Select(x=>x[..Math.Min(256,x.Length)]).ToArray();
            return new(name,"VALUE",ValueKind:type,DataLength:lines.Length,
                DataItems:items,DataTruncated:lines.Length>16 || lines.Any(x=>x.Length>256));
        }
        if(value is byte[] bytes)
            return new(name,"VALUE",ValueKind:type,DataLength:bytes.Length,
                DataSha256:Convert.ToHexString(SHA256.HashData(bytes)));
        if(value is int i)
            return new(name,"VALUE",ValueKind:type,DataNumber:unchecked((uint)i));
        if(value is long l)
            return new(name,"VALUE",ValueKind:type,DataNumber:l);
        return new(name,"VALUE",ValueKind:type,DetailsUnavailable:true);
    }

    internal static SystemQueryResult Tasks(SystemQueryKind kind,DateTimeOffset at,Stopwatch watch,
        string folderPath,int pageSize,int offset,CancellationToken ct)
    {
        // Task Scheduler 2.0 ITaskService/ITaskFolder COM are the Windows API;
        // folder enumeration is intentionally nonrecursive and bounded.
        object? service=null,folder=null,collection=null;
        try
        {
            Type? type=Type.GetTypeFromProgID("Schedule.Service");
            if(type is null)return new(SystemQueryCode.QUERY_FAILED,kind,at,ElapsedMs:watch.ElapsedMilliseconds);
            service=Activator.CreateInstance(type);
            if(service is null)return new(SystemQueryCode.QUERY_FAILED,kind,at,ElapsedMs:watch.ElapsedMilliseconds);
            dynamic api=service;
            api.Connect();
            folder=api.GetFolder(folderPath);
            dynamic taskFolder=folder!;
            collection=taskFolder.GetTasks(1); // Include hidden tasks, subject to Windows ACLs.
            dynamic tasks=collection!;
            int count=(int)tasks.Count;
            var rows=new List<SystemScheduledTaskInfo>(pageSize);
            for(int i=offset;i<Math.Min(count,offset+pageSize);i++)
            {
                ct.ThrowIfCancellationRequested();
                object? item=null;
                try
                {
                    item=tasks.Item(i+1); // COM collection is 1-based.
                    dynamic task=item!;
                    string name=(string)task.Name;
                    string path=(string)task.Path;
                    int state=(int)task.State;
                    bool enabled=(bool)task.Enabled;
                    int lastResult=(int)task.LastTaskResult;
                    DateTime last=(DateTime)task.LastRunTime;
                    DateTime next=(DateTime)task.NextRunTime;
                    rows.Add(new(name,path,state switch {0=>"UNKNOWN",1=>"DISABLED",2=>"QUEUED",
                        3=>"READY",4=>"RUNNING",_=>"UNKNOWN"},enabled,Timestamp(last),
                        Timestamp(next),lastResult));
                }
                finally {ReleaseCom(item);}
            }
            int? nextOffset=offset+rows.Count<count?offset+rows.Count:null;
            return new(SystemQueryCode.COMPLETED,kind,at,ScheduledTasks:[..rows],
                NextOffset:nextOffset,ElapsedMs:watch.ElapsedMilliseconds);
        }
        catch(COMException e)
        {
            var code=e.HResult==unchecked((int)0x80070005)?SystemQueryCode.ACCESS_DENIED:
                e.HResult==unchecked((int)0x80070002)?SystemQueryCode.NOT_FOUND:SystemQueryCode.QUERY_FAILED;
            return new(code,kind,at,NativeError:e.HResult,ElapsedMs:watch.ElapsedMilliseconds);
        }
        finally {ReleaseCom(collection);ReleaseCom(folder);ReleaseCom(service);}
    }

    private static DateTimeOffset? Timestamp(DateTime value) => value.Year<2000?null:
        new DateTimeOffset(value.ToUniversalTime());

    private static void ReleaseCom(object? item)
    {
        if(item is not null && Marshal.IsComObject(item))Marshal.FinalReleaseComObject(item);
    }
}
