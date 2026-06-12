using Ferula.Api.Data;
using Ferula.Api.Models;
using Microsoft.EntityFrameworkCore;

// ── Compatibilidad de timestamps con PostgreSQL ───────────────────────────────
// SQLite devuelve DateTime con Kind=Unspecified al cargar desde la BD local.
// PostgreSQL (timestamptz) exige Kind=Utc y rechaza Unspecified con excepción.
// Este switch ordena a Npgsql tratar todos los DateTime como UTC,
// independientemente de su Kind. Debe ejecutarse antes de cualquier uso de Npgsql.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

// ──────────────────────────────────────────────────────────────────────────────
// Builder
// ──────────────────────────────────────────────────────────────────────────────
var builder = WebApplication.CreateBuilder(args);

// ── Puerto dinámico para despliegue en la nube (Render.com) ──────────────────
// Render inyecta la variable de entorno PORT en cada contenedor.
// En desarrollo local se cae al valor 8080 si PORT no está definida.
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://*:{port}");

// ── Cadena de conexión ────────────────────────────────────────────────────────
// Prioridad:
//   1. DATABASE_URL   → variable de entorno de Render.com (URI PostgreSQL estándar)
//   2. DefaultConnection → appsettings.json / appsettings.Development.json
var connectionString =
    Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException(
        "No se encontró cadena de conexión. " +
        "Define DATABASE_URL (producción) o ConnectionStrings:DefaultConnection (desarrollo).");

// Npgsql no acepta URIs postgres:// directamente — las convierte a formato key=value.
// Render.com inyecta DATABASE_URL como URI estándar, así que hay que convertirla.
if (connectionString.StartsWith("postgres://") || connectionString.StartsWith("postgresql://"))
{
    var uri      = new Uri(connectionString);
    var userInfo = uri.UserInfo.Split(':');
    connectionString =
        $"Host={uri.Host};Port={uri.Port};Database={uri.AbsolutePath.TrimStart('/')};" +
        $"Username={userInfo[0]};Password={Uri.UnescapeDataString(userInfo[1])};" +
        $"SSL Mode=Require;Trust Server Certificate=true";
}

// ── DbContext con PostgreSQL (Npgsql) ─────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

// ── OpenAPI (Swagger UI disponible en /openapi/v1.json en desarrollo) ─────────
builder.Services.AddOpenApi();

// ──────────────────────────────────────────────────────────────────────────────
// App
// ──────────────────────────────────────────────────────────────────────────────
var app = builder.Build();

