using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FerulaSoftware.App.ViewModels;

/// <summary>
/// ViewModel de la pantalla de inicio de sesión.
/// El botón "Ingresar" no valida credenciales en este PoC —
/// navega directamente al Dashboard para demostrar el flujo de navegación.
/// </summary>
public sealed partial class LoginViewModel : ViewModelBase
{
    private readonly Action _onIngreso;

    [ObservableProperty] private string _usuario     = string.Empty;
    [ObservableProperty] private string _contrasena  = string.Empty;

    public LoginViewModel(Action onIngreso) => _onIngreso = onIngreso;

    /// <summary>Navega al Dashboard. (PoC: sin validación de credenciales.)</summary>
    [RelayCommand]
    private void Ingresar() => _onIngreso();
}
