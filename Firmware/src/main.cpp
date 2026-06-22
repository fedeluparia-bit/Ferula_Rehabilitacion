#include <Arduino.h>
#include <WiFi.h>
#include <WebSocketsServer.h>
#include <ArduinoJson.h>
#include "config.h"

// ── Estado global compartido ─────────────────────────────────────────────────
SystemState  sysState = {};
portMUX_TYPE stateMux = portMUX_INITIALIZER_UNLOCKED;

// ── Override de presión desde la App C# (simulador de fuerza para testing) ──
// Escrito por Core 0 (applyCommand, cmd "sim_prs").
// Leído por Core 1 (TaskControl, lectura de sensores).
// uint32_t / bool están garantizados como atómicos en ESP32 Xtensa (alineados).
//
// simulacionUIActiva: true en cuanto llega el primer "sim_prs", nunca vuelve a
//   false en esta sesión. Garantiza que val=0 (botón soltado) se publique como
//   presión cero limpia en vez de leer un pin flotante con ruido EM.
volatile uint32_t presionSimuladaApp  = 0;
volatile bool     simulacionUIActiva  = false;

// ── Homing: vuelta a 0° solicitada por la App ("Detener Sesión") ─────────────
// Escrito por Core 0 (applyCommand, cmd "home"); leído/limpiado por Core 1.
// Mientras es true, Core 1 lleva ambos servos a SERVO_MIN_DEG sin importar el
// estado, y lo pone en false al llegar. bool volátil → atómico en Xtensa.
volatile bool homingActivo = false;

// ── ArduinoJson: pools globales, sin heap dinámico (regla: no String) ────────
StaticJsonDocument<JSON_TX_SIZE> docTx;
StaticJsonDocument<JSON_RX_SIZE> docRx;
char txBuf[JSON_BUF_SIZE];

// ── WebSocket (accedido únicamente desde Core 0) ─────────────────────────────
WebSocketsServer wsServer(WS_PORT);

// ── Fase de movimiento (estado interno de Core 1, no compartido) ─────────────
enum class MotionPhase : uint8_t { IDLE, OPENING, CLOSING };

// ── Prototipos ────────────────────────────────────────────────────────────────
void        applyCommand(const char* cmd, int val, int mod);
void        broadcastTelemetry();
uint32_t    angleToDuty(int16_t degrees);
void        onWebSocketEvent(uint8_t clientId, WStype_t type,
                             uint8_t* payload, size_t length);

// ─────────────────────────────────────────────────────────────────────────────
// angleToDuty — mapea 0–180° al duty cycle LEDC correcto para 50 Hz
// Interpolación lineal: 0° → 1638 (0.5 ms)  |  180° → 8192 (2.5 ms)
// ─────────────────────────────────────────────────────────────────────────────
uint32_t angleToDuty(int16_t degrees) {
    if (degrees <= SERVO_MIN_DEG) return SERVO_DUTY_0_DEG;
    if (degrees >= SERVO_MAX_DEG) return SERVO_DUTY_180_DEG;
    return SERVO_DUTY_0_DEG +
           static_cast<uint32_t>((SERVO_DUTY_180_DEG - SERVO_DUTY_0_DEG) *
                                  static_cast<uint32_t>(degrees)) / SERVO_MAX_DEG;
}

