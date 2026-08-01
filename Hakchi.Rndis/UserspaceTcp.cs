using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Hakchi.Rndis;

/// <summary>
/// Minimal userspace IPv4 TCP client over RNDIS Ethernet + ARP.
/// Enough for a single outbound SSH connection (no fancy options).
/// </summary>
public sealed class UserspaceTcpClient : IDisposable
{
    private readonly RndisDevice _rndis;
    private readonly byte[] _localMac;
    private readonly byte[] _remoteMac;
    private readonly uint _localIp;
    private readonly uint _remoteIp;
    private readonly ConcurrentQueue<byte[]> _rx = new();
    private readonly object _tcpLock = new();
    private CancellationTokenSource? _pumpCts;
    private Task? _pump;
    private uint _localSeq;
    private uint _remoteSeq;
    private uint _peerAck; // last cumulative ACK from peer (next seq they expect from us)
    private ushort _localPort;
    private ushort _remotePort;
    private bool _established;
    private bool _closed;
    private ushort _ipId = 1;

    /// <summary>Max TCP payload per segment. Must stay under Ethernet MTU (~1500) minus headers.</summary>
    private const int TcpMss = 1200;

    public bool Connected => _established && !_closed;

    public UserspaceTcpClient(RndisDevice rndis, IPAddress localIp, IPAddress remoteIp, byte[] remoteMac)
    {
        _rndis = rndis;
        _localMac = (byte[])rndis.HostMac.Clone();
        _remoteMac = (byte[])remoteMac.Clone();
        _localIp = ToU32(localIp);
        _remoteIp = ToU32(remoteIp);
    }

