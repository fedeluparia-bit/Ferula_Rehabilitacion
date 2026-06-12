# Blueprint del Proyecto: Férula de Rehabilitación Inteligente (PoC)

## 1. Contexto del Proyecto
Eres el Ingeniero de Software Senior a cargo de codificar el backend, frontend y firmware de una Prueba de Concepto (PoC) para una férula mecatrónica de mano. El sistema consta de dos partes:
1.  **Firmware (Dispositivo):** Un ESP32 que controla 2 servomotores y lee 2 sensores de corriente.
2.  **Software (Control):** Una aplicación de escritorio que se comunica con el ESP32, envía comandos, muestra gráficas en tiempo real y guarda el historial de pacientes.

## 2. Stack Tecnológico Estricto (No desviar)
* **Firmware:** C++ usando el framework de Arduino sobre **PlatformIO**.
* **Aplicación de Escritorio:** **C#** con **Avalonia UI** (Patrón MVVM estricto).
* **Comunicaciones:** WebSockets (El ESP32 actúa como Access Point WiFi y Servidor WebSocket; la App C# es el Cliente).
* **Base de Datos Local:** SQLite usando Entity Framework Core.
* **Gráficas (UI):** LiveCharts2.
* **Reportes (UI):** QuestPDF.

## 3. Protocolo de Comunicación (Contrato JSON)
Ambos sistemas se comunicarán usando paquetes JSON ligeros. 
Frecuencia de telemetría: 10Hz.

**A. Telemetría (ESP32 -> App C#)**
```json
{
  "t": "tel",
  "st": 1, 
  "mod": 0, 
  "rep": 5,
  "m": [
    { "id": 0, "ang": 45, "cur": 120 },
    { "id": 1, "ang": 45, "cur": 115 }
  ]
}

Diccionario: st = Estado (0: Reposo, 1: Ejecutando, 99: E-STOP). mod = Modo (0: Resistencia, 1: Asistido). m = Array de motores (id 0: Índice/Medio, id 1: Anular/Meñique). ang = Ángulo (0 a 90 grados). cur = Corriente en mA.

B. Comandos (App C# -> ESP32)
JSON

{
  "cmd": "start",
  "val": 0
}

Comandos válidos: start, stop, set_mod, estop.
4. Reglas de Negocio y Arquitectura Críticas
4.1 Reglas del Firmware (ESP32)

    PROHIBIDO usar la clase String. Debes usar ArduinoJson con un StaticJsonDocument global para evitar la fragmentación del Heap.

    PROHIBIDO usar PID. El control de los motores se hará mediante Motion Profiling (generación de trayectorias con incrementos graduales en un bucle temporal) para evitar tirones bruscos.

    Multithreading: Debes usar FreeRTOS. El Core 0 manejará exclusivamente la red (WiFi AP y WebSockets). El Core 1 manejará la lectura de sensores (INA219 vía I2C) y la escritura PWM a los motores.

    PWM: Prohibido usar librerías externas o módulos PCA9685. Debes usar el periférico de hardware interno LEDC del ESP32.

    Seguridad: Si el sensor INA219 lee una corriente superior al umbral máximo permitido, el software debe pausar el incremento del ángulo inmediatamente.

4.2 Reglas del Software (C# Avalonia UI)

    La comunicación WebSocket (ClientWebSocket) DEBE correr en un hilo asíncrono secundario para no congelar la UI de Avalonia.

    La UI solo debe actualizarse cuando cambia el ViewModel (Data Binding).

    No usar base de datos externa. Configurar Entity Framework Core con SQLite para que cree un archivo local ferula_data.db.

5. Plan de Ejecución (Paso a Paso)

Claude, debes ejecutar este proyecto en las siguientes fases. Pide confirmación al usuario antes de avanzar a la siguiente fase.

    Fase 1: Estructura C#. Crea la solución de C#, el proyecto de Avalonia UI, instala los paquetes NuGet necesarios (Entity Framework, LiveCharts2, QuestPDF, Newtonsoft.Json/System.Text.Json) y crea la estructura de carpetas (Models, ViewModels, Views, Services).

    Fase 2: Base de Datos C#. Crea los Models (Paciente, Sesion) y el DbContext de SQLite.

    Fase 3: Firmware Base. Crea el platformio.ini con las dependencias (WebSocketsServer, ArduinoJson, Adafruit INA219).

    Fase 4: Core de Firmware. Escribe el código C++ dividiendo las tareas en FreeRTOS (Core 0 para red, Core 1 para control de motores y lectura I2C).

    Fase 5: Conectividad. Implementa el servicio WebSocket en el ViewModel de C# para conectarse al ESP32 y deserializar la telemetría a 10Hz.

    Fase 6: UI y Gráficas. Construye las vistas XAML en Avalonia y bindea los datos a LiveCharts2.