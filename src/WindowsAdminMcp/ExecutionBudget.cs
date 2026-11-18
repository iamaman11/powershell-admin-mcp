using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<BudgetReason>))]
public enum BudgetReason { AVAILABLE, MEMORY_RESERVE, MEMORY_READBACK_FAILED }

// A launch reservation, not a promise that arbitrary administrative scripts
// cannot consume more memory. The OS remains the owner of CPU scheduling.
public sealed record ExecutionBudget(BudgetReason Reason,int ActiveExecutions,
    ulong AvailablePhysicalBytes,ulong AvailableCommitBytes,ulong ReserveBytes,
    ulong PerExecutionReservationBytes,int? NativeError=null)
{
    public bool CanStart=>Reason==BudgetReason.AVAILABLE;
    private const ulong MiB=1024UL*1024UL;
    internal const ulong Reservation=256*MiB;
    internal static ExecutionBudget Evaluate(int active,ulong totalPhysical,ulong availablePhysical,ulong availableCommit)
    {
        ulong reserve=Math.Max(1024*MiB,totalPhysical/10);
        // Conservative accounting also covers admission before child allocation.
        ulong needed=reserve+(checked((ulong)active)+1)*Reservation;
        return new(Math.Min(availablePhysical,availableCommit)>=needed?BudgetReason.AVAILABLE:BudgetReason.MEMORY_RESERVE,
            active,availablePhysical,availableCommit,reserve,Reservation);
    }
    internal static ExecutionBudget Observe(int active)
    {
        var state=new MemoryStatus { Length=(uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref state)?Evaluate(active,state.TotalPhysical,state.AvailablePhysical,state.AvailablePageFile):
            new(BudgetReason.MEMORY_READBACK_FAILED,active,0,0,0,Reservation,Marshal.GetLastWin32Error());
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length,MemoryLoad;
        public ulong TotalPhysical,AvailablePhysical,TotalPageFile,AvailablePageFile,TotalVirtual,AvailableVirtual,AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus state);
}
