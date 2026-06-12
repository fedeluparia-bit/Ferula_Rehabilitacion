using System;

namespace FerulaSoftware.App.Models;

/// <summary>
/// DTO espejo de la entidad InvitacionRutina de la API.
/// Representa una invitación pendiente en la bandeja de entrada.
/// </summary>
public class InvitacionRutina
{
    public int      Id                    { get; set; }
    public int      RemitenteId           { get; set; }
    public int      DestinatarioId        { get; set; }
    public string   RemitenteNombre       { get; set; } = string.Empty;
    public bool     RemitenteEsTerapeuta  { get; set; }
    public int      ModoActivo            { get; set; }
    public int      RepeticionesObjetivo  { get; set; }
    public string   Estado                { get; set; } = "Pendiente";
    public DateTime FechaEnvio            { get; set; }

    // ── Propiedades calculadas para la UI ──────────────────────────────────
    public string ModoTexto  => ModoActivo == 0 ? "Resistencia (activo-asistido)" : "Asistido (pasivo continuo)";
    public string FechaTexto => FechaEnvio.ToString("dd/MM/yyyy HH:mm");
}
