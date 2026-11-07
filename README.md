# Ledly para Windows

Ambilight para Windows: una tira de LEDs detrás del monitor que sigue los colores del borde de la pantalla, hace efectos (también con la música que suena) y se controla por API. Vive en el área de notificación (junto al reloj) y funciona con un **Arduino Nano** y una tira **WS2812B**. Es el port de [Ledly para macOS](https://github.com/drarekx/ledly): mismas funciones, mismo firmware y misma API. Réplica limpia de [HyperHDR](https://github.com/awawa-dev/HyperHDR).

## Instalar

Abre PowerShell (no hace falta como administrador) y pega:

```powershell
irm https://raw.githubusercontent.com/drarekx/ledly-windows/main/install.ps1 | iex
```

El instalador descarga la última versión, la copia en `%LOCALAPPDATA%\Programs\Ledly`, crea el acceso directo del menú Inicio y la abre. No hace falta nada más: ni Arduino IDE, ni .NET, normalmente ni drivers (Windows Update instala solo los del CH340, FTDI y CP210x). El firmware del Nano va dentro de la app.

Ledly se actualiza sola: busca versiones nuevas al arrancar y una vez al día (o desde el menú → **Buscar actualizaciones…**), comprueba la descarga con su SHA-256 y se reinicia ya actualizada. Se puede desactivar en Ajustes. Volver a ejecutar el comando de instalación también actualiza.

**Instalación manual:** descarga `Ledly-win-x64.zip` (o `Ledly-win-arm64.zip` en PCs ARM) de [Releases](https://github.com/drarekx/ledly-windows/releases/latest), descomprímelo donde quieras y abre `Ledly.exe`. La app no está firmada, así que la primera vez SmartScreen puede avisar: **Más información → Ejecutar de todas formas**.

Requisitos: Windows 10 versión 2004 o posterior, o Windows 11 (x64 o ARM64). La ventana usa Microsoft Edge WebView2, que ya viene con Windows 11 y con casi todos los Windows 10; si falta, Ledly abre la misma interfaz en el navegador. Un Arduino Nano (vale cualquier clon con CH340, FTDI o CP210x) y una tira WS2812B.

## Primeros pasos

1. **Monta la tira** detrás del monitor y conéctala al Nano:
   - Datos de la tira (DIN) a un pin digital del Nano (D6 es lo habitual), con una resistencia de 330 Ω si puedes.
   - GND de la tira con GND del Nano.
   - Alimenta la tira con una fuente de 5 V propia. El USB del PC solo da para unos pocos LEDs a poco brillo.
2. **Conecta el Nano por USB.** Ledly lo detecta solo (puerto `COM`).
3. **Instala el firmware:** Ledly → **Dispositivo** → elige el pin → **Instalar firmware**. Tarda unos segundos y antes guarda una copia del programa que tuviera el Nano. Solo hace falta una vez (y cuando la app avise de que hay una versión nueva).
4. **Di cómo está colocada la tira:** **Disposición → Asistente de disposición** enciende un solo LED que llevas al último y a cada esquina, y calcula el resto. También se puede poner a mano (LEDs de cada lado, esquina del primer LED y sentido, mirando la pantalla de frente); **Comprobar → Dirección** enciende el primer LED en verde y el último en rojo para ver si cuadra.
5. **Pon el límite de consumo** de tu fuente en **Dispositivo → Consumo** (1000 mA por defecto) para no forzarla.

En Windows no hay que dar permiso para leer la pantalla. En Windows 10 aparece un borde amarillo alrededor de lo que se captura (en Windows 11 no).

## Qué hace

- **Pantalla (ambilight)**: de toda la pantalla o solo de la ventana de una app (un juego, un reproductor), con una **vista de la captura** que enseña qué zona lee cada LED. Color de cada zona del borde (media, lineal o dominante), ignora las franjas negras de las películas, suavizado (exponencial o tipo muelle), anti-parpadeo en escenas oscuras, gamma, saturación, temperatura, luz mínima, ganancia por canal, **calibración avanzada** y asistente de balance de blancos. Captura con Windows.Graphics.Capture y reduce la imagen en la GPU, así que gasta muy poco.
- **Efectos**: todos los de HyperHDR (remolinos arcoíris, doble y atómico, ondas, olas, manchas de color en seis tonos, respiración, vela, coche fantástico, policía, estroboscopio, chispas, plasma, cine, aviso, apagado del sistema) y más (fuego, serpiente, barrido, Navidad, reloj, aurora, meteoro), con velocidad y color.
- **Música**: pulso, ondas, vúmetro y un **ecualizador** con 100 combinaciones (disposición × estilo × colores, incluido estéreo) y combinaciones rápidas. Usa lo que suena por la salida de audio predeterminada, sin instalar nada (y la sigue si la cambias).
- **Color fijo** con muestras rápidas.
- **Arduino Nano**: detecta el puerto, instala y actualiza el firmware, reconecta solo. Hasta 400 LEDs (300 LEDs a ~46 fps).
- **Limitador de consumo**: baja el brillo si la tira pasaría del límite de tu fuente.
- **API** HTTP + WebSocket, **tiempo real por DDP** (UDP) y compatibilidad con **WLED** (Home Assistant y las apps de WLED la encuentran en la red).
- Apagado al suspender el PC o al apagarse las pantallas, abrir al iniciar sesión, menú del área de notificación con modos, efectos y brillo.
- **Idiomas**: castellano, inglés y catalán (Ajustes → Idioma; por defecto, el de Windows).

## Stream Deck

El plugin de Stream Deck de Ledly (`com.mrdrarek.ledly.sdPlugin`, en [drarekx/streamdeck-plugins](https://github.com/drarekx/streamdeck-plugins)) funciona igual con la versión de Windows: habla con la misma API en `http://localhost:8765`.

## API

Por defecto en el puerto `8765` (Ajustes → API). La documentación completa, con un botón para probar cada ruta, está en la propia app: **http://localhost:8765/api/docs**. La especificación OpenAPI 3.0 está en `/api/openapi.json`. Con token, las rutas `/api` piden `Authorization: Bearer <token>` (o `?token=`).

| Ruta | Qué hace |
|---|---|
| `GET /api/state` | Estado: encendido, modo, efecto, color, brillo, LEDs, fps, dispositivo, captura, consumo, audio, tiempo real |
| `POST /api/state` | Cambia el estado (ver abajo) |
| `GET /api/effects` | Lista de efectos (`id`, nombre, si usa color) |
| `GET /api/leds` | Colores que se están enviando ahora |
| `GET /api/device` · `POST /api/device/reconnect` | Puerto, pin, firmware y estado de la conexión |
| `GET /api/layout` | Disposición de la tira |
| `GET /api/docs` · `GET /api/openapi.json` | Documentación (HTML) y especificación OpenAPI; nunca piden token |
| `WS /api/ws` | Estado en vivo; manda `{"subscribe":"leds"}` para recibir los colores (~15 fps). Acepta los mismos cuerpos que `POST /api/state` |

La ventana de la app es la interfaz web que sirve la propia API (`http://localhost:8765/`), así que también se puede abrir desde el navegador o desde otro equipo de la red. Sus rutas propias (`/api/app/…`) están en [docs/app-api.md](docs/app-api.md).

Cuerpo de `POST /api/state` (todo opcional):

```json
{
  "on": true,
  "toggle": false,
  "mode": "screen | effect | color",
  "effect": "fire",
  "speed": 1.5,
  "equalizer": { "preset": "classic", "layout": "stereo", "style": "bars", "colors": "heat", "sensitivity": 1.2 },
  "color": "#FF2D95",
  "brightness": 60,
  "duration": 3,
  "clear": false,
  "live": false
}
```

- `color` acepta `"#RRGGBB"`, `[r,g,b]` o `{"r":…,"g":…,"b":…}`. Mandar solo un color pasa a modo color; solo un efecto, a modo efecto.
- `equalizer` cambia el ecualizador y lo muestra. `preset`: `classic`, `aurora`, `stereo`, `fire`, `rain`, `halo`, `club`, `mono`; los demás campos retocan la combinación.
- `duration` (segundos) muestra el color o efecto un rato y luego vuelve a lo que había (avisos). `clear` lo corta antes.
- `live: false` suelta el tiempo real (DDP) al momento.

```powershell
Invoke-RestMethod -Method Post http://localhost:8765/api/state -Body '{"effect":"strobe","color":"#FF0000","duration":3}'
```

```sh
curl -X POST localhost:8765/api/state -d '{"effect":"strobe","color":"#FF0000","duration":3}'
```

### Tiempo real (DDP)

UDP en el puerto **4048** (Ajustes → API), sin token, con la misma política de red que la API (desde Docker o WSL: `host.docker.internal`). Detalles y ejemplos en **http://localhost:8765/api/docs#ddp-section**.

- Cabecera de 10 bytes (14 con timecode): flags (`0x40` = versión 1, `| 0x01` push en el último paquete), secuencia, tipo `0x0B` (RGB 8 bits), destino `1`, desplazamiento en bytes (32 bits, big-endian), longitud (16 bits). Luego R,G,B por LED en el orden de la tira: el LED 0 es el inicio de la disposición.
- Un fotograma puede ir en varios paquetes (hasta 1440 bytes de datos cada uno); se pinta al llegar el que lleva push.
- Mientras llegan, mandan sobre la pantalla, los efectos, el color y los avisos, y encienden la tira aunque esté apagada (al terminar vuelve a apagarse). Se aplican brillo, temperatura, ganancias y límite de consumo; gamma y saturación no.
- Sin fotogramas durante 2,5 s vuelve sola a lo que tenía, sin tocar los ajustes. `POST /api/state {"live": false}` (o `/json/state {"live": false}`, como en WLED) suelta al momento e ignora durante 1 s los paquetes que lleguen con retraso.

### WLED y Home Assistant

`GET /json`, `/json/state`, `/json/info`, `/json/eff`, `/json/pal`, `POST /json/state`, `/win&…` y el WebSocket `/ws`. El efecto 0 es «Solid» (color fijo), luego los de Ledly y el último «Ambilight» (pantalla). Con «Anunciar como WLED» se publica `_wled._tcp` por mDNS y Home Assistant la descubre sola (integración WLED); si no aparece, añádela a mano con la IP del PC y el puerto 8765.

La primera vez que Ledly escucha en la red, el Firewall de Windows pregunta si se permite: acepta en redes privadas para que Home Assistant, las apps de WLED o el DDP lleguen desde otros equipos. Desde el propio PC funciona siempre.

## Problemas frecuentes

- **«Acceso denegado» / puerto ocupado**: otra app tiene el puerto del Nano (HyperHDR, Arduino IDE, Prismatik o el monitor serie de otra herramienta). Ciérrala y pulsa **Reconectar**. Ledly no se abre dos veces: la segunda copia muestra la ventana de la primera.
- **No encuentra el Arduino**: prueba otro cable USB (algunos solo cargan). Si en el Administrador de dispositivos sale como desconocido, instala el driver del CH340 de su fabricante.
- **Falla al instalar el firmware**: algunos Nano viejos necesitan pulsar el botón de reset justo al empezar la instalación.
- **Colores en el lado equivocado**: Disposición → cambia la esquina del primer LED o el sentido, y comprueba con **Dirección**.
- **Con HDR activado los colores salen apagados**: la captura se hace en SDR; baja la gamma o sube la saturación en **Imagen**.
- **Un juego en pantalla completa exclusiva no se ve**: ponlo en ventana sin bordes, o captura la ventana del juego (Pantalla → App).
- **La tira parpadea o se reinicia con mucho brillo**: la fuente no da para tanto. Baja el límite de consumo o usa una fuente más grande.

## Dónde guarda las cosas

Todo en `%APPDATA%\Ledly`:

- `settings.json`: ajustes.
- `ledly.log`: registro (útil si algo falla).
- `Backups\`: copias del firmware anterior del Nano.

Los datos de la ventana (WebView2) van en `%LOCALAPPDATA%\Ledly\WebView2`.

## Desinstalar

Sal de Ledly (menú del icono → **Salir de Ledly**) y borra la carpeta de la app, el acceso directo y, si quieres, los ajustes:

```powershell
Remove-Item -Recurse -Force "$env:LOCALAPPDATA\Programs\Ledly", "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Ledly.lnk"
Remove-ItemProperty -Path HKCU:\Software\Microsoft\Windows\CurrentVersion\Run -Name Ledly -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force "$env:APPDATA\Ledly", "$env:LOCALAPPDATA\Ledly"   # ajustes, registro, copias del firmware y datos de la ventana
```

## Desarrollo

C# / .NET 10 sin más dependencias que WebView2. Tres proyectos en `Ledly.slnx`:

- `src/Ledly.Core`: motor, efectos, audio, API/WLED/DDP, ajustes, traducciones, actualizador. Multiplataforma.
- `src/Ledly`: la app de Windows (bandeja, ventana WebView2, captura, audio, puertos COM, inicio con Windows, suspensión).
- `src/Ledly.Dev`: host de desarrollo con pantalla y audio falsos, para probar núcleo, API e interfaz desde un Mac o Linux.

```sh
dotnet build Ledly.slnx                     # todo; en macOS también compila la app de Windows
dotnet run --project src/Ledly.Dev          # núcleo + API + interfaz en http://127.0.0.1:8765/
dotnet run --project src/Ledly.Dev -- --flash /dev/cu.usbserial-XXXX 6
```

En Windows:

```powershell
.\build.ps1 -Version 1.1.0                  # build\Ledly-win-x64.zip + .sha256 (un solo Ledly.exe autocontenido)
.\build.ps1 -Runtime win-x64,win-arm64
Ledly.exe --flash COM3 6                    # instala el firmware del pin D6
Ledly.exe --read-flash COM3 copia.hex       # vuelca el firmware del Nano (solo lee)
```

Publicar: `git tag v1.1.0 && git push origin v1.1.0`; GitHub Actions compila y crea el release con los `.zip` y sus `.sha256`. El icono se genera con `python3 tools/make-icon.py`. La interfaz web está en `web/` y el firmware en `firmware/`. Más detalles en [CLAUDE.md](CLAUDE.md).
