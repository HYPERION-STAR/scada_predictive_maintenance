using System.Windows;
using ScadaDashboard.Services;

namespace ScadaDashboard;

/// <summary>
/// Ayarlar: canlı + AI sunucu adresleri, tema, alarm eşiği.
/// </summary>
public partial class SettingsWindow : Window
{
    public AppSettings Result { get; private set; }
    private readonly bool _origLight;

    public SettingsWindow(AppSettings current)
    {
        InitializeComponent();
        WindowFx.Apply(this);
        Result = current;
        _origLight = current.LightTheme;
        LightThemeBox.IsChecked = current.LightTheme;
        LiveUrlBox.Text = current.LiveUrl;
        AiRespondUrlBox.Text = current.AiRespondUrl;
        DbConnBox.Text = current.DatabaseConnectionString;
        ThresholdSlider.Value = current.CriticalHealthThreshold;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        string liveUrl = LiveUrlBox.Text.Trim();
        string aiUrl = AiRespondUrlBox.Text.Trim();
        string dbConn = DbConnBox.Text.Trim();

        if (!Uri.TryCreate(liveUrl, UriKind.Absolute, out _))
        {
            ErrorText.Text = "Geçerli bir canlı adres girin (ör. http://host:8000/api/scada/live_data).";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        if (!Uri.TryCreate(aiUrl, UriKind.Absolute, out _))
        {
            ErrorText.Text = "AI taban adresi zorunlu (ör. http://host:9000). /ai_respond?since=N kullanılır.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        Result = new AppSettings
        {
            LiveUrl = liveUrl,
            AiRespondUrl = aiUrl,
            DatabaseConnectionString = dbConn,
            LightTheme = LightThemeBox.IsChecked == true,
            PollIntervalSeconds = Result.PollIntervalSeconds,
            TimeoutSeconds = Result.TimeoutSeconds,
            HealthDisplayMode = Result.HealthDisplayMode,
            HealthOutlineAggregate = Result.HealthOutlineAggregate,
            CriticalHealthThreshold = ThresholdSlider.Value,
            PieSeparatorColorHex = Result.PieSeparatorColorHex,
        };
        Result.Save();
        DialogResult = true;
    }

    private void Theme_Changed(object sender, RoutedEventArgs e)
        => ThemeManager.Apply(LightThemeBox.IsChecked == true ? ScadaDashboard.ThemeMode.Acik : ScadaDashboard.ThemeMode.Koyu);

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        ThemeManager.Apply(_origLight ? ScadaDashboard.ThemeMode.Acik : ScadaDashboard.ThemeMode.Koyu);
        DialogResult = false;
    }
}
