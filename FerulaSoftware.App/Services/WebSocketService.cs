using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FerulaSoftware.App.Models;

namespace FerulaSoftware.App.Services;

public sealed class WebSocketService : IWebSocketService
{
    // URI por defecto: 127.0.0.1 fuerza IPv4.
    // "localhost" en Windows resuelve ::1 (IPv6) primero; el forwarder de Wokwi
    // solo escucha en 127.0.0.1, por lo que la conexión sería rechazada con IPv6.
    public static readonly Uri DefaultUri = new("ws://127.0.0.1:81");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;

    public event EventHandler<TelemetryPacket>? TelemetryReceived;
    public event EventHandler<bool>?            ConnectionChanged;

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    // ── Conectar ─────────────────────────────────────────────────────────────
    // ClientWebSocket no puede reutilizarse tras cerrar → se crea una nueva instancia.
    public async Task ConnectAsync(Uri uri, CancellationToken ct = default)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _socket?.Dispose();

        _socket = new ClientWebSocket();
        await _socket.ConnectAsync(uri, ct);

        _cts = new CancellationTokenSource();

        ConnectionChanged?.Invoke(this, true);

        // Bucle de lectura en hilo de background — fire-and-forget controlado
        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token), CancellationToken.None);
    }

    // ── Enviar comando ───────────────────────────────────────────────────────
    // Serializa el EspCommand completo (cmd + val + mod) y lo envía como texto.
    // El ESP32 lee "mod" sólo en "start"; ignora el campo para otros comandos.
    public async Task SendCommandAsync(EspCommand command, CancellationToken ct = default)
    {
        if (_socket?.State != WebSocketState.Open) return;

        var json  = JsonSerializer.Serialize(command, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            ct);
    }

    // ── Desconectar ──────────────────────────────────────────────────────────
    public async Task DisconnectAsync()
    {
        _cts?.Cancel();

        if (_socket?.State == WebSocketState.Open)
        {
            await _socket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                string.Empty,
                CancellationToken.None);
        }
    }

    // ── Bucle de recepción (hilo de background) ──────────────────────────────
    // Lee mensajes WebSocket, que pueden llegar en múltiples segmentos.
    // Sólo propaga paquetes cuyo campo "t" == "tel" (ignora otros tipos futuros).
    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[1024];
        using var ms = new MemoryStream(capacity: 1024);

        try
        {
            while (!ct.IsCancellationRequested && _socket?.State == WebSocketState.Open)
            {
                ms.SetLength(0);   // reutiliza el buffer sin realocación

                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await _socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        string.Empty,
                        CancellationToken.None);
                    break;
                }

                if (result.MessageType != WebSocketMessageType.Text) continue;

                // Deserializar directamente desde el buffer interno del MemoryStream
                // AsSpan(0, Length) evita ToArray() → cero asignaciones adicionales
                var packet = JsonSerializer.Deserialize<TelemetryPacket>(
                    ms.GetBuffer().AsSpan(0, (int)ms.Length),
                    JsonOptions);

                if (packet?.Type == "tel")
                    TelemetryReceived?.Invoke(this, packet);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException)         { }
        finally
        {
            ConnectionChanged?.Invoke(this, false);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _socket?.Dispose();
    }
}
