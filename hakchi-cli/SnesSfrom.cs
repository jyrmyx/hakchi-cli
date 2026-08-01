using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Hakchi.Cli;

/// <summary>
/// Minimal port of hakchi_gui SnesGame.MakeSfrom — wraps a raw SMC/SFC ROM in canoe .sfrom.
/// </summary>
internal static class SnesSfrom
{
    public enum SnesRomType : byte { LoRom = 0x14, HiRom = 0x15 }

    private static readonly HashSet<byte> SfxTypes = new() { 0x13, 0x14, 0x15, 0x1a };
    private static readonly HashSet<byte> Dsp1Types = new() { 0x03, 0x05 };
    private static readonly HashSet<byte> Sa1Types = new() { 0x34, 0x35 };

    /// <summary>
    /// Convert headerless SNES ROM bytes to .sfrom. Strips 512-byte copier header if present.
    /// </summary>
    public static byte[] ConvertToSfrom(byte[] rawRomData, out byte saveCount)
    {
        if (rawRomData.Length == 0)
            throw new InvalidOperationException("Empty SNES ROM");

        // Copier header: size not multiple of 1024
        if ((rawRomData.Length % 1024) != 0 && rawRomData.Length > 512)
        {
            var stripped = new byte[rawRomData.Length - 512];
            Array.Copy(rawRomData, 512, stripped, 0, stripped.Length);
            rawRomData = stripped;
        }

        var romHeader = GetCorrectHeader(rawRomData, out var romType, out var gameTitle);
        ushort presetId = 0;
        byte chip = 0;
        if (SfxTypes.Contains(romHeader.RomType))
            chip = 0x0C;
        if (Dsp1Types.Contains(romHeader.RomType))
            presetId = 0x10BD; // Mario Kart DSP-1
        if (Sa1Types.Contains(romHeader.RomType))
            presetId = 0x109C; // Super Mario RPG SA-1

        var h1 = new SfromHeader1((uint)rawRomData.Length);
        var h2 = new SfromHeader2((uint)rawRomData.Length, presetId, romType, chip);
        var h1b = h1.GetBytes();
        var h2b = h2.GetBytes();
        var result = new byte[h1b.Length + rawRomData.Length + h2b.Length];
        Array.Copy(h1b, 0, result, 0, h1b.Length);
        Array.Copy(rawRomData, 0, result, h1b.Length, rawRomData.Length);
        Array.Copy(h2b, 0, result, h1b.Length + rawRomData.Length, h2b.Length);

        saveCount = romHeader.SramSize > 0 ? (byte)3 : (byte)0;
        Trace.WriteLine($"SFROM: title='{gameTitle}' type={romType} preset=0x{presetId:X4} chip=0x{chip:X2} sram={romHeader.SramSize}");
        return result;
    }

