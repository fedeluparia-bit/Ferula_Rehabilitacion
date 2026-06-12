using System;
using CommunityToolkit.Mvvm.ComponentModel;
using FerulaSoftware.App.Data;
using FerulaSoftware.App.Models;
using FerulaSoftware.App.Services;

namespace FerulaSoftware.App.ViewModels;

public sealed partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly IWebSocketService _ws;
    private readonly ApiSyncService    _apiSync;

    [ObservableProperty] private ViewModelBase _vistaActual = null!;

    public MainViewModel(IWebSocketService ws, ApiSyncService apiSync)
    {
        _ws      = ws;
        _apiSync = apiSync;
        NavegarALogin();
    }

    // ── Navegación ────────────────────────────────────────────────────────────

    public void NavegarALogin()
    {
        ApiSyncService.Logout();
        if (VistaActual is IDisposable anterior) anterior.Dispose();
        VistaActual = new LoginViewModel(NavegarADashboard, NavegarARegistro, _apiSync);
    }

    public void NavegarARegistro()
    {
        VistaActual = new RegistroViewModel(NavegarALogin, _apiSync);
    }

    public void NavegarADashboard(Usuario usuario)
    {
        VistaActual = new DashboardViewModel(
            _ws,
            () => new AppDbContext(),
            _apiSync,
            NavegarALogin);
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (VistaActual is IDisposable v) v.Dispose();
        _apiSync.Dispose();
        _ws.Dispose();
    }
}
