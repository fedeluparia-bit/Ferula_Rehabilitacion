using System;
using System.ComponentModel.DataAnnotations;

namespace Ferula.Api.Models;

public class Rutina
{
    [Key]
    public int Id { get; set; }

    public int PacienteId { get; set; }

    public DateTime FechaAsignacion { get; set; }

    /// <summary>0 = Resistencia, 1 = Asistido (igual que ModoSeleccionado en el cliente).</summary>
    public int ModoActivo { get; set; }

    public int RepeticionesObjetivo { get; set; }

    /// <summary>True una vez que el paciente ejecuta la sesión vinculada.</summary>
    public bool Completada { get; set; }

    // ── Navegación ─────────────────────────────────────────────────────────────
    public Paciente? Paciente { get; set; }
}
