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
    public static readonly Uri DefaultBaseAddress = new("https://ferula-rehabilitacion.onrender.com/");
    public static readonly Uri LocalBaseAddress   = new("http://localhost:8080/");

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

    /// <summary>Usuario autenticado actualmente. Null si no hay sesión activa.</summary>
    public static Models.Usuario? UsuarioActual { get; private set; }

    public ApiSyncService(Uri? baseAddress = null)
    {
        _http = new HttpClient
        {
            BaseAddress = baseAddress ?? DefaultBaseAddress,
            Timeout     = TimeSpan.FromSeconds(15)   // falla rápido si el servidor no responde
        };
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    // ── Autenticación ────────────────────────────────────────────────────────

    /// <summary>
    /// Autentica al usuario contra <c>POST /api/auth/login</c>.
    /// En éxito guarda el resultado en <see cref="UsuarioActual"/> y lo devuelve.
    /// Devuelve null si las credenciales son incorrectas o hay error de red.
    /// </summary>
    public async Task<Models.Usuario?> LoginAsync(string email, string password)
    {
        try
        {
            var payload = new { email, password };
            var json    = JsonSerializer.Serialize(payload, JsonOpts);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _http.PostAsync("/api/auth/login", content);
            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"[ApiSync] POST /api/auth/login → HTTP {(int)response.StatusCode}");
                return null;
            }

            var body = await response.Content.ReadAsStringAsync();
            UsuarioActual = JsonSerializer.Deserialize<Models.Usuario>(body, JsonOpts);
            return UsuarioActual;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ApiSync] LoginAsync: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Limpia la sesión local (no invalida nada en el servidor).</summary>
    public static void Logout() => UsuarioActual = null;

    /// <summary>
    /// Registra un nuevo usuario (<c>POST /api/auth/registro</c>).
    /// Retorna <c>(true, false)</c> en éxito, <c>(false, true)</c> si el email
    /// ya existe (409) y <c>(false, false)</c> ante cualquier otro error.
    /// </summary>
    public async Task<(bool Success, bool EmailYaExiste)> RegistrarUsuarioAsync(
        string nombre,
        string apellido,
        string email,
        string password,
        bool   esTerapeuta = false)
    {
        try
        {
            var payload = new { nombre, apellido, email, password, esTerapeuta };
            var json    = JsonSerializer.Serialize(payload, JsonOpts);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _http.PostAsync("/api/auth/registro", content);

            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                return (false, true);

            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"[ApiSync] POST /api/auth/registro → HTTP {(int)response.StatusCode}");
                return (false, false);
            }

            return (true, false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ApiSync] RegistrarUsuarioAsync: {ex.GetType().Name}: {ex.Message}");
            return (false, false);
        }
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

    // ── Usuarios ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Crea o devuelve el usuario en la nube identificado por Nombre+Apellido.
    /// Actualiza <c>EsTerapeuta</c> si cambió. Devuelve el ID de PostgreSQL.
    /// </summary>
    public async Task<int?> SincronizarUsuarioAsync(
        string  nombre,
        string  apellido,
        bool    esTerapeuta = false,
        string? email       = null)
    {
        try
        {
            var payload = new { nombre, apellido, esTerapeuta, email };
            var json    = JsonSerializer.Serialize(payload, JsonOpts);
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await _http.PostAsync("/api/usuarios", content);
            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"[ApiSync] POST /api/usuarios → HTTP {(int)response.StatusCode}");
                return null;
            }

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("id").GetInt32();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ApiSync] SincronizarUsuarioAsync: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Busca usuarios en la nube cuyo nombre o apellido contenga <paramref name="query"/>.
    /// </summary>
    public async Task<List<Models.Usuario>> BuscarUsuariosAsync(string query)
    {
        try
        {
            var encoded  = Uri.EscapeDataString(query);
            var response = await _http.GetAsync($"/api/usuarios?nombre={encoded}");
            if (!response.IsSuccessStatusCode)
                return [];

            var body = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<List<Models.Usuario>>(body, JsonOpts) ?? [];
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ApiSync] BuscarUsuariosAsync: {ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }

    // ── Invitaciones ─────────────────────────────────────────────────────────

    /// <summary>
    /// Envía una invitación de rutina buscando al destinatario por email exacto.
    /// Retorna <c>(true, false)</c> en éxito, <c>(false, true)</c> si el email no está
    /// registrado (404) y <c>(false, false)</c> ante cualquier otro error.
    /// </summary>
    public async Task<(bool Success, bool DestinatarioNoEncontrado)> EnviarInvitacionAsync(
        int    remitenteId,
        string remitenteNombre,
        bool   remitenteEsTerapeuta,
        string emailDestinatario,
        int    modoActivo,
        int    repeticionesObjetivo)
    {
        try
        {
            var payload = new
            {
                remitenteId,
                remitenteNombre,
                remitenteEsTerapeuta,
                emailDestinatario,
                modoActivo,
                repeticionesObjetivo
            };
            var json    = JsonSerializer.Serialize(payload, JsonOpts);
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await _http.PostAsync("/api/invitaciones", content);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return (false, true);

            if (!response.IsSuccessStatusCode)
                Debug.WriteLine($"[ApiSync] POST /api/invitaciones → HTTP {(int)response.StatusCode}");

            return (response.IsSuccessStatusCode, false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ApiSync] EnviarInvitacionAsync: {ex.GetType().Name}: {ex.Message}");
            return (false, false);
        }
    }

    /// <summary>
    /// Descarga las invitaciones pendientes de la bandeja de entrada de un usuario.
    /// </summary>
    public async Task<List<Models.InvitacionRutina>> ObtenerInvitacionesPendientesAsync(int usuarioId)
    {
        try
        {
            var response = await _http.GetAsync($"/api/invitaciones/pendientes/{usuarioId}");
            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"[ApiSync] GET /api/invitaciones/pendientes/{usuarioId} → HTTP {(int)response.StatusCode}");
                return [];
            }

            var body = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<List<Models.InvitacionRutina>>(body, JsonOpts) ?? [];
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ApiSync] ObtenerInvitacionesPendientesAsync: {ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Acepta o rechaza una invitación. Cuando acepta, la API crea la Rutina atómicamente.
    /// </summary>
    public async Task<bool> ResponderInvitacionAsync(int invitacionId, bool aceptada, int? cloudPacienteId)
    {
        try
        {
            var payload = new { aceptada, pacienteId = cloudPacienteId };
            var json    = JsonSerializer.Serialize(payload, JsonOpts);
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await _http.PostAsync($"/api/invitaciones/{invitacionId}/responder", content);
            if (!response.IsSuccessStatusCode)
                Debug.WriteLine($"[ApiSync] POST /api/invitaciones/{invitacionId}/responder → HTTP {(int)response.StatusCode}");

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ApiSync] ResponderInvitacionAsync: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Descarga las rutinas pendientes (<c>Completada = false</c>) de un paciente
    /// identificado por su ID en la nube (PostgreSQL).
    /// Devuelve lista vacía ante cualquier error de red o respuesta no exitosa.
    /// </summary>
    public async Task<List<Models.Rutina>> ObtenerRutinasPendientesAsync(int cloudPacienteId)
    {
        try
        {
            var response = await _http.GetAsync($"/api/rutinas/paciente/{cloudPacienteId}");
            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine(
                    $"[ApiSync] GET /api/rutinas/paciente/{cloudPacienteId} → HTTP {(int)response.StatusCode}");
                return [];
            }

            var body = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<List<Models.Rutina>>(body, JsonOpts) ?? [];
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ApiSync] ObtenerRutinasPendientesAsync: {ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Llama a <c>PATCH /api/rutinas/{id}/completar</c> para marcar la rutina como realizada.
    /// </summary>
    public async Task<bool> MarcarRutinaCompletadaAsync(int rutinaId)
    {
        try
        {
            var response = await _http.PatchAsync($"/api/rutinas/{rutinaId}/completar", null);

            if (!response.IsSuccessStatusCode)
                Debug.WriteLine(
                    $"[ApiSync] PATCH /api/rutinas/{rutinaId}/completar → HTTP {(int)response.StatusCode}");

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ApiSync] MarcarRutinaCompletadaAsync: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // ── Consultas de lectura para el panel de terapeuta ──────────────────────

    /// <summary>
    /// Lista todos los pacientes registrados en la nube, ordenados por apellido.
    /// </summary>
    public async Task<List<Models.Paciente>> ObtenerPacientesAsync()
    {
        try
        {
            var response = await _http.GetAsync("/api/pacientes");
            if (!response.IsSuccessStatusCode) return [];
            var body = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<List<Models.Paciente>>(body, JsonOpts) ?? [];
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ApiSync] ObtenerPacientesAsync: {ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Descarga el historial de sesiones de un paciente (sin telemetría) desde la nube.
    /// </summary>
    public async Task<List<Models.Sesion>> ObtenerHistorialPacienteAsync(int pacienteId)
    {
        try
        {
            var response = await _http.GetAsync($"/api/sesiones/paciente/{pacienteId}");
            if (!response.IsSuccessStatusCode) return [];
            var body = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<List<Models.Sesion>>(body, JsonOpts) ?? [];
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ApiSync] ObtenerHistorialPacienteAsync: {ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Descarga la telemetría completa de una sesión cloud para reconstruir las gráficas.
    /// </summary>
    public async Task<List<Models.DetalleTelemetria>> ObtenerDetallesSesionAsync(int sesionId)
    {
        try
        {
            var response = await _http.GetAsync($"/api/sesiones/{sesionId}/detalles");
            if (!response.IsSuccessStatusCode) return [];
            var body = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<List<Models.DetalleTelemetria>>(body, JsonOpts) ?? [];
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ApiSync] ObtenerDetallesSesionAsync: {ex.GetType().Name}: {ex.Message}");
            return [];
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
