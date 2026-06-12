using System.Text.Json.Serialization;

namespace FerulaSoftware.App.Models;

/// <summary>
/// Comando enviado desde la app C# al ESP32.
/// Contrato JSON: {"cmd":"start","val":10,"mod":0}
/// Comandos válidos: start, stop, set_mod, estop.
/// "mod" sólo es relevante para "start"; el ESP32 ignora el campo en el resto.
/// </summary>
public sealed record EspCommand(
    [property: JsonPropertyName("cmd")] string Cmd,
    [property: JsonPropertyName("val")] int    Val,
    [property: JsonPropertyName("mod")] int    Mod = 0
);
