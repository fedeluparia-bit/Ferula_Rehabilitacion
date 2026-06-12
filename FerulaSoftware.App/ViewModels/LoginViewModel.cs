using System;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FerulaSoftware.App.Models;
using FerulaSoftware.App.Services;

namespace FerulaSoftware.App.ViewModels;

public sealed partial class LoginViewModel : ViewModelBase
{
    private readonly Action<Usuario> _onIngreso;
    private readonly Action          _onRegistro;
    private readonly ApiSyncService  _apiSync;

    [ObservableProperty] private string _email      = string.Empty;
    [ObservableProperty] private string _contrasena = string.Empty;
    [ObservableProperty] private string _error      = string.Empty;
    [ObservableProperty] private bool   _cargando   = false;

    public LoginViewModel(Action<Usuario> onIngreso, Action onRegistro, ApiSyncService apiSync)
    {
        _onIngreso  = onIngreso;
        _onRegistro = onRegistro;
        _apiSync    = apiSync;
    }

    [RelayCommand]
    private async Task IngresarAsync()
    {
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Contrasena))
        {
            Error = "Ingresá tu correo y contraseña.";
            return;
        }

        Cargando = true;
        Error    = string.Empty;

        try
        {
            var usuario = await _apiSync.LoginAsync(Email.Trim(), Contrasena);

            if (usuario is null)
            {
                Error = "Credenciales incorrectas.";
                return;
            }

            _onIngreso(usuario);
        }
        catch (Exception ex)
        {
            Error = $"Error de conexión: {ex.Message}";
            Debug.WriteLine($"[Login] {ex}");
        }
        finally
        {
            Cargando = false;
        }
    }

    [RelayCommand]
    private void IrARegistro() => _onRegistro();
}