    public void Connect(int remotePort, int timeoutMs = 8000)
    {
        _remotePort = (ushort)remotePort;
        _localPort = (ushort)Random.Shared.Next(40000, 60000);
        _localSeq = (uint)Random.Shared.Next(1, int.MaxValue);
        _peerAck = _localSeq;
        _established = false;
        _closed = false;

        _pumpCts = new CancellationTokenSource();
        _pump = Task.Run(() => Pump(_pumpCts.Token));

        // ARP warm-up: announce ourselves (gratuitous) and send SYN
        SendArpAnnounce();
        SendTcp(flags: 0x02, payload: ReadOnlySpan<byte>.Empty, seq: _localSeq, ack: 0); // SYN

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (_established) return;
            if (_closed) throw new IOException("TCP closed during handshake");
            Thread.Sleep(10);
        }
        throw new TimeoutException("TCP SYN timeout — is SSH running on the Classic?");
    }

    public int Send(byte[] buffer, int offset, int count)
    {
        if (!_established) throw new InvalidOperationException("not connected");
        if (count <= 0) return 0;

        // Segment to MSS and stop-and-wait for peer ACK. Without this, large SSH
        // exec/payload frames exceed USB Ethernet MTU and the session stalls.
        int sent = 0;
        while (sent < count)
        {
            if (_closed) throw new IOException("TCP closed during send");

            int n = Math.Min(TcpMss, count - sent);
            byte[] payload;
            uint seqAtSend;
            uint seqAfter;
            lock (_tcpLock)
            {
                payload = buffer.AsSpan(offset + sent, n).ToArray();
                seqAtSend = _localSeq;
                seqAfter = _localSeq + (uint)n;
                // PSH only on last segment of this Write call batch piece
                byte flags = 0x18; // PSH+ACK — keep simple; peer handles
                SendTcp(flags, payload, seqAtSend, _remoteSeq);
                _localSeq = seqAfter;
            }

            // Wait until peer ACKs this segment (cumulative ACK >= seqAfter)
            if (!WaitForAck(seqAfter, timeoutMs: 8000))
            {
                // one retransmit
                lock (_tcpLock)
                {
                    SendTcp(0x18, payload, seqAtSend, _remoteSeq);
                }
                if (!WaitForAck(seqAfter, timeoutMs: 8000))
                    throw new TimeoutException($"TCP send ACK timeout after {sent + n} / {count} bytes");
            }

            sent += n;
        }
        return count;
    }

    private bool WaitForAck(uint needAck, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (_closed) return false;
            // unsigned wrap-safe: peerAck - needAck as int >= 0 means peerAck >= needAck
            if (SeqGte(_peerAck, needAck))
                return true;
            Thread.Sleep(2);
        }
        return SeqGte(_peerAck, needAck);
    }

    private static bool SeqGte(uint a, uint b) => unchecked((int)(a - b)) >= 0;

    public int Receive(byte[] buffer, int offset, int count, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (_rx.TryDequeue(out var chunk))
            {
                var n = Math.Min(count, chunk.Length);
                Array.Copy(chunk, 0, buffer, offset, n);
                if (n < chunk.Length)
                {
                    // put remainder back (simple, order-preserving only if single consumer)
                    var rest = new byte[chunk.Length - n];
                    Array.Copy(chunk, n, rest, 0, rest.Length);
                    // prepend remainder — use stack via new queue head by re-dequeuing is hard;
                    // for SSH small reads, copy all or nothing when buffer too small
                    if (rest.Length > 0)
                    {
                        // re-queue rest in front: drain and rebuild
                        var all = new List<byte[]>(_rx.Count + 1) { rest };
                        while (_rx.TryDequeue(out var x)) all.Add(x);
                        foreach (var x in all) _rx.Enqueue(x);
                    }
                }
                return n;
            }
            if (_closed && _rx.IsEmpty) return 0;
            Thread.Sleep(2);
        }
        return 0; // timeout soft
    }

    private void Pump(CancellationToken ct)
    {
        var buf = new byte[4096];
        while (!ct.IsCancellationRequested && !_closed)
        {
            try
            {
                if (!_rndis.TryReadEthernetFrame(buf, out var len, 50))
                    continue;
                HandleFrame(buf.AsSpan(0, len));
            }
            catch
            {
                Thread.Sleep(5);
            }
        }
    }

    private void HandleFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 14) return;
        var ethType = (frame[12] << 8) | frame[13];
        if (ethType == 0x0806)
        {
            HandleArp(frame);
            return;
        }
        if (ethType != 0x0800 || frame.Length < 34) return;

        var ip = frame[14..];
        var ihl = (ip[0] & 0x0F) * 4;
        if (ip[9] != 6 || ip.Length < ihl + 20) return; // TCP
        var srcIp = ReadU32BE(ip, 12);
        var dstIp = ReadU32BE(ip, 16);
        if (srcIp != _remoteIp || dstIp != _localIp) return;

        var tcp = ip[ihl..];
        var srcPort = (ushort)((tcp[0] << 8) | tcp[1]);
        var dstPort = (ushort)((tcp[2] << 8) | tcp[3]);
        if (srcPort != _remotePort || dstPort != _localPort) return;

        var seq = ReadU32BE(tcp, 4);
        var ack = ReadU32BE(tcp, 8);
        var dataOff = ((tcp[12] >> 4) * 4);
        var flags = tcp[13];
        var payload = dataOff < tcp.Length ? tcp[dataOff..] : ReadOnlySpan<byte>.Empty;

        lock (_tcpLock)
        {
            if ((flags & 0x02) != 0 && (flags & 0x10) != 0 && !_established) // SYN+ACK
            {
                _remoteSeq = seq + 1;
                _localSeq = ack;
                _peerAck = ack;
                SendTcp(flags: 0x10, payload: ReadOnlySpan<byte>.Empty, seq: _localSeq, ack: _remoteSeq); // ACK
                _established = true;
                return;
            }

            if (!_established) return;

            // Track cumulative ACK from peer (for our stop-and-wait sender)
            if ((flags & 0x10) != 0)
            {
                if (SeqGte(ack, _peerAck))
                    _peerAck = ack;
            }

            if ((flags & 0x01) != 0 || (flags & 0x04) != 0) // FIN/RST
            {
                if (payload.Length > 0 && seq == _remoteSeq)
                {
                    _remoteSeq += (uint)payload.Length;
                    _rx.Enqueue(payload.ToArray());
                }
                _closed = true;
                return;
            }

            if (payload.Length > 0)
            {
                // accept only in-order simple path
                if (seq == _remoteSeq)
                {
                    _remoteSeq += (uint)payload.Length;
                    _rx.Enqueue(payload.ToArray());
                    SendTcp(flags: 0x10, payload: ReadOnlySpan<byte>.Empty, seq: _localSeq, ack: _remoteSeq);
                }
                else if (SeqGte(_remoteSeq, seq + (uint)payload.Length))
                {
                    // duplicate — re-ACK
                    SendTcp(flags: 0x10, payload: ReadOnlySpan<byte>.Empty, seq: _localSeq, ack: _remoteSeq);
                }
            }
        }
    }

    private void HandleArp(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 42) return;
        var op = (frame[20] << 8) | frame[21];
        var tpa = ReadU32BE(frame, 38);
        if (op == 1 && tpa == _localIp)
        {
            // reply
            var reply = new byte[42];
            frame.Slice(6, 6).CopyTo(reply.AsSpan(0, 6));
            _localMac.CopyTo(reply.AsSpan(6, 6));
            reply[12] = 0x08; reply[13] = 0x06;
            reply[14] = 0; reply[15] = 1; reply[16] = 0x08; reply[17] = 0x00;
            reply[18] = 6; reply[19] = 4; reply[20] = 0; reply[21] = 2;
            _localMac.CopyTo(reply.AsSpan(22, 6));
            WriteU32BE(reply, 28, _localIp);
            frame.Slice(6, 6).CopyTo(reply.AsSpan(32, 6));
            WriteU32BE(reply, 38, ReadU32BE(frame, 28));
            _rndis.WriteEthernetFrame(reply);
        }
    }

    private void SendArpAnnounce()
    {
        var frame = new byte[42];
        for (int i = 0; i < 6; i++) frame[i] = 0xff;
        _localMac.CopyTo(frame.AsSpan(6, 6));
        frame[12] = 0x08; frame[13] = 0x06;
        frame[14] = 0; frame[15] = 1; frame[16] = 0x08; frame[17] = 0x00;
        frame[18] = 6; frame[19] = 4; frame[20] = 0; frame[21] = 1;
        _localMac.CopyTo(frame.AsSpan(22, 6));
        WriteU32BE(frame, 28, _localIp);
        WriteU32BE(frame, 38, _remoteIp);
        try { _rndis.WriteEthernetFrame(frame); } catch { }
    }

    private void SendTcp(byte flags, ReadOnlySpan<byte> payload, uint seq, uint ack)
    {
        const int eth = 14, ip = 20, tcp = 20;
        var packet = new byte[eth + ip + tcp + payload.Length];
        // eth
        _remoteMac.CopyTo(packet.AsSpan(0, 6));
        _localMac.CopyTo(packet.AsSpan(6, 6));
        packet[12] = 0x08; packet[13] = 0x00;
        // ip
        var ipOff = eth;
        packet[ipOff] = 0x45;
        int total = ip + tcp + payload.Length;
        packet[ipOff + 2] = (byte)(total >> 8);
        packet[ipOff + 3] = (byte)total;
        packet[ipOff + 4] = (byte)(_ipId >> 8);
        packet[ipOff + 5] = (byte)_ipId;
        _ipId++;
        packet[ipOff + 8] = 64;
        packet[ipOff + 9] = 6;
        WriteU32BE(packet, ipOff + 12, _localIp);
        WriteU32BE(packet, ipOff + 16, _remoteIp);
        WriteIpChecksum(packet, ipOff, 20);
        // tcp
        var tOff = eth + ip;
        packet[tOff] = (byte)(_localPort >> 8);
        packet[tOff + 1] = (byte)_localPort;
        packet[tOff + 2] = (byte)(_remotePort >> 8);
        packet[tOff + 3] = (byte)_remotePort;
        WriteU32BE(packet, tOff + 4, seq);
        WriteU32BE(packet, tOff + 8, ack);
        packet[tOff + 12] = 0x50; // data off = 5
        packet[tOff + 13] = flags;
        packet[tOff + 14] = 0x40; packet[tOff + 15] = 0x00; // window 16384
        payload.CopyTo(packet.AsSpan(tOff + 20));
        WriteTcpChecksum(packet, ipOff, tOff, tcp + payload.Length);
        _rndis.WriteEthernetFrame(packet);
    }

    private static void WriteIpChecksum(byte[] buf, int off, int len)
    {
        buf[off + 10] = 0; buf[off + 11] = 0;
        int sum = 0;
        for (int i = 0; i < len; i += 2)
            sum += (buf[off + i] << 8) | (i + 1 < len ? buf[off + i + 1] : 0);
        while ((sum >> 16) != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        sum = ~sum & 0xFFFF;
        buf[off + 10] = (byte)(sum >> 8);
        buf[off + 11] = (byte)sum;
    }

    private static void WriteTcpChecksum(byte[] packet, int ipOff, int tcpOff, int tcpLen)
    {
        packet[tcpOff + 16] = 0; packet[tcpOff + 17] = 0;
        int sum = 0;
        // pseudo header
        for (int i = 12; i < 20; i += 2)
            sum += (packet[ipOff + i] << 8) | packet[ipOff + i + 1];
        sum += 6;
        sum += tcpLen;
        for (int i = 0; i < tcpLen; i += 2)
            sum += (packet[tcpOff + i] << 8) | (i + 1 < tcpLen ? packet[tcpOff + i + 1] : 0);
        while ((sum >> 16) != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        sum = ~sum & 0xFFFF;
        packet[tcpOff + 16] = (byte)(sum >> 8);
        packet[tcpOff + 17] = (byte)sum;
    }

    private static uint ToU32(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
    }

    private static uint ReadU32BE(ReadOnlySpan<byte> b, int o) =>
        (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);

    private static void WriteU32BE(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }

    public void Dispose()
    {
        _closed = true;
        try { _pumpCts?.Cancel(); } catch { }
        try
        {
            if (_established)
            {
                lock (_tcpLock)
                    SendTcp(flags: 0x11, payload: ReadOnlySpan<byte>.Empty, seq: _localSeq, ack: _remoteSeq); // FIN+ACK
            }
        }
        catch { }
        // Never block process exit on the pump thread.
        try { _pump?.Wait(200); } catch { }
        try { _pumpCts?.Dispose(); } catch { }
        _pump = null;
        _pumpCts = null;
    }
}

/// <summary>Relays a userspace TCP connection to a local loopback TCP port for SSH.NET.</summary>
public sealed class LocalTcpRelay : IDisposable
{
    private readonly TcpListener _listener;
    private readonly UserspaceTcpClient _remote;
    private CancellationTokenSource? _cts;
    public int LocalPort { get; }

    public LocalTcpRelay(UserspaceTcpClient remote)
    {
        _remote = remote;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Task.Run(() =>
        {
            using var local = _listener.AcceptTcpClient();
            local.NoDelay = true;
            using var stream = local.GetStream();
            var buf1 = new byte[8192];
            var buf2 = new byte[8192];

            var t1 = Task.Run(() =>
            {
                while (!ct.IsCancellationRequested && _remote.Connected)
                {
                    int n;
                    try { n = stream.Read(buf1, 0, buf1.Length); }
                    catch { break; }
                    if (n <= 0) break;
                    _remote.Send(buf1, 0, n);
                }
            }, ct);

            var t2 = Task.Run(() =>
            {
                while (!ct.IsCancellationRequested && _remote.Connected)
                {
                    var n = _remote.Receive(buf2, 0, buf2.Length, 500);
                    if (n > 0)
                    {
                        try { stream.Write(buf2, 0, n); }
                        catch { break; }
                    }
                    else if (!_remote.Connected) break;
                }
            }, ct);

            try { Task.WaitAny(new[] { t1, t2 }, ct); } catch { }
        }, ct);
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        try { _remote.Dispose(); } catch { }
        try { _cts?.Dispose(); } catch { }
        _cts = null;
    }
}
