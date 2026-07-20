using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Klydis.Core.Hardware;

/// <summary>
/// Detects physical P-Cores on Intel/AMD processors and applies CPU core affinity masks
/// to lock inference computation strictly to P-Cores, preventing E-core SIMD throttling.
/// </summary>
public static class CpuAffinityHelper
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(
        int relationshipType,
        IntPtr buffer,
        ref int returnedLength);

    private const int RelationProcessorCore = 0;

    /// <summary>
    /// Gets the count of physical P-Cores on the system.
    /// </summary>
    public static int GetPCoreCount()
    {
        try
        {
            int pCores = 0;
            int length = 0;

            GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length);
            if (length > 0)
            {
                IntPtr buffer = Marshal.AllocHGlobal(length);
                try
                {
                    if (GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length))
                    {
                        int offset = 0;
                        while (offset < length)
                        {
                            int type = Marshal.ReadInt32(buffer, offset);
                            int size = Marshal.ReadInt32(buffer, offset + 4);
                            byte efficiencyClass = Marshal.ReadByte(buffer, offset + 16);

                            // EfficiencyClass: 0 = E-Core (or standard core on older CPUs), >0 = P-Core on hybrid CPUs
                            if (efficiencyClass > 0)
                            {
                                pCores++;
                            }

                            if (size <= 0) break;
                            offset += size;
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            if (pCores > 0) return pCores;
        }
        catch
        {
            // Fallback for non-hybrid or legacy systems
        }

        // Fallback: Estimate physical P-cores as half of total logical threads (min 4, max 8)
        int totalLogical = Environment.ProcessorCount;
        return Math.Clamp(totalLogical / 2, 4, 8);
    }

    /// <summary>
    /// Generates a bitmask corresponding to physical P-Cores.
    /// </summary>
    public static IntPtr GetPCoreAffinityMask()
    {
        int pCoreCount = GetPCoreCount();
        long mask = 0;

        // Lock to even-numbered logical processor indices (0, 2, 4, 6...) representing physical P-cores
        for (int i = 0; i < pCoreCount && (i * 2) < 64; i++)
        {
            mask |= (1L << (i * 2));
        }

        if (mask == 0) mask = (1L << Environment.ProcessorCount) - 1;
        return new IntPtr(mask);
    }

    /// <summary>
    /// Locks the current process execution to physical P-Cores.
    /// </summary>
    public static void ApplyPCoreAffinityToProcess()
    {
        try
        {
            IntPtr pCoreMask = GetPCoreAffinityMask();
            Process.GetCurrentProcess().ProcessorAffinity = pCoreMask;
        }
        catch
        {
            // Ignore if OS permissions restrict process affinity modification
        }
    }
}
