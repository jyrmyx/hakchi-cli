using System.Buffers.Binary;
using Hakchi.Usb;
using LibUsbDotNet;
using LibUsbDotNet.Main;

namespace Hakchi.Rndis;

/// <summary>
/// Minimal host-side RNDIS client for the hakchi "classic" USB gadget (04E8:6863).
/// Control path uses CDC encapsulated commands; data path uses bulk endpoints.
/// </summary>
public sealed class RndisDevice : IDisposable
{
    public const int DefaultVid = ClassicIds.RndisVid;
    public const int DefaultPid = ClassicIds.RndisPid;

    private readonly object _controlLock = new();
    private UsbDevice? _device;
    private IUsbDevice? _whole;
    private UsbEndpointReader? _bulkIn;
    private UsbEndpointWriter? _bulkOut;
    private UsbEndpointReader? _interruptIn;
    private int _controlIf;
    private int _dataIf;
    private uint _xid = 1;
    private bool _claimed;

    public byte[] HostMac { get; private set; } = new byte[6];
    public byte[] DeviceMac { get; private set; } = new byte[6];
    public uint MaxTransferSize { get; private set; } = 0x4000;
    public bool IsOpen => _device != null;

    public void Open(int vid = DefaultVid, int pid = DefaultPid)
    {
        LibUsbBootstrap.EnsureInitialized();
        Close();

        var finder = new UsbDeviceFinder(vid, pid);
        _device = UsbDevice.OpenUsbDevice(finder)
            ?? throw new InvalidOperationException($"RNDIS device {vid:X4}:{pid:X4} not found. Is the Classic powered on?");

        _whole = _device as IUsbDevice;
        _whole?.SetConfiguration(1);

        // Interface map from earlier probe:
        // 0 = Wireless/RNDIS control (int EP 0x84)
        // 1 = Data (bulk 0x81 / 0x01)
        _controlIf = 0;
        _dataIf = 1;

        if (_whole != null)
        {
            _whole.ClaimInterface(_controlIf);
            _whole.ClaimInterface(_dataIf);
            _claimed = true;
        }

        _interruptIn = _device.OpenEndpointReader((ReadEndpointID)0x84, 64);
        _bulkIn = _device.OpenEndpointReader((ReadEndpointID)0x81, 8192);
        _bulkOut = _device.OpenEndpointWriter((WriteEndpointID)0x01);

        InitializeSession();
        StartNotificationPump();
    }

    private CancellationTokenSource? _notifyCts;
    private Task? _notifyTask;

