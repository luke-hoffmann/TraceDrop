using System.Runtime.InteropServices;

namespace Tracedrop.Core.Ai;

/// <summary>
/// Picks the graphics card DirectML should use. DirectML's device index is DXGI's adapter order, and on laptops
/// with two GPUs index 0 is usually the integrated one that drives the screen (it shares system RAM and resets
/// under a large model). This picks the hardware adapter with the most dedicated video memory instead: the
/// discrete GPU when there is one.
/// </summary>
public static class GpuPicker
{
    public sealed record Adapter(int Index, string Name, ulong DedicatedVideoMemory, bool Software);

    /// <summary>All DXGI adapters in DirectML's order (empty off Windows or when DXGI is unavailable).</summary>
    public static IReadOnlyList<Adapter> List()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<Adapter>();
        try { return Enumerate(); }
        catch { return Array.Empty<Adapter>(); }
    }

    /// <summary>The best hardware GPU, or null when there's none (DirectML then isn't worth trying).</summary>
    public static Adapter? Best() =>
        List().Where(a => !a.Software).OrderByDescending(a => a.DedicatedVideoMemory).ThenBy(a => a.Index).FirstOrDefault();

    [DllImport("dxgi.dll", ExactSpelling = true)]
    static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct AdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    const uint SoftwareAdapterFlag = 2; // DXGI_ADAPTER_FLAG_SOFTWARE
    const int NotFound = unchecked((int)0x887A0002); // DXGI_ERROR_NOT_FOUND

    // vtable slots: IUnknown(3) + IDXGIObject(4) + IDXGIFactory(5) → IDXGIFactory1::EnumAdapters1 = 12.
    // IUnknown(3) + IDXGIObject(4) + IDXGIAdapter(3) → IDXGIAdapter1::GetDesc1 = 10. Release = 2.
    static unsafe List<Adapter> Enumerate()
    {
        var result = new List<Adapter>();
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        if (CreateDXGIFactory1(ref iid, out var factory) < 0 || factory == IntPtr.Zero) return result;
        try
        {
            var factoryVtbl = *(IntPtr**)factory;
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)factoryVtbl[12];
            for (uint i = 0; i < 16; i++)
            {
                IntPtr adapter;
                var hr = enumAdapters1(factory, i, &adapter);
                if (hr == NotFound || hr < 0 || adapter == IntPtr.Zero) break;
                try
                {
                    var adapterVtbl = *(IntPtr**)adapter;
                    var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)adapterVtbl[10];
                    var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<AdapterDesc1>());
                    try
                    {
                        if (getDesc1(adapter, buffer) >= 0)
                        {
                            var desc = Marshal.PtrToStructure<AdapterDesc1>(buffer);
                            var software = (desc.Flags & SoftwareAdapterFlag) != 0 || desc.VendorId == 0x1414; // Microsoft Basic Render Driver
                            result.Add(new Adapter((int)i, desc.Description.Trim(), desc.DedicatedVideoMemory, software));
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
                finally
                {
                    Release(adapter);
                }
            }
        }
        finally
        {
            Release(factory);
        }
        return result;
    }

    static unsafe void Release(IntPtr unknown)
    {
        var vtbl = *(IntPtr**)unknown;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)vtbl[2])(unknown);
    }
}
