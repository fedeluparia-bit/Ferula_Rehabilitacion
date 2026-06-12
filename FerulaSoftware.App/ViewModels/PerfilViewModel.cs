using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FerulaSoftware.App.Services;

namespace FerulaSoftware.App.ViewModels;

public sealed partial class PerfilViewModel : ViewModelBase
{
    private readonly Action _onLogout;

    // ── Datos del usuario (inmutables en esta pantalla) ───────────────────────
    public string Nombre      { get; }
    public string Apellido    { get; }
    public string Email       { get; }
    public bool   EsTerapeuta { get; }

    // ── Propiedades computadas ────────────────────────────────────────────────
    public string NombreCompleto => $"{Nombre} {Apellido}".Trim();
    public string Iniciales      => $"{Nombre.FirstOrDefault()}{Apellido.FirstOrDefault()}".ToUpperInvariant();
    public string RolTexto       => EsTerapeuta ? "Terapeuta Verificado" : "Paciente";
    public string RolColor       => EsTerapeuta ? "#4CC9F0" : "#3FB950";

    public PerfilViewModel(Action onLogout)
    {
        _onLogout = onLogout;

        var u     = ApiSyncService.UsuarioActual;
        Nombre      = u?.Nombre      ?? "—";
        Apellido    = u?.Apellido    ?? "—";
        Email       = u?.Email       ?? "—";
        EsTerapeuta = u?.EsTerapeuta ?? false;
    }

    [RelayCommand]
    private void Logout() => _onLogout();
}
