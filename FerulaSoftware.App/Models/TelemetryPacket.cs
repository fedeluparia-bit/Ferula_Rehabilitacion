using System.Text.Json.Serialization;

namespace FerulaSoftware.App.Models;

/// <summary>
/// Paquete de telemetría enviado por el ESP32 a 10 Hz.
/// Contrato JSON: {"t":"tel","st":1,"mod":0,"rep":5,"m":[{"id":0,"ang":45,"prs":2048},...]}
/// </summary>
public sealed record TelemetryPacket(
    [property: JsonPropertyName("t")]   string   Type,
    [property: JsonPropertyName("st")]  int      Estado,
    [property: JsonPropertyName("mod")] int      Modo,
    [property: JsonPropertyName("rep")] int      Repeticiones,
    [property: JsonPropertyName("m")]   MotorData[] Motores
);

/// <summary>
/// Datos de un motor individual dentro del paquete de telemetría.
/// "prs" = lectura ADC cruda del FSR402 (0–4095).
/// </summary>
public sealed record MotorData(
    [property: JsonPropertyName("id")]  int Id,
    [property: JsonPropertyName("ang")] int Angulo,
    [property: JsonPropertyName("prs")] int Presion
);
