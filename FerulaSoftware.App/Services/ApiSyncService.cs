using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using FerulaSoftware.App.Models;

namespace FerulaSoftware.App.Services;

/// <summary>
/// Cliente HTTP para sincronizar sesiones de rehabilitación con la API en la nube.
///
/// Decisiones de diseño:
///   · Un único HttpClient por instancia de servicio → evita socket exhaustion.
///   · BaseAddress configurable → permite apuntar a localhost (pruebas) o a Render.com.
///   · Payload construido como objeto anónimo → elimina propiedades de navegación
///     de EF Core antes de serializar, previniendo ciclos y reduciendo el tamaño del body.
///   · Tres capas de catch → HttpRequestException (red), TaskCanceledException
///     (timeout), Exception (serialización u otros).
///   · CancellationTokenSource reutilizable para auto-cancelar el timer
///     del mensaje de estado si se ejecuta el comando dos veces seguidas.
/// </summary>
public sealed class ApiSyncService : IDisposable
{
    public static readonly Uri DefaultBaseAddress = new("http://localhost:8080/");

    private readonly HttpClient _http;

    // camelCase para que coincida con la convención de la API ASP.NET Core.
    // IgnoreCycles como red de seguridad ante referencias cruzadas de EF Core.
    // WhenWritingNull para omitir Paciente/Sesion navigation properties si son null.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        ReferenceHandler       = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ApiSyncService(Uri? baseAddress = null)
    {
        _http = new HttpClient
        {
            BaseAddress = baseAddress ?? DefaultBaseAddress,
            Timeout     = TimeSpan.FromSeconds(15)   // falla rápido si el servidor no responde
        };
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    // ── API pública ───────────────────────────────────────────────────────────

    /// <summary>
    /// Crea o devuelve el paciente en la nube y retorna su ID de PostgreSQL.
    /// Null indica error de red o respuesta no exitosa.
    /// </summary>
    public async Task<int?> SincronizarPacienteAsync(Paciente paciente)
    {
        try
        {
            var payload = new
            {
                nombre      = paciente.Nombre,
                apellido    = paciente.Apellido,
                fechaInicio = paciente.FechaInicio
            };
            var json          = JsonSerializer.Serialize(payload, JsonOpts);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _http.PostAsync("/api/pacientes", content);
            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine(
                    $"[ApiSync] POST /api/pacientes → HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                return null;
            }

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("id").GetInt32();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ApiSync] SincronizarPacienteAsync: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Serializa y envía una sesión + su telemetría a <c>POST /api/sesiones</c>.
    /// Usa el ID de paciente de la nube obtenido de <see cref="SincronizarPacienteAsync"/>.
    ///
    /// Flujo feliz: 2xx → true.
    /// Flujo de error:
    ///   · HttpRequestException  → servidor caído / sin red / conexión rechazada
    ///   · TaskCanceledException → timeout de 15 s
    ///   · Exception             → error inesperado de serialización u otro
    /// En todos los casos de error se devuelve false y se escribe en Debug.
    /// </summary>
    public async Task<bool> SincronizarSesionAsync(Sesion sesion, Paciente paciente, List<DetalleTelemetria> detalles)
    {
        // Asegurar que el paciente existe en la nube y obtener su ID de PostgreSQL
        var cloudPacienteId = await SincronizarPacienteAsync(paciente);
        if (cloudPacienteId is null)
            return false;

        try
        {
            // ── Construir payload sin navigation properties ────────────────────
            var payload = new
            {
                pacienteId           = cloudPacienteId.Value,
                fechaHora            = sesion.FechaHora,
                modoActivo           = sesion.ModoActivo,
                repeticionesObjetivo = sesion.RepeticionesObjetivo,
                repeticionesHechas   = sesion.RepeticionesHechas,
                presionMaxima        = sesion.PresionMaxima,
                detalles             = detalles.Select(d => new
                {
                    d.Milisegundo,
                    d.Motor0Angulo,
                    d.Motor0Presion,
                    d.Motor1Angulo,
                    d.Motor1Presion
                })
            };

            var json          = JsonSerializer.Serialize(payload, JsonOpts);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _http.PostAsync("/api/sesiones", content);

            if (!response.IsSuccessStatusCode)
                Debug.WriteLine(
                    $"[ApiSync] POST /api/sesiones → HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException ex)
        {
            // Red caída, servidor no disponible, conexión rechazada (ECONNREFUSED)
            Debug.WriteLine($"[ApiSync] HttpRequestException: {ex.Message}");
            return false;
        }
        catch (TaskCanceledException ex) when (!ex.CancellationToken.IsCancellationRequested)
        {
            // Timeout — se agotaron los 15 s sin respuesta
            Debug.WriteLine("[ApiSync] Timeout: el servidor no respondió en 15 s.");
            return false;
        }
        catch (Exception ex)
        {
            // Error inesperado: serialización rota, BadImageFormat, etc.
            Debug.WriteLine($"[ApiSync] Error inesperado: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Llama a <c>GET /api/status</c> para verificar disponibilidad sin enviar datos.
    /// Útil para comprobar la conectividad antes de mostrar el botón activo.
    /// </summary>
    public async Task<bool> VerificarConexionAsync()
    {
        try
        {
            var response = await _http.GetAsync("/api/status");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}
