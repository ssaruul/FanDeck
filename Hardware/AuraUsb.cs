using HidSharp;

namespace FanDeck.Hardware;

/// <summary>Hardware lighting effects the Aura controller runs by itself.</summary>
public enum AuraEffect : byte
{
    Off = 0,
    Static = 1,
    Breathing = 2,
    Strobing = 3,
    ColorCycle = 4,
    Rainbow = 5,
}

/// <summary>A group of LEDs that takes one effect: the board's own LEDs, or one ARGB header.</summary>
public sealed record AuraZone(string Name, byte EffectChannel, int FirstLed, int LedCount);

/// <summary>
/// The ASUS Aura USB controller on the motherboard (0B05:19AF on the ROG STRIX B650E-F).
/// Protocol ported from OpenRGB (Controllers/AsusAuraUSBController): 65-byte HID reports, report ID 0xEC.
/// </summary>
public sealed class AuraUsb : IDisposable
{
    const int Vid = 0x0B05;
    static readonly int[] MainboardPids = [0x19AF, 0x18F3, 0x1939, 0x1AA6, 0x1BED];
    const uint VendorUsagePage = 0xFF72;
    const byte ReportId = 0xEC;

    const byte RequestFirmware = 0x82, FirmwareReply = 0x02;
    const byte RequestConfig = 0xB0, ConfigReply = 0x30;
    const byte SetEffect = 0x35, SetEffectColor = 0x36, Commit = 0x3F;

    readonly HidStream _stream;
    readonly int _outLength;
    readonly int _inLength;
    bool _gen1Sent;

    public string Firmware { get; }
    public int ArgbHeaders { get; }
    public int BoardLeds { get; }
    public int RgbHeaders { get; }
    public IReadOnlyList<AuraZone> Zones { get; }

    AuraUsb(HidDevice device, HidStream stream)
    {
        _stream = stream;
        _stream.ReadTimeout = 500;
        _outLength = device.GetMaxOutputReportLength();
        _inLength = device.GetMaxInputReportLength();

        var fw = Query(RequestFirmware, FirmwareReply)
                 ?? throw new IOException("The Aura controller did not answer the firmware request.");
        Firmware = System.Text.Encoding.ASCII.GetString(fw, 2, 16).TrimEnd('\0', ' ');

        var reply = Query(RequestConfig, ConfigReply)
                    ?? throw new IOException("The Aura controller did not answer the config-table request.");
        var config = reply.AsSpan(4, 60);
        ArgbHeaders = config[0x02];
        BoardLeds = config[0x1B];
        RgbHeaders = config[0x1D] <= BoardLeds ? config[0x1D] : 0;

        // Same channel layout as OpenRGB: the board's LEDs first (if any), then one channel per ARGB header.
        var zones = new List<AuraZone>();
        byte channel = 0;
        int led = 0;
        if (BoardLeds > 0)
        {
            zones.Add(new AuraZone("Board LEDs + RGB header", channel++, led, BoardLeds));
            led += BoardLeds;
        }
        for (int i = 0; i < ArgbHeaders; i++)
            zones.Add(new AuraZone($"ARGB header {i + 1}", channel++, led++, 1));
        Zones = zones;
    }

    public static AuraUsb? TryOpen(out string? error)
    {
        error = "No ASUS Aura motherboard controller found.";
        var candidates = DeviceList.Local.GetHidDevices(Vid)
            .Where(d => MainboardPids.Contains(d.ProductID) && HasUsagePage(d, VendorUsagePage));
        foreach (var device in candidates)
        {
            if (!device.TryOpen(out HidStream stream))
            {
                error = "The Aura controller is busy or access was denied.";
                continue;
            }
            try
            {
                error = null;
                return new AuraUsb(device, stream);
            }
            catch (Exception ex)
            {
                stream.Dispose();
                error = ex.Message;
            }
        }
        return null;
    }

    public static bool UsesColor(AuraEffect effect) =>
        effect is AuraEffect.Static or AuraEffect.Breathing or AuraEffect.Strobing;

    /// <summary>
    /// Sets an effect on one zone (or all when <paramref name="zone"/> is -1) and saves it in the controller,
    /// so it keeps running without FanDeck and across reboots.
    /// </summary>
    public void Apply(AuraEffect effect, Color color, int zone)
    {
        if (!_gen1Sent)
        {
            // OpenRGB sends this once before driving the headers; without it ARGB headers may ignore effects.
            Send(0x52, 0x53, 0x00, 0x01);
            _gen1Sent = true;
        }

        var zones = zone >= 0 && zone < Zones.Count ? [Zones[zone]] : Zones;
        foreach (var z in zones)
        {
            Send(SetEffect, z.EffectChannel, 0x00, 0x00, (byte)effect);

            var buf = NewReport(SetEffectColor);
            ushort mask = (ushort)(((1 << z.LedCount) - 1) << z.FirstLed);
            buf[2] = (byte)(mask >> 8);
            buf[3] = (byte)(mask & 0xFF);
            buf[4] = 0x00;
            for (int i = 0; i < z.LedCount; i++)
            {
                int offset = 5 + 3 * (z.FirstLed + i);
                buf[offset] = color.R;
                buf[offset + 1] = color.G;
                buf[offset + 2] = color.B;
            }
            _stream.Write(buf);
        }

        Send(Commit, 0x55);
    }

    public void Dispose() => _stream.Dispose();

    byte[] NewReport(byte command)
    {
        var buf = new byte[_outLength];
        buf[0] = ReportId;
        buf[1] = command;
        return buf;
    }

    void Send(byte command, params byte[] args)
    {
        var buf = NewReport(command);
        args.CopyTo(buf, 2);
        _stream.Write(buf);
    }

    byte[]? Query(byte command, byte expectedReply)
    {
        _stream.Write(NewReport(command));
        var buf = new byte[_inLength];
        // Other programs may have requests in flight; skip replies that are not ours.
        for (int attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                int n = _stream.Read(buf, 0, buf.Length);
                if (n >= 2 && buf[0] == ReportId && buf[1] == expectedReply)
                    return buf;
            }
            catch (TimeoutException)
            {
                return null;
            }
        }
        return null;
    }

    static bool HasUsagePage(HidDevice device, uint page)
    {
        try
        {
            return device.GetReportDescriptor().DeviceItems
                .Any(item => item.Usages.GetAllValues().Any(usage => usage >> 16 == page));
        }
        catch
        {
            return false;
        }
    }
}
