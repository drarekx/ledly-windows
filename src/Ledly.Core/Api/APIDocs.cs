using System.Text.Json.Nodes;

namespace Ledly;

/// <summary>
/// API reference served by the API itself: <c>GET /api/openapi.json</c> (OpenAPI 3.0) and
/// <c>GET /api/docs</c> (an HTML page that renders that spec and can try every endpoint).
/// Port of the Mac app's APIDocs.swift, plus the "App" tag (docs/app-api.md).
/// </summary>
public static partial class APIDocs
{
    static string JsonList(IEnumerable<string> items) => new JsonArray(items.Select(i => (JsonNode)i).ToArray()).ToJsonString();

    public static JsonObject OpenAPI(int port)
    {
        var ddpPort = Settings.Shared.DdpPort;
        var json = Spec
            .Replace("@VERSION@", Updater.CurrentVersion)
            .Replace("@PORT@", port.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("@DDPPORT@", ddpPort.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("\"@EFFECTS@\"", JsonList(Enum.GetNames<EffectKind>()))
            .Replace("\"@MODES@\"", JsonList(Enum.GetNames<LightMode>()))
            .Replace("\"@PRESETS@\"", JsonList(EqualizerOptions.Presets.Select(p => p.Id)))
            .Replace("\"@EQLAYOUTS@\"", JsonList(Enum.GetNames<EqualizerLayout>()))
            .Replace("\"@EQSTYLES@\"", JsonList(Enum.GetNames<EqualizerStyle>()))
            .Replace("\"@EQCOLORS@\"", JsonList(Enum.GetNames<EqualizerColors>()))
            .Replace("\"@CORNERS@\"", JsonList(Enum.GetNames<StartCorner>()));
        var spec = JsonNode.Parse(json)!.AsObject();
        spec["x-ddp"] = Ddp(ddpPort, port);
        return spec;
    }

    /// <summary>
    /// Real-time frames over UDP. OpenAPI can't describe UDP, so it travels as an extension
    /// that the docs page renders as its own section.
    /// </summary>
    public static JsonObject Ddp(int port, int apiPort)
    {
        JsonObject Field(string bytes, string field, string value, string description) =>
            new() { ["bytes"] = bytes, ["field"] = field, ["value"] = value, ["description"] = description };
        JsonObject Example(string title, string code) => new() { ["title"] = title, ["code"] = code };
        return new JsonObject
        {
            ["title"] = "Tiempo real (DDP)",
            ["summary"] = "Manda fotogramas a la tira por UDP con el protocolo DDP (el mismo que WLED). Pensado para animaciones desde otro programa: un paquete por fotograma, sin HTTP.",
            ["transport"] = $"UDP, puerto {port} (Ajustes → API → Tiempo real). Sin token. Misma política de red que la API: con «Solo este PC» solo escucha en 127.0.0.1.",
            ["header"] = new JsonArray(
                Field("0", "flags", "0x41 / 0x40",
                    "Bits 7-6 = 01 (versión 1). 0x01 = push: pinta el fotograma; ponlo en el último paquete. 0x10 = hay timecode (la cabecera pasa a 14 bytes y se ignora). 0x02 = consulta: no se responde."),
                Field("1", "secuencia", "0–15", "4 bits bajos. Informativo; no se reordena."),
                Field("2", "tipo", "0x0B", "RGB de 8 bits. También se aceptan 0x01 y 0x00 como RGB."),
                Field("3", "destino", "1", "Salida por defecto. 255 (todas) también vale; otros se ignoran."),
                Field("4–7", "desplazamiento", "0", "Bytes desde el primer LED, 32 bits big-endian. LED k empieza en k × 3."),
                Field("8–9", "longitud", "n × 3", "Bytes de datos de este paquete, 16 bits big-endian. Hasta 1440 por paquete (480 LEDs)."),
                Field("10…", "datos", "R,G,B…",
                    "Tres bytes por LED en el orden de la tira: el LED 0 es el inicio de la disposición (esquina y sentido de Disposición).")),
            ["rules"] = new JsonArray(
                "Un fotograma puede ir en varios paquetes; se pinta al llegar el que lleva push. Las partes no enviadas conservan lo anterior.",
                "Con el primer fotograma empieza el tiempo real: manda sobre la pantalla, los efectos, el color fijo y los avisos, y enciende la tira aunque esté apagada.",
                "Se aplican el brillo de Ledly, la temperatura, las ganancias de color y el límite de consumo. No se aplican gamma ni saturación.",
                "Se muestra tal cual llega, sin suavizado: la animación la hace quien manda.",
                "Si sobran LEDs se ignoran; si faltan, los que no llegan quedan apagados. El número de LEDs está en `leds` de GET /api/state.",
                "Sin fotogramas durante 2,5 s vuelve sola a lo que tenía (también a apagada), sin tocar ningún ajuste.",
                "POST /api/state {\"live\": false} (o POST /json/state {\"live\": false}, como en WLED) suelta el tiempo real al momento; los paquetes que lleguen durante 1 s después se ignoran.",
                "GET /api/state: `live` (true/false) y `liveSource` (dirección de quien manda)."),
            ["examples"] = new JsonArray(
                Example("PHP (un paquete por fotograma)", $$"""
                    $sock = socket_create(AF_INET, SOCK_DGRAM, SOL_UDP);
                    $leds = 96;                                    // GET /api/state → leds
                    for ($seq = 0; ; $seq++) {
                        $data = '';
                        for ($i = 0; $i < $leds; $i++) {
                            $data .= chr(255) . chr(0) . chr(($i * 8) % 256);   // R, G, B
                        }
                        $header = pack('CCCCNn', 0x41, $seq & 0x0F, 0x0B, 1, 0, strlen($data));
                        socket_sendto($sock, $header . $data, 10 + strlen($data), 0, 'host.docker.internal', {{port}});
                        usleep(33333);                             // 30 fps
                    }
                    """),
                Example("PHP (fotograma en varios paquetes)", $$"""
                    $chunks = str_split($data, 1440);
                    foreach ($chunks as $k => $chunk) {
                        $push = $k === count($chunks) - 1 ? 0x01 : 0;
                        $header = pack('CCCCNn', 0x40 | $push, $seq & 0x0F, 0x0B, 1, $k * 1440, strlen($chunk));
                        socket_sendto($sock, $header . $chunk, 10 + strlen($chunk), 0, 'host.docker.internal', {{port}});
                    }
                    """),
                Example("Python", $$"""
                    import socket
                    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
                    data = bytes([255, 0, 0]) * 96
                    header = bytes([0x41, 0, 0x0B, 1]) + (0).to_bytes(4, "big") + len(data).to_bytes(2, "big")
                    sock.sendto(header + data, ("localhost", {{port}}))
                    """),
                Example("Soltar el tiempo real", $$"""
                    curl -X POST http://localhost:{{apiPort}}/api/state -d '{"live": false}'
                    """)),
        };
    }

    const string Spec = """
    {
      "openapi": "3.0.3",
      "info": {
        "title": "Ledly API",
        "version": "@VERSION@",
        "description": "Controla la tira de LEDs de Ledly y consulta su estado.\n\n- **Autenticación**: si hay token en Ajustes → API, las rutas `/api/*` piden `Authorization: Bearer <token>` o `?token=<token>`. Desde este PC (127.0.0.1) no hace falta. La documentación, `/ws` y la parte WLED quedan abiertas.\n- **CORS** abierto: se puede llamar desde cualquier página web.\n- **WebSocket** `ws://<host>:@PORT@/api/ws` (con `?token=` si hace falta): al conectar envía `{\"state\":…,\"wled\":…}` y lo repite en cada cambio. Manda `{\"subscribe\":\"leds\"}` para recibir `{\"leds\":[\"#RRGGBB\",…]}` a ~15 fps (`{\"unsubscribe\":\"leds\"}` para parar). También acepta los mismos cuerpos que `POST /api/state`.\n- **Tiempo real (DDP)**: fotogramas por UDP en el puerto @DDPPORT@, sin token. Ver la sección «Tiempo real (DDP)» más abajo.\n- **WLED**: `/json/*`, `/win` y el WebSocket `/ws` imitan WLED 0.14 para Home Assistant y las apps de WLED. Efecto 0 = Solid (color), luego los de Ledly en orden, y el último = Ambilight (pantalla).\n- **API desactivada**: solo escucha en 127.0.0.1 y solo responde la interfaz de la app (`/`, `/app/*`, `/api/app/*`, `/api/ws`) y esta documentación; el resto devuelve 403."
      },
      "servers": [{"url": "http://localhost:@PORT@"}],
      "tags": [
        {"name": "Estado", "description": "Encendido, modo, efecto, color y brillo."},
        {"name": "Información", "description": "Efectos, LEDs, dispositivo y disposición."},
        {"name": "WLED", "description": "Compatibilidad con WLED."},
        {"name": "App", "description": "Rutas de la interfaz web de la app (ventana de Ledly). JSON en camelCase; los enums van por su nombre. Errores: {\"error\": \"…\"} con 400/404/409/500. Además, por /api/ws: {\"subscribe\":\"app\"} → {\"type\":\"app-status\",…} en cada cambio; {\"subscribe\":\"colors\"} → {\"type\":\"colors\",\"colors\":[…]} a ~15 fps."},
        {"name": "Documentación"}
      ],
      "components": {
        "securitySchemes": {"token": {"type": "http", "scheme": "bearer"}},
        "responses": {
          "BadRequest": {"description": "Petición no válida", "content": {"application/json": {"schema": {"type": "object", "properties": {"error": {"type": "string"}}}}}},
          "Unauthorized": {"description": "Falta el token o no coincide", "content": {"application/json": {"schema": {"type": "object", "properties": {"error": {"type": "string", "example": "unauthorized"}}}}}}
        },
        "schemas": {
          "State": {
            "type": "object",
            "properties": {
              "on": {"type": "boolean"},
              "mode": {"type": "string", "enum": "@MODES@"},
              "effect": {"type": "string", "enum": "@EFFECTS@"},
              "speed": {"type": "number", "minimum": 0.1, "maximum": 3},
              "equalizer": {"$ref": "#/components/schemas/Equalizer"},
              "color": {"type": "string", "example": "#FF8A3D"},
              "brightness": {"type": "integer", "minimum": 0, "maximum": 100},
              "overlay": {"type": "boolean", "description": "Hay un aviso temporal (`duration`) en pantalla."},
              "live": {"type": "boolean", "description": "Se están mostrando fotogramas recibidos por DDP (tiempo real)."},
              "liveSource": {"type": "string", "nullable": true, "description": "Dirección de quien manda los fotogramas."},
              "leds": {"type": "integer", "description": "LEDs de la disposición."},
              "fps": {"type": "number", "description": "Fotogramas por segundo que llegan a la tira."},
              "capture": {"type": "string", "description": "stopped, starting, running, no-permission o error: …"},
              "captureSource": {"type": "string", "description": "Qué se captura: la pantalla o la ventana de la app elegida en Imagen → Capturar."},
              "audio": {"type": "object", "nullable": true, "description": "Audio del sistema que ven los efectos de música (null si no hay ninguno activo): `level`, `bass` y 16 `bands` de 0 a 1, y `lastBeat`."},
              "power": {"type": "object", "description": "Consumo estimado de la tira.", "properties": {
                "milliamps": {"type": "integer"},
                "limited": {"type": "boolean", "description": "El limitador está bajando el brillo."},
                "limit": {"type": "integer", "description": "Límite en mA; 0 = sin límite."}
              }},
              "device": {"$ref": "#/components/schemas/Device"}
            }
          },
          "StateChange": {
            "type": "object",
            "description": "Todo es opcional. Mandar solo `color` pasa a modo color; solo `effect`, a modo efecto. Cambiar modo, color o efecto enciende la tira salvo que se mande `on`.",
            "properties": {
              "on": {"type": "boolean"},
              "toggle": {"type": "boolean", "description": "Invierte el encendido."},
              "mode": {"type": "string", "enum": "@MODES@"},
              "effect": {"type": "string", "enum": "@EFFECTS@"},
              "speed": {"type": "number", "minimum": 0.1, "maximum": 3, "description": "1 = normal."},
              "color": {"$ref": "#/components/schemas/Color"},
              "equalizer": {"$ref": "#/components/schemas/Equalizer"},
              "brightness": {"type": "number", "minimum": 0, "maximum": 100},
              "duration": {"type": "number", "minimum": 0, "maximum": 3600, "description": "Segundos. Muestra el color o efecto ese tiempo y vuelve a lo que había (avisos). No cambia los ajustes guardados."},
              "clear": {"type": "boolean", "description": "Quita el aviso temporal en curso."},
              "live": {"type": "boolean", "enum": [false], "description": "`false` suelta el tiempo real al momento; los paquetes que lleguen en el siguiente segundo se ignoran (los que venían con retraso). El tiempo real empieza solo al llegar fotogramas DDP."}
            },
            "example": {"effect": "fire", "speed": 1.5, "brightness": 70}
          },
          "Color": {
            "description": "\"#RRGGBB\", [r,g,b] o {\"r\":…,\"g\":…,\"b\":…} (0–255)",
            "oneOf": [
              {"type": "string", "pattern": "^#?[0-9A-Fa-f]{6}$", "example": "#FF2D95"},
              {"type": "array", "items": {"type": "integer", "minimum": 0, "maximum": 255}, "minItems": 3, "maxItems": 3},
              {"type": "object", "properties": {"r": {"type": "integer"}, "g": {"type": "integer"}, "b": {"type": "integer"}}}
            ]
          },
          "Device": {
            "type": "object",
            "properties": {
              "status": {"type": "string", "enum": ["disconnected", "connecting", "connected", "error"]},
              "port": {"type": "string", "nullable": true, "example": "COM3"},
              "pin": {"type": "integer", "example": 6},
              "firmware": {"type": "string", "nullable": true, "example": "Ada Ledly 2 pin 6 max 400 baud 1000000"},
              "error": {"type": "string"}
            }
          },
          "Equalizer": {
            "type": "object",
            "description": "Opciones del efecto `musicSpectrum`. Al cambiarlas se muestra el ecualizador. `preset` aplica una combinación y los demás campos la retocan.",
            "properties": {
              "preset": {"type": "string", "enum": "@PRESETS@"},
              "layout": {"type": "string", "enum": "@EQLAYOUTS@"},
              "style": {"type": "string", "enum": "@EQSTYLES@"},
              "colors": {"type": "string", "enum": "@EQCOLORS@"},
              "sensitivity": {"type": "number", "minimum": 0.5, "maximum": 2.5}
            }
          },
          "Effect": {
            "type": "object",
            "properties": {
              "id": {"type": "string", "enum": "@EFFECTS@"},
              "name": {"type": "string"},
              "usesColor": {"type": "boolean", "description": "El efecto usa el color principal."}
            }
          },
          "Layout": {
            "type": "object",
            "properties": {
              "top": {"type": "integer"}, "right": {"type": "integer"},
              "bottom": {"type": "integer"}, "left": {"type": "integer"},
              "start": {"type": "string", "enum": "@CORNERS@"},
              "clockwise": {"type": "boolean"}, "offset": {"type": "integer"},
              "bottomGap": {"type": "integer"}, "count": {"type": "integer"}
            }
          },
          "Ok": {"type": "object", "properties": {"ok": {"type": "boolean", "example": true}}},
          "AppSettings": {
            "type": "object",
            "description": "Todos los ajustes (los mismos nombres que settings.json) más `launchAtLogin`. Objetos anidados: `layout`, `equalizer`, `calibration`.",
            "properties": {
              "isOn": {"type": "boolean"}, "mode": {"type": "string", "enum": "@MODES@"}, "effect": {"type": "string", "enum": "@EFFECTS@"},
              "effectSpeed": {"type": "number"}, "equalizer": {"type": "object"}, "colorHex": {"type": "string"},
              "brightness": {"type": "number", "minimum": 0, "maximum": 1}, "portPath": {"type": "string", "nullable": true},
              "dataPin": {"type": "integer"}, "layout": {"type": "object"}, "displayId": {"type": "string"},
              "captureApp": {"type": "string", "nullable": true}, "captureFps": {"type": "integer"},
              "sampling": {"type": "string"}, "blackBorders": {"type": "boolean"}, "smoothingMs": {"type": "number"},
              "smoothing": {"type": "string"}, "antiFlicker": {"type": "boolean"}, "gamma": {"type": "number"},
              "saturation": {"type": "number"}, "temperature": {"type": "number"}, "backlight": {"type": "number"},
              "calibration": {"type": "object"}, "apiEnabled": {"type": "boolean"}, "apiPort": {"type": "integer"},
              "apiLocalOnly": {"type": "boolean"}, "apiToken": {"type": "string"}, "ddpEnabled": {"type": "boolean"},
              "ddpPort": {"type": "integer"}, "wledDiscovery": {"type": "boolean"}, "powerLimitEnabled": {"type": "boolean"},
              "powerLimitMilliamps": {"type": "integer"}, "offWhenAsleep": {"type": "boolean"}, "language": {"type": "string"},
              "autoCheckUpdates": {"type": "boolean"}, "launchAtLogin": {"type": "boolean"}
            }
          },
          "AppStatus": {
            "type": "object",
            "description": "Estado de la app para la interfaz: dispositivo, puertos, pantallas, apps, captura, fps, consumo, instalación de firmware, API, DDP, actualizaciones y audio. Pantallas y apps se guardan ~2 s.",
            "properties": {
              "version": {"type": "string"}, "platform": {"type": "string"},
              "device": {"type": "object", "description": "status, port, firmware, error, ledlyVersion, ledlyPin, firmwareOutdated"},
              "ports": {"type": "array", "items": {"type": "object"}}, "displays": {"type": "array", "items": {"type": "object"}},
              "apps": {"type": "array", "items": {"type": "object"}},
              "capture": {"type": "object", "description": "status (stopped, starting, running, noPermission, failed), message, source"},
              "fps": {"type": "number"}, "power": {"type": "object"}, "overlay": {"type": "boolean"},
              "live": {"type": "boolean"}, "liveSource": {"type": "string", "nullable": true},
              "flash": {"type": "object", "description": "state (idle, running, done, failed), step, progress, error"},
              "api": {"type": "object", "description": "status, port, error, clients, addresses (IPv4 de este PC)"},
              "ddp": {"type": "object"}, "update": {"type": "object", "nullable": true}, "audio": {"type": "object", "nullable": true}
            }
          }
        }
      },
      "security": [{"token": []}, {}],
      "paths": {
        "/api/state": {
          "get": {
            "tags": ["Estado"], "summary": "Estado actual", "operationId": "getState",
            "responses": {"200": {"description": "OK", "content": {"application/json": {"schema": {"$ref": "#/components/schemas/State"}}}}, "401": {"$ref": "#/components/responses/Unauthorized"}}
          },
          "post": {
            "tags": ["Estado"], "summary": "Cambiar el estado", "operationId": "setState",
            "description": "También acepta PUT y PATCH. Devuelve el estado resultante.",
            "requestBody": {"required": true, "content": {"application/json": {
              "schema": {"$ref": "#/components/schemas/StateChange"},
              "examples": {
                "color": {"summary": "Color fijo al 60 %", "value": {"color": "#FF2D95", "brightness": 60}},
                "efecto": {"summary": "Efecto", "value": {"effect": "fire", "speed": 1.5}},
                "pantalla": {"summary": "Volver a la pantalla", "value": {"mode": "screen"}},
                "ecualizador": {"summary": "Ecualizador estéreo en barras", "value": {"equalizer": {"layout": "stereo", "style": "bars", "colors": "heat"}}},
                "soltar": {"summary": "Soltar el tiempo real (DDP)", "value": {"live": false}},
                "aviso": {"summary": "Aviso rojo de 3 s", "value": {"effect": "strobe", "color": "#FF0000", "duration": 3}},
                "apagar": {"summary": "Apagar", "value": {"on": false}}
              }
            }}},
            "responses": {"200": {"description": "OK", "content": {"application/json": {"schema": {"$ref": "#/components/schemas/State"}}}}, "400": {"$ref": "#/components/responses/BadRequest"}, "401": {"$ref": "#/components/responses/Unauthorized"}}
          }
        },
        "/api/effects": {
          "get": {
            "tags": ["Información"], "summary": "Efectos disponibles", "operationId": "getEffects",
            "responses": {"200": {"description": "OK", "content": {"application/json": {"schema": {"type": "array", "items": {"$ref": "#/components/schemas/Effect"}}}}}, "401": {"$ref": "#/components/responses/Unauthorized"}}
          }
        },
        "/api/leds": {
          "get": {
            "tags": ["Información"], "summary": "Colores que se envían ahora a la tira", "operationId": "getLEDs",
            "description": "Ya corregidos (brillo, gamma…) y en el orden de la tira.",
            "responses": {"200": {"description": "OK", "content": {"application/json": {"schema": {"type": "object", "properties": {
              "count": {"type": "integer"},
              "colors": {"type": "array", "items": {"type": "string", "example": "#351D0D"}}
            }}}}}, "401": {"$ref": "#/components/responses/Unauthorized"}}
          }
        },
        "/api/device": {
          "get": {
            "tags": ["Información"], "summary": "Arduino: puerto, pin, firmware y conexión", "operationId": "getDevice",
            "responses": {"200": {"description": "OK", "content": {"application/json": {"schema": {"$ref": "#/components/schemas/Device"}}}}, "401": {"$ref": "#/components/responses/Unauthorized"}}
          }
        },
        "/api/device/reconnect": {
          "post": {
            "tags": ["Información"], "summary": "Reconectar con el Arduino", "operationId": "reconnect",
            "description": "Cierra el puerto y vuelve a abrirlo en el siguiente ciclo (resetea el Nano).",
            "responses": {"200": {"description": "OK", "content": {"application/json": {"schema": {"$ref": "#/components/schemas/Device"}}}}, "401": {"$ref": "#/components/responses/Unauthorized"}}
          }
        },
        "/api/layout": {
          "get": {
            "tags": ["Información"], "summary": "Disposición de la tira", "operationId": "getLayout",
            "responses": {"200": {"description": "OK", "content": {"application/json": {"schema": {"$ref": "#/components/schemas/Layout"}}}}, "401": {"$ref": "#/components/responses/Unauthorized"}}
          }
        },
        "/api/app/settings": {
          "get": {
            "tags": ["App"], "summary": "Todos los ajustes", "operationId": "appGetSettings",
            "responses": {"200": {"description": "OK", "content": {"application/json": {"schema": {"$ref": "#/components/schemas/AppSettings"}}}}}
          },
          "patch": {
            "tags": ["App"], "summary": "Cambiar ajustes", "operationId": "appPatchSettings",
            "description": "Cualquier subconjunto de los ajustes; los objetos anidados se mezclan (`{\"layout\":{\"top\":30}}`). `launchAtLogin` va al sistema. Devuelve todos los ajustes. También acepta POST.",
            "requestBody": {"required": true, "content": {"application/json": {"schema": {"type": "object"},
              "examples": {
                "brillo": {"summary": "Brillo al 50 %", "value": {"brightness": 0.5}},
                "disposicion": {"summary": "Cambiar un lado", "value": {"layout": {"top": 30}}}
              }}}},
            "responses": {"200": {"description": "OK", "content": {"application/json": {"schema": {"$ref": "#/components/schemas/AppSettings"}}}}, "400": {"$ref": "#/components/responses/BadRequest"}}
          }
        },
        "/api/app/settings/reset-image": {
          "post": {
            "tags": ["App"], "summary": "Valores por defecto de la sección Imagen", "operationId": "appResetImage",
            "responses": {"200": {"description": "OK", "content": {"application/json": {"schema": {"$ref": "#/components/schemas/AppSettings"}}}}}
          }
        },
        "/api/app/status": {
          "get": {
            "tags": ["App"], "summary": "Estado de la app", "operationId": "appStatus",
            "responses": {"200": {"description": "OK", "content": {"application/json": {"schema": {"$ref": "#/components/schemas/AppStatus"}}}}}
          }
        },
        "/api/app/meta": {
          "get": {
            "tags": ["App"], "summary": "Listas fijas: efectos, ecualizador, firmware, idiomas, esquinas de calibración", "operationId": "appMeta",
            "description": "`effects` en el orden de la app, con `titleKey` (clave de traducción), `music`, `usesColor` y `wled` (número de efecto WLED).",
            "responses": {"200": {"description": "OK", "content": {"application/json": {"schema": {"type": "object"}}}}}
          }
        },
        "/api/app/test": {
          "post": {
            "tags": ["App"], "summary": "Patrón de prueba de los asistentes", "operationId": "appTest",
            "description": "`pattern`: direction, sides, calibration (con `color`), cursor (con `index` y `count`) o null para quitarlo. cursor y calibration se quitan solos si no llega otra llamada en 30 s.",
            "requestBody": {"required": true, "content": {"application/json": {"schema": {"type": "object", "properties": {
              "pattern": {"type": "string", "nullable": true, "enum": ["direction", "sides", "calibration", "cursor", null]},
              "color": {"type": "string", "example": "#FF0000"}, "index": {"type": "integer"}, "count": {"type": "integer"}
            }}, "examples": {
              "cursor": {"summary": "LED 3 de 64", "value": {"pattern": "cursor", "index": 3, "count": 64}},
              "quitar": {"summary": "Quitar", "value": {"pattern": null}}
            }}}},
            "responses": {"200": {"description": "OK", "content": {"application/json": {"schema": {"$ref": "#/components/schemas/Ok"}}}}, "400": {"$ref": "#/components/responses/BadRequest"}}
          }
        },
        "/api/app/layout/solve": {
          "post": {
            "tags": ["App"], "summary": "Calcular la disposición a partir de las esquinas marcadas", "operationId": "appSolveLayout",
            "description": "No guarda nada: devuelve `{\"layout\": …}` para guardarlo con PATCH /api/app/settings, o 400.",
            "requestBody": {"required": true, "content": {"application/json": {"schema": {"type": "object"},
              "example": {"count": 64, "corners": {"topLeft": 0, "topRight": 20, "bottomRight": 32, "bottomLeft": 52}, "hasBottomGap": false}}}},
            "responses": {"200": {"description": "OK", "content": {"application/json": {"schema": {"type": "object", "properties": {"layout": {"type": "object"}}}}}}, "400": {"$ref": "#/components/responses/BadRequest"}}
          }
        },
        "/api/app/device/reconnect": {
          "post": {"tags": ["App"], "summary": "Reconectar con el Arduino", "operationId": "appReconnect",
            "responses": {"200": {"description": "OK", "content": {"application/json": {"schema": {"$ref": "#/components/schemas/Ok"}}}}}}
        },
        "/api/app/device/refresh-ports": {
          "post": {"tags": ["App"], "summary": "Volver a buscar puertos serie", "operationId": "appRefreshPorts",
            "responses": {"200": {"description": "OK", "content": {"application/json": {"schema": {"$ref": "#/components/schemas/Ok"}}}}}}
        },
        "/api/app/device/flash": {
          "post": {"tags": ["App"], "summary": "Instalar el firmware en el Arduino", "operationId": "appFlash",
            "description": "Responde 202 al momento; el progreso va en `flash` de /api/app/status. 409 si ya hay una instalación en marcha.",
            "requestBody": {"required": true, "content": {"application/json": {"schema": {"type": "object"}, "example": {"pin": 6, "port": "COM3"}}}},
            "responses": {"202": {"description": "Empezada", "content": {"application/json": {"schema": {"$ref": "#/components/schemas/Ok"}}}}, "400": {"$ref": "#/components/responses/BadRequest"}, "409": {"$ref": "#/components/responses/BadRequest"}}}
        },
        "/api/app/device/flash/reset": {
          "post": {"tags": ["App"], "summary": "Olvidar el resultado de la última instalación", "operationId": "appFlashReset",
            "responses": {"200": {"description": "OK", "content": {"application/json": {"schema": {"$ref": "#/components/schemas/Ok"}}}}}}
        },
        "/api/app/capture.png": {
          "get": {"tags": ["App"], "summary": "Última imagen de la captura", "operationId": "appCapture",
            "description": "Cabeceras `X-Content-Rect: x,y,w,h` (píxeles de la imagen, tras quitar bordes negros) y `X-Image-Size: w,h`. 204 si aún no hay ninguna. Cada petición mantiene las imágenes 3 s.",
            "responses": {"200": {"description": "PNG", "content": {"image/png": {"schema": {"type": "string", "format": "binary"}}}}, "204": {"description": "Sin imagen todavía"}}}
        },
        "/api/app/overlay/clear": {
          "post": {"tags": ["App"], "summary": "Quitar el aviso temporal", "operationId": "appOverlayClear",
            "responses": {"200": {"description": "OK", "content": {"application/json": {"schema": {"$ref": "#/components/schemas/Ok"}}}}}}
        },
        "/api/app/live/end": {
          "post": {"tags": ["App"], "summary": "Soltar el tiempo real (DDP)", "operationId": "appLiveEnd",
            "responses": {"200": {"description": "OK", "content": {"application/json": {"schema": {"$ref": "#/components/schemas/Ok"}}}}}}
        },
        "/api/app/update/check": {
          "post": {"tags": ["App"], "summary": "Buscar actualizaciones ahora", "operationId": "appUpdateCheck",
            "description": "Responde 202; el resultado va en `update` de /api/app/status.",
            "responses": {"202": {"description": "Empezada", "content": {"application/json": {"schema": {"$ref": "#/components/schemas/Ok"}}}}}}
        },
        "/api/app/update/install": {
          "post": {"tags": ["App"], "summary": "Descargar e instalar la actualización", "operationId": "appUpdateInstall",
            "responses": {"202": {"description": "Empezada", "content": {"application/json": {"schema": {"$ref": "#/components/schemas/Ok"}}}}}}
        },
        "/api/app/update/skip": {
          "post": {"tags": ["App"], "summary": "Saltar esta versión", "operationId": "appUpdateSkip",
            "responses": {"200": {"description": "OK", "content": {"application/json": {"schema": {"$ref": "#/components/schemas/Ok"}}}}}}
        },
        "/api/app/open": {
          "post": {"tags": ["App"], "summary": "Abrir un enlace en el navegador", "operationId": "appOpen",
            "description": "Solo URL http(s).",
            "requestBody": {"required": true, "content": {"application/json": {"schema": {"type": "object"}, "example": {"url": "https://github.com/drarekx/ledly-windows"}}}},
            "responses": {"200": {"description": "OK", "content": {"application/json": {"schema": {"$ref": "#/components/schemas/Ok"}}}}, "400": {"$ref": "#/components/responses/BadRequest"}}}
        },
        "/api/app/quit": {
          "post": {"tags": ["App"], "summary": "Cerrar la app", "operationId": "appQuit",
            "description": "Solo desde este PC.",
            "responses": {"200": {"description": "OK", "content": {"application/json": {"schema": {"$ref": "#/components/schemas/Ok"}}}}, "403": {"$ref": "#/components/responses/BadRequest"}}}
        },
        "/api": {
          "get": {
            "tags": ["Documentación"], "summary": "Índice de rutas", "operationId": "index",
            "responses": {"200": {"description": "OK", "content": {"application/json": {"schema": {"type": "object"}}}}}
          }
        },
        "/api/openapi.json": {
          "get": {
            "tags": ["Documentación"], "summary": "Esta especificación (OpenAPI 3.0)", "operationId": "openapi",
            "security": [], "responses": {"200": {"description": "OK", "content": {"application/json": {"schema": {"type": "object"}}}}}
          }
        },
        "/api/docs": {
          "get": {
            "tags": ["Documentación"], "summary": "Esta página", "operationId": "docs", "security": [],
            "responses": {"200": {"description": "HTML", "content": {"text/html": {"schema": {"type": "string"}}}}}
          }
        },
        "/json": {
          "get": {
            "tags": ["WLED"], "summary": "state + info + effects + palettes", "operationId": "wledAll", "security": [],
            "responses": {"200": {"description": "OK", "content": {"application/json": {"schema": {"type": "object"}}}}}
          },
          "post": {
            "tags": ["WLED"], "summary": "Igual que POST /json/state", "operationId": "wledSetAll", "security": [],
            "requestBody": {"content": {"application/json": {"schema": {"type": "object"}, "example": {"on": true, "bri": 128}}}},
            "responses": {"200": {"description": "OK", "content": {"application/json": {"schema": {"type": "object"}}}}}
          }
        },
        "/json/state": {
          "get": {
            "tags": ["WLED"], "summary": "Estado WLED", "operationId": "wledState", "security": [],
            "responses": {"200": {"description": "OK", "content": {"application/json": {"schema": {"type": "object"}}}}}
          },
          "post": {
            "tags": ["WLED"], "summary": "Cambiar el estado al estilo WLED", "operationId": "wledSetState", "security": [],
            "description": "Admite `on` (o \"t\"), `bri` (0–255), `v` (devolver el estado) y `seg` con `on`, `col`, `fx`, `sx`.",
            "requestBody": {"content": {"application/json": {"schema": {"type": "object"}, "example": {"on": true, "bri": 128, "seg": [{"col": [[0, 120, 255]], "fx": 0}], "v": true}}}},
            "responses": {"200": {"description": "OK", "content": {"application/json": {"schema": {"type": "object"}}}}}
          }
        },
        "/json/info": {
          "get": {"tags": ["WLED"], "summary": "Información WLED", "operationId": "wledInfo", "security": [],
            "responses": {"200": {"description": "OK", "content": {"application/json": {"schema": {"type": "object"}}}}}}
        },
        "/json/eff": {
          "get": {"tags": ["WLED"], "summary": "Nombres de efectos WLED", "operationId": "wledEffects", "security": [],
            "responses": {"200": {"description": "OK", "content": {"application/json": {"schema": {"type": "array", "items": {"type": "string"}}}}}}}
        },
        "/json/pal": {
          "get": {"tags": ["WLED"], "summary": "Paletas WLED (solo «Default»)", "operationId": "wledPalettes", "security": [],
            "responses": {"200": {"description": "OK", "content": {"application/json": {"schema": {"type": "array", "items": {"type": "string"}}}}}}}
        },
        "/json/si": {
          "get": {"tags": ["WLED"], "summary": "state + info", "operationId": "wledStateInfo", "security": [],
            "responses": {"200": {"description": "OK", "content": {"application/json": {"schema": {"type": "object"}}}}}}
        },
        "/win": {
          "get": {
            "tags": ["WLED"], "summary": "API HTTP antigua de WLED", "operationId": "wledLegacy", "security": [],
            "description": "Parámetros tras `&`: `/win&T=1&A=128&R=255&G=0&B=0&FX=3&SX=128`. T: 0 apagar, 1 encender, 2 alternar; A: brillo 0–255.",
            "parameters": [
              {"name": "T", "in": "query", "schema": {"type": "integer", "enum": [0, 1, 2]}},
              {"name": "A", "in": "query", "schema": {"type": "integer", "minimum": 0, "maximum": 255}},
              {"name": "R", "in": "query", "schema": {"type": "integer"}},
              {"name": "G", "in": "query", "schema": {"type": "integer"}},
              {"name": "B", "in": "query", "schema": {"type": "integer"}},
              {"name": "FX", "in": "query", "schema": {"type": "integer"}},
              {"name": "SX", "in": "query", "schema": {"type": "integer"}}
            ],
            "responses": {"200": {"description": "XML", "content": {"text/xml": {"schema": {"type": "string"}}}}}
          }
        }
      }
    }
    """;
}
