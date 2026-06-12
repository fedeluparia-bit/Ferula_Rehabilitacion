#pragma once
#include <Arduino.h>

// ── WiFi Station (Wokwi-GUEST / red real) ───────────────────────────────────
constexpr char     WIFI_SSID[]      = "Wokwi-GUEST";
constexpr char     WIFI_PASSWORD[]  = "";          // Wokwi open network
constexpr uint32_t WIFI_TIMEOUT_MS  = 10000;       // 10 s; si no conecta → offline

// ── WebSocket ────────────────────────────────────────────────────────────────
constexpr uint16_t WS_PORT = 81;

// ── Pines FSR402 (ADC1 — compatible con WiFi activo) ────────────────────────
// GPIO 32 = ADC1_CH4  |  GPIO 33 = ADC1_CH5
constexpr uint8_t PIN_FSR_0 = 32;   // Motor 0: Índice/Medio
constexpr uint8_t PIN_FSR_1 = 33;   // Motor 1: Anular/Meñique

// ── Pines Servo ──────────────────────────────────────────────────────────────
constexpr uint8_t PIN_SERVO_0 = 18;
constexpr uint8_t PIN_SERVO_1 = 19;

// ── Resolución LEDC ──────────────────────────────────────────────────────────
// Define LEDC_USE_16BIT para alta precisión (36 cuentas/grado).
// Comenta la línea y descomenta el bloque 8-bit si Wokwi tiene bugs visuales.
#define LEDC_USE_16BIT

#ifdef LEDC_USE_16BIT
    constexpr uint8_t  LEDC_RES_BITS     = 16;
    constexpr uint32_t LEDC_DUTY_MAX     = 65535;
    // Pulso servo estándar: 0.5 ms → 0°, 2.5 ms → 180°, período 20 ms
    // duty = (pulso_ms / 20.0) * 65535
    constexpr uint32_t SERVO_DUTY_0_DEG   = 1638;  // 0.5 ms → 0°
    constexpr uint32_t SERVO_DUTY_180_DEG = 8192;  // 2.5 ms → 180°
#else
    // Fallback 8-bit: 255 cuentas totales
    constexpr uint8_t  LEDC_RES_BITS     = 8;
    constexpr uint32_t LEDC_DUTY_MAX     = 255;
    constexpr uint32_t SERVO_DUTY_0_DEG   = 6;    // ~0.5 ms
    constexpr uint32_t SERVO_DUTY_180_DEG = 32;   // ~2.5 ms
#endif

constexpr uint8_t  LEDC_CH_0    = 0;
constexpr uint8_t  LEDC_CH_1    = 1;
constexpr uint32_t LEDC_FREQ_HZ = 50;

// ── Límites de movimiento ────────────────────────────────────────────────────
constexpr int16_t SERVO_MIN_DEG  = 0;
constexpr int16_t SERVO_MAX_DEG  = 180;
// Velocidad de profiling: 2°/ciclo a 10 Hz → ~9 s por carrera (0→180 o 180→0)
constexpr int16_t SERVO_STEP_DEG = 2;

// ── Seguridad FSR ────────────────────────────────────────────────────────────
constexpr uint16_t UMBRAL_PRESION_MAX = 3500;   // ADC 12-bit (0–4095) — límite de E-STOP
constexpr uint16_t UMBRAL_TRABAJO     = 1000;   // ADC 12-bit — umbral mínimo para avanzar en MODO_RESISTENCIA

// ── Telemetría ───────────────────────────────────────────────────────────────
constexpr uint32_t TELEMETRY_INTERVAL_MS = 100;  // 10 Hz

// ── Tamaños JSON ─────────────────────────────────────────────────────────────
constexpr size_t JSON_TX_SIZE  = 256;
constexpr size_t JSON_RX_SIZE  = 128;
constexpr size_t JSON_BUF_SIZE = 256;

// ── Constantes de estado ─────────────────────────────────────────────────────
constexpr uint8_t ESTADO_REPOSO      = 0;
constexpr uint8_t ESTADO_EJECUTANDO  = 1;
constexpr uint8_t ESTADO_ESTOP       = 99;

constexpr uint8_t MODO_RESISTENCIA   = 0;
constexpr uint8_t MODO_ASISTIDO      = 1;

// ── Estado compartido entre Core 0 y Core 1 ─────────────────────────────────
struct MotorState {
    volatile int16_t  anguloActual;     // Posición actual del servo (0–180°)
    volatile uint16_t presionActual;    // Lectura ADC cruda FSR402 (0–4095)
};

struct SystemState {
    volatile uint8_t estado;                // ESTADO_*
    volatile uint8_t modo;                  // MODO_*
    volatile uint8_t repeticionesObjetivo;  // Fijado por comando "start"
    volatile uint8_t repeticionesHechas;    // Incrementado por Core 1 al completar rep
    MotorState        motores[2];
};
