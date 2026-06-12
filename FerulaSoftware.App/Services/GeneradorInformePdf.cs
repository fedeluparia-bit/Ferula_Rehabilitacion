using System;
using System.Collections.Generic;
using System.Linq;
using FerulaSoftware.App.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace FerulaSoftware.App.Services;

/// <summary>
/// Genera un informe clínico en PDF a partir de los datos de una sesión de rehabilitación.
///
/// Estructura del documento:
///   1. Encabezado — título + fecha de emisión + línea divisoria
///   2. Sección 1 — Datos de la sesión (paciente, fecha, modalidad, duración)
///   3. Sección 2 — Resultados clínicos (reps, % éxito, presión máxima)
///   4. Sección 3 — Tabla de telemetría muestreada a 1 Hz (un registro por segundo)
///   5. Pie de página — identificación del sistema + numeración de páginas
///
/// Llamar desde un Task.Run — QuestPDF.GeneratePdf() es síncrono y puede tardar
/// varios cientos de ms en sesiones largas.
/// </summary>
public static class GeneradorInformePdf
{
    // ── Paleta de colores del documento ──────────────────────────────────────
    private const string CAzul      = "#1E3A5F";   // encabezados de sección
    private const string CAzulMed   = "#2D5282";   // encabezados de columna en tabla
    private const string CAzulClaro = "#EEF2F7";   // filas alternas (info)
    private const string CGrisF     = "#F8F9FA";   // filas alternas (telemetría)
    private const string CGrisTxt   = "#9CA3AF";   // texto secundario / pie

    // ── Punto de entrada ─────────────────────────────────────────────────────

