using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Ferula.Api.Models;

public class Usuario
{
    [Key]
    public int Id { get; set; }

    [Required]
    public string Nombre { get; set; } = string.Empty;

    [Required]
    public string Apellido { get; set; } = string.Empty;

    /// <summary>True para fisioterapeutas verificados; false para pacientes regulares.</summary>
    public bool EsTerapeuta { get; set; }

    [MaxLength(200)]
    public string? Email { get; set; }

    [MaxLength(200)]
    public string? Password { get; set; }

    // Navegación
    public ICollection<InvitacionRutina> InvitacionesEnviadas   { get; set; } = [];
    public ICollection<InvitacionRutina> InvitacionesRecibidas  { get; set; } = [];
}
