namespace FerulaSoftware.App.Models;

/// <summary>
/// DTO espejo del modelo Usuario de la API.
/// No se persiste en SQLite local — solo se usa para resultados de búsqueda
/// y para enviar/recibir invitaciones de rutinas.
/// </summary>
public class Usuario
{
    public int    Id           { get; set; }
    public string Nombre       { get; set; } = string.Empty;
    public string Apellido     { get; set; } = string.Empty;
    public bool   EsTerapeuta  { get; set; }

    public string NombreCompleto => $"{Nombre} {Apellido}".Trim();
}
