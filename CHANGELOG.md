# CHANGELOG — Férula de Rehabilitación Inteligente (PoC)

---

## Fase 1 — Estructura C# / Avalonia UI

**Archivos creados**
- `FerulaSoftware.slnx` — solución .NET
- `FerulaSoftware.App/FerulaSoftware.App.csproj` — proyecto Avalonia UI
- Estructura de carpetas: `Models/`, `ViewModels/`, `Views/`, `Services/`, `Converters/`, `Data/`

**Paquetes NuGet instalados**
| Paquete | Versión | Motivo |
|---|---|---|
| Avalonia | 11.0.11 | UI framework (fijado: 11.2+ incompatible con LiveCharts 2.0.2) |
| Avalonia.Desktop | 11.0.11 | Soporte escritorio |
| Avalonia.Themes.Fluent | 11.0.11 | Tema visual |
| LiveChartsCore.SkiaSharpView.Avalonia | 2.0.2 | Gráficas en tiempo real |
| CommunityToolkit.Mvvm | 8.4.1 | Generación de código MVVM |
| Microsoft.EntityFrameworkCore.Sqlite | 10.0.8 | Base de datos local |
| Microsoft.EntityFrameworkCore.Design | 10.0.8 | Migraciones EF Core |
| QuestPDF | 2026.5.0 | Generación de reportes PDF |

**Correcciones durante Fase 1**
- Eliminado `AvaloniaUI.DiagnosticsSupport` (jalaba Avalonia 11.2.0 transitivamente → 34 errores MSB3277)
- Eliminado `.WithDeveloperTools()` de `Program.cs` (dependía del paquete eliminado)
- Eliminado `System.Net.WebSockets.Client` (redundante en .NET 10, advertencia NU1510)

---

## Fase 2 — Base de Datos / Modelos EF Core

**Archivos creados**
- `Models/Paciente.cs` — entidad con `Id`, `Nombre`, `Apellido`, `FechaRegistro`, `NotasMedicas?`, navegación `Sesiones`
- `Models/SesionRehabilitacion.cs` — entidad con `Id`, `PacienteId (FK)`, `FechaHora`, `Modo`, `Repeticiones`, `DuracionSegundos`, `PresionMaximaAlcanzada (int?)`
- `Data/AppDbContext.cs` — SQLite `"Data Source=ferula_data.db"`, cascade delete Paciente→Sesiones

**Cambio de arquitectura aprobado (mid-Fase 2)**
- Sensor `INA219 (I2C, corriente mA)` → `FSR402 (ADC analógico, 0–4095)`
- `FuerzaMaximaAlcanzada (double?)` renombrado a `PresionMaximaAlcanzada (int?)`
- Clave JSON `"cur"` → `"prs"`

---

## Fase 3 — Firmware Base (PlatformIO)

**Archivos creados**
- `Firmware/platformio.ini`
  ```ini
  platform  = espressif32
  board     = esp32doit-devkit-v1
  framework = arduino
  lib_deps  =
      bblanchon/ArduinoJson @ ^6.21.5
      links2004/WebSockets  @ ^2.4.1
  ```
- `Firmware/include/config.h` — todas las constantes del sistema

**Constantes definidas en `config.h`**
| Constante | Valor | Descripción |
|---|---|---|
| `WIFI_SSID` | `"Wokwi-GUEST"` | Red Wokwi (STA, no AP) |
| `WS_PORT` | `81` | Puerto WebSocket |
| `PIN_FSR_0` | `32` | ADC1_CH4 (WiFi-safe) |
| `PIN_FSR_1` | `33` | ADC1_CH5 (WiFi-safe) |
| `PIN_SERVO_0` | `18` | LEDC canal 0 |
| `PIN_SERVO_1` | `19` | LEDC canal 1 |
| `LEDC_RES_BITS` | `16` | Alta precisión (36 cuentas/grado) |
| `SERVO_DUTY_0_DEG` | `1638` | 0.5 ms → 0° |
| `SERVO_DUTY_180_DEG` | `8192` | 2.5 ms → 180° |
| `SERVO_STEP_DEG` | `2` | Velocidad motion profiling |
| `UMBRAL_PRESION_MAX` | `3500` | Límite E-STOP |
| `UMBRAL_TRABAJO` | `1000` | Umbral mínimo Modo Resistencia |
| `TELEMETRY_INTERVAL_MS` | `100` | 10 Hz |

---

## Fase 4 — Core de Firmware (FreeRTOS + Motion Profiling)

**Archivo creado:** `Firmware/src/main.cpp`

**Arquitectura dual-core**
- `Core 0 / TaskNetwork` — WiFi STA + WebSocket server (puerto 81)
  - Timeout no bloqueante de 10 s; arranca WebSocket incluso sin WiFi
  - Telemetría broadcast a 10 Hz
- `Core 1 / TaskControl` — ADC + LEDC + máquina de estados
  - Ciclo estricto a 10 Hz con `vTaskDelayUntil`
  - Sincronización con `portMUX_TYPE stateMux` (spinlock FreeRTOS)

