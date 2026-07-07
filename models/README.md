# Model Dosyalari

Bu klasor, egitilmis model dosyalarini icerir.

## Dosyalar

| Dosya | Aciklama |
|-------|----------|
| `random_forest_rul.pkl` | Random Forest regresyon modeli (RUL tahmini) |
| `random_forest_anomaly.pkl` | Random Forest siniflandirma modeli (anomali tespiti) |
| `scaler.pkl` | StandardScaler (ozellik ölceklendirme) |
| `random_forest_rul.onnx` | ONNX formatinda model (C# entegrasyonu) |

## C# Kullanimi (ONNX)

```csharp
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Collections.Generic;

// Model yukle
var session = new InferenceSession("models/random_forest_rul.onnx");

// Input hazirla (88 feature)
var inputs = new Dictionary<string, Tensor>
{
    { "float_input", new DenseTensor<float>(features) }
};

// Tahmin yap
var results = session.Run(inputs);
float rulPrediction = results[0].AsTensor<float>().First();

// Health score hesapla
float healthScore = Math.Max(0, Math.Min(100, (rulPrediction / 130) * 100));
```

## Python Kullanimi

```python
from src.model_predictor import Predictor

predictor = Predictor()
result = predictor.predict(sensor_readings)
```