// ── Crear / verificar el esquema de BD al iniciar ─────────────────────────────
// EnsureCreated se envuelve en try/catch para que la API arranque aunque
// PostgreSQL no esté disponible todavía (frecuente en Render.com donde el DB
// puede tardar unos segundos más que el servicio web en estar listo).
// El endpoint /api/status reporta el estado real de la BD en tiempo de ejecución.
using (var scope = app.Services.CreateScope())
{
    var db     = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        db.Database.EnsureCreated();

        // Migración idempotente: crea la tabla Rutinas si no existe todavía.
        // EnsureCreated solo crea el esquema completo en una BD nueva; no altera
        // una BD existente. Este bloque cubre la ruta de actualización en producción.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "Rutinas" (
                "Id"                   INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "PacienteId"           INTEGER NOT NULL,
                "FechaAsignacion"      TIMESTAMP WITHOUT TIME ZONE NOT NULL,
                "ModoActivo"           INTEGER NOT NULL DEFAULT 0,
                "RepeticionesObjetivo" INTEGER NOT NULL DEFAULT 10,
                "Completada"           BOOLEAN NOT NULL DEFAULT FALSE,
                CONSTRAINT "FK_Rutinas_Pacientes_PacienteId"
                    FOREIGN KEY ("PacienteId") REFERENCES "Pacientes" ("Id") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_Rutinas_PacienteId" ON "Rutinas" ("PacienteId");
            """);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "Usuarios" (
                "Id"          INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "Nombre"      VARCHAR(100) NOT NULL,
                "Apellido"    VARCHAR(100) NOT NULL,
                "EsTerapeuta" BOOLEAN NOT NULL DEFAULT FALSE
            );

            CREATE TABLE IF NOT EXISTS "InvitacionesRutina" (
                "Id"                   INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "RemitenteId"          INTEGER NOT NULL,
                "DestinatarioId"       INTEGER NOT NULL,
                "RemitenteNombre"      VARCHAR(200) NOT NULL,
                "RemitenteEsTerapeuta" BOOLEAN NOT NULL DEFAULT FALSE,
                "ModoActivo"           INTEGER NOT NULL DEFAULT 0,
                "RepeticionesObjetivo" INTEGER NOT NULL DEFAULT 10,
                "Estado"               VARCHAR(20) NOT NULL DEFAULT 'Pendiente',
                "FechaEnvio"           TIMESTAMP WITHOUT TIME ZONE NOT NULL,
                CONSTRAINT "FK_InvitacionesRutina_Usuarios_RemitenteId"
                    FOREIGN KEY ("RemitenteId") REFERENCES "Usuarios" ("Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_InvitacionesRutina_Usuarios_DestinatarioId"
                    FOREIGN KEY ("DestinatarioId") REFERENCES "Usuarios" ("Id") ON DELETE RESTRICT
            );

            CREATE INDEX IF NOT EXISTS "IX_InvitacionesRutina_DestinatarioId_Estado"
                ON "InvitacionesRutina" ("DestinatarioId", "Estado");
            """);

        // Columna Email agregada en fase de invitaciones por correo electrónico.
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE "Usuarios" ADD COLUMN IF NOT EXISTS "Email" VARCHAR(200);
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Usuarios_Email"
                ON "Usuarios" ("Email") WHERE "Email" IS NOT NULL;
            """);

        logger.LogInformation("Esquema de base de datos verificado correctamente.");
    }
    catch (Exception ex)
    {
        logger.LogWarning(
            "No se pudo conectar a la BD al iniciar: {Message}. " +
            "La API continúa operativa — reintentará en cada operación.",
            ex.Message);
    }
}

// ── OpenAPI solo en Development ───────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// ──────────────────────────────────────────────────────────────────────────────
// ENDPOINTS — Minimal APIs
// ──────────────────────────────────────────────────────────────────────────────

// ┌─────────────────────────────────────────────────────────────────────────────
// │ GET /api/status
// │ Health-check: devuelve estado del servidor y de la conexión a PostgreSQL.
// └─────────────────────────────────────────────────────────────────────────────
app.MapGet("/api/status", async (AppDbContext db) =>
{
    string dbStatus;
    try
    {
        // ExecuteSqlRaw lanza excepción con mensaje detallado; CanConnectAsync solo retorna bool.
        await db.Database.ExecuteSqlRawAsync("SELECT 1");
        dbStatus = "ok";
    }
    catch (Exception ex)
    {
        dbStatus = $"error: {ex.GetType().Name}: {ex.Message}";
    }
    return Results.Ok(new
    {
        status    = "online",
        db        = dbStatus,
        timestamp = DateTime.UtcNow
    });
})
.WithName("GetStatus")
.WithTags("Health");

// ┌─────────────────────────────────────────────────────────────────────────────
// │ POST /api/pacientes
// │ Crea el paciente si no existe, o devuelve el existente (upsert por nombre).
// │ La app local usa IDs de SQLite que no coinciden con los de PostgreSQL, por
// │ lo que debe sincronizar el paciente primero y usar el ID devuelto.
// └─────────────────────────────────────────────────────────────────────────────
app.MapPost("/api/pacientes", async (Paciente paciente, AppDbContext db) =>
{
    var fecha    = DateTime.SpecifyKind(paciente.FechaInicio, DateTimeKind.Utc).Date;
    var fechaFin = fecha.AddDays(1);

    var existente = await db.Pacientes.FirstOrDefaultAsync(p =>
        p.Nombre    == paciente.Nombre   &&
        p.Apellido  == paciente.Apellido &&
        p.FechaInicio >= fecha && p.FechaInicio < fechaFin);

    if (existente is not null)
        return Results.Ok(new { id = existente.Id });

    paciente.Id          = 0;
    paciente.FechaInicio = DateTime.SpecifyKind(paciente.FechaInicio, DateTimeKind.Utc);
    db.Pacientes.Add(paciente);
    await db.SaveChangesAsync();

    return Results.Created($"/api/pacientes/{paciente.Id}", new { id = paciente.Id });
})
.WithName("UpsertPaciente")
.WithTags("Pacientes");

// ┌─────────────────────────────────────────────────────────────────────────────
// │ POST /api/sesiones
// │ Recibe una Sesion con su colección Detalles y la persiste en PostgreSQL.
// │ EF Core asigna automáticamente los SesionId a los DetalleTelemetria al
// │ resolver la propiedad de navegación.
// │
// │ Body JSON:
// │ {
// │   "pacienteId": 1,
// │   "fechaHora": "2026-06-02T14:30:00Z",
// │   "modoActivo": 0,
// │   "repeticionesObjetivo": 10,
// │   "repeticionesHechas": 8,
// │   "presionMaxima": 1840,
// │   "detalles": [
// │     { "milisegundo": 0,   "motor0Angulo": 0, "motor0Presion": 120, ... },
// │     { "milisegundo": 100, "motor0Angulo": 2, "motor0Presion": 340, ... }
// │   ]
// │ }
// └─────────────────────────────────────────────────────────────────────────────
app.MapPost("/api/sesiones", async (Sesion sesion, AppDbContext db) =>
{
    // Rechazar Ids del cliente — la BD asigna las PKs
    sesion.Id = 0;

    // Normalizar FechaHora a UTC.
    // SQLite no preserva DateTimeKind, así que al cargar desde la BD local el
    // Kind llega como Unspecified. PostgreSQL exige Kind=Utc en columnas timestamptz.
    sesion.FechaHora = DateTime.SpecifyKind(sesion.FechaHora, DateTimeKind.Utc);

    foreach (var detalle in sesion.Detalles)
    {
        detalle.Id       = 0;
        detalle.SesionId = 0;   // EF lo resolverá por la navegación
    }

    db.Sesiones.Add(sesion);
    await db.SaveChangesAsync();

    return Results.Created($"/api/sesiones/{sesion.Id}", new { id = sesion.Id });
})
.WithName("CreateSesion")
.WithTags("Sesiones");

// ┌─────────────────────────────────────────────────────────────────────────────
// │ GET /api/sesiones
// │ Lista de sesiones ordenada por fecha descendente (sin detalles de telemetría
// │ para mantener el payload reducido).
// └─────────────────────────────────────────────────────────────────────────────
app.MapGet("/api/sesiones", async (AppDbContext db) =>
    Results.Ok(
        await db.Sesiones
            .OrderByDescending(s => s.FechaHora)
            .Select(s => new
            {
                s.Id,
                s.PacienteId,
                s.FechaHora,
                s.ModoActivo,
                s.RepeticionesObjetivo,
                s.RepeticionesHechas,
                s.PresionMaxima
            })
            .ToListAsync()))
.WithName("GetSesiones")
.WithTags("Sesiones");

// ┌─────────────────────────────────────────────────────────────────────────────
// │ GET /api/sesiones/{id}/detalles
// │ Telemetría completa de una sesión específica, ordenada por tiempo.
// └─────────────────────────────────────────────────────────────────────────────
app.MapGet("/api/sesiones/{id:int}/detalles", async (int id, AppDbContext db) =>
{
    var detalles = await db.DetallesTelemetria
        .Where(d => d.SesionId == id)
        .OrderBy(d => d.Milisegundo)
        .ToListAsync();

    return detalles.Count > 0
        ? Results.Ok(detalles)
        : Results.NotFound(new { mensaje = $"No hay detalles para la sesión #{id}." });
})
.WithName("GetDetallesSesion")
.WithTags("Sesiones");

// ┌─────────────────────────────────────────────────────────────────────────────
// │ POST /api/rutinas
// │ Crea una rutina asignada por el fisioterapeuta a un paciente.
// └─────────────────────────────────────────────────────────────────────────────
app.MapPost("/api/rutinas", async (Rutina rutina, AppDbContext db) =>
{
    rutina.Id         = 0;
    rutina.Completada = false;
    rutina.FechaAsignacion = DateTime.SpecifyKind(
        rutina.FechaAsignacion == default ? DateTime.UtcNow : rutina.FechaAsignacion,
        DateTimeKind.Utc);

    db.Rutinas.Add(rutina);
    await db.SaveChangesAsync();

    return Results.Created($"/api/rutinas/{rutina.Id}", new { id = rutina.Id });
})
.WithName("CreateRutina")
.WithTags("Rutinas");

// ┌─────────────────────────────────────────────────────────────────────────────
// │ GET /api/rutinas/paciente/{pacienteId}
// │ Lista las rutinas pendientes (Completada = false) de un paciente.
// └─────────────────────────────────────────────────────────────────────────────
app.MapGet("/api/rutinas/paciente/{pacienteId:int}", async (int pacienteId, AppDbContext db) =>
{
    var rutinas = await db.Rutinas
        .Where(r => r.PacienteId == pacienteId && !r.Completada)
        .OrderByDescending(r => r.FechaAsignacion)
        .Select(r => new
        {
            r.Id,
            r.PacienteId,
            r.FechaAsignacion,
            r.ModoActivo,
            r.RepeticionesObjetivo,
            r.Completada
        })
        .ToListAsync();

    return Results.Ok(rutinas);
})
.WithName("GetRutinasPaciente")
.WithTags("Rutinas");

// ┌─────────────────────────────────────────────────────────────────────────────
// │ PATCH /api/rutinas/{id}/completar
// │ Marca una rutina como completada tras ejecutar la sesión vinculada.
// └─────────────────────────────────────────────────────────────────────────────
app.MapMethods("/api/rutinas/{id:int}/completar", ["PATCH"], async (int id, AppDbContext db) =>
{
    var rutina = await db.Rutinas.FindAsync(id);
    if (rutina is null)
        return Results.NotFound(new { mensaje = $"Rutina #{id} no encontrada." });

    rutina.Completada = true;
    await db.SaveChangesAsync();

    return Results.Ok(new { id = rutina.Id, completada = true });
})
.WithName("CompletarRutina")
.WithTags("Rutinas");

// ══════════════════════════════════════════════════════════════════════════════
// USUARIOS
// ══════════════════════════════════════════════════════════════════════════════

// ┌─────────────────────────────────────────────────────────────────────────────
// │ POST /api/usuarios
// │ Upsert: busca por Nombre+Apellido, crea si no existe, actualiza EsTerapeuta.
// └─────────────────────────────────────────────────────────────────────────────
app.MapPost("/api/usuarios", async (Usuario usuario, AppDbContext db) =>
{
    var existente = await db.Usuarios.FirstOrDefaultAsync(
        u => u.Nombre == usuario.Nombre && u.Apellido == usuario.Apellido);

    if (existente is not null)
    {
        bool cambio = false;
        if (existente.EsTerapeuta != usuario.EsTerapeuta)
        {
            existente.EsTerapeuta = usuario.EsTerapeuta;
            cambio = true;
        }
        if (!string.IsNullOrWhiteSpace(usuario.Email) && existente.Email != usuario.Email)
        {
            existente.Email = usuario.Email;
            cambio = true;
        }
        if (cambio) await db.SaveChangesAsync();
        return Results.Ok(new { id = existente.Id, esTerapeuta = existente.EsTerapeuta });
    }

    usuario.Id = 0;
    db.Usuarios.Add(usuario);
    await db.SaveChangesAsync();

    return Results.Created($"/api/usuarios/{usuario.Id}",
        new { id = usuario.Id, esTerapeuta = usuario.EsTerapeuta });
})
.WithName("UpsertUsuario").WithTags("Usuarios");

// ┌─────────────────────────────────────────────────────────────────────────────
// │ GET /api/usuarios?nombre=xxx
// │ Búsqueda por nombre o apellido (parcial, hasta 20 resultados).
// └─────────────────────────────────────────────────────────────────────────────
app.MapGet("/api/usuarios", async (string? nombre, AppDbContext db) =>
{
    var query = db.Usuarios.AsQueryable();

    if (!string.IsNullOrWhiteSpace(nombre))
        query = query.Where(u => u.Nombre.Contains(nombre) || u.Apellido.Contains(nombre));

    var resultados = await query
        .OrderBy(u => u.Apellido).ThenBy(u => u.Nombre)
        .Take(20)
        .Select(u => new { u.Id, u.Nombre, u.Apellido, u.EsTerapeuta })
        .ToListAsync();

    return Results.Ok(resultados);
})
.WithName("BuscarUsuarios").WithTags("Usuarios");

// ══════════════════════════════════════════════════════════════════════════════
// INVITACIONES
// ══════════════════════════════════════════════════════════════════════════════

// ┌─────────────────────────────────────────────────────────────────────────────
// │ POST /api/invitaciones
// │ Crea una invitación de rutina entre dos usuarios.
// └─────────────────────────────────────────────────────────────────────────────
app.MapPost("/api/invitaciones", async (CrearInvitacionDto dto, AppDbContext db) =>
{
    // Búsqueda estricta por correo — privacidad clínica garantizada.
    var destinatario = await db.Usuarios
        .FirstOrDefaultAsync(u => u.Email == dto.EmailDestinatario);

    if (destinatario is null)
        return Results.NotFound(new { mensaje = "No se encontró ningún usuario con ese correo electrónico." });

    var inv = new InvitacionRutina
    {
        RemitenteId          = dto.RemitenteId,
        DestinatarioId       = destinatario.Id,
        RemitenteNombre      = dto.RemitenteNombre,
        RemitenteEsTerapeuta = dto.RemitenteEsTerapeuta,
        ModoActivo           = dto.ModoActivo,
        RepeticionesObjetivo = dto.RepeticionesObjetivo,
        Estado               = "Pendiente",
        FechaEnvio           = DateTime.UtcNow
    };

    db.InvitacionesRutina.Add(inv);
    await db.SaveChangesAsync();

    return Results.Created($"/api/invitaciones/{inv.Id}", new { id = inv.Id });
})
.WithName("CreateInvitacion").WithTags("Invitaciones");

// ┌─────────────────────────────────────────────────────────────────────────────
// │ GET /api/invitaciones/pendientes/{usuarioId}
// │ Bandeja de entrada: invitaciones donde Destinatario = usuarioId y Estado = Pendiente.
// └─────────────────────────────────────────────────────────────────────────────
app.MapGet("/api/invitaciones/pendientes/{usuarioId:int}", async (int usuarioId, AppDbContext db) =>
{
    var invitaciones = await db.InvitacionesRutina
        .Where(i => i.DestinatarioId == usuarioId && i.Estado == "Pendiente")
        .OrderByDescending(i => i.FechaEnvio)
        .Select(i => new
        {
            i.Id,
            i.RemitenteId,
            i.DestinatarioId,
            i.RemitenteNombre,
            i.RemitenteEsTerapeuta,
            i.ModoActivo,
            i.RepeticionesObjetivo,
            i.Estado,
            i.FechaEnvio
        })
        .ToListAsync();

    return Results.Ok(invitaciones);
})
.WithName("GetInvitacionesPendientes").WithTags("Invitaciones");

// ┌─────────────────────────────────────────────────────────────────────────────
// │ POST /api/invitaciones/{id}/responder
// │ Acepta o rechaza una invitación.
// │ Si se acepta, crea una Rutina atómicamente en la misma transacción.
// └─────────────────────────────────────────────────────────────────────────────
app.MapPost("/api/invitaciones/{id:int}/responder",
    async (int id, ResponderInvitacionDto dto, AppDbContext db) =>
{
    var inv = await db.InvitacionesRutina.FindAsync(id);
    if (inv is null)
        return Results.NotFound(new { mensaje = $"Invitación #{id} no encontrada." });
    if (inv.Estado != "Pendiente")
        return Results.BadRequest(new { mensaje = $"La invitación ya fue {inv.Estado.ToLower()}." });

    if (dto.Aceptada)
    {
        inv.Estado = "Aceptada";

        // Creación atómica: la Rutina se guarda junto al cambio de estado
        // en el mismo SaveChangesAsync → misma transacción implícita de EF Core.
        var rutina = new Rutina
        {
            PacienteId           = dto.PacienteId ?? inv.DestinatarioId,
            FechaAsignacion      = DateTime.UtcNow,
            ModoActivo           = inv.ModoActivo,
            RepeticionesObjetivo = inv.RepeticionesObjetivo,
            Completada           = false
        };
        db.Rutinas.Add(rutina);
    }
    else
    {
        inv.Estado = "Rechazada";
    }

    await db.SaveChangesAsync();

    return Results.Ok(new { estado = inv.Estado });
})
.WithName("ResponderInvitacion").WithTags("Invitaciones");

// ──────────────────────────────────────────────────────────────────────────────
app.Run();

// ─── Declaraciones de tipos (deben ir después del último top-level statement) ──
record ResponderInvitacionDto(bool Aceptada, int? PacienteId = null);
record CrearInvitacionDto(
    int    RemitenteId,
    string RemitenteNombre,
    bool   RemitenteEsTerapeuta,
    string EmailDestinatario,
    int    ModoActivo,
    int    RepeticionesObjetivo);