    public static SnesRomHeader GetCorrectHeader(byte[] rawRomData, out SnesRomType romType, out string gameTitle)
    {
        var lo = SnesRomHeader.Read(rawRomData, 0x7FC0);
        var hi = SnesRomHeader.Read(rawRomData, 0xFFC0);
        var titleLo = lo.GameTitle;
        var titleHi = hi.GameTitle;

        bool loOk = ((lo.Checksum ^ 0xFFFF) == lo.ChecksumComplement) && !string.IsNullOrEmpty(titleLo);
        bool hiOk = ((hi.Checksum ^ 0xFFFF) == hi.ChecksumComplement) && !string.IsNullOrEmpty(titleHi);

        if (loOk && !hiOk)
        {
            romType = SnesRomType.LoRom;
            gameTitle = titleLo;
            return lo;
        }
        if (hiOk && !loOk)
        {
            romType = SnesRomType.HiRom;
            gameTitle = titleHi;
            return hi;
        }
        if (loOk && hiOk)
        {
            // Prefer map mode bit
            if ((lo.RomMakeup & 1) == 0)
            {
                romType = SnesRomType.LoRom;
                gameTitle = titleLo;
                return lo;
            }
            romType = SnesRomType.HiRom;
            gameTitle = titleHi;
            return hi;
        }

        // Fallback: score by printable title
        if (titleLo.Length >= titleHi.Length && titleLo.Length > 0)
        {
            romType = SnesRomType.LoRom;
            gameTitle = titleLo;
            return lo;
        }
        if (titleHi.Length > 0)
        {
            romType = SnesRomType.HiRom;
            gameTitle = titleHi;
            return hi;
        }

        throw new InvalidOperationException("Can't detect SNES ROM type (corrupt or unsupported image).");
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SnesRomHeader
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 21)]
        public byte[] GameTitleArr;
        public byte RomMakeup;
        public byte RomType;
        public byte RomSize;
        public byte SramSize;
        public byte Country;
        public byte License;
        public byte Version;
        public ushort ChecksumComplement;
        public ushort Checksum;

        public string GameTitle
        {
            get
            {
                if (GameTitleArr == null || GameTitleArr.Length == 0) return "";
                var data = new List<byte>(GameTitleArr);
                if (data.Contains(0) || data.Contains(0xFF) || data[0] == 0x20) return "";
                while (data.Count > 0 && data[^1] == 0x20) data.RemoveAt(data.Count - 1);
                return Encoding.ASCII.GetString(data.ToArray());
            }
        }

        public static SnesRomHeader Read(byte[] buffer, int pos)
        {
            var size = Marshal.SizeOf(typeof(SnesRomHeader));
            if (buffer.Length < pos + size)
                throw new ArgumentOutOfRangeException(nameof(pos));
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.Copy(buffer, pos, ptr, size);
                return (SnesRomHeader)Marshal.PtrToStructure(ptr, typeof(SnesRomHeader))!;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SfromHeader1
    {
        public uint Unknown1;
        public uint FileSize;
        public uint Unknown2;
        public uint RomEnd;
        public uint FooterStart;
        public uint Header2;
        public uint Header3;
        public uint Unknown3;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] VCGameID;
        public uint Unknown4;

        public SfromHeader1(uint romSize)
        {
            Unknown1 = 0x00000100;
            FileSize = (uint)(romSize + Marshal.SizeOf(typeof(SfromHeader1)) + Marshal.SizeOf(typeof(SfromHeader2)));
            Unknown2 = 0x00000030;
            RomEnd = (uint)(Marshal.SizeOf(typeof(SfromHeader1)) + romSize);
            FooterStart = FileSize;
            Header2 = RomEnd;
            Header3 = FileSize;
            Unknown3 = 0;
            Flags = RomEnd + 27;
            VCGameID = new byte[8];
            var id = Encoding.ASCII.GetBytes("WUP-XXXX");
            Array.Copy(id, VCGameID, id.Length);
            Unknown4 = 0;
        }

        public byte[] GetBytes() => StructToBytes(this);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SfromHeader2
    {
        public byte FPS;
        public uint RomSize;
        public uint PcmSize;
        public uint FooterSize;
        public ushort PresetID;
        public byte MaxControllers;
        public byte Volume;
        public byte RomType;
        public uint Chip;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
        public byte[] Padding1;
        public uint Unknown3;
        public uint Unknown4;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 13)]
        public byte[] Padding2;

        public SfromHeader2(uint romSize, ushort presetId, SnesRomType romType, byte chip)
        {
            FPS = 60;
            RomSize = romSize;
            PcmSize = 0;
            FooterSize = 0;
            PresetID = presetId;
            MaxControllers = 2;
            Volume = 0x5A;
            RomType = (byte)romType;
            Chip = chip;
            Padding1 = new byte[5];
            Unknown3 = 0x00000001;
            Unknown4 = 0x00000001;
            Padding2 = new byte[13];
        }

        public byte[] GetBytes() => StructToBytes(this);
    }

    private static byte[] StructToBytes<T>(T value) where T : struct
    {
        int size = Marshal.SizeOf(value);
        var arr = new byte[size];
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, ptr, false);
            Marshal.Copy(ptr, arr, 0, size);
            return arr;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
