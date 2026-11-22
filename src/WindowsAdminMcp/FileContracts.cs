using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<FileQueryAction>))]
public enum FileQueryAction { STAT, LIST }

[JsonConverter(typeof(JsonStringEnumConverter<NativeFileAction>))]
internal enum NativeFileAction { STAT, LIST, READ_CHUNK }

[JsonConverter(typeof(JsonStringEnumConverter<NativeFileCode>))]
public enum NativeFileCode
{
    COMPLETED, INVALID_ARGUMENT, INVALID_CURSOR, NOT_FOUND, NOT_FILE, NOT_DIRECTORY,
    REPARSE_POINT, ACCESS_DENIED, SHARING_VIOLATION, CHANGED_SINCE_LIST,
    CHANGED_SINCE_READ, CAPABILITY_UNAVAILABLE, IO_ERROR, TIMEOUT,
    SERVICE_UNAVAILABLE, USER_CONTEXT_UNAVAILABLE
}

[JsonConverter(typeof(JsonStringEnumConverter<NativeFileKind>))]
public enum NativeFileKind { FILE, DIRECTORY, REPARSE_POINT, OTHER }

[JsonConverter(typeof(JsonStringEnumConverter<NativeFileOwnerCode>))]
public enum NativeFileOwnerCode { OBSERVED, ACCESS_DENIED, UNAVAILABLE, NOT_REQUESTED }

public sealed record NativeFileEntry(
    [property:JsonPropertyName("name")] string Name,
    [property:JsonPropertyName("kind")] NativeFileKind Kind,
    [property:JsonPropertyName("size")] long? Size = null,
    [property:JsonPropertyName("attributes")] string? Attributes = null,
    [property:JsonPropertyName("last_write_utc")] DateTimeOffset? LastWriteUtc = null);

public sealed record NativeFileInfo(
    [property:JsonPropertyName("kind")] NativeFileKind Kind,
    [property:JsonPropertyName("size")] long Size,
    [property:JsonPropertyName("created_utc")] DateTimeOffset CreatedUtc,
    [property:JsonPropertyName("last_write_utc")] DateTimeOffset LastWriteUtc,
    [property:JsonPropertyName("attributes")] string Attributes,
    [property:JsonPropertyName("version")] string Version,
    [property:JsonPropertyName("reparse_tag")] uint? ReparseTag = null,
    [property:JsonPropertyName("owner_sid")] string? OwnerSid = null,
    [property:JsonPropertyName("owner_status")] NativeFileOwnerCode OwnerStatus = NativeFileOwnerCode.NOT_REQUESTED,
    [property:JsonPropertyName("sha256")] string? Sha256 = null);

public sealed record NativeFileReply(
    [property:JsonPropertyName("status")] NativeFileCode Status,
    [property:JsonPropertyName("execution_context")] CommandContext ExecutionContext,
    [property:JsonPropertyName("observed_at")] DateTimeOffset ObservedAt,
    [property:JsonPropertyName("info"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] NativeFileInfo? Info = null,
    [property:JsonPropertyName("entries"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] NativeFileEntry[]? Entries = null,
    [property:JsonPropertyName("next_cursor"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? NextCursor = null,
    [property:JsonPropertyName("directory_version"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? DirectoryVersion = null,
    [property:JsonPropertyName("offset"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] long? Offset = null,
    [property:JsonPropertyName("actual_length"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] int? ActualLength = null,
    [property:JsonPropertyName("eof"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] bool? Eof = null,
    [property:JsonPropertyName("data_base64"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? DataBase64 = null,
    [property:JsonPropertyName("chunk_sha256"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? ChunkSha256 = null,
    [property:JsonPropertyName("version"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? Version = null,
    [property:JsonPropertyName("native_error"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] int? NativeError = null,
    [property:JsonPropertyName("stage"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? Stage = null);

internal sealed record NativeFileRequest(NativeFileAction Action, string Path,
    CommandContext ExecutionContext, int PageSize = 50, string? Cursor = null,
    bool IncludeSha256 = false, long Offset = 0, int Length = 65536,
    string? ExpectedVersion = null);
