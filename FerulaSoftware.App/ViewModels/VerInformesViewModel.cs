using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FerulaSoftware.App.Data;
using FerulaSoftware.App.Models;
using FerulaSoftware.App.Services;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using Microsoft.EntityFrameworkCore;

namespace FerulaSoftware.App.ViewModels;

/// <summary>
/// ViewModel del módulo de análisis clínico histórico.
///
/// Flujo de uso:
///   1. Al navegar a esta vista, DashboardViewModel llama a CargarSesionesAsync().
///   2. El usuario selecciona una sesión de la lista → se dispara CargarTelemetriaAsync().
///   3. Los datos de DetalleTelemetria se mapean a series de LiveCharts2 para
///      reconstruir las curvas de presión y ángulo de esa sesión.
/// </summary>
public sealed partial class VerInformesViewModel : ViewModelBase
{
    private readonly Func<AppDbContext> _dbFactory;
    private readonly ApiSyncService     _apiSync;

    // ── Modo terapeuta (inmutable tras construcción) ──────────────────────────
    // true  → repositorio nube  (API REST): selector de paciente + GET sesiones/detalles
    // false → repositorio local (SQLite EF Core): comportamiento original
    public bool ModoTerapeuta { get; }

    // ── Lista de pacientes cloud (solo se puebla en modo terapeuta) ───────────
    public ObservableCollection<Paciente> PacientesCloud { get; } = [];

    // ── Paciente seleccionado por el terapeuta ────────────────────────────────
    [ObservableProperty] private Paciente? _pacienteCloudSeleccionado;

    // ── Lista de sesiones ─────────────────────────────────────────────────────
    public ObservableCollection<Sesion> SesionesHistoricas { get; } = [];

    // ── Buffer de telemetría de la sesión actualmente cargada ─────────────────
    // Se llena en CargarTelemetriaAsync y se consume en ExportarPdfAsync.
    // Toda escritura ocurre en el UI thread → no necesita sincronización.
    private List<DetalleTelemetria> _detallesCargados = [];

    // ── Sesión actualmente seleccionada ───────────────────────────────────────
    [ObservableProperty] private Sesion? _sesionSeleccionada;

    // ── Estado de carga y sincronización ─────────────────────────────────────
    [ObservableProperty] private bool   _cargando;
    [ObservableProperty] private bool   _hayDatos;
    [ObservableProperty] private bool   _sincronizando;
    [ObservableProperty] private string _mensajeEstado = string.Empty;
    [ObservableProperty] private string _mensajeColor  = "#8B949E";

    // CancellationTokenSource para auto-cancelar el temporizador de limpieza
    // del mensaje si el usuario lanza dos sincronizaciones seguidas.
    private CancellationTokenSource? _mensajeCts;

    // ── Tarjetas de resumen ───────────────────────────────────────────────────
    [ObservableProperty] private string _duracionTotal       = "—";
    [ObservableProperty] private string _porcentajeExito     = "—";
    [ObservableProperty] private int    _presionMaximaSesion;
    [ObservableProperty] private string _modoTextoSeleccionado = "—";

    // ── LiveCharts2: series como ObservableProperty → CartesianChart re-renderiza
    //    cuando la referencia del array cambia.                                ──
    [ObservableProperty] private ISeries[] _seriesPresion = [];
    [ObservableProperty] private ISeries[] _seriesAngulo  = [];

    // Ejes — readonly, construidos una vez
    public Axis[] YAxesPresion { get; } =
    [
        new Axis { Name = "Presión FSR (0–4095)", MinLimit = 0, MaxLimit = 4095 }
    ];
    public Axis[] YAxesAngulo { get; } =
    [
        new Axis { Name = "Ángulo (0–180°)", MinLimit = 0, MaxLimit = 180 }
    ];
    public Axis[] XAxesTiempo { get; } =
    [
        new Axis { Name = "Tiempo (s)" }
    ];

    // ── Constructor ───────────────────────────────────────────────────────────

