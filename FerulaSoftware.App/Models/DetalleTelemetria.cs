using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FerulaSoftware.App.Models;

/// <summary>
/// Muestra de telemetría cruda registrada durante una sesión a 10 Hz.
/// Permite reconstruir la curva completa de presión y ángulo post-sesión
/// y exportarla a PDF mediante QuestPDF.
/// Relación: pertenece a una Sesion (cascade delete).
/// </summary>
public class DetalleTelemetria
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int SesionId { get; set; }

    /// <summary>
    /// Tiempo relativo al inicio de la sesión en milisegundos.
    /// Permite reconstruir el eje X de la gráfica sin depender de DateTime.
    /// Rango útil: int cubre ~24 días; suficiente para cualquier sesión clínica.
    /// </summary>
    public int Milisegundo { get; set; }

    // ── Motor 0 (Índice / Medio) ──────────────────────────────────────────────
    public int Motor0Angulo   { get; set; }   // 0–180 °
    public int Motor0Presion  { get; set; }   // ADC 0–4095 (FSR402)

    // ── Motor 1 (Anular / Meñique) ────────────────────────────────────────────
    public int Motor1Angulo   { get; set; }   // 0–180 °
    public int Motor1Presion  { get; set; }   // ADC 0–4095 (FSR402)

    // ── Navegación ────────────────────────────────────────────────────────────

    [ForeignKey(nameof(SesionId))]
    public Sesion Sesion { get; set; } = null!;
}
