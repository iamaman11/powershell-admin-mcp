using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Security;
using System.Text.Json.Serialization;
using System.Xml.Linq;

// Read the kernel's persistent System event log, not wall-clock-minus-uptime,
// service start time, or a reboot request. Missing/cleared/rolled-over evidence
// must never authorize execution. Physical reboot validation is still required.
[JsonConverter(typeof(JsonStringEnumConverter<BootEvidenceCode>))]
public enum BootEvidenceCode { FULL_BOOT_OBSERVED, EVIDENCE_UNAVAILABLE, AMBIGUOUS, SAME_BOOT, NEW_FULL_BOOT_OBSERVED }
internal sealed record BootStamp(
    [property:JsonPropertyName("start_record_id")] long StartRecordId,
    [property:JsonPropertyName("start_utc")] DateTimeOffset StartUtc,
    [property:JsonPropertyName("type_record_id")] long TypeRecordId,
    [property:JsonPropertyName("type_utc")] DateTimeOffset TypeUtc,
    [property:JsonPropertyName("boot_type")] int BootType);
internal sealed record BootObservation(
    [property:JsonPropertyName("status")] BootEvidenceCode Status,
    [property:JsonPropertyName("stamp")] BootStamp? Stamp = null,
    [property:JsonPropertyName("native_error")] int? NativeError = null);

internal static class BootEvidence
{
    private const string StartQuery = "Provider[@Name='Microsoft-Windows-Kernel-General'] and EventID=12";
    private const string TypeQuery = "Provider[@Name='Microsoft-Windows-Kernel-Boot'] and EventID=27";
    private static readonly XNamespace EventNs = "http://schemas.microsoft.com/win/2004/08/events/event";
    private sealed record Entry(long Id, DateTimeOffset Utc, int? BootType);

    private static Entry? Read(string predicate, bool type, long? recordId = null)
    {
        string filter = recordId is long id
            ? FormattableString.Invariant($" and EventRecordID={id}") : "";
        var query = new EventLogQuery("System", PathType.LogName,
            "*[System["+predicate+filter+"]]") { ReverseDirection = true };
        using var reader = new EventLogReader(query);
        using var row = reader.ReadEvent(TimeSpan.FromSeconds(3));
        if (row?.RecordId is not long rid || rid <= 0 || row.TimeCreated is not DateTime time) return null;
        int? bootType = null;
        if (type)
        {
            var values = XDocument.Parse(row.ToXml()).Root?
                .Element(EventNs+"EventData")?.Elements(EventNs+"Data")
                .Where(e => (string?)e.Attribute("Name") == "BootType").ToArray();
            if (values is not {Length:1} ||
                !int.TryParse(values[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n)) return null;
            bootType = n;
        }
        return new(rid, new DateTimeOffset(time.ToUniversalTime()), bootType);
    }

    internal static bool Valid(BootStamp s) => s.StartRecordId > 0 && s.TypeRecordId > 0 &&
        s.TypeRecordId != s.StartRecordId && s.BootType == 0 &&
        Math.Abs((s.TypeUtc-s.StartUtc).TotalSeconds) <= 10 &&
        Math.Abs((decimal)s.TypeRecordId-s.StartRecordId) <= 256;

    private static BootStamp? Pair(Entry? start, Entry? type) =>
        start is not null && type is {BootType:int n}
            ? new(start.Id,start.Utc,type.Id,type.Utc,n) : null;

    internal static BootObservation Observe()
    {
        try
        {
            var s=Pair(Read(StartQuery,false), Read(TypeQuery,true));
            if(s is null)return new(BootEvidenceCode.EVIDENCE_UNAVAILABLE);
            return Valid(s)?new(BootEvidenceCode.FULL_BOOT_OBSERVED,s):new(BootEvidenceCode.AMBIGUOUS);
        }
        catch(Exception e) when(e is EventLogException or UnauthorizedAccessException or SecurityException or InvalidOperationException or System.Xml.XmlException)
        {return new(BootEvidenceCode.EVIDENCE_UNAVAILABLE,NativeError:e.HResult);}
    }

    // The saved stamp must still be independently readable from the same log.
    // Compare chronology by record IDs; no comparison of wall-clock boot times
    // across boots. A cleared/rolled-over log is ambiguous, not a new boot.
    internal static BootEvidenceCode Classify(BootStamp prior, BootStamp? retained, BootStamp? current)
    {
        if(!Valid(prior) || retained!=prior || current is null || !Valid(current))
            return BootEvidenceCode.AMBIGUOUS;
        if(current==prior)return BootEvidenceCode.SAME_BOOT;
        if(Math.Min(current.StartRecordId,current.TypeRecordId)>Math.Max(prior.StartRecordId,prior.TypeRecordId))
            return BootEvidenceCode.NEW_FULL_BOOT_OBSERVED;
        return BootEvidenceCode.AMBIGUOUS;
    }
    internal static BootObservation After(BootStamp prior)
    {
        try
        {
            var current=Observe();
            if(current.Status!=BootEvidenceCode.FULL_BOOT_OBSERVED)return current;
            var retained=Pair(Read(StartQuery,false,prior.StartRecordId),Read(TypeQuery,true,prior.TypeRecordId));
            return new(Classify(prior,retained,current.Stamp),current.Stamp);
        }
        catch(Exception e) when(e is EventLogException or UnauthorizedAccessException or SecurityException or InvalidOperationException or System.Xml.XmlException)
        {return new(BootEvidenceCode.EVIDENCE_UNAVAILABLE,NativeError:e.HResult);}
    }
}
