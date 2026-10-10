using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ConnectionManager.Core.Ipc;

namespace ConnectionManager.Tray;

/// Round tray icons drawn at start-up, in the settings UI's colours: green = Game Mode on, violet = Data Saver on,
/// half and half = both, grey = waiting, red = both turned off, amber = a problem, grey ring = no service.
internal sealed class TrayIcons : IDisposable
{
    private static readonly Color Game = ColorTranslator.FromHtml("#3ddc84");
    private static readonly Color Saver = ColorTranslator.FromHtml("#b79cff");
    private static readonly Color Warn = ColorTranslator.FromHtml("#f5b94a");
    private static readonly Color Danger = ColorTranslator.FromHtml("#ff7b72");
    private static readonly Color Idle = ColorTranslator.FromHtml("#8693a1");
    private static readonly Color Ink = ColorTranslator.FromHtml("#0e1218"); // the "C", dark for contrast on light fills

    private readonly Dictionary<TrayIconKind, Icon> _icons = new()
    {
        [TrayIconKind.Disconnected] = Draw(Idle, Idle, hollow: true),
        [TrayIconKind.Off] = Draw(Danger, Danger),
        [TrayIconKind.Waiting] = Draw(Idle, Idle),
        [TrayIconKind.GameActive] = Draw(Game, Game),
        [TrayIconKind.SaverActive] = Draw(Saver, Saver),
        [TrayIconKind.Both] = Draw(Game, Saver),
        [TrayIconKind.Problem] = Draw(Warn, Warn),
    };

    public Icon this[TrayIconKind kind] => _icons[kind];

    // Left half one colour, right half the other. Hollow: a ring and a grey "C", for "not connected".
    private static Icon Draw(Color left, Color right, bool hollow = false)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            if (hollow)
            {
                using var ring = new Pen(left, 3);
                g.DrawEllipse(ring, 3.5f, 3.5f, 25, 25);
            }
            else
            {
                using var fillLeft = new SolidBrush(left);
                using var fillRight = new SolidBrush(right);
                g.FillPie(fillLeft, 2, 2, 28, 28, 90, 180);
                g.FillPie(fillRight, 2, 2, 28, 28, 270, 180);
            }
            using var pen = new Pen(hollow ? left : Ink, 3);
            g.DrawArc(pen, 9, 9, 14, 14, 45, 270); // a "C" for Connection Manager
        }
        var handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        foreach (var icon in _icons.Values) icon.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
