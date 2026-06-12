using System;
using System.ComponentModel.DataAnnotations;

namespace Ferula.Api.Models;

public class InvitacionRutina
{
    [Key]
    public int Id { get; set; }

    public int RemitenteId    { get; set; }
    public int DestinatarioId { get; set; }

    /// <summary>Nombre completo desnormalizado para mostrar en la UI sin JOIN.</summary>
    public string RemitenteNombre      { get; set; } = string.Empty;

    /// <summary>Indica si el remitente tiene rol de terapeuta al momento de enviar.</summary>
    public bool RemitenteEsTerapeuta   { get; set; }

    /// <summary>0 = Resistencia, 1 = Asistido.</summary>
    public int ModoActivo              { get; set; }

    public int RepeticionesObjetivo    { get; set; }

    /// <summary>"Pendiente" | "Aceptada" | "Rechazada"</summary>
    public string Estado               { get; set; } = "Pendiente";

    public DateTime FechaEnvio         { get; set; }

    // Navegación
    public Usuario? Remitente    { get; set; }
    public Usuario? Destinatario { get; set; }
}
