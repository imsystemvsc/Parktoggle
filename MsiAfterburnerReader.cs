using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace ParkToggleWpf;

public static class MsiAfterburnerReader
{
    private const string MapFileName = "MAHMSharedMemory";
    private const uint Signature = 0x4D41484D; // 'M' 'A' 'H' 'M'

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct MAHM_SHARED_MEMORY_HEADER
    {
        public uint dwSignature;
        public uint dwVersion;
        public uint dwHeaderSize;
        public uint dwNumEntries;
        public uint dwEntrySize;
        public uint time;
    }

    public static double? GetGpuVoltage()
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(MapFileName, MemoryMappedFileRights.Read);
            using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            accessor.Read(0, out MAHM_SHARED_MEMORY_HEADER header);
            if (header.dwSignature != Signature || header.dwNumEntries == 0 || header.dwEntrySize == 0)
            {
                return null;
            }

            long offset = header.dwHeaderSize;
            for (uint i = 0; i < header.dwNumEntries; i++)
            {
                long entryOffset = offset + (i * header.dwEntrySize);
                if (entryOffset + header.dwEntrySize > accessor.Capacity)
                {
                    break;
                }

                // Read szSrcName (260 bytes string)
                byte[] nameBytes = new byte[260];
                accessor.ReadArray(entryOffset, nameBytes, 0, 260);
                string srcName = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');

                // Read szSrcUnits (260 bytes string)
                byte[] unitBytes = new byte[260];
                accessor.ReadArray(entryOffset + 260, unitBytes, 0, 260);
                string srcUnits = Encoding.ASCII.GetString(unitBytes).TrimEnd('\0');

                // Read data (float at offset + 520 / 0x208)
                float data = accessor.ReadSingle(entryOffset + 520);

                if (srcName.Contains("voltage", StringComparison.OrdinalIgnoreCase) ||
                    srcName.Contains("VCore", StringComparison.OrdinalIgnoreCase) ||
                    srcName.Contains("VDDC", StringComparison.OrdinalIgnoreCase))
                {
                    // Check if it's GPU voltage (and not CPU voltage)
                    if (!srcName.Contains("CPU", StringComparison.OrdinalIgnoreCase))
                    {
                        double val = data;
                        // MSI Afterburner can export voltage in mV (e.g. 950 mV) or V (e.g. 0.950 V)
                        if (val > 10.0)
                        {
                            val /= 1000.0; // Convert mV to V
                        }
                        if (val > 0.1 && val < 3.0)
                        {
                            return val;
                        }
                    }
                }
            }
        }
        catch
        {
            // Shared memory not found or MSI Afterburner not running
        }

        return null;
    }
}
