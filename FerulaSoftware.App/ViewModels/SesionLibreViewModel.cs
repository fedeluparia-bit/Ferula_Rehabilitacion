using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Threading;
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
/// ViewModel de la vista "Sesión Libre".
/// Responsabilidades:
///   1. Control del ESP32 vía WebSocket (conectar, iniciar, detener, e-stop).
///   2. Visualización de telemetría en tiempo real (LiveCharts2, 10 Hz).
///   3. Buffering en memoria de cada paquete durante una sesión activa.
///   4. Volcado atómico a SQLite al finalizar (Stop, auto-fin o E-Stop).
/// </summary>
public sealed partial class SesionLibreViewModel : ViewModelBase, IDisposable
{
    // ── Configuración ─────────────────────────────────────────────────────────
    private const int MaxDataPoints = 100;   // ventana deslizante LiveCharts: 10 s

    // Estados del sistema — espejo de config.h del firmware
    private const int EstadoReposo     = 0;
    private const int EstadoEjecutando = 1;
    private const int EstadoEStop      = 99;

    // ── Dependencias ──────────────────────────────────────────────────────────
    private readonly IWebSocketService  _ws;

    // Factory de contextos EF Core.
    // Se usa () => new AppDbContext() para que cada operación de BD tenga
    // su propio contexto de vida corta — patrón recomendado en apps de escritorio.
    private readonly Func<AppDbContext> _dbFactory;

    // ── Motor de persistencia — todo manipulado en el UI thread ───────────────

    private readonly List<DetalleTelemetria> _bufferTelemetria = [];
    private readonly Stopwatch               _stopwatchSesion  = new();

    /// <summary>
    /// Sesión en curso. null cuando no hay rutina activa.
    /// Actúa como semáforo: ponerlo a null ANTES del await en FlushSesionAsync
    /// garantiza que ninguna segunda llamada concurrente re-flashee.
    /// </summary>
    private Sesion? _sesionActual;

    /// <summary>Estado del tick anterior para detectar la transición Ejecutando→Reposo.</summary>
    private int _estadoSistemaAnterior = EstadoReposo;

    /// <summary>Id del paciente activo (resuelto asíncronamente en el constructor).</summary>
    private int _pacienteIdActual;

    // ── Propiedades observables ───────────────────────────────────────────────

    [ObservableProperty] private int    _estadoSistema;
    [ObservableProperty] private int    _modoActivo;
    [ObservableProperty] private int    _repeticionesHechas;
    // decimal? para coincidir con NumericUpDown.Value (Avalonia); se castea a int al guardar.
    [ObservableProperty] private decimal? _repeticionesObjetivo = 10;
    [ObservableProperty] private int    _presionMaxima;
    [ObservableProperty] private int    _anguloMotor0;
    [ObservableProperty] private int    _anguloMotor1;
    [ObservableProperty] private bool   _estaConectado;
    [ObservableProperty] private string _wsUri          = WebSocketService.DefaultUri.ToString();
    [ObservableProperty] private string _errorConexion  = string.Empty;
    [ObservableProperty] private int    _modoSeleccionado        = 0;
    [ObservableProperty] private int    _presionSimuladaSlider   = 0;

    // ── Estado de rutina asignada ─────────────────────────────────────────────
    /// <summary>True cuando la sesión fue iniciada desde una Rutina Programada.
    /// Bloquea los controles de Repeticiones y Modo en la UI.</summary>
    [ObservableProperty] private bool   _modosBloqueados = false;

    /// <summary>Descripción de la rutina activa que se muestra en el banner.</summary>
    [ObservableProperty] private string _bannerRutina    = string.Empty;

    /// <summary>ID de la rutina en la nube que disparó esta sesión. Null en sesión libre.</summary>
    private int? _rutinaId;

    /// <summary>
    /// Callback invocado al finalizar una sesión vinculada a una rutina.
    /// El parámetro es el ID de la rutina en la nube (PostgreSQL).
    /// Asignado por DashboardViewModel para desacoplar la llamada HTTP de este ViewModel.
    /// </summary>
    public Func<int, Task>? OnRutinaCompletada { get; set; }

