// Loading screen for the browser renderer: what's happening, how far along, and what's stuck. A plain script (not a
// module) so it shows even if the 3D code never arrives. The big libraries are fetched here first, with progress,
// so the module imports that follow come straight from the cache.
(function () {
  const css = `#boot{position:fixed;inset:0;z-index:50;background:#16120f;color:#ece4d2;font:14px ui-sans-serif,system-ui,-apple-system;display:flex;align-items:center;justify-content:center;padding:24px}
#boot .card{width:min(440px,100%)}#boot h1{font-size:17px;margin:0 0 4px;color:#e8a33d}#boot .sub{color:#9a9184;font-size:12px;margin-bottom:14px}
#boot .row{display:flex;gap:10px;align-items:flex-start;padding:5px 0;border-top:1px solid #2a262b}#boot .ic{width:18px;flex:none;text-align:center}
#boot .row.wait{color:#6f6860}#boot .row.run .ic{color:#e8a33d}#boot .row.ok .ic{color:#7fc27a}#boot .row.err{color:#e0605a}
#boot .d{color:#9a9184;font-size:12px}#boot .bar{height:6px;background:#2a262b;border-radius:3px;margin-top:12px;overflow:hidden}#boot .bar i{display:block;height:100%;width:0;background:#e8a33d;transition:width .2s}
#boot .slow{color:#e8a33d;font-size:12px;margin-top:10px;min-height:16px}#boot button{margin-top:10px;background:#2a262b;color:#ece4d2;border:1px solid #3a343b;border-radius:8px;padding:6px 12px}`;
  const st = document.createElement("style"); st.textContent = css; document.head.appendChild(st);
  const STEPS = [["page", "Page loaded"], ["engine", "Downloading the 3D engine"], ["start", "Starting the renderer"], ["data", "Connecting to the game"], ["map", "Loading the map"], ["models", "Loading models"], ["frame", "First picture"]];
  const el = document.createElement("div"); el.id = "boot";
  el.innerHTML = `<div class="card"><h1>Pezz · 3D</h1><div class="sub" id="bootSub">Getting the game onto your screen…</div><div id="bootRows"></div><div class="bar"><i id="bootBar"></i></div><div class="slow" id="bootSlow"></div><div id="bootBtn"></div></div>`;
  document.body.appendChild(el);
  const rows = {}; const box = el.querySelector("#bootRows");
  for (const [id, label] of STEPS) { const r = document.createElement("div"); r.className = "row wait"; r.innerHTML = `<span class="ic">○</span><span><span class="t">${label}</span> <span class="d"></span></span>`; box.appendChild(r); rows[id] = r; }
  let current = null, since = Date.now(), done = 0;
  const icon = { wait: "○", run: "◐", ok: "✓", err: "✕" };
  function set(id, state, detail) {
    const r = rows[id]; if (!r) return;
    r.className = "row " + state; r.querySelector(".ic").textContent = icon[state];
    if (detail != null) r.querySelector(".d").textContent = detail;
    if (state === "run" && current !== id) { current = id; since = Date.now(); }
    done = STEPS.filter(([k]) => rows[k].classList.contains("ok")).length;
    el.querySelector("#bootBar").style.width = Math.round(100 * done / STEPS.length) + "%";
  }
  // Nothing moving for a while: say where it's stuck, so a slow link is told apart from a broken page.
  setInterval(() => {
    const s = Math.round((Date.now() - since) / 1000), lbl = current && STEPS.find(([k]) => k === current)[1];
    el.querySelector("#bootSlow").textContent = current && s >= 8 && !rows[current].classList.contains("ok") ? `Still on “${lbl}” after ${s}s: the connection may be slow. It keeps trying.` : "";
  }, 1000);
  function fail(msg) {
    if (current) set(current, "err");
    el.querySelector("#bootSub").textContent = "Something went wrong:";
    el.querySelector("#bootSlow").textContent = msg;
    el.querySelector("#bootBtn").innerHTML = `<button onclick="location.reload()">Try again</button>`;
    el.style.display = "flex";
  }
  window.__boot = {
    step: set,
    fail,
    ready() { window.__readyAt = performance.now(); set("frame", "ok"); el.style.transition = "opacity .4s"; el.style.opacity = "0"; setTimeout(() => (el.style.display = "none"), 450); },
  };
  addEventListener("error", (e) => { if (el.style.display !== "none") fail((e.message || "a script failed to load") + (e.filename ? ` (${e.filename.split("/").pop()})` : "")); }, true);
  addEventListener("unhandledrejection", (e) => { if (el.style.display !== "none") fail(String(e.reason?.message || e.reason)); });

  set("page", "ok");
  // Fetch the libraries with a running byte count (they're cached for the imports that follow).
  const files = (document.currentScript?.dataset.files || "").split(",").filter(Boolean);
  const main = document.currentScript?.dataset.main || "./main.js";
  async function grab(url, add) {
    const r = await fetch(url);
    if (!r.ok) throw new Error(`${url.split("/").pop()}: HTTP ${r.status}`);
    const rd = r.body.getReader();
    for (;;) { const { done, value } = await rd.read(); if (done) break; add(value.length); }
  }
  (async () => {
    set("engine", "run", "");
    let got = 0; const t0 = Date.now();
    const tick = (n) => { got += n; const kb = got / 1024, rate = kb / Math.max(0.5, (Date.now() - t0) / 1000); set("engine", "run", `${kb.toFixed(0)} KB · ${rate.toFixed(0)} KB/s`); };
    try { await Promise.all(files.map((f) => grab(f, tick))); }
    catch (e) { return fail(`Couldn't download the 3D engine (${e.message}). Check the connection and try again.`); }
    set("engine", "ok", `${(got / 1024).toFixed(0)} KB in ${((Date.now() - t0) / 1000).toFixed(1)}s`);
    set("start", "run");
    try { await import(main); } catch (e) { fail(`The renderer didn't start: ${e.message}`); }
  })();
})();
