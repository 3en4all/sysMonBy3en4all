using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace SysMon;

/// Ruch sieci (tylko karty sprzętowe, bez wirtualnych Hyper-V/VMware — inaczej ten sam ruch liczyłby się podwójnie)
/// i przepływ dysków (PDH PhysicalDisk _Total) + wolne miejsce na dyskach stałych.
sealed class SiecDyskiProbnik
{
    [DllImport("iphlpapi.dll")] static extern int GetIfTable2(out IntPtr tabela);
    [DllImport("iphlpapi.dll")] static extern void FreeMibTable(IntPtr pamiec);

    // Tylko do wyliczenia przesunięć pól (Marshal.OffsetOf); danych nie kopiujemy.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MIB_IF_ROW2
    {
        public ulong InterfaceLuid; public uint InterfaceIndex; public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Alias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Description;
        public uint PhysicalAddressLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PhysicalAddress;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PermanentPhysicalAddress;
        public uint Mtu, Type, TunnelType, MediaType, PhysicalMediumType, AccessType, DirectionType;
        public byte Flagi; // bit 0 HardwareInterface, bit 1 FilterInterface
        public uint OperStatus, AdminStatus, MediaConnectState;
        public Guid NetworkGuid; public uint ConnectionType;
        public ulong TransmitLinkSpeed, ReceiveLinkSpeed, InOctets, InUcastPkts, InNUcastPkts, InDiscards, InErrors, InUnknownProtos,
            InUcastOctets, InMulticastOctets, InBroadcastOctets, OutOctets, OutUcastPkts, OutNUcastPkts, OutDiscards, OutErrors,
            OutUcastOctets, OutMulticastOctets, OutBroadcastOctets, OutQLen;
    }

    static readonly int RozmiarWiersza = Marshal.SizeOf<MIB_IF_ROW2>();
    static readonly int OffFlagi = (int)Marshal.OffsetOf<MIB_IF_ROW2>(nameof(MIB_IF_ROW2.Flagi)),
        OffStatus = (int)Marshal.OffsetOf<MIB_IF_ROW2>(nameof(MIB_IF_ROW2.OperStatus)),
        OffWe = (int)Marshal.OffsetOf<MIB_IF_ROW2>(nameof(MIB_IF_ROW2.InOctets)),
        OffWy = (int)Marshal.OffsetOf<MIB_IF_ROW2>(nameof(MIB_IF_ROW2.OutOctets));

    readonly Pdh dysk = new(@"\PhysicalDisk(_Total)\Disk Bytes/sec");
    long pWe = -1, pWy, pCzas;
    int takt;

    public double Pobieranie, Wysylanie; // bity/s
    public double Dysk; // bajty/s
    public string Wolne = "";
    public List<(char Litera, double Proc)> WolneProc = [];

    public void Odczyt()
    {
        if (dysk.Zbierz()) Dysk = dysk.Jedna(0);

        if (GetIfTable2(out IntPtr t) == 0)
        {
            long we = 0, wy = 0;
            try
            {
                int n = Marshal.ReadInt32(t);
                for (int i = 0; i < n; i++)
                {
                    IntPtr w = t + 8 + i * RozmiarWiersza; // MIB_IF_TABLE2: NumEntries, potem tablica wyrównana do 8
                    if ((Marshal.ReadByte(w, OffFlagi) & 3) != 1 || Marshal.ReadInt32(w, OffStatus) != 1) continue; // sprzętowa, nie filtr, Up
                    we += Marshal.ReadInt64(w, OffWe);
                    wy += Marshal.ReadInt64(w, OffWy);
                }
            }
            finally { FreeMibTable(t); }
            long teraz = Stopwatch.GetTimestamp();
            double s = (teraz - pCzas) / (double)Stopwatch.Frequency;
            // pierwszy odczyt albo karta zniknęła/wróciła (liczniki spadły) → bez skoku
            if (pWe >= 0 && we >= pWe && wy >= pWy && s > 0) (Pobieranie, Wysylanie) = (8 * (we - pWe) / s, 8 * (wy - pWy) / s);
            (pWe, pWy, pCzas) = (we, wy, teraz);
        }

        if (takt++ % 10 == 0) // wolne miejsce co 10 s — zmienia się powoli
        {
            WolneProc = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady && d.TotalSize > 0)
                .Select(d => (d.Name[0], 100.0 * d.AvailableFreeSpace / d.TotalSize)).ToList();
            Wolne = "wolne: " + string.Join(" · ", WolneProc.Select(d => $"{d.Litera} {d.Proc:0}%"));
        }
    }

    public static string? Samotest() =>
        RozmiarWiersza == 1352 && OffWe < OffWy ? null : $"zły układ MIB_IF_ROW2 ({RozmiarWiersza} B zamiast 1352)";
}
