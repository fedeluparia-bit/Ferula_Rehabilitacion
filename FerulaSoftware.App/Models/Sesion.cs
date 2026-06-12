using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FerulaSoftware.App.Models;

/// <summary>
/// Representa una sesión de rehabilitación completada o en curso.
/// Relación: pertenece a un Paciente; tiene N DetalleTelemetria (cascade delete).
/// </summary>
public class Sesion
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int PacienteId { get; set; }

    /// <summary>Fecha y hora de inicio de la sesión (UTC).</summary>
    public DateTime FechaHora { get; set; }

    /// <summary>Modo del ESP32 durante la sesión: 0 = Resistencia, 1 = Asistido.</summary>
    public int ModoActivo { get; set; }

    /// <summary>Repeticiones configuradas antes de iniciar.</summary>
    public int RepeticionesObjetivo { get; set; }

    /// <summary>Repeticiones efectivamente completadas.</summary>
    public int RepeticionesHechas { get; set; }

    /// <summary>Pico de presión FSR402 (ADC 0–4095) alcanzado en la sesión.</summary>
    public int PresionMaxima { get; set; }

    // ── Navegación ────────────────────────────────────────────────────────────

    [ForeignKey(nameof(PacienteId))]
    public Paciente Paciente { get; set; } = null!;

    public ICollection<DetalleTelemetria> Detalles { get; set; } = [];
}
