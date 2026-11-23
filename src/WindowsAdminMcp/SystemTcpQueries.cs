using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<SystemTcpFamily>))]
public enum SystemTcpFamily { IPV4, IPV6 }

[JsonConverter(typeof(JsonStringEnumConverter<SystemTcpState>))]
public enum SystemTcpState : uint
{
    UNKNOWN = 0, CLOSED = 1, LISTEN = 2, SYN_SENT = 3, SYN_RECEIVED = 4,
    ESTABLISHED = 5, FIN_WAIT_1 = 6, FIN_WAIT_2 = 7, CLOSE_WAIT = 8,
    CLOSING = 9, LAST_ACK = 10, TIME_WAIT = 11, DELETE_TCB = 12
}

public sealed record SystemTcpConnectionInfo(
    [property:JsonPropertyName("pid")] uint Pid,
    [property:JsonPropertyName("family")] SystemTcpFamily Family,
    [property:JsonPropertyName("state")] SystemTcpState State,
    [property:JsonPropertyName("local_address")] string LocalAddress,
    [property:JsonPropertyName("local_port")] int LocalPort,
    [property:JsonPropertyName("remote_address"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? RemoteAddress,
    [property:JsonPropertyName("remote_port"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] int? RemotePort);

// The Windows IP Helper API is the owner of these read-only TCP snapshots.
// No netstat.exe, PowerShell process, new service, or network connection.
internal static class SystemTcpQueries
{
    private const uint AfInet = 2, AfInet6 = 23;
    private const uint TcpTableOwnerPidAll = 5;
    private const uint ErrorAccessDenied = 5, ErrorInsufficientBuffer = 122;
    private const int MaxTableBytes = 8 * 1024 * 1024;

    [StructLayout(LayoutKind.Sequential)]
    private struct Tcp4Row
    {
        public uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort, Pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Tcp6Row
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddress;
        public uint LocalScope, LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] RemoteAddress;
        public uint RemoteScope, RemotePort, State, Pid;
    }

    // OffsetOf handles any native alignment between dwNumEntries and table[0].
    [StructLayout(LayoutKind.Sequential)]
    private struct Tcp4Table { public uint Count; public Tcp4Row First; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Tcp6Table { public uint Count; public Tcp6Row First; }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size,
        [MarshalAs(UnmanagedType.Bool)] bool order, uint family, uint tableClass, uint reserved);

    private sealed record TableRead(SystemQueryCode Status, List<SystemTcpConnectionInfo> Rows,
        int? NativeError = null);

    private static TableRead NativeFailure(uint code) => new(
        code == ErrorAccessDenied ? SystemQueryCode.ACCESS_DENIED : SystemQueryCode.QUERY_FAILED,
        [], unchecked((int)code));

    internal static int NativePort(uint value) =>
        (int)(((value & 0xff) << 8) | ((value >> 8) & 0xff));

    internal static uint NativeScope(uint value) =>
        BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(value) : value;

    internal static bool NativeLayoutsValid => Marshal.SizeOf<Tcp4Row>() == 24 &&
        Marshal.SizeOf<Tcp6Row>() == 56 &&
        Marshal.OffsetOf<Tcp4Table>(nameof(Tcp4Table.First)).ToInt32() == 4 &&
        Marshal.OffsetOf<Tcp6Table>(nameof(Tcp6Table.First)).ToInt32() == 4;

    private static SystemTcpState State(uint raw) => Enum.IsDefined((SystemTcpState)raw)
        ? (SystemTcpState)raw : SystemTcpState.UNKNOWN;

    internal static SystemQueryResult Connections(SystemQueryKind kind, DateTimeOffset at,
        Stopwatch watch, int? pid, int pageSize, int offset, CancellationToken ct)
    {
        SystemQueryResult Result(SystemQueryCode code, int? native = null) =>
            new(code, kind, at, NativeError: native, ElapsedMs: watch.ElapsedMilliseconds);
        if (!NativeLayoutsValid) return Result(SystemQueryCode.QUERY_FAILED);
        var ipv4 = ReadTable(AfInet, watch, ct);
        if (ipv4.Status != SystemQueryCode.COMPLETED)
            return Result(ipv4.Status, ipv4.NativeError);
        var ipv6 = ReadTable(AfInet6, watch, ct);
        if (ipv6.Status != SystemQueryCode.COMPLETED)
            return Result(ipv6.Status, ipv6.NativeError);

        var ordered = ipv4.Rows.Concat(ipv6.Rows)
            .Where(row => pid is null || row.Pid == (uint)pid.Value)
            .OrderBy(row => row.Pid).ThenBy(row => row.Family)
            .ThenBy(row => row.LocalAddress, StringComparer.Ordinal)
            .ThenBy(row => row.LocalPort)
            .ThenBy(row => row.RemoteAddress, StringComparer.Ordinal)
            .ThenBy(row => row.RemotePort).ToArray();
        if (pid is not null && ordered.Length == 0) return Result(SystemQueryCode.NOT_FOUND);
        var rows = ordered.Skip(offset).Take(pageSize).ToArray();
        int? next = offset + rows.Length < ordered.Length ? offset + rows.Length : null;
        return new(SystemQueryCode.COMPLETED, kind, at, TcpConnections: rows,
            NextOffset: next, ElapsedMs: watch.ElapsedMilliseconds);
    }

    private static TableRead ReadTable(uint family, Stopwatch watch, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        uint size = 0;
        uint result = GetExtendedTcpTable(IntPtr.Zero, ref size, true, family, TcpTableOwnerPidAll, 0);
        if (result != ErrorInsufficientBuffer && result != 0)
            return NativeFailure(result);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (watch.Elapsed > TimeSpan.FromSeconds(3))
                return new(SystemQueryCode.TIMEOUT, []);
            if (size < sizeof(uint) || size > MaxTableBytes)
                return new(SystemQueryCode.RESOURCE_LIMIT, []);
            IntPtr buffer;
            try { buffer = Marshal.AllocHGlobal((int)size); }
            catch (OutOfMemoryException) { return new(SystemQueryCode.RESOURCE_LIMIT, []); }
            try
            {
                uint received = size;
                result = GetExtendedTcpTable(buffer, ref received, true, family, TcpTableOwnerPidAll, 0);
                if (result == ErrorInsufficientBuffer)
                {
                    size = received;
                    continue; // The TCP table grew; retry only this read-only snapshot.
                }
                if (result != 0)
                    return NativeFailure(result);
                if (received < sizeof(uint) || received > size)
                    return new(SystemQueryCode.QUERY_FAILED, []);
                int rowSize = family == AfInet ? Marshal.SizeOf<Tcp4Row>() : Marshal.SizeOf<Tcp6Row>();
                int header = family == AfInet
                    ? Marshal.OffsetOf<Tcp4Table>(nameof(Tcp4Table.First)).ToInt32()
                    : Marshal.OffsetOf<Tcp6Table>(nameof(Tcp6Table.First)).ToInt32();
                uint count = unchecked((uint)Marshal.ReadInt32(buffer));
                if (received < header || count > (received - header) / rowSize)
                    return new(SystemQueryCode.QUERY_FAILED, []);
                var rows = new List<SystemTcpConnectionInfo>((int)count);
                for (int i = 0; i < count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (watch.Elapsed > TimeSpan.FromSeconds(3))
                        return new(SystemQueryCode.TIMEOUT, []);
                    IntPtr address = IntPtr.Add(buffer, header + i * rowSize);
                    rows.Add(family == AfInet
                        ? ToInfo(Marshal.PtrToStructure<Tcp4Row>(address))
                        : ToInfo(Marshal.PtrToStructure<Tcp6Row>(address)));
                }
                return new(SystemQueryCode.COMPLETED, rows);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return new(SystemQueryCode.QUERY_FAILED, [], (int)ErrorInsufficientBuffer);
    }

    private static SystemTcpConnectionInfo ToInfo(Tcp4Row row)
    {
        var state = State(row.State);
        return new(row.Pid, SystemTcpFamily.IPV4, state,
            new IPAddress(BitConverter.GetBytes(row.LocalAddress)).ToString(), NativePort(row.LocalPort),
            state == SystemTcpState.LISTEN ? null :
                new IPAddress(BitConverter.GetBytes(row.RemoteAddress)).ToString(),
            state == SystemTcpState.LISTEN ? null : NativePort(row.RemotePort));
    }

    private static SystemTcpConnectionInfo ToInfo(Tcp6Row row)
    {
        var state = State(row.State);
        return new(row.Pid, SystemTcpFamily.IPV6, state,
            new IPAddress(row.LocalAddress, NativeScope(row.LocalScope)).ToString(), NativePort(row.LocalPort),
            state == SystemTcpState.LISTEN ? null :
                new IPAddress(row.RemoteAddress, NativeScope(row.RemoteScope)).ToString(),
            state == SystemTcpState.LISTEN ? null : NativePort(row.RemotePort));
    }
}
