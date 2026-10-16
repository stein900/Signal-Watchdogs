using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SignalWatchdogs.Native
{
    public sealed class DxgiAdapterInfo
    {
        public string Name;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public ulong DedicatedVideoMemory;
        public ulong SharedSystemMemory;
        public LUID Luid;
        public bool Software;
        public int Outputs;
        public string Vendor => VendorId switch
        {
            0x10DE => "NVIDIA",
            0x1002 => "AMD",
            0x1022 => "AMD",
            0x8086 => "Intel",
            0x1414 => "Microsoft",
            0x5143 => "Qualcomm",
            _ => $"0x{VendorId:X4}"
        };
        public string PciKey => $"VEN_{VendorId:X4}&DEV_{DeviceId:X4}";
    }

    /// <summary>Énumération des cartes graphiques via DXGI (LUID, VRAM exacte, nombre de sorties).</summary>
    public static class Dxgi
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DXGI_ADAPTER_DESC1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            public UIntPtr DedicatedVideoMemory;
            public UIntPtr DedicatedSystemMemory;
            public UIntPtr SharedSystemMemory;
            public LUID AdapterLuid;
            public uint Flags;
        }

        [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIFactory1
        {
            [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
            [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
            [PreserveSig] int EnumAdapters(uint index, out IntPtr adapter);
            [PreserveSig] int MakeWindowAssociation(IntPtr hwnd, uint flags);
            [PreserveSig] int GetWindowAssociation(out IntPtr hwnd);
            [PreserveSig] int CreateSwapChain(IntPtr device, IntPtr desc, out IntPtr swapChain);
            [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr adapter);
            [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
            [PreserveSig] int IsCurrent();
        }

        [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIAdapter1
        {
            [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
            [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
            [PreserveSig] int EnumOutputs(uint index, out IntPtr output);
            [PreserveSig] int GetDesc(IntPtr desc);
            [PreserveSig] int CheckInterfaceSupport(ref Guid interfaceName, out long umdVersion);
            [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
        }

        [DllImport("dxgi.dll")]
        static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1 factory);

        public static List<DxgiAdapterInfo> EnumerateAdapters()
        {
            var res = new List<DxgiAdapterInfo>();
            var iid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out var factory) != 0 || factory == null) return res;
            try
            {
                for (uint i = 0; i < 16; i++)
                {
                    if (factory.EnumAdapters1(i, out var adapter) != 0 || adapter == null) break;
                    try
                    {
                        if (adapter.GetDesc1(out var desc) != 0) continue;
                        int outputs = 0;
                        for (uint o = 0; o < 16; o++)
                        {
                            if (adapter.EnumOutputs(o, out var output) != 0) break;
                            outputs++;
                            if (output != IntPtr.Zero) Marshal.Release(output);
                        }
                        res.Add(new DxgiAdapterInfo
                        {
                            Name = desc.Description?.Trim(),
                            VendorId = desc.VendorId,
                            DeviceId = desc.DeviceId,
                            SubSysId = desc.SubSysId,
                            Revision = desc.Revision,
                            DedicatedVideoMemory = (ulong)desc.DedicatedVideoMemory,
                            SharedSystemMemory = (ulong)desc.SharedSystemMemory,
                            Luid = desc.AdapterLuid,
                            Software = (desc.Flags & 2) != 0,
                            Outputs = outputs,
                        });
                    }
                    finally { Marshal.ReleaseComObject(adapter); }
                }
            }
            finally { Marshal.ReleaseComObject(factory); }
            return res;
        }
    }

    /// <summary>Lecture de compteurs de performance avec joker via PDH (rapide, pas de WMI).</summary>
    public sealed class PdhQuery : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct PDH_FMT_COUNTERVALUE_DOUBLE { public uint CStatus; public double doubleValue; }

        [StructLayout(LayoutKind.Sequential)]
        struct PDH_FMT_COUNTERVALUE_ITEM_DOUBLE { public IntPtr szName; public PDH_FMT_COUNTERVALUE_DOUBLE FmtValue; }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern uint PdhOpenQuery(string dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern uint PdhAddEnglishCounter(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

        [DllImport("pdh.dll")]
        static extern uint PdhCloseQuery(IntPtr query);

        const uint PDH_FMT_DOUBLE = 0x00000200;
        const uint PDH_FMT_NOCAP100 = 0x00008000;
        const uint PDH_MORE_DATA = 0x800007D2;

        IntPtr _query;
        readonly Dictionary<string, IntPtr> _counters = new Dictionary<string, IntPtr>();

        public PdhQuery()
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) _query = IntPtr.Zero;
        }

        public bool Add(string key, string path)
        {
            if (_query == IntPtr.Zero) return false;
            if (PdhAddEnglishCounter(_query, path, IntPtr.Zero, out var c) != 0) return false;
            _counters[key] = c;
            return true;
        }

        public bool Collect() => _query != IntPtr.Zero && PdhCollectQueryData(_query) == 0;

        public Dictionary<string, double> Read(string key)
        {
            var res = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (!_counters.TryGetValue(key, out var counter)) return res;
            uint size = 0;
            uint st = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out uint count, IntPtr.Zero);
            if (st != PDH_MORE_DATA || size == 0) return res;
            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                st = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, buf);
                if (st != 0) return res;
                int itemSize = Marshal.SizeOf(typeof(PDH_FMT_COUNTERVALUE_ITEM_DOUBLE));
                for (int i = 0; i < count; i++)
                {
                    var item = (PDH_FMT_COUNTERVALUE_ITEM_DOUBLE)Marshal.PtrToStructure(buf + i * itemSize, typeof(PDH_FMT_COUNTERVALUE_ITEM_DOUBLE));
                    if (item.FmtValue.CStatus > 1) continue;
                    var name = Marshal.PtrToStringUni(item.szName) ?? "";
                    res[name] = res.TryGetValue(name, out var prev) ? prev + item.FmtValue.doubleValue : item.FmtValue.doubleValue;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return res;
        }

        public void Dispose()
        {
            if (_query != IntPtr.Zero) PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }
}
