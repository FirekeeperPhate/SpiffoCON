using Forms = System.Windows.Forms;

namespace SpiffoCON.Services;

/// <summary>
/// Desktop notifications through a tray icon: Windows 10/11 show its balloon tips as toasts.
/// Clicking one brings SpiffoCON back.
/// </summary>
public sealed class TrayNotifier : IDisposable
{
    readonly Forms.NotifyIcon _icon;

    public TrayNotifier(Action activate)
    {
        _icon = new Forms.NotifyIcon
        {
            Text = "SpiffoCON",
            Icon = Environment.ProcessPath is { } exe ? System.Drawing.Icon.ExtractAssociatedIcon(exe) : System.Drawing.SystemIcons.Application,
            Visible = true,
        };
        _icon.BalloonTipClicked += (_, _) => activate();
        _icon.DoubleClick += (_, _) => activate();
    }

    /// <summary>Windows ignores the timeout since Vista (it keeps its own); an empty text is refused.</summary>
    public void Show(string title, string text) =>
        _icon.ShowBalloonTip(5000, Trim(title, 63), Trim(string.IsNullOrWhiteSpace(text) ? "(no text)" : text, 255), Forms.ToolTipIcon.None);

    static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
