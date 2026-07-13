# SCADA Dashboard — Kişi 3: C# Masaüstü UI

Endüstriyel kestirimci bakım paneli (WPF, .NET 8). Makinelerin sağlık durumunu
%0-100 barlarla ve canlı sensör grafikleriyle gösterir; risk eşiği aşıldığında
kırmızı alarm verir ve e-posta/SMS uyarısını simüle eder.

> **Durum:** Veri kaynağı şu an **SİMÜLASYON**. Kişi 2'nin MQTT akışı ve Kişi 1'in
> canlı modeli hazır olduğunda tek bir sınıf (`IDataSource`) değiştirilerek gerçek
> veriye bağlanır — arayüzün geri kalanı değişmez.

## Çalıştırma

```powershell
# .NET 8 SDK gerekli (winget install Microsoft.DotNet.SDK.8)
cd ui
dotnet run --project ScadaDashboard
```

Visual Studio ile: `ui/ScadaDashboard.sln` dosyasını açıp F5.

## Özellikler

| Özellik | Açıklama |
|---------|----------|
| Sağlık barı | Her makine için %0-100 sağlık skoru (model `health_score`) |
| Canlı grafikler | Sıcaklık / basınç / titreşim trendi (harici NuGet yok, `MiniChart`) |
| RUL | Kalan faydalı ömür (döngü) — model `rul` çıktısı |
| Anomali | Anomali olasılığı + eşik altında **KRİTİK** durum |
| Alarm | Kritik geçişte kırmızı uyarı + e-posta/SMS simülasyonu (`logs/alerts.log`) |
| Bakım Yap | Makineyi elle sağlıklı duruma döndürme (demo/etkileşim) |

## Mimari

```
ScadaDashboard/
├── Models/            MachineDescriptor, MachineSnapshot (model çıktı formatı)
├── Services/
│   ├── IDataSource        veri kaynağı soyutlaması (bugün simülasyon, yarın MQTT)
│   ├── SimulatorDataSource  bozulan sensör verisi üretir, model mantığını taklit eder
│   └── AlertService         e-posta/SMS simülasyonu (log dosyası)
├── ViewModels/        MainViewModel, MachineViewModel (INotifyPropertyChanged, MVVM)
├── Controls/          MiniChart (bağımsız canlı çizgi grafiği)
├── MainWindow.xaml    dashboard düzeni
└── App.xaml           koyu endüstriyel tema + stiller
```

### Kişi 1'in modeliyle uyum

`MachineSnapshot`, [`src/model_predictor.py`](../src/model_predictor.py) çıktısıyla
birebir aynı alanları taşır: `rul`, `health_score`, `anomaly`, `is_degraded`,
`anomaly_probability`. `SimulatorDataSource` aynı eşikleri kullanır
(`MAX_RUL = 130`, `health < 20 → anomali`, `RUL < 30 → degraded`).

## Gerçek veriye geçiş (ileride)

1. `IDataSource`'u uygulayan bir `MqttDataSource` yaz (MQTTnet ile
   `factory/motor/sensor_data` topic'ine abone ol).
2. Kişi 1'in `random_forest_rul.onnx` modelini `Microsoft.ML.OnnxRuntime` ile yükle
   ve gelen sensör verisinden `rul` / `health` üret.
3. `MainWindow.xaml.cs` içinde `new SimulatorDataSource()` yerine
   `new MqttDataSource(...)` koy. Başka değişiklik gerekmez.