    // ── Propiedades computadas ────────────────────────────────────────────────

    public string EstadoConexionTexto => EstaConectado ? "Online" : "Offline";

    partial void OnEstaConectadoChanged(bool value) =>
        OnPropertyChanged(nameof(EstadoConexionTexto));

    partial void OnPresionSimuladaSliderChanged(int value) =>
        _ = _ws.SendCommandAsync(new EspCommand("sim_prs", value));

    // ── LiveCharts2 ───────────────────────────────────────────────────────────

    public ObservableCollection<ObservableValue> DatosPresion0 { get; } = [];
    public ObservableCollection<ObservableValue> DatosPresion1 { get; } = [];
    public ISeries[] SeriesPresion { get; }
    public Axis[]    YAxesPresion  { get; } =
    [
        new Axis { Name = "Presión FSR (0–4095)", MinLimit = 0, MaxLimit = 4095 }
    ];

    // ── Constructor ───────────────────────────────────────────────────────────

    public SesionLibreViewModel(IWebSocketService ws, Func<AppDbContext> dbFactory)
    {
        _ws        = ws;
        _dbFactory = dbFactory;

        _ws.TelemetryReceived += OnTelemetryReceived;
        _ws.ConnectionChanged += OnConnectionChanged;

        SeriesPresion =
        [
            new LineSeries<ObservableValue>
            {
                Values = DatosPresion0,
                Name   = "FSR Motor 0 — Índice/Medio"
            },
            new LineSeries<ObservableValue>
            {
                Values = DatosPresion1,
                Name   = "FSR Motor 1 — Anular/Meñique"
            }
        ];

        // Inicialización asíncrona del paciente dummy.
        // Fire-and-forget controlado: el Id estará disponible mucho antes de
        // que el usuario pueda presionar "Iniciar Rutina".
        _ = InicializarPacienteDummyAsync();
    }

    // ── Comandos ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ConectarAsync()
    {
        ErrorConexion = string.Empty;
        try
        {
            await _ws.ConnectAsync(new Uri(WsUri));
        }
        catch (Exception ex)
        {
            var inner = ex.InnerException is { } ie ? $" → {ie.Message}" : string.Empty;
            ErrorConexion = $"{ex.GetType().Name}: {ex.Message}{inner}";
        }
    }

    /// <summary>
    /// Inicia una nueva rutina de rehabilitación.
    /// Si había una sesión sin cerrar, la vuelca primero (caso edge: doble "Iniciar").
    /// </summary>
    [RelayCommand]
    private async Task IniciarRutinaAsync(decimal? repeticionesObjetivo)
    {
        var reps = (int)(repeticionesObjetivo ?? RepeticionesObjetivo ?? 10);

        // Cierra cualquier sesión anterior sin cerrar (edge case)
        await FlushSesionAsync();

        PresionMaxima      = 0;
        RepeticionesHechas = 0;
        _bufferTelemetria.Clear();

        _sesionActual = new Sesion
        {
            PacienteId           = _pacienteIdActual,
            FechaHora            = DateTime.UtcNow,
            ModoActivo           = ModoSeleccionado,
            RepeticionesObjetivo = reps
        };

        _stopwatchSesion.Restart();

        await _ws.SendCommandAsync(new EspCommand("start", reps, ModoSeleccionado));
    }

    /// <summary>
    /// Detiene la rutina de forma controlada.
    /// Envía "stop" al ESP32 y vuelca el buffer como fallback (si el ESP32
    /// no respondiera con REPOSO, los datos no se perderían).
    /// </summary>
    [RelayCommand]
    private async Task StopAsync()
    {
        await _ws.SendCommandAsync(new EspCommand("stop", 0));
        await FlushSesionAsync();
    }

    /// <summary>
    /// Dispara el E-STOP lógico. Vuelca los datos recolectados hasta el momento
    /// para no perder la sesión parcial ante una emergencia.
    /// </summary>
    [RelayCommand]
    private async Task EStopAsync()
    {
        await _ws.SendCommandAsync(new EspCommand("estop", 0));
        await FlushSesionAsync();
    }

