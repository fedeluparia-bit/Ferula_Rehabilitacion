using FerulaSoftware.App.Models;
using Microsoft.EntityFrameworkCore;

namespace FerulaSoftware.App.Data;

/// <summary>
/// Contexto de EF Core para la base de datos local SQLite.
/// El archivo físico <c>ferula_local.db</c> se crea automáticamente en el
/// directorio de trabajo de la app la primera vez que se llama a
/// <c>EnsureCreated()</c> (ver App.axaml.cs).
///
/// Relaciones:
///   Paciente  1──N  Sesion              (cascade delete)
///   Sesion    1──N  DetalleTelemetria   (cascade delete)
/// </summary>
public class AppDbContext : DbContext
{
    // ── DbSets ────────────────────────────────────────────────────────────────

    public DbSet<Paciente>          Pacientes          { get; set; }
    public DbSet<Sesion>            Sesiones           { get; set; }
    public DbSet<DetalleTelemetria> DetallesTelemetria { get; set; }

    // ── Configuración de la conexión ──────────────────────────────────────────

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.UseSqlite("Data Source=ferula_local.db");

    // ── Modelo relacional (Fluent API) ────────────────────────────────────────

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ── Paciente ─────────────────────────────────────────────────────────
        modelBuilder.Entity<Paciente>(entity =>
        {
            entity.HasKey(p => p.Id);

            entity.Property(p => p.Nombre)
                  .IsRequired()
                  .HasMaxLength(100);

            entity.Property(p => p.Apellido)
                  .IsRequired()
                  .HasMaxLength(100);

            entity.Property(p => p.FechaInicio)
                  .IsRequired();
        });

        // ── Sesion ────────────────────────────────────────────────────────────
        modelBuilder.Entity<Sesion>(entity =>
        {
            entity.HasKey(s => s.Id);

            // FK: Sesion → Paciente (cascade: al borrar un paciente, se borran sus sesiones)
            entity.HasOne(s => s.Paciente)
                  .WithMany(p => p.Sesiones)
                  .HasForeignKey(s => s.PacienteId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.Property(s => s.FechaHora).IsRequired();
        });

        // ── DetalleTelemetria ─────────────────────────────────────────────────
        modelBuilder.Entity<DetalleTelemetria>(entity =>
        {
            entity.HasKey(d => d.Id);

            // FK: DetalleTelemetria → Sesion (cascade: al borrar sesión, se borran sus detalles)
            entity.HasOne(d => d.Sesion)
                  .WithMany(s => s.Detalles)
                  .HasForeignKey(d => d.SesionId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Índice compuesto para consultas de reconstrucción de señal ordenada por tiempo
            entity.HasIndex(d => new { d.SesionId, d.Milisegundo });
        });
    }
}
