using System;
using System.Threading;
using System.Threading.Tasks;
using FerulaSoftware.App.Models;

namespace FerulaSoftware.App.Services;

public interface IWebSocketService : IDisposable
{
    /// <summary>Se dispara en el hilo de background del WebSocket al recibir telemetría válida.</summary>
    event EventHandler<TelemetryPacket>? TelemetryReceived;

    /// <summary>true = conectado, false = desconectado o error.</summary>
    event EventHandler<bool>? ConnectionChanged;

    bool IsConnected { get; }

    Task ConnectAsync(Uri uri, CancellationToken ct = default);
    Task SendCommandAsync(EspCommand command, CancellationToken ct = default);
    Task DisconnectAsync();
}