    private void StartNotificationPump()
    {
        _notifyCts = new CancellationTokenSource();
        var ct = _notifyCts.Token;
        _notifyTask = Task.Run(() =>
        {
            var buf = new byte[64];
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_interruptIn == null) break;
                    var ec = _interruptIn.Read(buf, 200, out var got);
                    if (ec == ErrorCode.Ok && got >= 1)
                    {
                        // CDC notification: 0x01 = RESPONSE_AVAILABLE
                        // Drain any pending encapsulated responses / keepalives.
                        DrainControlResponses();
                    }
                }
                catch
                {
                    Thread.Sleep(20);
                }
            }
        }, ct);
    }

    private void DrainControlResponses()
    {
        for (var i = 0; i < 8; i++)
        {
            try
            {
                var buf = new byte[1024];
                var setupIn = new UsbSetupPacket(
                    (byte)(UsbCtrlFlags.RequestType_Class | UsbCtrlFlags.Recipient_Interface | UsbCtrlFlags.Direction_In),
                    CdcRequest.GET_ENCAPSULATED_RESPONSE,
                    0,
                    (short)_controlIf,
                    (short)buf.Length);
                if (_device == null) return;
                if (!_device.ControlTransfer(ref setupIn, buf, buf.Length, out var got) || got < 8)
                    return;

                var msgType = Le.ReadU32(buf, 0);
                if (msgType == RndisMsg.KEEPALIVE)
                {
                    var ka = new byte[16];
                    Le.WriteU32(ka, 0, RndisMsg.KEEPALIVE_C);
                    Le.WriteU32(ka, 4, 16);
                    Le.WriteU32(ka, 8, Le.ReadU32(buf, 8));
                    Le.WriteU32(ka, 12, 0);
                    var kaSetup = new UsbSetupPacket(
                        (byte)(UsbCtrlFlags.RequestType_Class | UsbCtrlFlags.Recipient_Interface | UsbCtrlFlags.Direction_Out),
                        CdcRequest.SEND_ENCAPSULATED_COMMAND,
                        0,
                        (short)_controlIf,
                        16);
                    _device.ControlTransfer(ref kaSetup, ka, 16, out _);
                }
                // INDICATE / other: ignore after drain
            }
            catch
            {
                return;
            }
        }
    }

    /// <summary>NDIS counters from the gadget (device-side net_device stats).</summary>
    public uint QueryXmitOk() => QueryU32(0x00020101);
    public uint QueryRcvOk() => QueryU32(0x00020102);
    public uint QueryMediaConnect() => QueryU32(RndisOid.GEN_MEDIA_CONNECT_STATUS);
    public uint QueryLinkSpeed() => QueryU32(RndisOid.GEN_LINK_SPEED);

    private void InitializeSession()
    {
        // INIT — use ~1 eth frame max transfer like the gadget reports (0x62c)
        var init = new byte[24];
        Le.WriteU32(init, 0, RndisMsg.INIT);
        Le.WriteU32(init, 4, 24);
        Le.WriteU32(init, 12, 1); // major
        Le.WriteU32(init, 16, 0); // minor
        Le.WriteU32(init, 20, 1580);

        var initC = Command(init);
        var status = Le.ReadU32(initC, 12);
        if (status != 0)
            throw new InvalidOperationException($"RNDIS INIT failed, status=0x{status:X8}");
        // INIT_C layout: flags@24 medium@28 max_packets@32 max_transfer@36
        if (initC.Length >= 40)
        {
            MaxTransferSize = Le.ReadU32(initC, 36);
        }
        if (MaxTransferSize == 0)
            MaxTransferSize = 1580;

        DeviceMac = QueryMac(RndisOid.IEEE_802_3_PERMANENT_ADDRESS);
        HostMac = (byte[])DeviceMac.Clone();
        HostMac[0] = (byte)((HostMac[0] | 0x02) & 0xFE);
        HostMac[5] ^= 0x5A;

        // Windows-like: query a few OIDs before enabling the filter
        _ = QueryU32(RndisOid.GEN_MAXIMUM_FRAME_SIZE);
        _ = QueryU32(RndisOid.GEN_LINK_SPEED);
        _ = QueryU32(RndisOid.GEN_MEDIA_CONNECT_STATUS);

        var set = new byte[32];
        Le.WriteU32(set, 0, RndisMsg.SET);
        Le.WriteU32(set, 4, 32);
        Le.WriteU32(set, 12, RndisOid.GEN_CURRENT_PACKET_FILTER);
        Le.WriteU32(set, 16, 4);
        Le.WriteU32(set, 20, 20);
        Le.WriteU32(set, 28, RndisFilter.Default);
        var setC = Command(set);
        status = Le.ReadU32(setC, 12);
        if (status != 0)
            throw new InvalidOperationException($"RNDIS SET packet filter failed, status=0x{status:X8}");
    }

    public uint QueryU32(uint oid)
    {
        const int pad = 4;
        var q = new byte[28 + pad];
        Le.WriteU32(q, 0, RndisMsg.QUERY);
        Le.WriteU32(q, 4, (uint)q.Length);
        Le.WriteU32(q, 12, oid);
        Le.WriteU32(q, 16, pad);
        Le.WriteU32(q, 20, 20);
        var resp = Command(q);
        var status = Le.ReadU32(resp, 12);
        if (status != 0)
            throw new InvalidOperationException($"RNDIS QUERY 0x{oid:X8} status=0x{status:X8}");
        var infoLen = Le.ReadU32(resp, 16);
        var infoOff = Le.ReadU32(resp, 20);
        var dataStart = 8 + (int)infoOff;
        if (infoLen < 4 || dataStart + 4 > resp.Length)
            throw new InvalidOperationException($"RNDIS QUERY 0x{oid:X8} bad payload");
        return Le.ReadU32(resp, dataStart);
    }

    private byte[] QueryMac(uint oid)
    {
        // ActiveSync quirk: pad query payload
        const int pad = 48;
        var q = new byte[28 + pad];
        Le.WriteU32(q, 0, RndisMsg.QUERY);
        Le.WriteU32(q, 4, (uint)q.Length);
        Le.WriteU32(q, 12, oid);
        Le.WriteU32(q, 16, pad);
        Le.WriteU32(q, 20, 20); // offset

        var resp = Command(q);
        var status = Le.ReadU32(resp, 12);
        if (status != 0)
            throw new InvalidOperationException($"RNDIS QUERY oid 0x{oid:X8} failed status=0x{status:X8}");

        var infoLen = Le.ReadU32(resp, 16);
        var infoOff = Le.ReadU32(resp, 20);
        // offset is from the byte after RequestID start? Linux: (unsigned char *)&get_c->request_id + off
        // request_id is at offset 8 in message
        var dataStart = 8 + (int)infoOff;
        if (dataStart < 0 || dataStart + 6 > resp.Length || infoLen < 6)
            throw new InvalidOperationException($"RNDIS QUERY MAC invalid off={infoOff} len={infoLen} buflen={resp.Length}");

        var mac = new byte[6];
        Buffer.BlockCopy(resp, dataStart, mac, 0, 6);
        return mac;
    }

    private byte[] Command(byte[] message)
    {
        lock (_controlLock)
        {
            if (_device == null)
                throw new ObjectDisposedException(nameof(RndisDevice));

            var reqType = Le.ReadU32(message, 0);
            var xid = _xid++;
            if (_xid == 0) _xid = 1;
            Le.WriteU32(message, 8, xid);

            var setupOut = new UsbSetupPacket(
                (byte)(UsbCtrlFlags.RequestType_Class | UsbCtrlFlags.Recipient_Interface | UsbCtrlFlags.Direction_Out),
                CdcRequest.SEND_ENCAPSULATED_COMMAND,
                0,
                (short)_controlIf,
                (short)message.Length);

            if (!_device.ControlTransfer(ref setupOut, message, message.Length, out var sent) || sent != message.Length)
                throw new InvalidOperationException($"RNDIS control OUT failed (sent={sent})");

            var expected = reqType | 0x80000000;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                // Poll interrupt (RESPONSE_AVAILABLE) then read encapsulated response.
                try
                {
                    var note = new byte[64];
                    _interruptIn?.Read(note, 30, out _);
                }
                catch { /* optional */ }

                var buf = new byte[1024];
                var setupIn = new UsbSetupPacket(
                    (byte)(UsbCtrlFlags.RequestType_Class | UsbCtrlFlags.Recipient_Interface | UsbCtrlFlags.Direction_In),
                    CdcRequest.GET_ENCAPSULATED_RESPONSE,
                    0,
                    (short)_controlIf,
                    (short)buf.Length);

                if (!_device.ControlTransfer(ref setupIn, buf, buf.Length, out var got) || got < 8)
                {
                    Thread.Sleep(15);
                    continue;
                }

                var msgType = Le.ReadU32(buf, 0);
                var msgLen = (int)Le.ReadU32(buf, 4);
                if (msgLen <= 0 || msgLen > got)
                    msgLen = got;

                if (msgType == RndisMsg.KEEPALIVE)
                {
                    var ka = new byte[16];
                    Le.WriteU32(ka, 0, RndisMsg.KEEPALIVE_C);
                    Le.WriteU32(ka, 4, 16);
                    Le.WriteU32(ka, 8, Le.ReadU32(buf, 8));
                    Le.WriteU32(ka, 12, 0);
                    var kaSetup = new UsbSetupPacket(
                        (byte)(UsbCtrlFlags.RequestType_Class | UsbCtrlFlags.Recipient_Interface | UsbCtrlFlags.Direction_Out),
                        CdcRequest.SEND_ENCAPSULATED_COMMAND,
                        0,
                        (short)_controlIf,
                        16);
                    _device.ControlTransfer(ref kaSetup, ka, 16, out _);
                    continue;
                }

                // MEDIA_CONNECT etc. — keep reading until our completion arrives
                if (msgType == RndisMsg.INDICATE)
                    continue;

                var rxid = Le.ReadU32(buf, 8);
                // INIT/HALT completions: match type; others also match xid
                var typeOk = msgType == expected;
                var xidOk = reqType is RndisMsg.INIT or RndisMsg.HALT || rxid == xid;
                if (typeOk && xidOk)
                {
                    var result = new byte[msgLen];
                    Buffer.BlockCopy(buf, 0, result, 0, msgLen);
                    return result;
                }

                Thread.Sleep(15);
            }

            throw new TimeoutException($"RNDIS control response timeout (req=0x{reqType:X8} xid={xid})");
        }
    }

    /// <summary>Write one Ethernet frame (no RNDIS header).</summary>
    public void WriteEthernetFrame(ReadOnlySpan<byte> frame)
    {
        if (_bulkOut == null) throw new ObjectDisposedException(nameof(RndisDevice));
        // RNDIS_PACKET header is 44 bytes typically; Linux uses 8+data_offset where data_offset=sizeof(hdr)-8
        // struct rndis_data_hdr is 44 bytes on Linux? Actually:
        // msg_type, msg_len, data_offset, data_len, oob..., = 44 bytes total common layout
        const int hdrSize = 44;
        var packet = new byte[hdrSize + frame.Length];
        Le.WriteU32(packet, 0, RndisMsg.PACKET);
        Le.WriteU32(packet, 4, (uint)packet.Length);
        Le.WriteU32(packet, 8, (uint)(hdrSize - 8)); // data_offset from end of RequestID-less header start... from byte 8
        Le.WriteU32(packet, 12, (uint)frame.Length);
        frame.CopyTo(packet.AsSpan(hdrSize));

        var pos = 0;
        while (pos < packet.Length)
        {
            var ec = _bulkOut.Write(packet, pos, packet.Length - pos, 2000, out var wrote);
            if (ec != ErrorCode.Ok && wrote <= 0)
                throw new IOException($"RNDIS bulk OUT error: {ec}");
            pos += wrote;
        }
    }

    /// <summary>Try to read one Ethernet frame. Returns false on timeout.</summary>
    public bool TryReadEthernetFrame(byte[] dest, out int frameLength, int timeoutMs = 100)
    {
        frameLength = 0;
        if (_bulkIn == null) throw new ObjectDisposedException(nameof(RndisDevice));

        var buf = new byte[Math.Max(2048, (int)MaxTransferSize)];
        var ec = _bulkIn.Read(buf, timeoutMs, out var got);
        if (ec != ErrorCode.Ok || got < 16)
            return false;

        var msgType = Le.ReadU32(buf, 0);
        if (msgType != RndisMsg.PACKET)
            return false;

        var dataOffset = Le.ReadU32(buf, 8);
        var dataLen = (int)Le.ReadU32(buf, 12);
        var start = 8 + (int)dataOffset;
        if (start < 0 || start + dataLen > got || dataLen <= 0 || dataLen > dest.Length)
            return false;

        Buffer.BlockCopy(buf, start, dest, 0, dataLen);
        frameLength = dataLen;
        return true;
    }

    public void Close()
    {
        // Tear down readers first so any blocking USB read unblocks quickly.
        try { _notifyCts?.Cancel(); } catch { }

        try { _bulkIn?.Dispose(); } catch { }
        try { _bulkOut?.Dispose(); } catch { }
        try { _interruptIn?.Dispose(); } catch { }
        _bulkIn = null;
        _bulkOut = null;
        _interruptIn = null;

        try { _notifyTask?.Wait(150); } catch { }
        try { _notifyCts?.Dispose(); } catch { }
        _notifyTask = null;
        _notifyCts = null;

        // Skip RNDIS HALT — it often stalls on macOS during process exit.

        if (_claimed && _whole != null)
        {
            try { _whole.ReleaseInterface(_dataIf); } catch { }
            try { _whole.ReleaseInterface(_controlIf); } catch { }
            _claimed = false;
        }

        try { _device?.Close(); } catch { }
        _device = null;
        _whole = null;
    }

    public void Dispose() => Close();

    public static string MacToString(byte[] mac) =>
        string.Join(":", mac.Select(b => b.ToString("x2")));
}
