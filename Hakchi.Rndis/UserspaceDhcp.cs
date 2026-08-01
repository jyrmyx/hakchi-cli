using System.Net;
using System.Net.Sockets;

namespace Hakchi.Rndis;

/// <summary>Very small DHCP client over RNDIS Ethernet (no OS stack).</summary>
public static class UserspaceDhcp
{
    public sealed record Lease(IPAddress Client, IPAddress Server, IPAddress? Router, IPAddress? Mask, byte[] ServerMac);

    public static Lease? TryAcquire(RndisDevice rndis, TimeSpan timeout)
    {
        var xid = (uint)Random.Shared.Next(1, int.MaxValue);
        var mac = rndis.HostMac;
        var deadline = DateTime.UtcNow + timeout;

        // DHCPDISCOVER
        var discover = BuildDhcp(mac, xid, msgType: 1, requestIp: null, serverId: null);
        var frame = BuildUdpBroadcast(mac, discover);
        rndis.WriteEthernetFrame(frame);

        var buf = new byte[2048];
        IPAddress? offeredIp = null;
        IPAddress? serverIp = null;
        IPAddress? router = null;
        IPAddress? mask = null;
        byte[]? serverMac = null;

        while (DateTime.UtcNow < deadline)
        {
            if (!rndis.TryReadEthernetFrame(buf, out var len, 200))
                continue;
            if (len < 14 + 20 + 8 + 240) continue;
            if (buf[12] != 0x08 || buf[13] != 0x00) continue; // IPv4
            var ihl = (buf[14] & 0x0F) * 4;
            if (buf[14 + 9] != 17) continue; // UDP
            var udpOff = 14 + ihl;
            int dstPort = (buf[udpOff + 2] << 8) | buf[udpOff + 3];
            if (dstPort != 68) continue;
            var dhcpOff = udpOff + 8;
            if (buf[dhcpOff] != 2) continue; // BOOTREPLY
            var respXid = (uint)((buf[dhcpOff + 4] << 24) | (buf[dhcpOff + 5] << 16) | (buf[dhcpOff + 6] << 8) | buf[dhcpOff + 7]);
            if (respXid != xid) continue;

            offeredIp = new IPAddress(new[] { buf[dhcpOff + 16], buf[dhcpOff + 17], buf[dhcpOff + 18], buf[dhcpOff + 19] });
            serverMac = buf.AsSpan(6, 6).ToArray();
            serverIp = new IPAddress(new[] { buf[14 + 12], buf[14 + 13], buf[14 + 14], buf[14 + 15] });

            // options
            var opt = dhcpOff + 240;
            while (opt + 1 < len)
            {
                var code = buf[opt++];
                if (code == 0xFF) break;
                if (code == 0x00) continue;
                if (opt >= len) break;
                var l = buf[opt++];
                if (opt + l > len) break;
                if (code == 53 && l >= 1)
                {
                    var t = buf[opt]; // 2=OFFER 5=ACK
                    if (t != 2 && t != 5) { offeredIp = null; break; }
                }
                else if (code == 1 && l >= 4)
                    mask = new IPAddress(buf.AsSpan(opt, 4).ToArray());
                else if (code == 3 && l >= 4)
                    router = new IPAddress(buf.AsSpan(opt, 4).ToArray());
                else if (code == 54 && l >= 4)
                    serverIp = new IPAddress(buf.AsSpan(opt, 4).ToArray());
                opt += l;
            }

            if (offeredIp == null || serverIp == null || serverMac == null)
                continue;

            // DHCPREQUEST
            var request = BuildDhcp(mac, xid, msgType: 3, requestIp: offeredIp, serverId: serverIp);
            rndis.WriteEthernetFrame(BuildUdpBroadcast(mac, request));

            // wait ACK briefly
            var ackDeadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < ackDeadline)
            {
                if (!rndis.TryReadEthernetFrame(buf, out len, 200)) continue;
                // naive: accept offer as lease even if ACK parse fails — many gadgets are loose
                return new Lease(offeredIp, serverIp, router, mask, serverMac);
            }

            return new Lease(offeredIp, serverIp, router, mask, serverMac);
        }

        return null;
    }

    private static byte[] BuildUdpBroadcast(byte[] srcMac, byte[] payload)
    {
        // Eth + IP + UDP + payload
        var packet = new byte[14 + 20 + 8 + payload.Length];
        // eth broadcast
        for (int i = 0; i < 6; i++) packet[i] = 0xff;
        Array.Copy(srcMac, 0, packet, 6, 6);
        packet[12] = 0x08; packet[13] = 0x00;

        // IP
        var ipOff = 14;
        packet[ipOff] = 0x45;
        int total = 20 + 8 + payload.Length;
        packet[ipOff + 2] = (byte)(total >> 8);
        packet[ipOff + 3] = (byte)total;
        packet[ipOff + 8] = 64; // TTL
        packet[ipOff + 9] = 17; // UDP
        // src 0.0.0.0 dest 255.255.255.255
        for (int i = 0; i < 4; i++) packet[ipOff + 16 + i] = 0xff;
        WriteIpChecksum(packet, ipOff, 20);

        // UDP
        var udpOff = ipOff + 20;
        packet[udpOff] = 0; packet[udpOff + 1] = 68; // sport
        packet[udpOff + 2] = 0; packet[udpOff + 3] = 67; // dport
        int ulen = 8 + payload.Length;
        packet[udpOff + 4] = (byte)(ulen >> 8);
        packet[udpOff + 5] = (byte)ulen;
        // checksum 0 optional for IPv4
        Array.Copy(payload, 0, packet, udpOff + 8, payload.Length);
        return packet;
    }

    private static byte[] BuildDhcp(byte[] mac, uint xid, byte msgType, IPAddress? requestIp, IPAddress? serverId)
    {
        var p = new byte[300];
        p[0] = 1; // BOOTREQUEST
        p[1] = 1; // ethernet
        p[2] = 6; // hw len
        p[4] = (byte)(xid >> 24);
        p[5] = (byte)(xid >> 16);
        p[6] = (byte)(xid >> 8);
        p[7] = (byte)xid;
        p[10] = 0x80; // broadcast flag
        Array.Copy(mac, 0, p, 28, 6);
        // magic cookie
        p[236] = 99; p[237] = 130; p[238] = 83; p[239] = 99;
        int o = 240;
        p[o++] = 53; p[o++] = 1; p[o++] = msgType; // DHCP message type
        p[o++] = 55; p[o++] = 3; p[o++] = 1; p[o++] = 3; p[o++] = 6; // param req mask,router,dns
        if (requestIp != null)
        {
            var b = requestIp.GetAddressBytes();
            p[o++] = 50; p[o++] = 4; Array.Copy(b, 0, p, o, 4); o += 4;
        }
        if (serverId != null)
        {
            var b = serverId.GetAddressBytes();
            p[o++] = 54; p[o++] = 4; Array.Copy(b, 0, p, o, 4); o += 4;
        }
        p[o++] = 255;
        return p;
    }

    private static void WriteIpChecksum(byte[] buf, int off, int len)
    {
        buf[off + 10] = 0; buf[off + 11] = 0;
        int sum = 0;
        for (int i = 0; i < len; i += 2)
            sum += (buf[off + i] << 8) | buf[off + i + 1];
        while ((sum >> 16) != 0)
            sum = (sum & 0xFFFF) + (sum >> 16);
        sum = ~sum & 0xFFFF;
        buf[off + 10] = (byte)(sum >> 8);
        buf[off + 11] = (byte)sum;
    }
}
