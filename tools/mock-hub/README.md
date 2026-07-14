# Mock Hub (test aracı)

Kişi 3 UI'sinin `HttpPipelineSource` yolunu, Kişi 2'nin gerçek FastAPI Hub'ı
olmadan test etmek için basit bir sahte sunucu. `HttpPipelineSource.cs`'teki
JSON sözleşmesini üretir.

```powershell
# 1) Sahte hub'ı başlat
node tools/mock-hub/server.js        # http://localhost:5099

# 2) UI'yi hub'a yönlendir
$env:SCADA_HUB_URL = "http://localhost:5099"
ui\ScadaDashboard\bin\Debug\net8.0-windows\ScadaDashboard.exe
```

Üst barda "Veri kaynağı: API HUB" görünür; harita canlı olarak hub'dan beslenir.
`SCADA_HUB_URL` boş bırakılırsa UI dahili simülasyona döner.

Gerçek Hub bu sözleşmeye uyduğunda (nodes/segments) UI değişmeden bağlanır.