    /// <summary>
    /// Precarga los parámetros de una Rutina Programada y bloquea la UI para que
    /// el paciente no pueda modificar el modo ni las repeticiones.
    /// Llamado por DashboardViewModel antes de navegar a esta vista.
    /// </summary>
    public void AplicarRutina(Models.Rutina rutina)
    {
        _rutinaId            = rutina.Id;
        ModoSeleccionado     = rutina.ModoActivo;
        RepeticionesObjetivo = (decimal)rutina.RepeticionesObjetivo;
        ModosBloqueados      = true;
        BannerRutina         = $"Rutina asignada · Modo: {rutina.ModoTexto} · {rutina.RepeticionesObjetivo} repeticiones";
    }

    // ── Manejadores de eventos WebSocket ──────────────────────────────────────

    private void OnTelemetryReceived(object? sender, TelemetryPacket packet)
    {
        // Todo el cuerpo corre en el UI thread — manipulación de buffer segura.
        Dispatcher.UIThread.Post(() =>
        {
            // ── Detectar fin natural de rutina ANTES de actualizar el estado ──
            // La transición Ejecutando→Reposo en el firmware indica que se
            // completaron todas las repeticiones objetivo.
            bool finNatural = _estadoSistemaAnterior == EstadoEjecutando
                           && packet.Estado          == EstadoReposo;

            // ── Actualizar observables para la UI ─────────────────────────────
            EstadoSistema              = packet.Estado;
            ModoActivo                 = packet.Modo;
            RepeticionesHechas         = packet.Repeticiones;
            _estadoSistemaAnterior     = packet.Estado;

            if (packet.Motores.Length < 2) return;

            MotorData m0 = packet.Motores[0];
            MotorData m1 = packet.Motores[1];

            AnguloMotor0 = m0.Angulo;
            AnguloMotor1 = m1.Angulo;

            int pMax = Math.Max(m0.Presion, m1.Presion);
            if (pMax > PresionMaxima) PresionMaxima = pMax;

            AppendDataPoint(DatosPresion0, m0.Presion);
            AppendDataPoint(DatosPresion1, m1.Presion);

            // ── Buffering — solo durante EJECUTANDO ───────────────────────────
            // No se bufferiza el paquete de REPOSO: ese valor ya está capturado
            // en RepeticionesHechas y PresionMaxima que FlushSesionAsync lee.
            if (_sesionActual is not null && packet.Estado == EstadoEjecutando)
            {
                _bufferTelemetria.Add(new DetalleTelemetria
                {
                    // SesionId lo asigna EF Core al resolver la FK de navegación
                    Milisegundo   = (int)_stopwatchSesion.ElapsedMilliseconds,
                    Motor0Angulo  = m0.Angulo,
                    Motor0Presion = m0.Presion,
                    Motor1Angulo  = m1.Angulo,
                    Motor1Presion = m1.Presion
                });
            }

            // ── Auto-flush en fin natural de rutina ───────────────────────────
            if (finNatural)
                _ = FlushSesionAsync();
        });
    }

    private void OnConnectionChanged(object? sender, bool conectado)
    {
        Dispatcher.UIThread.Post(() => EstaConectado = conectado);

        if (conectado)
            _ = _ws.SendCommandAsync(new EspCommand("sim_prs", 0));
    }

    // ── Motor de persistencia ─────────────────────────────────────────────────

