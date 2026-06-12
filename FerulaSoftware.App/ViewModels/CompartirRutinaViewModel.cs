using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FerulaSoftware.App.Data;
using FerulaSoftware.App.Models;
using FerulaSoftware.App.Services;
using Microsoft.EntityFrameworkCore;

namespace FerulaSoftware.App.ViewModels;

/// <summary>
/// ViewModel de "Compartir Rutina".
/// Permite buscar un destinatario por nombre y enviarle una invitación de rutina.
///
/// Flujo:
///   1. Usuario escribe el nombre del destinatario y presiona "Buscar".
///   2. Selecciona un resultado de la lista.
///   3. Configura Modo y Repeticiones.
///   4. Marca "Soy Terapeuta" si aplica (muestra badge verificado al destinatario).
///   5. Presiona "Enviar Rutina" → POST /api/invitaciones.
/// </summary>
public sealed partial class CompartirRutinaViewModel : ViewModelBase
{
    private readonly Func<AppDbContext> _dbFactory;
    private readonly ApiSyncService     _apiSync;

    [ObservableProperty] private string   _busquedaNombre           = string.Empty;
    [ObservableProperty] private Usuario? _destinatarioSeleccionado;
    [ObservableProperty] private int      _modoActivo               = 0;
    [ObservableProperty] private int      _repeticionesObjetivo     = 10;
    [ObservableProperty] private bool     _esTerapeuta              = false;
    [ObservableProperty] private bool     _enviando                 = false;
    [ObservableProperty] private string   _mensajeEstado            = string.Empty;
    [ObservableProperty] private bool     _hayResultados            = false;

    public ObservableCollection<Usuario> ResultadosBusqueda { get; } = [];

    public CompartirRutinaViewModel(Func<AppDbContext> dbFactory, ApiSyncService apiSync)
    {
        _dbFactory = dbFactory;
        _apiSync   = apiSync;
    }

    // ── Comandos ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task BuscarDestinatarioAsync()
    {
        if (string.IsNullOrWhiteSpace(BusquedaNombre)) return;

        MensajeEstado = string.Empty;
        ResultadosBusqueda.Clear();
        DestinatarioSeleccionado = null;

        try
        {
            var resultados = await _apiSync.BuscarUsuariosAsync(BusquedaNombre);

            foreach (var u in resultados)
                ResultadosBusqueda.Add(u);

            HayResultados = resultados.Count > 0;

            if (resultados.Count == 0)
                MensajeEstado = "No se encontraron usuarios con ese nombre.";
        }
        catch (Exception ex)
        {
            MensajeEstado = $"Error al buscar: {ex.Message}";
            Debug.WriteLine($"[CompartirRutina] {ex}");
        }
    }

    [RelayCommand]
    private async Task EnviarInvitacionAsync()
    {
        if (DestinatarioSeleccionado is null)
        {
            MensajeEstado = "Selecciona un destinatario de la lista.";
            return;
        }

        Enviando = true;
        MensajeEstado = "Enviando invitación...";

        try
        {
            // Resolver identidad del remitente en la nube
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

            var remitenteId = await _apiSync.SincronizarUsuarioAsync(
                paciente.Nombre, paciente.Apellido, EsTerapeuta);

            if (remitenteId is null)
            {
                MensajeEstado = "Error: no se pudo conectar con la nube.";
                return;
            }

            var remitenteNombre = $"{paciente.Nombre} {paciente.Apellido}".Trim();

            var ok = await _apiSync.EnviarInvitacionAsync(
                remitenteId.Value,
                DestinatarioSeleccionado.Id,
                remitenteNombre,
                EsTerapeuta,
                ModoActivo,
                RepeticionesObjetivo);

            MensajeEstado = ok
                ? $"Rutina enviada a {DestinatarioSeleccionado.NombreCompleto}."
                : "Error al enviar. Intenta de nuevo.";

            if (ok)
            {
                DestinatarioSeleccionado = null;
                BusquedaNombre           = string.Empty;
                ResultadosBusqueda.Clear();
                HayResultados            = false;
            }
        }
        catch (Exception ex)
        {
            MensajeEstado = $"Error inesperado: {ex.Message}";
            Debug.WriteLine($"[CompartirRutina] {ex}");
        }
        finally
        {
            Enviando = false;
        }
    }
}
