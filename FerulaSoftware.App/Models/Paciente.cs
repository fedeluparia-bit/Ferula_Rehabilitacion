using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace FerulaSoftware.App.Models;

/// <summary>
/// Representa un paciente registrado en el sistema.
/// Relación: un Paciente tiene N Sesiones (cascade delete).
/// </summary>
public class Paciente
{
    [Key]
    public int Id { get; set; }

    [Required]
    public string Nombre { get; set; } = string.Empty;

    [Required]
    public string Apellido { get; set; } = string.Empty;

    /// <summary>Fecha en que el paciente inició el programa de rehabilitación.</summary>
    public DateTime FechaInicio { get; set; }

    // ── Navegación ────────────────────────────────────────────────────────────
    public ICollection<Sesion> Sesiones { get; set; } = [];
}
