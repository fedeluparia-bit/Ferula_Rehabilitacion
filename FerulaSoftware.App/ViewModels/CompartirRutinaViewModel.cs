using System;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FerulaSoftware.App.Data;
using FerulaSoftware.App.Services;
using Microsoft.EntityFrameworkCore;

namespace FerulaSoftware.App.ViewModels;

public sealed partial class CompartirRutinaViewModel : ViewModelBase
{
    private readonly Func<AppDbContext> _dbFactory;
    private readonly ApiSyncService     _apiSync;

    [ObservableProperty] private string  _emailDestinatario    = string.Empty;
    [ObservableProperty] private int     _modoActivo           = 0;
    // decimal? para coincidir con NumericUpDown.Value (Avalonia); se castea a int al enviar.
    [ObservableProperty] private decimal? _repeticionesObjetivo = 10;
    [ObservableProperty] private bool    _esTerapeuta          = false;
    [ObservableProperty] private bool   _enviando             = false;
    [ObservableProperty] private string _mensajeEstado        = string.Empty;
    [ObservableProperty] private bool   _mensajeEsError       = false;
    [ObservableProperty] private bool   _mensajeEsExito       = false;

    public CompartirRutinaViewModel(Func<AppDbContext> dbFactory, ApiSyncService apiSync)
    {
        _dbFactory = dbFactory;
        _apiSync   = apiSync;
    }

    private void MostrarError(string mensaje)
    {
        MensajeEstado  = mensaje;
        MensajeEsError = true;
        MensajeEsExito = false;
    }

    private void MostrarExito(string mensaje)
    {
        MensajeEstado  = mensaje;
        MensajeEsError = false;
        MensajeEsExito = true;
    }

    [RelayCommand]
    private async Task EnviarInvitacionAsync()
    {
        if (string.IsNullOrWhiteSpace(EmailDestinatario))
        {
            MostrarError("Ingresa el correo del destinatario.");
            return;
        }

        Enviando       = true;
        MensajeEstado  = "Enviando invitación...";
        MensajeEsError = false;
        MensajeEsExito = false;

        try
        {
            // Usar el usuario logueado si está disponible; si no, sincronizar desde BD local.
            int    remitenteId;
            string remitenteNombre;
            if (ApiSyncService.UsuarioActual is { } loggedIn)
            {
                remitenteId     = loggedIn.Id;
                remitenteNombre = $"{loggedIn.Nombre} {loggedIn.Apellido}".Trim();
            }
            else
            {
                Models.Paciente? paciente = null;
                await Task.Run(async () =>
                {
                    await using var db = _dbFactory();
                    paciente = await db.Pacientes.FirstOrDefaultAsync();
                });

                if (paciente is null)
                {
                    MostrarError("Error: no hay paciente registrado localmente.");
                    return;
                }

                var syncId = await _apiSync.SincronizarUsuarioAsync(
                    paciente.Nombre, paciente.Apellido, EsTerapeuta);

                if (syncId is null)
                {
                    MostrarError("Error: no se pudo conectar con la nube.");
                    return;
                }

                remitenteId     = (int)syncId;
                remitenteNombre = $"{paciente.Nombre} {paciente.Apellido}".Trim();
            }

            var (ok, noEncontrado) = await _apiSync.EnviarInvitacionAsync(
                remitenteId,
                remitenteNombre,
                EsTerapeuta,
                EmailDestinatario.Trim(),
                ModoActivo,
                (int)(RepeticionesObjetivo ?? 10));

            if (noEncontrado)
            {
                MostrarError("El paciente no está registrado en el sistema.");
            }
            else if (ok)
            {
                MostrarExito("Rutina enviada correctamente.");
                EmailDestinatario = string.Empty;
            }
            else
            {
                MostrarError("Error al enviar. Intenta de nuevo.");
            }
        }
        catch (Exception ex)
        {
            MostrarError($"Error inesperado: {ex.Message}");
            Debug.WriteLine($"[CompartirRutina] {ex}");
        }
        finally
        {
            Enviando = false;
        }
    }
}