    public VerInformesViewModel(Func<AppDbContext> dbFactory, ApiSyncService apiSync)
    {
        _dbFactory    = dbFactory;
        _apiSync      = apiSync;
        ModoTerapeuta = ApiSyncService.UsuarioActual?.EsTerapeuta ?? false;
    }

    // ── Callbacks al cambiar selección ────────────────────────────────────────

    partial void OnSesionSeleccionadaChanged(Sesion? value) =>
        _ = CargarTelemetriaAsync(value);

    // Al cambiar el paciente seleccionado (modo terapeuta), carga sus sesiones.
    partial void OnPacienteCloudSeleccionadoChanged(Paciente? value) =>
        _ = CargarSesionesPorPacienteCloudAsync(value);

    // ── Comandos ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Punto de entrada para cargar datos iniciales del historial.
    ///
    /// Modo terapeuta  → descarga la lista de pacientes de la nube para el selector.
    ///                   Las sesiones se cargan en <see cref="CargarSesionesPorPacienteCloudAsync"/>
    ///                   cuando el terapeuta elige un paciente en el ComboBox.
    /// Modo paciente   → lee las sesiones propias de SQLite local (comportamiento original).
    /// </summary>
    [RelayCommand]
    public async Task CargarSesionesAsync()
    {
        try
        {
            if (ModoTerapeuta)
            {
                var pacientes = await _apiSync.ObtenerPacientesAsync();
                PacientesCloud.Clear();
                foreach (var p in pacientes)
                    PacientesCloud.Add(p);
            }
            else
            {
                // Filtrar por el paciente cuyo nombre coincide con el usuario logueado.
                // Si no hay sesión activa (modo demo), se muestran todas las sesiones.
                var usuario = ApiSyncService.UsuarioActual;
                var lista = await Task.Run(async () =>
                {
                    await using var db = _dbFactory();

                    if (usuario is not null)
                    {
                        // Buscar el PacienteId local que corresponde al usuario autenticado.
                        var pacienteId = await db.Pacientes
                            .Where(p => p.Nombre    == usuario.Nombre
                                     && p.Apellido  == usuario.Apellido)
                            .Select(p => (int?)p.Id)
                            .FirstOrDefaultAsync();

                        // Si existe el paciente local → solo sus sesiones.
                        // Si no existe aún (primer login sin sesiones propias) → lista vacía.
                        if (pacienteId is null)
                            return new System.Collections.Generic.List<Sesion>();

                        return await db.Sesiones
                            .Where(s => s.PacienteId == pacienteId.Value)
                            .OrderByDescending(s => s.FechaHora)
                            .ToListAsync();
                    }

                    // Sin login: comportamiento demo — todas las sesiones
                    return await db.Sesiones
                        .OrderByDescending(s => s.FechaHora)
                        .ToListAsync();
                });

                SesionesHistoricas.Clear();
                foreach (var s in lista)
                    SesionesHistoricas.Add(s);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DB/API] CargarSesiones: {ex.Message}");
        }
    }

    /// <summary>
    /// Carga las sesiones del paciente seleccionado desde la nube (solo modo terapeuta).
    /// </summary>
    private async Task CargarSesionesPorPacienteCloudAsync(Paciente? paciente)
    {
        SesionesHistoricas.Clear();
        LimpiarGraficas();

        if (paciente is null) return;

        try
        {
            var sesiones = await _apiSync.ObtenerHistorialPacienteAsync(paciente.Id);
            foreach (var s in sesiones)
                SesionesHistoricas.Add(s);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[API] CargarSesionesPorPacienteCloud: {ex.Message}");
        }
    }

    // ── Carga de telemetría ───────────────────────────────────────────────────

