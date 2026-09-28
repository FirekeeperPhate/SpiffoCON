using System.Runtime.InteropServices;
using System.Windows;

namespace SpiffoCON.Services;

/// <summary>
/// The clipboard can be held by another program (a clipboard manager, a remote desktop session):
/// copying then fails, which is not worth an error box.
/// </summary>
public static class SafeClipboard
{
    public static bool SetText(string text)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return true;
            }
            catch (ExternalException)
            {
                Thread.Sleep(50);
            }
        }
        return false;
    }
}
