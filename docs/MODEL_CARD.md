# Model Card - SCADA Kestirimci Bakim

## Model Bilgisi

| Alan | Deger |
|------|-------|
| **Model Adi** | SCADA Predictive Maintenance Ensemble |
| **Model Turu** | Random Forest (Regresyon + Siniflandirma) |
| **Gelistiren** | Kisi 1 - AI & Veri Bilimi |
| **Egitim Verisi** | NASA C-MAPSS FD001 (Sentetik) |
| **Tarih** | 2026-07-06 |

## Kullanim Amaci

Bu model, SCADA sistemlerinde kalan有用 omru (RUL - Remaining Useful Life) tahmin eder ve anomali tespiti yapar.

- **Regresyon Modeli**: Makinenin arizalanmasina kalan dongu sayisini tahmin eder
- **Siniflandirma Modeli**: Makinenin saglikli/bozulmus durumunu siniflandirir

## Performans Metrikleri

### Regresyon (RUL Tahmini)
- **MAE (Mean Absolute Error)**: 32.65 dongu
- **R-squared**: 0.6700

### Siniflandirma (Anomali Tespiti)
- **Accuracy**: %98
- **Degraded F1-Score**: 0.94
- **Degraded Recall**: 0.97

## Kullanim

```python
from src.model_predictor import Predictor

predictor = Predictor()

# Tek tahmin
result = predictor.predict({
    'sensor_1': 250.0, 'sensor_2': 1000.0,
    # ... 21 sensör
})
print(result)
# {'rul': 95.51, 'health_score': 73.47, 'anomaly': False}

# Cikti formati:
# - rul: Kalan useful life (dongu)
# - health_score: 0-100 araliginda saglik skoru
# - anomaly: Anomali bayragi
# - is_degraded: Bozulmus mu?
# - anomaly_probability: Anomali olasiligi
```

## Sinirlamalar

- Sentetik veri ile egitildi, gerçek fabrika verisine uyarlama gerekebilir
- Ilk tahminler icin gecmis veri (history_buffer) kullanilmali
- Model, egitim verisinde gormedigi tip arizalari tespit etmeyebilir
- GPU olmadan LSTM modeli egitilemedi (Random Forest kullanildi)

## Model Dosyalari

| Dosya | Boyut | Aciklama |
|-------|-------|----------|
| `models/random_forest_rul.pkl` | ~80 MB | RUL regresyon modeli |
| `models/random_forest_anomaly.pkl` | ~15 MB | Anomali siniflandirma modeli |
| `models/scaler.pkl` | ~3 KB | StandardScaler |
| `models/random_forest_rul.onnx` | ~45 MB | ONNX format (C# uyumlu) |

## Takim Entegrasyonu

### Kisi 2 (MQTT/Veri Akisi)
- Input topic: `factory/motor/sensor_data`
- Format: JSON, 21 sensör degeri

### Kisi 3 (C# Dashboard)
- Input: ONNX model + scaler
- Output: `{rul, health_score, anomaly}`

### Kisi 4 (SQL Server DB)
- Tablo: `TahminSonuclari`
- Kolonlar: `engine_id, timestamp, rul, health_score, anomaly_flag`
