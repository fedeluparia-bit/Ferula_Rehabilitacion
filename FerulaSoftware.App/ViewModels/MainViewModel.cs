using System;
using CommunityToolkit.Mvvm.ComponentModel;
using FerulaSoftware.App.Data;
using FerulaSoftware.App.Services;

namespace FerulaSoftware.App.ViewModels;

/// <summary>
/// Shell de navegación de la ventana principal.
/// Es el único propietario de <see cref="IWebSocketService"/> y
/// <see cref="ApiSyncService"/>; los dispone al cerrarse la aplicación.
/// </summary>
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
        if (VistaActual is IDisposable anterior) anterior.Dispose();
        VistaActual = new LoginViewModel(NavegarADashboard);
    }

    public void NavegarADashboard()
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
