# App API (`/api/app/*`) — contract between the server and the web UI

The Windows app's window is a WebView2 showing the web UI (`web/`), served by the API itself at `/`.
These endpoints exist only for that UI (they are not in the Mac app). They are documented in
`/api/docs` too, under an "App" tag.

General rules

- JSON in and out, camelCase. Enums are their raw names (`"screen"`, `"rainbowSwirl"`, `"bottomLeft"`).
- Requests from loopback (127.0.0.1 / ::1) never need the API token; remote ones do when a token is
  set (`Authorization: Bearer <token>` or `?token=`), like the rest of `/api`.
- Errors: `{"error": "message"}` with 400/404/409/500.
- Static files: `GET /` → `web/index.html`; `GET /app/<path>` → `web/<path>` (embedded resources,
  correct content types, `Cache-Control: no-cache`). `/api/docs` and `/api/openapi.json` stay the docs.

## Settings

- `GET /api/app/settings` → `Settings.ToJson()`: every setting (see `Settings.cs`), e.g.
  `{"isOn":true,"mode":"screen","effect":"rainbowSwirl","effectSpeed":1,"equalizer":{"layout":"fromBottom","style":"spectrum","colors":"rainbow","sensitivity":1},"colorHex":"#FF8A3D","brightness":0.8,"portPath":"COM3","dataPin":6,"layout":{"top":20,"right":12,"bottom":20,"left":12,"start":"bottomLeft","clockwise":true,"offset":0,"bottomGap":0,"depth":0.1,"overlap":0},"displayId":"","captureApp":null,"captureAppName":null,"captureFps":30,"sampling":"linear","blackBorders":true,"smoothingMs":150,"smoothing":"spring","antiFlicker":true,"gamma":1.5,"saturation":1,"temperature":6600,"backlight":0,"redGain":1,"greenGain":1,"blueGain":1,"calibration":{"enabled":false,"targets":{"red":"#FF1010"}},"apiEnabled":true,"apiPort":8765,"apiLocalOnly":false,"apiToken":"","ddpEnabled":true,"ddpPort":4048,"wledDiscovery":true,"powerLimitEnabled":true,"powerLimitMilliamps":1000,"offWhenAsleep":true,"language":"system","autoCheckUpdates":true,"lastUpdateCheck":null,"skippedVersion":null,"lastBaud":0,"deviceId":"a1b2c3d4e5f6"}`
  plus `"launchAtLogin": bool` (from `IPlatform`, not stored in the file).
- `PATCH /api/app/settings` body: any subset of those keys (nested objects merge: `{"layout":{"top":30}}`).
  `launchAtLogin` goes to `IPlatform.LaunchAtLogin`. Uses `Settings.Patch`. → the full settings, or 400.
- `POST /api/app/settings/reset-image` → `Settings.ResetImage()`, returns full settings.

## Status (polled ~1 s by the UI, and pushed over the WebSocket)

`GET /api/app/status` →

```json
{
  "version": "1.0.0", "platform": "windows",
  "device": {"status": "connected|connecting|disconnected|error", "port": "COM3", "firmware": "Ada Ledly 2 pin 6 …", "error": null,
             "ledlyVersion": 2, "ledlyPin": 6, "firmwareOutdated": false},
  "ports": [{"path": "COM3", "name": "USB-SERIAL CH340 (COM3)", "looksLikeArduino": true, "description": "…"}],
  "displays": [{"id": "\\\\.\\DISPLAY1", "name": "DELL U2720Q", "width": 3840, "height": 2160, "primary": true}],
  "apps": [{"id": "chrome", "name": "Google Chrome"}],
  "capture": {"status": "stopped|starting|running|noPermission|failed", "message": null, "source": "Pantalla DELL U2720Q"},
  "fps": 49.8,
  "power": {"milliamps": 812, "limited": false, "limit": 1000},
  "overlay": false, "live": false, "liveSource": null,
  "flash": {"state": "idle|running|done|failed", "step": "connecting|backingUp|writing|verifying|done", "progress": 0.4, "error": null},
  "api": {"status": "running|stopped|failed", "port": 8765, "error": null, "clients": 1, "addresses": ["192.168.1.20"]},
  "ddp": {"status": "running|stopped|failed", "port": 4048, "error": null},
  "update": {"state": "idle|checking|downloading|installing", "available": "1.1.0", "notes": "…", "error": null},
  "audio": null
}
```
`displays` and `apps` may be cached for a couple of seconds server-side.

## Metadata (once at load)

`GET /api/app/meta` →
`{"effects":[{"id":"rainbowSwirl","titleKey":"Remolino arcoíris","music":false,"wled":0}, …in EffectKindInfo.Ordered order],
  "equalizer":{"layouts":["fromBottom",…],"styles":[…],"colors":[…],"presets":[{"id":"classic","titleKey":"Clásico","layout":"fromBottom","style":"bars","colors":"traffic"},…]},
  "firmware":{"version":2,"pins":[2,…,13]}, "languages":["system","en","es","ca"],
  "calibrationCorners":[{"id":"red","titleKey":"Rojo","input":"#FF0000"},…], "repo": "drarekx/ledly-windows"}`

## Actions

- `POST /api/app/test` `{"pattern": "direction"|"sides"|"calibration"|"cursor"|null, "color": "#RRGGBB", "index": 3, "count": 64}` → sets `LEDEngine.TestPattern` (null clears). The UI clears it when leaving a wizard; the server also clears it if no `/api/app/test` call arrives for 30 s while it is `cursor`/`calibration` (UI crashed).
- `POST /api/app/layout/solve` `{"count": 64, "corners": {"topLeft": 0, "topRight": 20, "bottomRight": 32, "bottomLeft": 52}, "hasBottomGap": false}` → `{"layout": {…}}` (not saved; the UI then PATCHes it) or 400 `{"error": …}`. Uses `LEDLayout.Solved` on the current layout.
- `POST /api/app/device/reconnect`, `POST /api/app/device/refresh-ports`.
- `POST /api/app/device/flash` `{"pin": 6, "port": "COM3"}` → 202; progress in status.flash. `POST /api/app/device/flash/reset`.
- `GET /api/app/capture.png` → latest preview PNG (`LEDEngine.CapturePreview()`); headers `X-Content-Rect: x,y,w,h` (pixels of the image, after black-border crop) and `X-Image-Size: w,h`; 204 when none yet. Each request keeps previews coming for 3 s; the UI polls it ~5 times a second while the capture view is visible.
- `POST /api/app/overlay/clear`, `POST /api/app/live/end`.
- `POST /api/app/update/check` (user initiated) and `POST /api/app/update/install`, `POST /api/app/update/skip`.
- `POST /api/app/open` `{"url": "https://…"}` → `IPlatform.OpenUrl` (links from the UI open in the browser, not in the WebView). Only http(s) URLs.
- `POST /api/app/quit` (loopback only).

## Live updates

The existing WebSocket `/api/ws` is reused. Besides the Mac API's messages, the server sends
`{"type":"app-status", …status…}` whenever `LEDEngine.StateChanged` or a setting changes (debounced
100 ms) to clients that sent `{"subscribe":"app"}`, and `{"type":"colors","colors":["#RRGGBB",…]}`
(~15 fps) to clients that sent `{"subscribe":"colors"}` (each such client counts as a
`LEDEngine.AddColorListener()` until it unsubscribes or disconnects). The UI also polls status as a fallback.