    /// <summary>
    /// Escribe el PDF en <paramref name="rutaArchivo"/> de forma síncrona.
    /// </summary>
    /// <param name="rutaArchivo">Ruta completa del archivo de destino (extensión .pdf).</param>
    /// <param name="sesion">Datos de la sesión guardada en SQLite.</param>
    /// <param name="detalles">Lista completa de DetalleTelemetria ordenada por Milisegundo.</param>
    /// <param name="duracionTexto">Duración formateada, ej. "2m 34s".</param>
    /// <param name="porcentajeExitoTexto">Porcentaje de éxito formateado, ej. "80 %".</param>
    public static void Generar(
        string                    rutaArchivo,
        Sesion                    sesion,
        List<DetalleTelemetria>   detalles,
        string                    duracionTexto,
        string                    porcentajeExitoTexto)
    {
        var muestras  = MuestrarPorSegundo(detalles);
        var modoTexto = sesion.ModoActivo switch
        {
            0 => "Resistencia (activo-asistido)",
            1 => "Asistido (pasivo continuo)",
            _ => $"Código {sesion.ModoActivo}"
        };

        Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.MarginHorizontal(1.5f, Unit.Centimetre);
                page.MarginTop(1.5f,        Unit.Centimetre);
                page.MarginBottom(1.0f,     Unit.Centimetre);

                // ──────────────────────────────────────────────────────────────
                // ENCABEZADO
                // ──────────────────────────────────────────────────────────────
                page.Header().Column(col =>
                {
                    col.Item().Text(t =>
                    {
                        t.DefaultTextStyle(s => s.FontSize(18).Bold().FontColor(CAzul));
                        t.Span("INFORME DE REHABILITACIÓN BIOMECÁNICA");
                    });

                    col.Item().PaddingTop(3).Text(t =>
                    {
                        t.DefaultTextStyle(s => s.FontSize(9).FontColor(CGrisTxt));
                        t.Span("Férula Inteligente de Mano — Sistema PoC    ·    Emitido el ");
                        t.Span(DateTime.Now.ToString("dd/MM/yyyy 'a las' HH:mm"));
                    });

                    col.Item().PaddingTop(8).LineHorizontal(2).LineColor(CAzul);
                });

                // ──────────────────────────────────────────────────────────────
                // CONTENIDO
                // ──────────────────────────────────────────────────────────────
                page.Content().PaddingTop(16).Column(col =>
                {
                    col.Spacing(0);

                    // ── § 1  Datos de la sesión ───────────────────────────────
                    SeccionTitulo(col.Item(), "1.  DATOS DE LA SESIÓN");

                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(c =>
                        {
                            c.ConstantColumn(160);
                            c.RelativeColumn();
                        });

                        FilaInfo(t, "ID de Paciente", $"#{sesion.PacienteId}",                               true);
                        FilaInfo(t, "ID de Sesión",   $"#{sesion.Id}",                                       false);
                        FilaInfo(t, "Fecha y Hora",   sesion.FechaHora.ToString("dddd dd/MM/yyyy  HH:mm:ss"), true);
                        FilaInfo(t, "Modalidad",      modoTexto,                                              false);
                        FilaInfo(t, "Duración Total", duracionTexto,                                          true);
                    });

                    col.Item().PaddingVertical(14).LineHorizontal(1).LineColor("#E5E7EB");

                    // ── § 2  Resultados clínicos ──────────────────────────────
                    SeccionTitulo(col.Item(), "2.  RESULTADOS CLÍNICOS");

                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(c =>
                        {
                            c.ConstantColumn(160);
                            c.RelativeColumn();
                        });

                        int pctPresion = sesion.PresionMaxima > 0
                            ? (int)Math.Round(sesion.PresionMaxima * 100.0 / 4095)
                            : 0;

                        FilaInfo(t, "Repeticiones Objetivo", $"{sesion.RepeticionesObjetivo}",                         true);
                        FilaInfo(t, "Repeticiones Logradas", $"{sesion.RepeticionesHechas}",                           false);
                        FilaInfo(t, "Porcentaje de Éxito",   porcentajeExitoTexto,                                     true);
                        FilaInfo(t, "Presión Máxima (ADC)",  $"{sesion.PresionMaxima} / 4095   ({pctPresion} % del rango)", false);
                    });

                    col.Item().PaddingVertical(14).LineHorizontal(1).LineColor("#E5E7EB");

                    // ── § 3  Telemetría muestreada ────────────────────────────
                    SeccionTitulo(col.Item(),
                        $"3.  MUESTREO DE TELEMETRÍA  —  {muestras.Count} registros  (1 muestra / segundo)");

                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(c =>
                        {
                            c.ConstantColumn(65);
                            c.RelativeColumn();
                            c.RelativeColumn();
                            c.RelativeColumn();
                            c.RelativeColumn();
                        });

                        // Encabezados
                        foreach (var h in new[]
                            { "Tiempo (s)", "M0  Ángulo°", "M0  Presión", "M1  Ángulo°", "M1  Presión" })
                            CeldaEnc(t.Cell(), h);

                        // Datos
                        int fila = 0;
                        foreach (var d in muestras)
                        {
                            bool par = fila++ % 2 == 0;
                            CeldaDato(t.Cell(), $"{d.Milisegundo / 1000.0:F1}", par);
                            CeldaDato(t.Cell(), $"{d.Motor0Angulo}",            par);
                            CeldaDato(t.Cell(), $"{d.Motor0Presion}",           par);
                            CeldaDato(t.Cell(), $"{d.Motor1Angulo}",            par);
                            CeldaDato(t.Cell(), $"{d.Motor1Presion}",           par);
                        }
                    });
                });

                // ──────────────────────────────────────────────────────────────
                // PIE DE PÁGINA
                // ──────────────────────────────────────────────────────────────
                page.Footer().PaddingTop(6).BorderTop(1).BorderColor("#E5E7EB").Row(row =>
                {
                    row.RelativeItem().Text(t =>
                    {
                        t.DefaultTextStyle(s => s.FontSize(8).FontColor(CGrisTxt));
                        t.Span("Sistema de Rehabilitación Biomecánica — PoC  |  Documento de uso médico interno");
                    });

                    row.ConstantItem(80).AlignRight().Text(t =>
                    {
                        t.DefaultTextStyle(s => s.FontSize(8).FontColor(CGrisTxt));
                        t.Span("Página ");
                        t.CurrentPageNumber();
                        t.Span(" de ");
                        t.TotalPages();
                    });
                });
            });
        }).GeneratePdf(rutaArchivo);
    }

    // ── Helpers de diseño ─────────────────────────────────────────────────────

    /// <summary>Barra azul oscuro con texto blanco en negrita — separa secciones del informe.</summary>
    private static void SeccionTitulo(IContainer c, string titulo) =>
        c.Background(CAzul)
         .PaddingVertical(6)
         .PaddingHorizontal(10)
         .Text(t =>
         {
             t.DefaultTextStyle(s => s.FontSize(10).Bold().FontColor("#FFFFFF"));
             t.Span(titulo);
         });

    /// <summary>Par etiqueta/valor en la tabla de datos de sesión o resultados clínicos.</summary>
    private static void FilaInfo(TableDescriptor table, string label, string valor, bool sombreado)
    {
        string bg = sombreado ? CAzulClaro : "#FFFFFF";

        table.Cell()
             .Background(bg).PaddingVertical(6).PaddingHorizontal(10)
             .Text(t =>
             {
                 t.DefaultTextStyle(s => s.FontSize(9).SemiBold());
                 t.Span(label);
             });

        table.Cell()
             .Background(bg).PaddingVertical(6).PaddingHorizontal(10)
             .Text(t =>
             {
                 t.DefaultTextStyle(s => s.FontSize(9));
                 t.Span(valor);
             });
    }

    /// <summary>Celda de encabezado de la tabla de telemetría (fondo azul medio, texto blanco).</summary>
    private static void CeldaEnc(IContainer c, string texto) =>
        c.Background(CAzulMed)
         .PaddingVertical(5)
         .PaddingHorizontal(6)
         .Text(t =>
         {
             t.DefaultTextStyle(s => s.FontSize(8).SemiBold().FontColor("#FFFFFF"));
             t.Span(texto);
         });

    /// <summary>Celda de datos de la tabla de telemetría con filas alternas.</summary>
    private static void CeldaDato(IContainer c, string texto, bool sombreado) =>
        c.Background(sombreado ? CGrisF : "#FFFFFF")
         .PaddingVertical(4)
         .PaddingHorizontal(6)
         .Text(t =>
         {
             t.DefaultTextStyle(s => s.FontSize(8));
             t.Span(texto);
         });

    // ── Sampling ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Selecciona un registro por segundo agrupando por <c>Milisegundo / 1000</c>
    /// y tomando el primero de cada grupo. Si la sesión dura X segundos → X filas.
    /// </summary>
    private static List<DetalleTelemetria> MuestrarPorSegundo(List<DetalleTelemetria> detalles) =>
        detalles
            .GroupBy(d => d.Milisegundo / 1000)
            .Select(g => g.First())
            .ToList();
}
