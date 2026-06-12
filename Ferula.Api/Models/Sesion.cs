using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ferula.Api.Models;

/// <summary>Sesión de rehabilitación guardada por el cliente Avalonia y sincronizada a la nube.</summary>
public class Sesion
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int PacienteId { get; set; }

    /// <summary>Fecha y hora de inicio de la sesión (UTC).</summary>
    public DateTime FechaHora { get; set; }

    /// <summary>0 = Resistencia, 1 = Asistido.</summary>
    public int ModoActivo { get; set; }

    public int RepeticionesObjetivo { get; set; }
    public int RepeticionesHechas   { get; set; }

    /// <summary>Pico de presión FSR402 (ADC 0–4095) durante la sesión.</summary>
    public int PresionMaxima { get; set; }

    // Navegación
    [ForeignKey(nameof(PacienteId))]
    public Paciente? Paciente { get; set; }

    public ICollection<DetalleTelemetria> Detalles { get; set; } = [];
}
