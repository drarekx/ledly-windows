namespace Ledly;

public static partial class APIDocs
{
    /// <summary>Self-contained page (no CDN, works offline) that renders <c>/api/openapi.json</c>.</summary>
    public const string Html = """"
    <!doctype html>
    <html lang="es">
    <head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <title>Ledly API</title>
    <style>
    :root { --bg:#121214; --side:#18181A; --card:#1A1A1D; --field:#202023; --line:#2A2A2E; --line2:#3A3A3F;
      --text:#ECECEE; --text2:#A1A1A6; --text3:#8A8A90; --accent:#5C8CFF; --accentText:#7AA2FF; --accentFill:#3566E0;
      --ok:#3CCF7A; --warn:#F5C46B; --danger:#FF7B7B; }
    * { box-sizing: border-box; }
    body { margin:0; background:var(--bg); color:var(--text); font:14px/1.5 -apple-system, BlinkMacSystemFont, "SF Pro Text", system-ui, sans-serif; }
    a { color:var(--accentText); }
    code, pre, .mono { font-family: ui-monospace, "SF Mono", Menlo, monospace; font-size:12.5px; }
    code { background:var(--field); padding:1px 5px; border-radius:4px; }
    .layout { display:flex; min-height:100vh; }
    nav { width:260px; flex:none; background:var(--side); border-right:1px solid var(--line); padding:22px 12px; position:sticky; top:0; height:100vh; overflow:auto; }
    nav .brand { display:flex; gap:10px; align-items:center; font-weight:600; font-size:16px; padding:0 8px 18px; }
    nav .logo { width:28px; height:28px; border-radius:7px; background:var(--accentFill); display:grid; place-items:center; color:#fff; font-size:15px; }
    nav h4 { font-size:11px; letter-spacing:.6px; text-transform:uppercase; color:var(--text3); margin:16px 10px 6px; }
    nav a { display:flex; gap:8px; align-items:center; padding:5px 10px; border-radius:8px; color:var(--text2); text-decoration:none; font-size:13px; }
    nav a:hover { background:var(--field); color:var(--text); }
    main { flex:1; min-width:0; padding:36px 40px 80px; max-width:980px; }
    h1 { font-size:24px; margin:0 0 4px; } h2 { font-size:18px; margin:36px 0 12px; }
    .muted { color:var(--text2); }
    .card { background:var(--card); border:1px solid var(--line); border-radius:12px; padding:18px 20px; margin:12px 0; }
    .intro li { margin:4px 0; }
    .op { scroll-margin-top:16px; }
    .op-head { display:flex; gap:10px; align-items:center; cursor:pointer; flex-wrap:wrap; }
    .method { font-weight:700; font-size:11px; padding:3px 8px; border-radius:6px; min-width:54px; text-align:center; }
    .GET { background:#1E3A2A; color:var(--ok); } .UDP { background:#33264A; color:#C7A2FF; } .POST { background:#22304F; color:var(--accentText); }
    .PUT, .PATCH { background:#3A2F18; color:var(--warn); } .DELETE { background:#3A1E20; color:var(--danger); }
    .path { font-weight:600; }
    .summary { color:var(--text2); }
    .lock { color:var(--warn); font-size:12px; }
    .op-body { display:none; margin-top:14px; border-top:1px solid var(--line); padding-top:14px; }
    .op.open .op-body { display:block; }
    .label { font-size:11px; letter-spacing:.6px; text-transform:uppercase; color:var(--text3); margin:14px 0 6px; font-weight:600; }
    table { width:100%; border-collapse:collapse; font-size:13px; }
    td, th { text-align:left; padding:6px 8px; border-bottom:1px solid var(--line); vertical-align:top; }
    th { color:var(--text3); font-weight:600; font-size:12px; }
    pre { background:var(--field); border:1px solid var(--line); border-radius:8px; padding:12px; overflow:auto; margin:6px 0; white-space:pre-wrap; word-break:break-word; }
    textarea { width:100%; min-height:110px; background:var(--field); color:var(--text); border:1px solid var(--line2); border-radius:8px; padding:10px; font-family:ui-monospace, Menlo, monospace; font-size:12.5px; }
    input, select { background:var(--field); color:var(--text); border:1px solid var(--line2); border-radius:8px; padding:6px 10px; font:inherit; }
    button { background:var(--accentFill); color:#fff; border:0; border-radius:8px; padding:7px 14px; font:inherit; font-weight:600; cursor:pointer; }
    button.secondary { background:var(--field); color:var(--text); border:1px solid var(--line2); font-weight:400; }
    .row { display:flex; gap:8px; align-items:center; flex-wrap:wrap; }
    .status { font-size:12px; color:var(--text3); }
    .ok { color:var(--ok); } .err { color:var(--danger); }
    .chips { display:flex; flex-wrap:wrap; gap:6px; }
    .chip { background:var(--field); border:1px solid var(--line); border-radius:999px; padding:2px 10px; font-size:12px; }
    .topbar { display:flex; justify-content:space-between; align-items:flex-start; gap:16px; flex-wrap:wrap; }
    @media (max-width: 760px) { nav { display:none; } main { padding:24px 16px 60px; } }
    </style>
    </head>
    <body>
    <div class="layout">
      <nav id="nav"><div class="brand"><div class="logo">✦</div>Ledly API</div></nav>
      <main>
        <div class="topbar">
          <div><h1 id="title">Ledly API</h1><div class="muted" id="version"></div></div>
          <div class="row"><input id="token" placeholder="Token (si hay)" size="22" autocomplete="off">
            <a href="/api/openapi.json" class="mono">openapi.json</a></div>
        </div>
        <div class="card intro" id="intro"></div>
        <div id="ddp"></div>
        <div id="ops"></div>
      </main>
    </div>
    <script>
    const $ = (s, el = document) => el.querySelector(s);
    const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
    const md = s => esc(s).replace(/`([^`]+)`/g, '<code>$1</code>').replace(/\*\*([^*]+)\*\*/g, '<b>$1</b>');
    const tokenInput = $('#token');
    try { tokenInput.value = localStorage.getItem('ledly-token') || ''; } catch (e) {}
    tokenInput.addEventListener('input', () => { try { localStorage.setItem('ledly-token', tokenInput.value); } catch (e) {} });

    function resolve(spec, schema) {
      if (schema && schema.$ref) return resolve(spec, schema.$ref.split('/').slice(1).reduce((o, k) => o[k], spec));
      return schema || {};
    }
    function typeOf(spec, s) {
      s = resolve(spec, s);
      if (s.enum) return s.enum.map(v => '<code>' + esc(v) + '</code>').join(' ');
      if (s.oneOf) return 'texto, lista u objeto';
      if (s.type === 'array') return 'lista de ' + typeOf(spec, s.items);
      let t = s.type || 'objeto';
      if (s.minimum !== undefined || s.maximum !== undefined) t += ' (' + (s.minimum ?? '…') + '–' + (s.maximum ?? '…') + ')';
      return esc(t);
    }
    function propsTable(spec, schema) {
      const s = resolve(spec, schema);
      if (!s.properties) return '';
      let rows = Object.entries(s.properties).map(([k, v]) => {
        const r = resolve(spec, v);
        return `<tr><td class="mono">${esc(k)}</td><td>${typeOf(spec, v)}</td><td class="muted">${md(r.description || '')}</td></tr>`;
      }).join('');
      return (s.description ? `<p class="muted">${md(s.description)}</p>` : '') +
        `<table><tr><th>Campo</th><th>Tipo</th><th>Descripción</th></tr>${rows}</table>`;
    }

    async function load() {
      const spec = await (await fetch('/api/openapi.json')).json();
      document.title = spec.info.title;
      $('#title').textContent = spec.info.title;
      $('#version').textContent = 'Versión ' + spec.info.version + ' · ' + location.origin;
      $('#intro').innerHTML = '<ul>' + spec.info.description.split('\n').filter(l => l.startsWith('- '))
        .map(l => '<li>' + md(l.slice(2)).replaceAll('&lt;host&gt;', esc(location.host.split(':')[0])) + '</li>').join('') + '</ul>' +
        '<div class="label">Efectos</div><div class="chips">' +
        spec.components.schemas.Effect.properties.id.enum.map(e => `<span class="chip mono">${esc(e)}</span>`).join('') + '</div>';

      const ddp = spec['x-ddp'];
      if (ddp) {
        $('#nav').insertAdjacentHTML('beforeend', '<h4>Tiempo real</h4><a href="#ddp-section"><span class="method UDP" style="min-width:44px;font-size:10px">UDP</span>DDP</a>');
        $('#ddp').innerHTML = `<h2 id="ddp-section">${esc(ddp.title)}</h2><p class="muted">${md(ddp.summary)}</p>
          <div class="card"><div class="label" style="margin-top:0">Transporte</div><p>${md(ddp.transport)}</p>
          <div class="label">Cabecera (10 bytes)</div><table><tr><th>Byte</th><th>Campo</th><th>Valor</th><th>Descripción</th></tr>
          ${ddp.header.map(h => `<tr><td class="mono">${esc(h.bytes)}</td><td>${esc(h.field)}</td><td class="mono">${esc(h.value)}</td><td class="muted">${md(h.description)}</td></tr>`).join('')}</table>
          <div class="label">Comportamiento</div><ul>${ddp.rules.map(r => `<li>${md(r)}</li>`).join('')}</ul>
          ${ddp.examples.map(e => `<div class="label">${esc(e.title)}</div><pre>${esc(e.code)}</pre>`).join('')}</div>`;
      }

      const groups = {};
      for (const [path, methods] of Object.entries(spec.paths))
        for (const [method, op] of Object.entries(methods))
          (groups[op.tags[0]] ||= []).push({ path, method: method.toUpperCase(), op });

      const nav = $('#nav'), ops = $('#ops');
      for (const tag of spec.tags) {
        const list = groups[tag.name]; if (!list) continue;
        nav.insertAdjacentHTML('beforeend', `<h4>${esc(tag.name)}</h4>`);
        ops.insertAdjacentHTML('beforeend', `<h2>${esc(tag.name)}</h2>` + (tag.description ? `<p class="muted">${esc(tag.description)}</p>` : ''));
        for (const { path, method, op } of list) {
          const id = op.operationId;
          const locked = !(op.security && op.security.length === 0) && path.startsWith('/api');
          nav.insertAdjacentHTML('beforeend', `<a href="#${id}"><span class="method ${method}" style="min-width:44px;font-size:10px">${method}</span>${esc(path)}</a>`);
          const body = op.requestBody?.content?.['application/json'];
          const examples = body?.examples ? Object.values(body.examples) : body?.example ? [{ summary: 'Ejemplo', value: body.example }] : [];
          const response = op.responses['200']?.content?.['application/json']?.schema;
          let html = `<div class="card op" id="${id}"><div class="op-head">
            <span class="method ${method}">${method}</span><span class="path mono">${esc(path)}</span>
            <span class="summary">${esc(op.summary)}</span>${locked ? '<span class="lock" title="Pide token si hay uno configurado">🔒</span>' : ''}</div>
            <div class="op-body">`;
          if (op.description) html += `<p class="muted">${md(op.description)}</p>`;
          if (op.parameters) html += '<div class="label">Parámetros</div><table><tr><th>Nombre</th><th>Tipo</th></tr>' +
            op.parameters.map(p => `<tr><td class="mono">${esc(p.name)}</td><td>${typeOf(spec, p.schema)}</td></tr>`).join('') + '</table>';
          if (body) html += '<div class="label">Cuerpo</div>' + propsTable(spec, body.schema);
          if (response && resolve(spec, response).properties) html += '<div class="label">Respuesta</div>' + propsTable(spec, response);
          html += '<div class="label">Probar</div>';
          if (examples.length > 1) html += `<div class="row" style="margin-bottom:6px"><select class="ex">${examples.map((e, i) => `<option value="${i}">${esc(e.summary)}</option>`).join('')}</select></div>`;
          if (method !== 'GET') html += `<textarea class="body">${esc(examples[0] ? JSON.stringify(examples[0].value, null, 2) : '')}</textarea>`;
          const tryPath = path === '/win' ? '/win&T=2' : path;
          html += `<div class="row" style="margin-top:8px"><button class="run">Enviar</button>
            <button class="secondary curl">Copiar curl</button><span class="status"></span></div><pre class="out" hidden></pre></div></div>`;
          ops.insertAdjacentHTML('beforeend', html);
          const card = document.getElementById(id);
          $('.op-head', card).onclick = () => card.classList.toggle('open');
          const sel = $('.ex', card);
          if (sel) sel.onchange = () => $('.body', card).value = JSON.stringify(examples[sel.value].value, null, 2);
          const headers = () => {
            const h = { 'Content-Type': 'application/json' };
            if (tokenInput.value && path.startsWith('/api')) h.Authorization = 'Bearer ' + tokenInput.value;
            return h;
          };
          $('.run', card).onclick = async () => {
            const out = $('.out', card), status = $('.status', card);
            const started = performance.now();
            try {
              const res = await fetch(tryPath, { method, headers: headers(), body: method === 'GET' ? undefined : $('.body', card).value });
              const text = await res.text();
              let shown = text; try { shown = JSON.stringify(JSON.parse(text), null, 2); } catch (e) {}
              status.innerHTML = `<span class="${res.ok ? 'ok' : 'err'}">${res.status}</span> · ${Math.round(performance.now() - started)} ms`;
              out.textContent = shown; out.hidden = false;
            } catch (e) { status.innerHTML = '<span class="err">' + esc(e.message) + '</span>'; }
          };
          $('.curl', card).onclick = () => {
            const h = headers(); let cmd = `curl${method === 'GET' ? '' : ' -X ' + method}`;
            if (h.Authorization) cmd += ` -H 'Authorization: ${h.Authorization}'`;
            if (method !== 'GET') cmd += ` -d '${$('.body', card).value.replace(/\s*\n\s*/g, ' ')}'`;
            cmd += ` '${location.origin}${tryPath}'`;
            navigator.clipboard?.writeText(cmd);
            $('.status', card).textContent = 'curl copiado';
          };
        }
      }
      // Sections are built after loading, so jump to the link's target now.
      const target = location.hash && document.querySelector(location.hash);
      if (target) { target.classList.add('open'); target.scrollIntoView(); }
    }
    load().catch(e => { $('#intro').innerHTML = '<span class="err">No se pudo cargar openapi.json: ' + esc(e.message) + '</span>'; });
    </script>
    </body>
    </html>
    """";
}
