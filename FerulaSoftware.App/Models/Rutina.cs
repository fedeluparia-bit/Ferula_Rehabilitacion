using System;

namespace FerulaSoftware.App.Models;

/// <summary>
/// Espejo del modelo Rutina de la API.
/// Se usa únicamente para deserializar respuestas de la nube; no se persiste en SQLite local.
/// </summary>
public class Rutina
{
    public int      Id                   { get; set; }
    public int      PacienteId           { get; set; }
    public DateTime FechaAsignacion      { get; set; }
    public int      ModoActivo           { get; set; }
    public int      RepeticionesObjetivo { get; set; }
    public bool     Completada           { get; set; }

    // ── Propiedades calculadas para la UI ────────────────────────────────────
    public string ModoTexto  => ModoActivo == 0 ? "Resistencia (activo-asistido)" : "Asistido (pasivo continuo)";
    public string FechaTexto => FechaAsignacion.ToString("dd/MM/yyyy");
}
