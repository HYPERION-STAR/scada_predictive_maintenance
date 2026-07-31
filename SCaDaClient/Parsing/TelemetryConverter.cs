using System.Text.Json;
using System.Text.Json.Serialization;
using SCaDaClient.Models.Telemetry;

namespace SCaDaClient.Parsing;

/// <summary>
/// Telemetri converter — entity_type'a göre doğru DTO'yu seçer.
/// Sonsuz döngüyü önlemek için kendi kendini iç options'tan kaldırır.
/// </summary>
public sealed class TelemetryConverter : JsonConverter<TelemetryBase>
{
    public override TelemetryBase Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        // Nesneyi bir kez oku, entity_type'ı bul, doğru tipe yeniden çöz
        using var doc = JsonDocument.ParseValue(ref reader);
        var el = doc.RootElement;

        var type = el.TryGetProperty("entity_type", out var t)
            ? t.GetString()
            : null;

        // Converter'sız options — sonsuz döngüyü önler
        var inner = new JsonSerializerOptions(options);
        for (int i = inner.Converters.Count - 1; i >= 0; i--)
            if (inner.Converters[i] is TelemetryConverter)
                inner.Converters.RemoveAt(i);

        return type switch
        {
            "compressor" => el.Deserialize<CompressorTelemetry>(inner)!,
            "segment" => el.TryGetProperty("s_inlet_pressure_bar", out _)
                ? el.Deserialize<SegmentTelemetry>(inner)!
                : el.Deserialize<PointTelemetry>(inner)!,
            "ugs" or "border_entry" or "fsru" or "lng_terminal"
                => el.Deserialize<PointTelemetry>(inner)!,
            "oil_pump" => el.Deserialize<OilPumpTelemetry>(inner)!,
            "oil_storage" => el.Deserialize<OilStorageTelemetry>(inner)!,
            _ => el.Deserialize<GenericTelemetry>(inner)!
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        TelemetryBase value,
        JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}
