using System;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FerulaSoftware.App.Services;

namespace FerulaSoftware.App.ViewModels;

public sealed partial class RegistroViewModel : ViewModelBase
{
    private readonly Action         _onVolver;
    private readonly ApiSyncService _apiSync;

    [ObservableProperty] private string _nombre      = string.Empty;
    [ObservableProperty] private string _apellido    = string.Empty;
    [ObservableProperty] private string _email       = string.Empty;
    [ObservableProperty] private string _password    = string.Empty;
    [ObservableProperty] private bool   _esTerapeuta = false;
    [ObservableProperty] private bool   _cargando    = false;
    [ObservableProperty] private string _error       = string.Empty;
    [ObservableProperty] private bool   _exitoso     = false;

    public RegistroViewModel(Action onVolver, ApiSyncService apiSync)
    {
        _onVolver = onVolver;
        _apiSync  = apiSync;
    }

    [RelayCommand]
    private async Task CrearCuentaAsync()
    {
        // ── Validación local ──────────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(Nombre))
        {
            Error = "El nombre es obligatorio.";
            return;
        }
        if (string.IsNullOrWhiteSpace(Apellido))
        {
            Error = "El apellido es obligatorio.";
            return;
        }
        if (string.IsNullOrWhiteSpace(Email) || !Email.Contains('@'))
        {
            Error = "Ingresá un correo electrónico válido.";
            return;
        }
        if (Password.Length < 6)
        {
            Error = "La contraseña debe tener al menos 6 caracteres.";
            return;
        }

        Cargando = true;
        Error    = string.Empty;
        Exitoso  = false;

        try
        {
            var (ok, emailYaExiste) = await _apiSync.RegistrarUsuarioAsync(
                Nombre.Trim(), Apellido.Trim(), Email.Trim(), Password, EsTerapeuta);

            if (emailYaExiste)
            {
                Error = "Ese correo ya está registrado. Iniciá sesión.";
                return;
            }

            if (!ok)
            {
                Error = "Error al crear la cuenta. Intentá de nuevo.";
                return;
            }

            Exitoso = true;
            await Task.Delay(1500);
            _onVolver();
        }
        catch (Exception ex)
        {
            Error = $"Error de conexión: {ex.Message}";
            Debug.WriteLine($"[Registro] {ex}");
        }
        finally
        {
            Cargando = false;
        }
    }

    [RelayCommand]
    private void VolverAlLogin() => _onVolver();
}
