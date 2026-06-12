using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FerulaSoftware.App.Data;
using FerulaSoftware.App.Services;

namespace FerulaSoftware.App.ViewModels;

/// <summary>
/// ViewModel del Dashboard post-login.
/// Gestiona la navegación lateral entre sub-vistas:
///   Sesión Libre, Rutinas Programadas y Ver Informes.
/// </summary>
public sealed partial class DashboardViewModel : ViewModelBase, IDisposable
{
    private readonly Action             _onLogout;
    private readonly Func<AppDbContext> _dbFactory;
    private readonly ApiSyncService     _apiSync;

    private readonly SesionLibreViewModel _sesionLibre;
    private RutinasProgramadasViewModel?  _rutinasProgramadas;
    private VerInformesViewModel?         _verInformes;

    [ObservableProperty] private ViewModelBase _vistaInterior;

    public DashboardViewModel(
        IWebSocketService   ws,
        Func<AppDbContext>  dbFactory,
        ApiSyncService      apiSync,
        Action              onLogout)
    {
        _dbFactory     = dbFactory;
        _apiSync       = apiSync;
        _onLogout      = onLogout;
        _sesionLibre   = new SesionLibreViewModel(ws, dbFactory);
        _vistaInterior = _sesionLibre;
    }

    // ── Comandos de navegación ────────────────────────────────────────────────

    [RelayCommand]
    private void NavegarASesionLibre() => VistaInterior = _sesionLibre;

    [RelayCommand]
    private void NavegarARutinasProgramadas() =>
        VistaInterior = (_rutinasProgramadas ??= new RutinasProgramadasViewModel());

    [RelayCommand]
    private void NavegarAInformes()
    {
        // Creación lazy: el ViewModel vive mientras dure el Dashboard
        _verInformes ??= new VerInformesViewModel(_dbFactory, _apiSync);
        VistaInterior = _verInformes;

        // Recarga el historial cada vez que el usuario entra a la vista,
        // para mostrar sesiones guardadas en la sesión actual de la app
        _ = _verInformes.CargarSesionesAsync();
    }

    [RelayCommand]
    private void Logout() => _onLogout();

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose() => _sesionLibre.Dispose();
}