    /// <summary>
    /// Recupera los DetalleTelemetria de la sesión seleccionada y reconstruye
    /// las curvas de presión y ángulo en LiveCharts2.
    /// Toda actualización de observables ocurre en el UI thread.
    /// </summary>
    private async Task CargarTelemetriaAsync(Sesion? sesion)
    {
        LimpiarGraficas();

        if (sesion is null)
        {
            HayDatos = false;
            return;
        }

        Cargando = true;
        try
        {
            // Bifurcación repositorio: nube (terapeuta) vs. SQLite local (paciente)
            List<DetalleTelemetria> detalles;
            if (ModoTerapeuta)
            {
                detalles = await _apiSync.ObtenerDetallesSesionAsync(sesion.Id);
            }
            else
            {
                detalles = await Task.Run(async () =>
                {
                    await using var db = _dbFactory();
                    return await db.DetallesTelemetria
                        .Where(d => d.SesionId == sesion.Id)
                        .OrderBy(d => d.Milisegundo)
                        .ToListAsync();
                });
            }

            // Guardar copia cruda para la exportación PDF
            _detallesCargados = detalles;

            // ── Construir arrays de puntos (X = segundos, Y = valor) ──────────
            int n = detalles.Count;
            var pPresion0 = new ObservablePoint[n];
            var pPresion1 = new ObservablePoint[n];
            var pAngulo0  = new ObservablePoint[n];
            var pAngulo1  = new ObservablePoint[n];

            for (int i = 0; i < n; i++)
            {
                var d = detalles[i];
                double t = d.Milisegundo / 1000.0;   // milisegundos → segundos para el eje X

                pPresion0[i] = new ObservablePoint(t, d.Motor0Presion);
                pPresion1[i] = new ObservablePoint(t, d.Motor1Presion);
                pAngulo0[i]  = new ObservablePoint(t, d.Motor0Angulo);
                pAngulo1[i]  = new ObservablePoint(t, d.Motor1Angulo);
            }

            // Asignar arrays completos — una sola notificación de PropertyChanged
            // por serie en lugar de N inserciones individuales (más eficiente)
            SeriesPresion =
            [
                new LineSeries<ObservablePoint>
                {
                    Values       = pPresion0,
                    Name         = "FSR Motor 0 — Índice/Medio",
                    GeometrySize = 0   // sin círculos en cada punto → más limpio
                },
                new LineSeries<ObservablePoint>
                {
                    Values       = pPresion1,
                    Name         = "FSR Motor 1 — Anular/Meñique",
                    GeometrySize = 0
                }
            ];

            SeriesAngulo =
            [
                new LineSeries<ObservablePoint>
                {
                    Values       = pAngulo0,
                    Name         = "Ángulo Motor 0 — Índice/Medio",
                    GeometrySize = 0
                },
                new LineSeries<ObservablePoint>
                {
                    Values       = pAngulo1,
                    Name         = "Ángulo Motor 1 — Anular/Meñique",
                    GeometrySize = 0
                }
            ];

            // ── Tarjetas de resumen ───────────────────────────────────────────
            if (n > 0)
            {
                int ultimoMs   = detalles[^1].Milisegundo;
                int minutos    = ultimoMs / 60000;
                int segs       = (ultimoMs % 60000) / 1000;
                DuracionTotal  = $"{minutos}m {segs:D2}s";
            }
            else
            {
                DuracionTotal = "0m 00s";
            }

            PresionMaximaSesion    = sesion.PresionMaxima;
            PorcentajeExito        = sesion.RepeticionesObjetivo > 0
                ? $"{sesion.RepeticionesHechas * 100 / sesion.RepeticionesObjetivo} %"
                : "—";
            ModoTextoSeleccionado  = sesion.ModoActivo switch
            {
                0 => "0 — Resistencia (activo-asistido)",
                1 => "1 — Asistido (pasivo continuo)",
                _ => sesion.ModoActivo.ToString()
            };

            HayDatos = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DB] CargarTelemetria: {ex.Message}");
            HayDatos = false;
        }
        finally
        {
            Cargando = false;
        }
    }

    // ── Sincronización con la nube ────────────────────────────────────────────

    /// <summary>
    /// Envía la sesión actualmente cargada a la API en la nube.
    ///
    /// Manejo de mensajes de estado:
    ///   · Cada llamada cancela el timer de limpieza anterior (CancellationTokenSource).
    ///   · Tras 5 segundos sin nueva llamada, el mensaje se borra automáticamente.
    ///   · MensajeColor varía entre verde (#06D6A0) y rojo (#E63946) según el resultado.
    /// </summary>
    [RelayCommand]
    private async Task SincronizarNubeAsync()
    {
        if (_sesionSeleccionada is null || !HayDatos || Sincronizando) return;

        // Cancelar timer de limpieza previo para evitar solapamientos
        _mensajeCts?.Cancel();
        _mensajeCts?.Dispose();
        _mensajeCts = new CancellationTokenSource();

        Sincronizando = true;
        MensajeEstado = "⏳  Sincronizando...";
        MensajeColor  = "#8B949E";

        // Cargar el paciente desde la BD local para enviarlo a la nube.
        // Los IDs de SQLite y PostgreSQL no coinciden; el API hace upsert
        // por nombre+apellido+fechaInicio y devuelve el ID de la nube.
        var paciente = await Task.Run(async () =>
        {
            await using var db = _dbFactory();
            return await db.Pacientes.FindAsync(_sesionSeleccionada.PacienteId);
        });

        if (paciente is null)
        {
            Sincronizando = false;
            MensajeEstado = "⚠️  Error — Paciente no encontrado en la BD local";
            MensajeColor  = "#E63946";
            return;
        }

        var exito = await _apiSync.SincronizarSesionAsync(
            _sesionSeleccionada,
            paciente,
            _detallesCargados);

        Sincronizando = false;

        if (exito)
        {
            MensajeEstado = "☁️  Sesión sincronizada correctamente";
            MensajeColor  = "#06D6A0";   // verde éxito
        }
        else
        {
            MensajeEstado = "⚠️  Error — API no disponible";
            MensajeColor  = "#E63946";   // rojo error
        }

        // Auto-limpiar el mensaje tras 5 s — se cancela si se lanza otra sincronización
        try
        {
            await Task.Delay(5_000, _mensajeCts.Token);
            MensajeEstado = string.Empty;
        }
        catch (OperationCanceledException) { /* otra llamada ya tomó el control */ }
    }

    // ── Exportación PDF ───────────────────────────────────────────────────────

    /// <summary>
    /// Abre un diálogo de guardado de archivos y genera el informe PDF.
    /// El diálogo de Avalonia (SaveFilePickerAsync) debe ejecutarse en el UI thread.
    /// QuestPDF.GeneratePdf() se ejecuta en Task.Run para no bloquear el dispatcher.
    /// </summary>
    [RelayCommand]
    private async Task ExportarPdfAsync()
    {
        if (_sesionSeleccionada is null || !HayDatos) return;

        // Obtener el StorageProvider desde la ventana principal
        if (Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
            return;

        var file = await desktop.MainWindow.StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title             = "Exportar Informe de Sesión",
                SuggestedFileName =
                    $"informe_sesion_{_sesionSeleccionada.Id}_{_sesionSeleccionada.FechaHora:yyyyMMdd_HHmm}.pdf",
                DefaultExtension  = "pdf",
                FileTypeChoices   =
                [
                    new FilePickerFileType("Documento PDF")
                    {
                        Patterns  = ["*.pdf"],
                        MimeTypes = ["application/pdf"]
                    }
                ]
            });

        if (file is null) return;   // usuario canceló el diálogo

        // Capturar todos los datos en el UI thread ANTES de Task.Run
        var sesion   = _sesionSeleccionada;
        var detalles = new List<DetalleTelemetria>(_detallesCargados);   // copia defensiva
        var duracion = DuracionTotal;
        var exito    = PorcentajeExito;
        var ruta     = file.Path.LocalPath;

        try
        {
            // Generación síncrona OFF UI thread — puede tardar varios cientos de ms
            await Task.Run(() =>
                GeneradorInformePdf.Generar(ruta, sesion, detalles, duracion, exito));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PDF] Error al exportar: {ex.Message}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void LimpiarGraficas()
    {
        SeriesPresion          = [];
        SeriesAngulo           = [];
        DuracionTotal          = "—";
        PresionMaximaSesion    = 0;
        PorcentajeExito        = "—";
        ModoTextoSeleccionado  = "—";
        _detallesCargados      = [];
        HayDatos               = false;
    }
}
