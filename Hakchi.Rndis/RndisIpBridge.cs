using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Hakchi.Rndis;

/// <summary>
/// Bridges RNDIS Ethernet frames to a macOS utun device (IP only + userspace ARP).
/// </summary>
public sealed class RndisIpBridge : IDisposable
{
    private readonly RndisDevice _rndis;
    private readonly Utun _utun = new();
    private readonly Dictionary<uint, byte[]> _arp = new(); // IP -> MAC
    private readonly byte[] _ourMac;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private uint _ourIp;
    private uint _peerIp;

    public string InterfaceName => _utun.InterfaceName;
    public string? AssignedAddress { get; private set; }

    public RndisIpBridge(RndisDevice rndis)
    {
        _rndis = rndis;
        _ourMac = (byte[])rndis.HostMac.Clone();
    }

    public void Start()
    {
        _utun.Open();
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunLoop(_cts.Token));
    }

    public void ConfigureDhcp()
    {
        // macOS: ask configd to DHCP on the utun
        Run($"ipconfig set {InterfaceName} DHCP");
        // wait for address
        for (var i = 0; i < 40; i++)
        {
            Thread.Sleep(250);
            if (TryGetInterfaceIPv4(InterfaceName, out var ip, out var mask))
            {
                AssignedAddress = ip;
                _ourIp = IpToU32(IPAddress.Parse(ip));
                // guess peer as .1 on same /24 if mask is /24, else try network+1
                var parts = ip.Split('.').Select(int.Parse).ToArray();
                parts[3] = 1;
                if (parts[3] == int.Parse(ip.Split('.')[3]))
                    parts[3] = 2;
                _peerIp = IpToU32(IPAddress.Parse(string.Join('.', parts)));
                return;
            }
        }
    }

    public void ConfigureStatic(string address, string netmask = "255.255.255.0")
    {
        Run($"/sbin/ifconfig {InterfaceName} inet {address} netmask {netmask} up");
        AssignedAddress = address;
        _ourIp = IpToU32(IPAddress.Parse(address));
    }

    private void RunLoop(CancellationToken ct)
    {
        var eth = new byte[2048];
        var utunBuf = new byte[2048];

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // RNDIS -> utun
                if (_rndis.TryReadEthernetFrame(eth, out var ethLen, 20))
                {
                    HandleEthernetRx(eth.AsSpan(0, ethLen));
                }

                // utun -> RNDIS
                var n = _utun.Read(utunBuf);
                if (n > 4)
                {
                    // first 4 bytes: AF family in network order
                    var af = (utunBuf[0] << 24) | (utunBuf[1] << 16) | (utunBuf[2] << 8) | utunBuf[3];
                    if (af == 2 && n > 4) // AF_INET
                    {
                        var ipPacket = utunBuf.AsSpan(4, n - 4);
                        SendIpAsEthernet(ipPacket);
                    }
                }
                else
                {
                    Thread.Sleep(2);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("bridge loop: " + ex.Message);
                Thread.Sleep(50);
            }
        }
    }

    private void HandleEthernetRx(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 14) return;
        var ethType = (frame[12] << 8) | frame[13];
        if (ethType == 0x0806) // ARP
        {
            HandleArp(frame);
            return;
        }
        if (ethType != 0x0800) // IPv4 only
            return;

        var ip = frame[14..];
        if (ip.Length < 20) return;

        // learn ARP from source
        var srcIp = BinaryPrimitivesReadU32(ip, 12);
        var srcMac = frame.Slice(6, 6).ToArray();
        _arp[srcIp] = srcMac;
        if (_peerIp == 0)
            _peerIp = srcIp;

        // write to utun: AF_INET (2) + IP packet
        var outBuf = new byte[4 + ip.Length];
        outBuf[0] = 0; outBuf[1] = 0; outBuf[2] = 0; outBuf[3] = 2;
        ip.CopyTo(outBuf.AsSpan(4));
        try { _utun.Write(outBuf, outBuf.Length); } catch { /* drop */ }
    }

    private void HandleArp(ReadOnlySpan<byte> frame)
    {
        // Ethernet(14) + ARP(28)
        if (frame.Length < 42) return;
        var op = (frame[20] << 8) | frame[21];
        var sha = frame.Slice(22, 6).ToArray();
        var spa = BinaryPrimitivesReadU32(frame, 28);
        var tpa = BinaryPrimitivesReadU32(frame, 38);
        _arp[spa] = sha;

        if (op == 1 && _ourIp != 0 && tpa == _ourIp) // request for us
        {
            var reply = new byte[42];
            // eth
            Array.Copy(sha, 0, reply, 0, 6);
            Array.Copy(_ourMac, 0, reply, 6, 6);
            reply[12] = 0x08; reply[13] = 0x06;
            // arp
            reply[14] = 0; reply[15] = 1; // htype eth
            reply[16] = 0x08; reply[17] = 0x00; // ptype ip
            reply[18] = 6; reply[19] = 4;
            reply[20] = 0; reply[21] = 2; // reply
            Array.Copy(_ourMac, 0, reply, 22, 6);
            WriteU32(reply, 28, _ourIp);
            Array.Copy(sha, 0, reply, 32, 6);
            WriteU32(reply, 38, spa);
            _rndis.WriteEthernetFrame(reply);
        }
    }

    private void SendIpAsEthernet(ReadOnlySpan<byte> ipPacket)
    {
        if (ipPacket.Length < 20) return;
        var dstIp = BinaryPrimitivesReadU32(ipPacket, 16);
        byte[] dstMac;
        if (dstIp == 0xFFFFFFFF || (dstIp & 0xF0000000) == 0xE0000000)
        {
            dstMac = new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff };
        }
        else if (!_arp.TryGetValue(dstIp, out dstMac!))
        {
            // send ARP request then drop (next packet may succeed)
            SendArpRequest(dstIp);
            return;
        }

        var frame = new byte[14 + ipPacket.Length];
        Array.Copy(dstMac, 0, frame, 0, 6);
        Array.Copy(_ourMac, 0, frame, 6, 6);
        frame[12] = 0x08; frame[13] = 0x00;
        ipPacket.CopyTo(frame.AsSpan(14));
        _rndis.WriteEthernetFrame(frame);
    }

    private void SendArpRequest(uint targetIp)
    {
        if (_ourIp == 0) return;
        var frame = new byte[42];
        for (int i = 0; i < 6; i++) frame[i] = 0xff;
        Array.Copy(_ourMac, 0, frame, 6, 6);
        frame[12] = 0x08; frame[13] = 0x06;
        frame[14] = 0; frame[15] = 1;
        frame[16] = 0x08; frame[17] = 0x00;
        frame[18] = 6; frame[19] = 4;
        frame[20] = 0; frame[21] = 1; // request
        Array.Copy(_ourMac, 0, frame, 22, 6);
        WriteU32(frame, 28, _ourIp);
        // tha zeros
        WriteU32(frame, 38, targetIp);
        try { _rndis.WriteEthernetFrame(frame); } catch { }
    }

    private static uint BinaryPrimitivesReadU32(ReadOnlySpan<byte> b, int offset) =>
        (uint)((b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3]);

    private static void WriteU32(byte[] b, int offset, uint value)
    {
        b[offset] = (byte)(value >> 24);
        b[offset + 1] = (byte)(value >> 16);
        b[offset + 2] = (byte)(value >> 8);
        b[offset + 3] = (byte)value;
    }

    private static uint IpToU32(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
    }

    private static bool TryGetInterfaceIPv4(string ifName, out string address, out string netmask)
    {
        address = "";
        netmask = "";
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!string.Equals(ni.Name, ifName, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(ua.Address))
                {
                    address = ua.Address.ToString();
                    netmask = ua.IPv4Mask?.ToString() ?? "255.255.255.0";
                    return true;
                }
            }
        }
        return false;
    }

    private static void Run(string cmd)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            Arguments = "-c " + Quote(cmd),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit(10000);
    }

    private static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _loop?.Wait(500); } catch { }
        _utun.Dispose();
    }
}