    /// <summary>
    /// Vuelca la sesión activa y su buffer de telemetría a SQLite.
    ///
    /// Garantías de hilo:
    ///   · El preamble (snapshot, null-guard, lectura de observables) corre en
    ///     el UI thread — sin races con el buffer.
    ///   · Task.Run mueve el SaveChangesAsync a un hilo del pool — la UI nunca
    ///     se congela por escrituras de BD.
    ///   · _sesionActual = null ANTES del await → una segunda llamada concurrente
    ///     devuelve sin hacer nada (pattern null-first).
    /// </summary>
    private async Task FlushSesionAsync()
    {
        if (_sesionActual is null) return;

        _stopwatchSesion.Stop();

        // ── Snapshot atómico (UI thread) ──────────────────────────────────────
        var sesion = _sesionActual;
        var buffer = new List<DetalleTelemetria>(_bufferTelemetria);  // copia defensiva

        _sesionActual = null;                        // ← null-first: bloquea re-entrada
        _bufferTelemetria.Clear();

        // Estampar valores finales desde los observables actuales
        sesion.RepeticionesHechas = RepeticionesHechas;
        sesion.PresionMaxima      = PresionMaxima;
        sesion.Detalles           = buffer;          // EF Core asignará SesionId a cada item

        // ── Escritura a BD en hilo de pool (nunca bloquea el dispatcher) ──────
        bool guardadoOk = true;
        try
        {
            await Task.Run(async () =>
            {
                await using var db = _dbFactory();   // contexto de vida corta
                db.Sesiones.Add(sesion);             // EF Core inserta Sesion + Detalles en cascada
                await db.SaveChangesAsync();
            });
        }
        catch (Exception ex)
        {
            guardadoOk = false;
            Debug.WriteLine($"[DB] Error al guardar sesión: {ex.Message}");
        }

        // ── Restablecer estado de rutina (UI thread, tras el await) ──────────
        // Capturar antes de limpiar para poder notificar el ID correcto.
        var rutinaIdSnapshot = _rutinaId;
        _rutinaId       = null;
        ModosBloqueados = false;
        BannerRutina    = string.Empty;

        // Notificar a DashboardViewModel para que llame PATCH /api/rutinas/{id}/completar
        if (guardadoOk && rutinaIdSnapshot.HasValue && OnRutinaCompletada is not null)
            await OnRutinaCompletada(rutinaIdSnapshot.Value);
    }

    // ── Inicialización del paciente local ────────────────────────────────────

    /// <summary>
    /// Resuelve (o crea) el registro Paciente en SQLite que recibirá las sesiones.
    ///
    /// · Usuario logueado → busca Paciente por Nombre+Apellido del usuario; lo crea si no existe.
    ///   Garantiza que las sesiones de Federico queden bajo su propio registro, no bajo Demo.
    /// · Sin login (modo demo) → toma el primer Paciente o crea "Paciente Demo".
    /// </summary>
    private async Task InicializarPacienteDummyAsync()
    {
        try
        {
            await using var db = _dbFactory();

            var usuario  = ApiSyncService.UsuarioActual;
            Paciente? paciente;

            if (usuario is not null)
            {
                paciente = await db.Pacientes.FirstOrDefaultAsync(
                    p => p.Nombre == usuario.Nombre && p.Apellido == usuario.Apellido);

                if (paciente is null)
                {
                    paciente = new Paciente
                    {
                        Nombre      = usuario.Nombre,
                        Apellido    = usuario.Apellido,
                        FechaInicio = DateTime.UtcNow
                    };
                    db.Pacientes.Add(paciente);
                    await db.SaveChangesAsync();
                }
            }
            else
            {
                paciente = await db.Pacientes.FirstOrDefaultAsync();
                if (paciente is null)
                {
                    paciente = new Paciente
                    {
                        Nombre      = "Paciente",
                        Apellido    = "Demo",
                        FechaInicio = DateTime.UtcNow
                    };
                    db.Pacientes.Add(paciente);
                    await db.SaveChangesAsync();
                }
            }

            _pacienteIdActual = paciente.Id;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DB] Error inicializando paciente demo: {ex.Message}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void AppendDataPoint(ObservableCollection<ObservableValue> col, double valor)
    {
        if (col.Count >= MaxDataPoints) col.RemoveAt(0);
        col.Add(new ObservableValue(valor));
    }

    public void Dispose()
    {
        _ws.TelemetryReceived -= OnTelemetryReceived;
        _ws.ConnectionChanged -= OnConnectionChanged;
        // No llamar _ws.Dispose() — el servicio es propiedad del MainViewModel.
    }
}
