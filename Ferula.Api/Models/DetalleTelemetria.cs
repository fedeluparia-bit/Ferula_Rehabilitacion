using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ferula.Api.Models;

/// <summary>
/// Muestra de telemetría cruda a 10 Hz.
/// Permite reconstruir las curvas de presión y ángulo de una sesión.
/// </summary>
public class DetalleTelemetria
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int SesionId { get; set; }

    /// <summary>Tiempo relativo al inicio de la sesión en milisegundos.</summary>
    public int Milisegundo { get; set; }

    // Motor 0 (Índice / Medio)
    public int Motor0Angulo  { get; set; }
    public int Motor0Presion { get; set; }

    // Motor 1 (Anular / Meñique)
    public int Motor1Angulo  { get; set; }
    public int Motor1Presion { get; set; }

    // Navegación
    [ForeignKey(nameof(SesionId))]
    public Sesion? Sesion { get; set; }
}
