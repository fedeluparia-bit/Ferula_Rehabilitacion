using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FerulaSoftware.App.Data;
using FerulaSoftware.App.Models;
using FerulaSoftware.App.Services;
using Microsoft.EntityFrameworkCore;

namespace FerulaSoftware.App.ViewModels;

/// <summary>
/// Elemento de la lista de rutinas.
/// Envuelve los datos de <see cref="Rutina"/> junto con el comando de inicio
/// para que el DataTemplate en XAML pueda bindear ambos sin acceder al ViewModel padre.
/// </summary>
public sealed class RutinaItem
{
    public Rutina   Rutina         { get; init; }
    public ICommand IniciarCommand { get; init; }

    public RutinaItem(Rutina rutina, Action<Rutina> onIniciar)
    {
        Rutina         = rutina;
        IniciarCommand = new RelayCommand(() => onIniciar(rutina));
    }
}

/// <summary>
/// ViewModel de la vista "Rutinas Programadas".
/// Descarga desde la nube las rutinas pendientes del paciente y permite
/// lanzar una sesión vinculada a una rutina con un solo botón.
///
/// Flujo:
///   1. El usuario presiona "Sincronizar" → <see cref="DescargarRutinasCommand"/>.
///   2. Se resuelve el ID del paciente en la nube vía SincronizarPacienteAsync.
///   3. Se obtienen las rutinas pendientes (Completada = false).
///   4. Al presionar "Realizar Ejercicio", el callback onIniciarRutina inyectado
///      por DashboardViewModel navega a SesionLibre y aplica los parámetros.
/// </summary>
public sealed partial class RutinasProgramadasViewModel : ViewModelBase
{
    private readonly Func<AppDbContext>  _dbFactory;
    private readonly ApiSyncService      _apiSync;
    private readonly Action<Rutina>      _onIniciarRutina;

    [ObservableProperty] private bool   _cargando;
    [ObservableProperty] private string _mensajeEstado = "Presiona 'Sincronizar' para descargar tus rutinas.";

    public ObservableCollection<RutinaItem> Rutinas { get; } = [];

    public RutinasProgramadasViewModel(
        Func<AppDbContext>  dbFactory,
        ApiSyncService      apiSync,
        Action<Rutina>      onIniciarRutina)
    {
        _dbFactory       = dbFactory;
        _apiSync         = apiSync;
        _onIniciarRutina = onIniciarRutina;
    }

    // ── Comandos ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task DescargarRutinasAsync()
    {
        Cargando      = true;
        MensajeEstado = "Conectando con el servidor...";

        try
        {
            // 1. Resolver paciente local (SQLite)
            Paciente? paciente = null;
            await Task.Run(async () =>
            {
                await using var db = _dbFactory();
                paciente = await db.Pacientes.FirstOrDefaultAsync();
            });

            if (paciente is null)
            {
                MensajeEstado = "Error: no hay paciente registrado localmente.";
                return;
            }

            // 2. Obtener ID del paciente en la nube (upsert por nombre+fecha)
            var cloudId = await _apiSync.SincronizarPacienteAsync(paciente);
            if (cloudId is null)
            {
                MensajeEstado = "Error: no se pudo conectar con la nube. Verifica la conexión.";
                return;
            }

            // 3. Descargar rutinas pendientes
            var rutinas = await _apiSync.ObtenerRutinasPendientesAsync(cloudId.Value);

            Rutinas.Clear();
            foreach (var r in rutinas)
                Rutinas.Add(new RutinaItem(r, _onIniciarRutina));

            MensajeEstado = rutinas.Count > 0
                ? $"{rutinas.Count} rutina(s) pendiente(s) descargadas."
                : "No tienes rutinas pendientes asignadas.";
        }
        catch (Exception ex)
        {
            MensajeEstado = $"Error inesperado: {ex.Message}";
            Debug.WriteLine($"[RutinasProgramadas] {ex}");
        }
        finally
        {
            Cargando = false;
        }
    }

    // ── API pública ───────────────────────────────────────────────────────────

    /// <summary>
    /// Elimina una rutina de la lista local una vez que fue completada.
    /// Llamado por DashboardViewModel tras confirmar el PATCH a la nube.
    /// Debe ejecutarse en el UI thread.
    /// </summary>
    public void QuitarRutina(int rutinaId)
    {
        var item = Rutinas.FirstOrDefault(r => r.Rutina.Id == rutinaId);
        if (item is not null) Rutinas.Remove(item);
    }
}
