using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<SystemQueryKind>))]
public enum SystemQueryKind { PROCESSES, SERVICES, NETWORK_INTERFACES, EVENT_LOG, REGISTRY, SCHEDULED_TASKS, TCP_CONNECTIONS }

[JsonConverter(typeof(JsonStringEnumConverter<SystemQueryCode>))]
public enum SystemQueryCode { COMPLETED, INVALID_ARGUMENT, NOT_FOUND, ACCESS_DENIED, QUERY_FAILED, TIMEOUT, UNSUPPORTED_PLATFORM, RESOURCE_LIMIT }

[JsonConverter(typeof(JsonStringEnumConverter<SystemServiceStartMode>))]
public enum SystemServiceStartMode { BOOT, SYSTEM, AUTOMATIC, MANUAL, DISABLED, UNKNOWN }

public sealed record SystemProcessInfo(
    [property:JsonPropertyName("pid")] int Pid,
    [property:JsonPropertyName("name")] string Name,
    [property:JsonPropertyName("session_id")] int? SessionId,
    [property:JsonPropertyName("working_set_bytes")] long? WorkingSetBytes,
    [property:JsonPropertyName("handle_count")] int? HandleCount,
    [property:JsonPropertyName("start_utc"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? StartUtc = null,
    [property:JsonPropertyName("executable_path"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? ExecutablePath = null,
    [property:JsonPropertyName("cpu_total_ms"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] long? CpuTotalMs = null,
    [property:JsonPropertyName("details_unavailable")] bool DetailsUnavailable = false,
    [property:JsonPropertyName("parent_pid"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] int? ParentPid = null,
    [property:JsonPropertyName("thread_count"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] int? ThreadCount = null);

public sealed record SystemServiceInfo(
    [property:JsonPropertyName("service_name")] string ServiceName,
    [property:JsonPropertyName("display_name")] string DisplayName,
    [property:JsonPropertyName("status")] string Status,
    [property:JsonPropertyName("service_type")] string ServiceType,
    [property:JsonPropertyName("can_stop")] bool? CanStop,
    [property:JsonPropertyName("can_pause")] bool? CanPause,
    [property:JsonPropertyName("details_unavailable")] bool DetailsUnavailable = false,
    [property:JsonPropertyName("start_mode"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] SystemServiceStartMode? StartMode = null,
    [property:JsonPropertyName("depends_on"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string[]? DependsOn = null,
    [property:JsonPropertyName("dependencies_truncated")] bool DependenciesTruncated = false);

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
    [property:JsonPropertyName("events"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] SystemEventInfo[]? Events = null,
    [property:JsonPropertyName("registry_entries"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] SystemRegistryEntry[]? RegistryEntries = null,
    [property:JsonPropertyName("scheduled_tasks"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] SystemScheduledTaskInfo[]? ScheduledTasks = null,
    [property:JsonPropertyName("tcp_connections"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] SystemTcpConnectionInfo[]? TcpConnections = null,
    [property:JsonPropertyName("next_offset"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] int? NextOffset = null,
    [property:JsonPropertyName("native_error"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] int? NativeError = null,
    [property:JsonPropertyName("elapsed_ms")] long ElapsedMs = 0);
