using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace ScadaDashboard;

/// <summary>
/// Pencere cilası: Win11 koyu başlık çubuğu + yuvarlak köşeler (DWM) ve
/// açılışta yumuşak belirme. Eski Windows sürümlerinde DWM çağrıları
/// sessizce yok sayılır (öznitelik desteklenmiyorsa hata dönmez/önemsenmez).
/// </summary>
public static class WindowFx
{
    private const int DwmaUseImmersiveDarkMode = 20; // koyu başlık çubuğu
    private const int DwmaWindowCornerPreference = 33; // 2 = yuvarlak

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Apply(Window w)
    {
        w.SourceInitialized += (_, _) =>
        {
            try
            {
                var h = new WindowInteropHelper(w).Handle;
                int on = 1;
                DwmSetWindowAttribute(h, DwmaUseImmersiveDarkMode, ref on, sizeof(int));
                int round = 2;
                DwmSetWindowAttribute(h, DwmaWindowCornerPreference, ref round, sizeof(int));
            }
            catch { /* eski Windows: cila yok, islev ayni */ }
        };

        // Acilista 200ms yumusak belirme.
        w.Opacity = 0;
        w.Loaded += (_, _) => w.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
    }
}
