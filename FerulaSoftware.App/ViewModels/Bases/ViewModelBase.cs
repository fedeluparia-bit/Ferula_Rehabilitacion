using CommunityToolkit.Mvvm.ComponentModel;

// Namespace intencional: igual al directorio padre para que el ViewLocator
// resuelva FerulaSoftware.App.ViewModels.*ViewModel → Views.*View sin cambios.
namespace FerulaSoftware.App.ViewModels;

/// <summary>Clase base para todos los ViewModels del proyecto.</summary>
public abstract class ViewModelBase : ObservableObject
{
}
