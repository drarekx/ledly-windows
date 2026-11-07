# Ledly para Windows

Port a Windows de Ledly (`../monitor-led`, app de macOS en Swift): controla una tira WS2812B conectada a un Arduino Nano con los colores del borde de la pantalla (ambilight), efectos o un color fijo, y ofrece una API HTTP/WebSocket compatible con WLED. Mismas funciones, mismo firmware, misma API. Repo: https://github.com/drarekx/ledly-windows.

La app de Mac es la referencia: ante cualquier duda de comportamiento, mirar su código (`../monitor-led/Sources/Ledly/*.swift`) y su `CLAUDE.md`. `docs/hyperhdr-notes.md` (copiado de allí) describe Adalight, zonas, suavizado y efectos.

## Arquitectura

C# / .NET 10. Tres proyectos (`Ledly.slnx`):

| Proyecto | Qué es |
|---|---|
| `src/Ledly.Core` (`net10.0`) | Todo lo que no depende del sistema: motor, efectos, ecualizador, audio (FFT), muestreo de zonas, Adalight, instalador STK500, API/WLED/DDP/mDNS, ajustes, traducciones, actualizador. Corre en Windows y en macOS. |
| `src/Ledly` (`net10.0-windows`) | App de bandeja: icono y menú, ventana WebView2 con la interfaz web, captura (Windows.Graphics.Capture), audio (WASAPI loopback), puertos COM, inicio con Windows, suspensión, instalar actualizaciones. Implementa `IPlatform`. |
| `src/Ledly.Dev` (`net10.0`) | Host de desarrollo con pantalla y audio falsos: prueba núcleo, API e interfaz desde el Mac. |

- La interfaz es web (`web/`, recursos incrustados en el núcleo) y la sirve la propia API en `/`. Endpoints propios de la interfaz: `docs/app-api.md`.
- Lo que cambia por sistema pasa por `IPlatform` (`src/Ledly.Core/Platform/IPlatform.cs`).
- Hilos: el bucle de salida vive en `EngineQueue` (hilo propio a 50 Hz). `Settings` es seguro entre hilos y avisa con `Changed`. `LEDEngine` publica estado con `StateChanged` y colores con `FrameReady` (solo con oyentes).

## Compilar y ejecutar

```sh
dotnet build Ledly.slnx                          # todo (en macOS compila también la app de Windows: EnableWindowsTargeting)
dotnet run --project src/Ledly.Dev               # núcleo + API + interfaz en http://127.0.0.1:8765/ con pantalla falsa
dotnet run --project src/Ledly.Dev -- --flash /dev/cu.usbserial-XXXX 6
```

En Windows: `.\build.ps1` → `build\Ledly-win-x64.zip` (autocontenido, un solo `Ledly.exe`). `Ledly.exe --flash COM3 6`, `--read-flash COM3 out.hex`.

- No hay tests. Verificar compilando, con el host de desarrollo y la API (`curl localhost:8765/api/state`).
- Lo específico de Windows solo se prueba en Windows (o en CI, que compila con `windows-latest`).

## Publicar

En Windows no hay permiso de Grabación de pantalla que dependa de la firma: los releases los hace CI (`.github/workflows/release.yml`, se lanza al subir una etiqueta `vX.Y.Z`). Sube `Ledly-win-x64.zip` y `.sha256`; las copias instaladas se actualizan solas (`Updater`). Instalador: `install.ps1` (`irm … | iex`).

## Firmware (`firmware/`)

Copia del de la app de Mac (mismo sketch, mismos `.hex`). Si cambia allí, copiarlo aquí y subir `Firmware.Version`.

## Convenciones

- Código, comentarios y commits en inglés. Interfaz en castellano, inglés y catalán.
- Todo texto visible pasa por traducción; la clave es el castellano. Tablas en `web/i18n/en.json` y `ca.json`, compartidas por la interfaz web (`t("…")`) y el núcleo (`L.Tr("…")`). Formatos con `{0}`, `{1}`.
- La API y los ajustes usan los nombres de `LightMode`/`EffectKind`/… tal cual (minúsculas, como en Mac): no renombrarlos. Efectos nuevos al final del `enum` (número WLED = posición) y en su sitio en `EffectKindInfo.Ordered`.
- Mantener paridad con la app de Mac: misma API, mismos valores por defecto, mismos nombres.

## Cosas que ya costaron en Mac (aplican aquí)

- A 1 Mbaud el host espera el `k` del firmware tras cada fotograma.
- Reabrir el puerto enseguida no resetea el Nano: `LEDDevice.Connect` pulsa DTR a mano.
- Prioridades del bucle (`OutputLoop.TargetColors`): patrones de prueba > tiempo real (DDP) > apagado > arranque > aviso temporal > modo normal.
- Nada que cambie a ritmo de fotograma en el estado general: los colores van por `FrameReady` y solo con oyentes; la vista previa de captura solo se genera mientras alguien la pide.
- JSON: distinguir booleanos de números al leer (`true` ≠ `1`).