**Máquina de estados de movimiento**
```
IDLE → (start) → OPENING → (ángulo >= 180°) → CLOSING → (ángulo <= 0°) → rep++ → OPENING
                                                                          → (reps completas) → IDLE
```

**Modos biomecánicos**
- `MODO_ASISTIDO (1)` — OPENING avanza siempre (movimiento pasivo continuo)
- `MODO_RESISTENCIA (0)` — OPENING avanza solo si `prs0 > 1000 || prs1 > 1000` (activo-asistido)
- CLOSING siempre automático en ambos modos

**Función `angleToDuty`** — interpolación lineal 0°→1638 / 180°→8192 para LEDC 16-bit a 50 Hz

**Protocolo JSON**

Telemetría ESP32 → App (10 Hz):
```json
{ "t":"tel", "st":1, "mod":0, "rep":5, "m":[{"id":0,"ang":45,"prs":1200},{"id":1,"ang":45,"prs":1150}] }
```

Comandos App → ESP32:
```json
{ "cmd":"start", "val":10, "mod":0 }
{ "cmd":"stop",  "val":0 }
{ "cmd":"estop", "val":0 }
{ "cmd":"sim_prs","val":2000 }
```

---

## Fase 5 — Conectividad WebSocket (C#)

**Archivos creados**
- `Models/TelemetryPacket.cs` — record con `[JsonPropertyName]`; campo `Presion (int)` mapeado a `"prs"`
- `Models/EspCommand.cs` — record `(Cmd, Val, Mod=0)`; `Mod` agregado para enviar modo con `"start"`
- `Services/IWebSocketService.cs` — interfaz con `ConnectAsync`, `SendCommandAsync(EspCommand)`, eventos
- `Services/WebSocketService.cs`
  - Bucle de recepción en `Task.Run` (background, no bloquea UI)
  - Reutilización de `MemoryStream` sin realocación por mensaje
  - Deserialización zero-copy: `ms.GetBuffer().AsSpan(0, length)`
  - `ConnectionChanged` event dispara `false` en desconexión o excepción

**Archivos actualizados**
- `App.axaml.cs` — inyección manual de `WebSocketService` → `MainViewModel`; `desktop.Exit` llama a `Dispose()`

---

## Fase 6 — UI y Gráficas (Avalonia XAML)

**Archivos creados/actualizados**
- `Converters/BoolToColorConverter.cs` — `true` → `#06D6A0` (verde Online), `false` → `#E63946` (rojo Offline)
- `Views/MainWindow.axaml` — dashboard completo (tema oscuro `#0D1117`)
- `ViewModels/MainViewModel.cs` — propiedades observables, comandos, LiveCharts2

**Layout de MainWindow**
```
DockPanel
├── TOP: barra de conexión (URI, botón Conectar, indicador color, estado sistema)
└── Grid (2 columnas)
    ├── [280px] Panel de control izquierdo
    │   ├── Repeticiones objetivo (NumericUpDown)
    │   ├── ComboBox modo (Resistencia / Asistido) → ModoSeleccionado
    │   ├── Modo activo reportado por ESP32 (informativo)
    │   ├── ▶ Iniciar Rutina
    │   ├── ⏸ Detener
    │   ├── 🛑 E-STOP
    │   └── Slider simulador FSR (0–4000 ADC)
    └── [*] Panel de telemetría derecho
        ├── Tarjetas: Repeticiones, Presión Máx, Motor 0°, Motor 1°
        └── CartesianChart LiveCharts2 (ventana 10 s, eje Y fijo 0–4095)
```

**Propiedades observables en MainViewModel**
`EstadoSistema`, `ModoActivo`, `RepeticionesHechas`, `RepeticionesObjetivo`, `PresionMaxima`, `AnguloMotor0`, `AnguloMotor1`, `EstaConectado`, `WsUri`, `ModoSeleccionado`, `ErrorConexion`, `PresionSimuladaSlider`

**Comandos**
- `ConectarCommand` — conecta WebSocket; expone excepción en `ErrorConexion`
- `IniciarRutinaCommand` — envía `{"cmd":"start","val":reps,"mod":ModoSeleccionado}`; resetea `PresionMaxima` y `RepeticionesHechas`
- `StopCommand` — envía `{"cmd":"stop","val":0}`
- `EStopCommand` — envía `{"cmd":"estop","val":0}`

---

## Corrección de Bug — Ruido en Presión por Pin Flotante

**Problema:** al soltar el botón simulador, `val=0` hacía que el ESP32 leyera `analogRead()` sobre pines flotantes, inyectando ruido EM en la telemetría.

**Solución en firmware (`main.cpp`)**
- Agregado `volatile bool simulacionUIActiva = false;` (latch: se activa con el primer `sim_prs` y nunca vuelve a `false`)
- `applyCommand("sim_prs")` ahora establece `simulacionUIActiva = true` antes de guardar el valor
- `TaskControl` usa `if (simulacionUIActiva)` en lugar de `if (simVal > 0)`: cuando está en modo simulación, nunca llama a `analogRead()`, ni siquiera con `val=0`

