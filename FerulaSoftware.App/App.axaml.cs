using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FerulaSoftware.App.Data;
using FerulaSoftware.App.Services;
using FerulaSoftware.App.ViewModels;
using FerulaSoftware.App.Views;
using QuestPDF.Infrastructure;

namespace FerulaSoftware.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // ── Licencia QuestPDF (Community — gratuita para proyectos open-source) ─
        QuestPDF.Settings.License = LicenseType.Community;

        // ── Inicializar base de datos SQLite ──────────────────────────────────
        // EnsureCreated() crea el archivo ferula_local.db y su esquema si no existen.
        // Seguro de llamar repetidamente: no hace nada si la BD ya existe.
        // En una futura fase se reemplazará por migraciones (Migrate()) para
        // soportar actualizaciones de esquema sin pérdida de datos.
        using (var db = new AppDbContext())
        {
            db.Database.EnsureCreated();
        }

        // ── Inicializar capa de navegación ────────────────────────────────────
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var wsService  = new WebSocketService();
            var apiSync    = new ApiSyncService();    // apunta a http://localhost:8080 por defecto
            var mainVm     = new MainViewModel(wsService, apiSync);

            desktop.MainWindow = new MainWindow { DataContext = mainVm };

            // mainVm.Dispose() propaga → DashboardViewModel → SesionLibreViewModel (WS events)
            //                          → ApiSyncService.Dispose() (HttpClient)
            //                          → WebSocketService.Dispose() (socket)
            desktop.Exit += (_, _) => mainVm.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
