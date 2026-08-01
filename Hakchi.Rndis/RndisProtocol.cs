namespace Hakchi.Rndis;

internal static class RndisMsg
{
    public const uint PACKET = 0x00000001;
    public const uint INIT = 0x00000002;
    public const uint INIT_C = 0x80000002;
    public const uint QUERY = 0x00000004;
    public const uint QUERY_C = 0x80000004;
    public const uint SET = 0x00000005;
    public const uint SET_C = 0x80000005;
    public const uint HALT = 0x00000003;
    public const uint KEEPALIVE = 0x00000008;
    public const uint KEEPALIVE_C = 0x80000008;
    public const uint INDICATE = 0x00000007;
}

internal static class RndisOid
{
    public const uint GEN_PHYSICAL_MEDIUM = 0x00010202;
    public const uint GEN_CURRENT_PACKET_FILTER = 0x0001010E;
    public const uint GEN_MAXIMUM_FRAME_SIZE = 0x00010106;
    public const uint GEN_LINK_SPEED = 0x00010107;
    public const uint GEN_MEDIA_CONNECT_STATUS = 0x00010114;
    public const uint IEEE_802_3_PERMANENT_ADDRESS = 0x01010101;
    public const uint IEEE_802_3_CURRENT_ADDRESS = 0x01010102;
}

internal static class RndisFilter
{
    // Match Linux RNDIS_DEFAULT_FILTER
    public const uint Default =
        0x00000001 | // DIRECTED
        0x00000008 | // BROADCAST
        0x00000004 | // ALL_MULTICAST
        0x00000020;  // PROMISCUOUS (helps DHCP discovery)
}

internal static class CdcRequest
{
    public const byte SEND_ENCAPSULATED_COMMAND = 0x00;
    public const byte GET_ENCAPSULATED_RESPONSE = 0x01;
}

internal static class Le
{
    public static void WriteU32(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        buf[offset + 2] = (byte)((value >> 16) & 0xFF);
        buf[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    public static uint ReadU32(byte[] buf, int offset) =>
        (uint)(buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24));
}