// ─────────────────────────────────────────────────────────────────────────────
// TAREA CORE 0 — Red: WiFi STA + WebSocket
//
// Intenta conectar a WIFI_SSID con timeout no bloqueante.
// Si no conecta dentro del timeout, continúa en modo offline:
//   el WebSocket arranca igualmente (servirá en cuanto haya red).
// Core 1 (TaskControl) no espera a esta tarea → no hay riesgo de freeze.
// ─────────────────────────────────────────────────────────────────────────────
void TaskNetwork(void* pvParameters) {
    WiFi.mode(WIFI_STA);
    WiFi.begin(WIFI_SSID, WIFI_PASSWORD);

    // Espera no bloqueante con timeout de WIFI_TIMEOUT_MS
    uint32_t wifiStart = millis();
    while (WiFi.status() != WL_CONNECTED) {
        if (millis() - wifiStart >= WIFI_TIMEOUT_MS) {
            // Timeout: continuar offline
            break;
        }
        vTaskDelay(pdMS_TO_TICKS(500));
    }

    wsServer.begin();
    wsServer.onEvent(onWebSocketEvent);

    uint32_t lastTelemetryMs = 0;

    for (;;) {
        wsServer.loop();

        uint32_t now = millis();
        if (now - lastTelemetryMs >= TELEMETRY_INTERVAL_MS) {
            lastTelemetryMs = now;
            broadcastTelemetry();
        }

        vTaskDelay(1);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// TAREA CORE 1 — Control: FSR + Seguridad + Motion Profiling + LEDC
//
// Máquina de estados de movimiento (MotionPhase):
//
//   IDLE ──(start recibido)──► OPENING
//   OPENING: incrementa ángulo SERVO_STEP_DEG por ciclo
//            cuando ángulo >= SERVO_MAX_DEG ──► CLOSING
//   CLOSING: decrementa ángulo SERVO_STEP_DEG por ciclo
//            cuando ángulo <= SERVO_MIN_DEG:
//               repeticionesHechas++
//               si repeticionesHechas >= repeticionesObjetivo ──► auto-stop (REPOSO)
//               si no ──► OPENING (siguiente rep)
//
// E-STOP (presión > UMBRAL): congela el ángulo en posición actual, pausa todo.
// ─────────────────────────────────────────────────────────────────────────────
void TaskControl(void* pvParameters) {
    const TickType_t period   = pdMS_TO_TICKS(TELEMETRY_INTERVAL_MS);  // 10 Hz
    TickType_t       lastWake = xTaskGetTickCount();

    // Variables locales de Core 1 — ángulos y fase NUNCA se comparten en crudo
    int16_t     ang0  = SERVO_MIN_DEG;
    int16_t     ang1  = SERVO_MIN_DEG;
    MotionPhase phase = MotionPhase::IDLE;

    // EMA para suavizar ruido ADC del ESP32 (±30–80 LSB inherentes).
    // α=0.2 → ventana efectiva ~5 muestras (50 ms). Se inicializa en la
    // primera lectura real para evitar arrancar desde 0 artificialmente.
    float ema0 = -1.0f;
    float ema1 = -1.0f;
    constexpr float EMA_ALPHA = 0.2f;

    for (;;) {
        // ── 1. Leer FSR402 o aplicar override de simulación desde App C# ─────
        // Una sola lectura de presionSimuladaApp (atómica) determina la fuente.
        // Esto mantiene compatibilidad total con hardware real: si la App no
        // envía "sim_prs", presionSimuladaApp == 0 y se usan los sensores físicos.
        uint16_t prs0, prs1;
        if (simulacionUIActiva) {
            // Modo simulación: valor de la App, sin filtro (ya es limpio y discreto).
            uint16_t simVal = static_cast<uint16_t>(presionSimuladaApp);
            prs0 = simVal;
            prs1 = simVal;
        } else {
            // Modo hardware: EMA sobre muestra cruda del ADC.
            // Primera iteración: inicializar EMA con la primera muestra real
            // para evitar un arranque artificial desde 0.
            float raw0 = static_cast<float>(analogRead(PIN_FSR_0));
            float raw1 = static_cast<float>(analogRead(PIN_FSR_1));
            ema0 = (ema0 < 0.0f) ? raw0 : EMA_ALPHA * raw0 + (1.0f - EMA_ALPHA) * ema0;
            ema1 = (ema1 < 0.0f) ? raw1 : EMA_ALPHA * raw1 + (1.0f - EMA_ALPHA) * ema1;
            prs0 = static_cast<uint16_t>(ema0 + 0.5f);  // redondeo
            prs1 = static_cast<uint16_t>(ema1 + 0.5f);
        }

        // ── 2. Snapshot del estado + escritura de presión + E-STOP check ─────
        // E-STOP (UMBRAL_PRESION_MAX=3500) es independiente del modo y siempre activo.
        // UMBRAL_TRABAJO (1000) solo se usa en MODO_RESISTENCIA para la fase de ida.
        uint8_t estadoLocal, repObj, modoLocal;

        portENTER_CRITICAL(&stateMux);
        {
            sysState.motores[0].presionActual = prs0;
            sysState.motores[1].presionActual = prs1;

            if ((prs0 > UMBRAL_PRESION_MAX || prs1 > UMBRAL_PRESION_MAX) &&
                sysState.estado == ESTADO_EJECUTANDO) {
                sysState.estado = ESTADO_ESTOP;
            }

            estadoLocal = sysState.estado;
            repObj      = sysState.repeticionesObjetivo;
            modoLocal   = sysState.modo;
        }
        portEXIT_CRITICAL(&stateMux);

        // ── 3. Motion Profiling con lógica biomecánica por modo ─────────────
        //
        // MODO_ASISTIDO   (1): movimiento pasivo continuo. Ignora FSR en OPENING.
        // MODO_RESISTENCIA(0): activo-asistido.
        //   · OPENING: avanza 1 paso SOLO si prs0 > UMBRAL_TRABAJO
        //              O prs1 > UMBRAL_TRABAJO (paciente hace fuerza).
        //              Si no hay fuerza → motor queda quieto esperando.
        //   · CLOSING: siempre automático (vuelta pasiva en ambos modos).
        //
        if (homingActivo) {
            // ── Homing ("Detener Sesión"): vuelta gradual a 0° desde donde esté,
            //    con prioridad sobre cualquier estado. Al llegar, libera el flag.
            ang0 -= SERVO_STEP_DEG;
            ang1 -= SERVO_STEP_DEG;
            if (ang0 <= SERVO_MIN_DEG) ang0 = SERVO_MIN_DEG;
            if (ang1 <= SERVO_MIN_DEG) ang1 = SERVO_MIN_DEG;
            if (ang0 == SERVO_MIN_DEG && ang1 == SERVO_MIN_DEG) {
                homingActivo = false;
                phase        = MotionPhase::IDLE;
            }

        } else if (estadoLocal == ESTADO_EJECUTANDO) {

            uint8_t repHechas;
            portENTER_CRITICAL(&stateMux);
            repHechas = sysState.repeticionesHechas;
            portEXIT_CRITICAL(&stateMux);

            if (repHechas < repObj) {
                switch (phase) {

                    case MotionPhase::IDLE:
                        phase = MotionPhase::OPENING;   // arrancar apertura
                        // fall-through intencional

                    case MotionPhase::OPENING:
                        if (modoLocal == MODO_ASISTIDO) {
                            // ── Pasivo: avanza siempre, sin condición de FSR ──
                            ang0 += SERVO_STEP_DEG;
                            ang1 += SERVO_STEP_DEG;
                        } else {
                            // ── Activo-asistido: avanza solo si el paciente empuja ──
                            if (prs0 > UMBRAL_TRABAJO || prs1 > UMBRAL_TRABAJO) {
                                ang0 += SERVO_STEP_DEG;
                                ang1 += SERVO_STEP_DEG;
                            }
                            // Si no hay fuerza suficiente: hold position (no se modifica ang)
                        }
                        if (ang0 >= SERVO_MAX_DEG) {
                            ang0  = SERVO_MAX_DEG;
                            ang1  = SERVO_MAX_DEG;
                            phase = MotionPhase::CLOSING;
                        }
                        break;

                    case MotionPhase::CLOSING:
                        // ── Vuelta automática en ambos modos (extensión pasiva) ──
                        ang0 -= SERVO_STEP_DEG;
                        ang1 -= SERVO_STEP_DEG;
                        if (ang0 <= SERVO_MIN_DEG) {
                            ang0 = SERVO_MIN_DEG;
                            ang1 = SERVO_MIN_DEG;

                            portENTER_CRITICAL(&stateMux);
                            {
                                sysState.repeticionesHechas++;
                                if (sysState.repeticionesHechas >= sysState.repeticionesObjetivo) {
                                    sysState.estado = ESTADO_REPOSO;  // auto-stop
                                    phase = MotionPhase::IDLE;
                                } else {
                                    phase = MotionPhase::OPENING;     // siguiente rep
                                }
                            }
                            portEXIT_CRITICAL(&stateMux);
                        }
                        break;
                }
            }

        } else {
            // REPOSO o ESTOP: congelar ángulo, resetear fase para próximo "start"
            phase = MotionPhase::IDLE;
        }

        // ── 4. Escribir duty LEDC a ambos servos ────────────────────────────
        ledcWrite(LEDC_CH_0, angleToDuty(ang0));
        ledcWrite(LEDC_CH_1, angleToDuty(ang1));

        // ── 5. Publicar ángulo actual al estado compartido (para telemetría) ─
        portENTER_CRITICAL(&stateMux);
        {
            sysState.motores[0].anguloActual = ang0;
            sysState.motores[1].anguloActual = ang1;
        }
        portEXIT_CRITICAL(&stateMux);

        // Ciclo estricto a 10 Hz — usa DelayUntil para compensar jitter
        vTaskDelayUntil(&lastWake, period);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Evento WebSocket — ejecutado dentro de wsServer.loop() (Core 0)
// ─────────────────────────────────────────────────────────────────────────────
void onWebSocketEvent(uint8_t clientId, WStype_t type,
                      uint8_t* payload, size_t length) {
    switch (type) {
        case WStype_CONNECTED:
            broadcastTelemetry();
            break;

        case WStype_TEXT: {
            docRx.clear();
            DeserializationError err = deserializeJson(docRx, payload, length);
            if (err) break;

            const char* cmd = docRx["cmd"] | "";
            int         val = docRx["val"] | 0;
            int         mod = docRx["mod"] | 0;   // ignorado por stop/estop/set_mod
            applyCommand(cmd, val, mod);
            break;
        }

        case WStype_DISCONNECTED:
        case WStype_ERROR:
        default:
            break;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Aplica un comando JSON recibido — ejecutado en Core 0
//
// "start" + val + mod:
//   val = repeticiones objetivo
//   mod = modo (0=Resistencia, 1=Asistido) — leído de doc["mod"] en el evento
//   El modo se fija ANTES de que Core 1 empiece a ejecutar la lógica biomecánica.
// ─────────────────────────────────────────────────────────────────────────────
void applyCommand(const char* cmd, int val, int mod) {
    // "sim_prs" se maneja ANTES del spinlock: ambas variables son atómicas en Xtensa.
    // simulacionUIActiva se fija a true con el primer comando y no vuelve a false:
    //   garantiza que val=0 (botón soltado) produzca presión cero limpia en lugar
    //   de un analogRead() sobre un pin flotante que inyectaría ruido EM.
    if (strcmp(cmd, "sim_prs") == 0) {
        simulacionUIActiva = true;
        presionSimuladaApp = static_cast<uint32_t>(val >= 0 ? val : 0);
        return;
    }

    // "home" (Detener Sesión): Core 1 devuelve los servos a 0° gradualmente.
    // Se marca REPOSO (no E-STOP) y se activa el homing.
    if (strcmp(cmd, "home") == 0) {
        homingActivo = true;
        portENTER_CRITICAL(&stateMux);
        sysState.estado = ESTADO_REPOSO;
        portEXIT_CRITICAL(&stateMux);
        return;
    }

    portENTER_CRITICAL(&stateMux);
    {
        if (strcmp(cmd, "start") == 0) {
            homingActivo                  = false;                       // cancela homing pendiente
            sysState.modo                 = static_cast<uint8_t>(mod);   // fijar modo antes de arrancar
            sysState.repeticionesObjetivo = static_cast<uint8_t>(val);
            sysState.repeticionesHechas   = 0;
            sysState.estado               = ESTADO_EJECUTANDO;

        } else if (strcmp(cmd, "stop") == 0) {
            // Pausa: congela los servos en su posición; NO resetea el contador
            // de repeticiones para poder reanudar después con "resume".
            sysState.estado = ESTADO_REPOSO;

        } else if (strcmp(cmd, "resume") == 0) {
            // Reanuda desde donde se pausó: mantiene ángulo y repeticionesHechas.
            homingActivo = false;
            if (sysState.repeticionesHechas < sysState.repeticionesObjetivo) {
                sysState.estado = ESTADO_EJECUTANDO;
            }

        } else if (strcmp(cmd, "set_mod") == 0) {
            sysState.modo = static_cast<uint8_t>(val);

        } else if (strcmp(cmd, "estop") == 0) {
            homingActivo    = false;
            sysState.estado = ESTADO_ESTOP;
        }
    }
    portEXIT_CRITICAL(&stateMux);
}

// ─────────────────────────────────────────────────────────────────────────────
// Serializa y hace broadcast de telemetría a todos los clientes (Core 0)
// ─────────────────────────────────────────────────────────────────────────────
void broadcastTelemetry() {
    // Snapshot atómico del estado compartido
    uint8_t  st, mod, repHechas;
    int16_t  ang0, ang1;
    uint16_t prs0, prs1;

    portENTER_CRITICAL(&stateMux);
    {
        st        = sysState.estado;
        mod       = sysState.modo;
        repHechas = sysState.repeticionesHechas;
        ang0      = sysState.motores[0].anguloActual;
        prs0      = sysState.motores[0].presionActual;
        ang1      = sysState.motores[1].anguloActual;
        prs1      = sysState.motores[1].presionActual;
    }
    portEXIT_CRITICAL(&stateMux);

    // Construir paquete JSON — sin String, sin allocaciones dinámicas
    docTx.clear();
    docTx["t"]   = "tel";
    docTx["st"]  = st;
    docTx["mod"] = mod;
    docTx["rep"] = repHechas;   // repeticiones completadas (progreso en tiempo real)

    JsonArray motors = docTx.createNestedArray("m");

    JsonObject m0 = motors.createNestedObject();
    m0["id"]  = 0;
    m0["ang"] = ang0;
    m0["prs"] = prs0;

    JsonObject m1 = motors.createNestedObject();
    m1["id"]  = 1;
    m1["ang"] = ang1;
    m1["prs"] = prs1;

    size_t len = serializeJson(docTx, txBuf, JSON_BUF_SIZE);
    wsServer.broadcastTXT(txBuf, len);
}

// ─────────────────────────────────────────────────────────────────────────────
// setup — inicialización hardware antes de que el scheduler arranque
// ─────────────────────────────────────────────────────────────────────────────
void setup() {
    Serial.begin(115200);

    // ADC1: resolución 12-bit (0–4095) para FSR402
    analogReadResolution(12);
    pinMode(PIN_FSR_0, INPUT);
    pinMode(PIN_FSR_1, INPUT);

    // LEDC: periférico de hardware interno del ESP32
    ledcSetup(LEDC_CH_0, LEDC_FREQ_HZ, LEDC_RES_BITS);
    ledcSetup(LEDC_CH_1, LEDC_FREQ_HZ, LEDC_RES_BITS);
    ledcAttachPin(PIN_SERVO_0, LEDC_CH_0);
    ledcAttachPin(PIN_SERVO_1, LEDC_CH_1);

    // Posición inicial: ambos servos en 0°
    ledcWrite(LEDC_CH_0, SERVO_DUTY_0_DEG);
    ledcWrite(LEDC_CH_1, SERVO_DUTY_0_DEG);

    // Estado inicial del sistema
    sysState.estado                = ESTADO_REPOSO;
    sysState.modo                  = MODO_RESISTENCIA;
    sysState.repeticionesObjetivo  = 0;
    sysState.repeticionesHechas    = 0;
    sysState.motores[0].anguloActual  = 0;
    sysState.motores[0].presionActual = 0;
    sysState.motores[1].anguloActual  = 0;
    sysState.motores[1].presionActual = 0;

    // Pinear tareas a sus cores dedicados
    xTaskCreatePinnedToCore(TaskNetwork, "Net",  4096, nullptr, 1, nullptr, 0);
    xTaskCreatePinnedToCore(TaskControl, "Ctrl", 4096, nullptr, 1, nullptr, 1);
}

// loop vacío — FreeRTOS gestiona toda la ejecución
void loop() {
    vTaskDelete(nullptr);
}
