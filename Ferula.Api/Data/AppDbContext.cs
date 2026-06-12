using Ferula.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Ferula.Api.Data;

/// <summary>
/// DbContext de la API — configurado para PostgreSQL via Npgsql.
/// Las opciones (cadena de conexión) se inyectan desde Program.cs; este contexto
/// NO sobreescribe OnConfiguring para no hardcodear credenciales.
///
/// Las relaciones y el índice compuesto son idénticos al AppDbContext
/// del cliente Avalonia, garantizando paridad de esquema entre SQLite y PostgreSQL.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    // ── DbSets ────────────────────────────────────────────────────────────────

    public DbSet<Paciente>          Pacientes          { get; set; }
    public DbSet<Sesion>            Sesiones           { get; set; }
    public DbSet<DetalleTelemetria> DetallesTelemetria { get; set; }
    public DbSet<Rutina>            Rutinas            { get; set; }

    // ── Modelo relacional (Fluent API) ────────────────────────────────────────

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ── Paciente ──────────────────────────────────────────────────────────
        modelBuilder.Entity<Paciente>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Nombre).IsRequired().HasMaxLength(100);
            entity.Property(p => p.Apellido).IsRequired().HasMaxLength(100);
            entity.Property(p => p.FechaInicio).IsRequired();
        });

        // ── Sesion ────────────────────────────────────────────────────────────
        modelBuilder.Entity<Sesion>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.Property(s => s.FechaHora).IsRequired();

            // FK: Sesion → Paciente (cascade delete)
            entity.HasOne(s => s.Paciente)
                  .WithMany(p => p.Sesiones)
                  .HasForeignKey(s => s.PacienteId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // ── DetalleTelemetria ─────────────────────────────────────────────────
        modelBuilder.Entity<DetalleTelemetria>(entity =>
        {
            entity.HasKey(d => d.Id);

            // FK: DetalleTelemetria → Sesion (cascade delete)
            entity.HasOne(d => d.Sesion)
                  .WithMany(s => s.Detalles)
                  .HasForeignKey(d => d.SesionId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Índice compuesto para reconstrucción de señal ordenada por tiempo (O log n)
            entity.HasIndex(d => new { d.SesionId, d.Milisegundo });
        });

        // ── Rutina ────────────────────────────────────────────────────────────
        modelBuilder.Entity<Rutina>(entity =>
        {
            entity.HasKey(r => r.Id);

            // FK: Rutina → Paciente (cascade delete)
            entity.HasOne(r => r.Paciente)
                  .WithMany(p => p.Rutinas)
                  .HasForeignKey(r => r.PacienteId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.Property(r => r.Completada).HasDefaultValue(false);
        });
    }
}
