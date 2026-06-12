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
/// Elemento de la bandeja de entrada.
/// Envuelve una <see cref="InvitacionRutina"/> con sus comandos de respuesta
/// para que el DataTemplate en XAML pueda bindear directamente.
/// </summary>
public sealed class InvitacionItem
{
    public InvitacionRutina Invitacion      { get; init; }
    public ICommand         AceptarCommand  { get; init; }
    public ICommand         RechazarCommand { get; init; }

    public InvitacionItem(InvitacionRutina inv, Func<int, bool, Task> onResponder)
    {
        Invitacion      = inv;
        AceptarCommand  = new AsyncRelayCommand(() => onResponder(inv.Id, true));
        RechazarCommand = new AsyncRelayCommand(() => onResponder(inv.Id, false));
    }
}

/// <summary>
/// ViewModel de la Bandeja de Entrada.
/// Muestra las invitaciones de rutinas pendientes y permite aceptarlas o rechazarlas.
///
/// Cuando se acepta: la API crea la Rutina atómicamente (misma transacción EF Core).
/// El cliente solo necesita pasar su PacienteId en la nube para vincular la Rutina.
/// </summary>
public sealed partial class BandejaEntradaViewModel : ViewModelBase
{
    private readonly Func<AppDbContext>  _dbFactory;
    private readonly ApiSyncService      _apiSync;

    [ObservableProperty] private bool   _cargando;
    [ObservableProperty] private string _mensajeEstado = "Presiona 'Actualizar' para ver tus invitaciones.";

    public ObservableCollection<InvitacionItem> Invitaciones { get; } = [];

    public BandejaEntradaViewModel(Func<AppDbContext> dbFactory, ApiSyncService apiSync)
    {
        _dbFactory = dbFactory;
        _apiSync   = apiSync;
    }

    // ── Comandos ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task DescargarInvitacionesAsync()
    {
        Cargando      = true;
        MensajeEstado = "Conectando con el servidor...";

        try
        {
            // Resolver paciente y usuario en la nube
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

            var cloudUsuarioId = await _apiSync.SincronizarUsuarioAsync(
                paciente.Nombre, paciente.Apellido);

            if (cloudUsuarioId is null)
            {
                MensajeEstado = "Error: no se pudo conectar con la nube.";
                return;
            }

            var invitaciones = await _apiSync.ObtenerInvitacionesPendientesAsync(cloudUsuarioId.Value);

            Invitaciones.Clear();
            foreach (var inv in invitaciones)
                Invitaciones.Add(new InvitacionItem(inv, ResponderAsync));

            MensajeEstado = invitaciones.Count > 0
                ? $"{invitaciones.Count} invitación(es) pendiente(s)."
                : "Tu bandeja de entrada está vacía.";
        }
        catch (Exception ex)
        {
            MensajeEstado = $"Error inesperado: {ex.Message}";
            Debug.WriteLine($"[BandejaEntrada] {ex}");
        }
        finally
        {
            Cargando = false;
        }
    }

    // ── Responder a una invitación ────────────────────────────────────────────

    private async Task ResponderAsync(int invitacionId, bool aceptada)
    {
        int? cloudPacienteId = null;

        if (aceptada)
        {
            // Necesitamos el PacienteId en la nube para que la API vincule la Rutina
            Paciente? paciente = null;
            await Task.Run(async () =>
            {
                await using var db = _dbFactory();
                paciente = await db.Pacientes.FirstOrDefaultAsync();
            });

            if (paciente is not null)
                cloudPacienteId = await _apiSync.SincronizarPacienteAsync(paciente);
        }

        var ok = await _apiSync.ResponderInvitacionAsync(invitacionId, aceptada, cloudPacienteId);

        if (ok)
        {
            var item = Invitaciones.FirstOrDefault(i => i.Invitacion.Id == invitacionId);
            if (item is not null) Invitaciones.Remove(item);

            MensajeEstado = aceptada
                ? "Rutina aceptada — ya aparece en tus Rutinas Programadas."
                : "Invitación rechazada.";
        }
        else
        {
            MensajeEstado = "Error al procesar la respuesta. Intenta de nuevo.";
        }
    }
}