---

## Mejora — Filtro EMA en Lecturas ADC

**Problema:** el ADC del ESP32 tiene ruido inherente de ±30–80 LSB con señal estable.

**Solución en `TaskControl` (modo hardware únicamente)**
```cpp
float ema0 = -1.0f, ema1 = -1.0f;
constexpr float EMA_ALPHA = 0.2f;   // ventana efectiva ~5 muestras / 50 ms

// Primera iteración: inicializar con muestra real (evita arranque en 0 artificial)
ema0 = (ema0 < 0.0f) ? raw0 : EMA_ALPHA * raw0 + (1.0f - EMA_ALPHA) * ema0;
prs0 = static_cast<uint16_t>(ema0 + 0.5f);
```
El modo simulación (`simulacionUIActiva = true`) no pasa por el filtro (valor ya es discreto y limpio).

---

## Mejora — Presión en 0 al Conectar

**Problema:** al conectar la app, `simulacionUIActiva = false` en el ESP32 → se leían pines flotantes desde el primer tick.

**Solución en `MainViewModel.OnConnectionChanged`**
```csharp
if (conectado)
    _ = _ws.SendCommandAsync(new EspCommand("sim_prs", 0));
```
Al conectar, se activa inmediatamente `simulacionUIActiva = true` en el firmware con `presionSimuladaApp = 0`. La gráfica arranca en 0 limpio.

---

## Diagnóstico y Fix de Conectividad WebSocket

**Problema:** la app no se conectaba al simulador Wokwi.

**Causa raíz:** `ws://localhost:81` en Windows resuelve `::1` (IPv6) primero; el forwarder de Wokwi escucha únicamente en `127.0.0.1` (IPv4).

**Cambios aplicados**
| Archivo | Cambio |
|---|---|
| `WebSocketService.cs` | `DefaultUri` cambiado a `ws://127.0.0.1:81` |
| `MainViewModel.cs` | `catch (Exception ex)` expone `ex.Message + ex.InnerException` en `ErrorConexion` |
| `MainWindow.axaml` | `TextBlock` rojo con `ErrorConexion` visible solo cuando hay error |

**Nota sobre firmware:** `wsServer.begin()` se ejecuta incondicionalmente tras el timeout de WiFi — correcto por diseño, el forwarder de Wokwi opera sobre loopback.

---

## Entorno de Simulación Wokwi

**Archivos creados en `Firmware/`**

`wokwi.toml`
```toml
[wokwi]
version  = 1
firmware = ".pio/build/esp32doit-devkit-v1/firmware.bin"
elf      = ".pio/build/esp32doit-devkit-v1/firmware.elf"

[[forward]]
port = 81
```

`diagram.json` — circuito virtual:
| Componente | ID | Señal | Pin ESP32 |
|---|---|---|---|
| Servo Motor 0 (Índice/Medio) | `servo0` | PWM | GPIO 18 |
| Servo Motor 1 (Anular/Meñique) | `servo1` | PWM | GPIO 19 |
| Slide Potentiometer 0 (simula FSR Motor 0) | `pot0` | SIG | GPIO 32 |
| Slide Potentiometer 1 (simula FSR Motor 1) | `pot1` | SIG | GPIO 33 |

Todos los componentes alimentados desde `3V3` y `GND` del ESP32.

---

## Refactor — Simulador de Fuerza: Botón → Slider

**Problema:** el botón `SIMULAR FUERZA` con `PointerPressed`/`PointerReleased` no funcionaba correctamente en Avalonia.

**Cambios**

| Archivo | Eliminado | Agregado |
|---|---|---|
| `MainViewModel.cs` | `_simTimer`, `_presionSimulada`, `SimPrsStep`, `SimPrsMax`, `IniciarSimulacionPresion()`, `DetenerSimulacionPresion()`, `OnSimTimerElapsed()` | `PresionSimuladaSlider (int)` + `partial void OnPresionSimuladaSliderChanged` → envía `sim_prs` en cada cambio |
| `MainWindow.axaml` | `btn-simular` style, `Button BtnSimular` | `Slider` (0–4000, TickFrequency=100) + etiqueta de valor en tiempo real |
| `MainWindow.axaml.cs` | `BtnSimular_PointerPressed`, `BtnSimular_PointerReleased` | — (code-behind vacío de lógica) |

**Flujo actual del simulador**
1. Conectar → app envía `sim_prs=0` → firmware activa `simulacionUIActiva=true`, presión en 0 limpio
2. Mover slider → binding actualiza `PresionSimuladaSlider` → callback envía `{"cmd":"sim_prs","val":N}`
3. ESP32 publica el valor en telemetría → gráfica refleja la presión inmediatamente
4. En Modo Resistencia + Iniciar Rutina: servo avanza solo si presión slider > 1000 ADC

---

*Generado el 2026-05-15*
