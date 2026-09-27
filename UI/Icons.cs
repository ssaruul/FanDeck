using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using FanDeck.Cooling;

namespace FanDeck.UI;

/// <summary>Draws the fan icon in the current profile's colour, so the tray shows the profile at a glance.</summary>
static class Icons
{
    /// <summary>Grey: FanDeck has stepped aside and the BIOS is running the fans.</summary>
    public static readonly Color GameSafe = Color.FromArgb(0x6B, 0x72, 0x80);

    public static Color ColorFor(Profile profile) => profile switch
    {
        Profile.Silent => Color.FromArgb(0x3B, 0x82, 0xF6),
        Profile.Balanced => Color.FromArgb(0x16, 0xA3, 0x4A),
        Profile.Performance => Color.FromArgb(0xEA, 0x8A, 0x0C),
        Profile.Max => Color.FromArgb(0xDC, 0x26, 0x26),
        _ => Color.FromArgb(0x93, 0x4F, 0xD8),
    };

    /// <summary>The caller owns <paramref name="handle"/> and frees it with <see cref="Destroy"/>.</summary>
    public static Icon Fan(Color color, out IntPtr handle)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var blade = new SolidBrush(color);
            for (int i = 0; i < 4; i++)
            {
                var state = g.Save();
                g.TranslateTransform(16, 16);
                g.RotateTransform(i * 90 + 25);
                g.FillEllipse(blade, -5, -16, 10, 16);
                g.Restore(state);
            }
            g.FillEllipse(blade, 11, 11, 10, 10);
            using var hub = new SolidBrush(Color.White);
            g.FillEllipse(hub, 13.5f, 13.5f, 5, 5);
        }
        handle = bmp.GetHicon();
        return Icon.FromHandle(handle);
    }

    public static void Destroy(IntPtr handle) => DestroyIcon(handle);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}
