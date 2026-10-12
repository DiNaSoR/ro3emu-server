// RO3Emu Launcher page. The host (MainWindow + LauncherController) posts state/progress/server/settings/news messages and
// receives {cmd} messages. Opened in a plain browser the page runs against a mock host: index.html?mock=install|update|
// ready|downloading|repair|offline (&tab=news|settings).
(function () {
  "use strict";
  var $ = function (id) { return document.getElementById(id); };
  var host;
  var state = { phase: "checking" };
  var busy = false;
  var stage = null;

  var DEFAULT_NEWS = [
    { tag: "Welcome", color: "blue", title: "Welcome to RO3Emu", text: "Our own Ragnarok Online 3 server. Everything is still being built, so report anything that breaks." },
    { tag: "Account", color: "green", title: "No registration needed", text: "Type any username and password in the game. The account is created on your first login." },
    { tag: "Updates", color: "orange", title: "Automatic game updates", text: "New versions download here. Interrupted downloads resume where they stopped." },
  ];
  var COLORS = { blue: "var(--blue)", green: "var(--green)", orange: "var(--orange)", red: "var(--red)" };

  function send(o) { host.post(o); }
  function size(b) { return b >= 1073741824 ? (b / 1073741824).toFixed(2) + " GB" : Math.round(b / 1048576) + " MB"; }
  function eta(s) {
    if (!(s > 0) || !isFinite(s)) return "";
    s = Math.round(s);
    if (s >= 3600) return Math.floor(s / 3600) + " h " + Math.floor(s % 3600 / 60) + " min";
    if (s >= 60) return Math.floor(s / 60) + " min " + (s % 60) + " s";
    return s + " s";
  }

  // phase -> active step (1 install, 2 verify, 3 ready), button label/icon, default message
  var PHASES = {
    checking: { step: 0, button: "Checking…", icon: "download", wait: true, text: "Checking for updates…" },
    install: { step: 1, button: "Install", icon: "download", text: "The game is not installed yet. About 13 GB to download." },
    update: { step: 1, button: "Update", icon: "download", text: "A new version is available." },
    repair: { step: 2, button: "Repair", icon: "repair", text: "Some game files differ from the release.", warn: true },
    ready: { step: 3, button: "Play", icon: null, text: "Up to date. Have fun!" },
    offline: { step: 0, button: "Retry", icon: "repair", text: "Can't reach the update server.", warn: true },
    error: { step: 0, button: "Retry", icon: "repair", text: "Something went wrong.", warn: true },
    launching: { step: 3, button: "Starting…", icon: null, wait: true, text: "Starting the game…" },
    running: { step: 3, button: "Play", icon: null, text: "The game is running." },
  };
  var STAGES = { scanning: "Checking files", downloading: "Downloading", assembling: "Assembling files", verifying: "Verifying", committing: "Saving" };

  function setSteps(active) {
    for (var i = 1; i <= 3; i++) {
      $("step-" + i).className = "step" + (i < active ? " done" : i === active ? " active" : "");
    }
  }

  function render() {
    var p = PHASES[state.phase] || PHASES.checking;
    $("progress").hidden = !busy;
    $("message").hidden = busy;
    if (busy) setSteps(stage === "verifying" || stage === "committing" ? 2 : 1);
    else setSteps(p.step);
    var msg = $("message");
    msg.className = "message" + (p.warn ? " warn" : "");
    $("message-text").textContent = state.message || p.text;

    var btn = $("primary"), ico = $("primary-icon");
    if (busy) {
      btn.disabled = false;
      $("primary-label").textContent = "Pause";
      ico.hidden = true;
    } else {
      btn.disabled = !!p.wait;
      $("primary-label").textContent = p.button;
      ico.hidden = !p.icon;
      // Button icons: drop PNGs into ui/assets/ui/<name>.png and set ico.src here.
    }
    $("repair").disabled = busy || !state.installed || !!p.wait;
    $("set-dir-change").disabled = busy;

    var v = "Launcher " + (state.launcher || "") ;
    if (state.installed && state.remote && state.installed !== state.remote) v += " · Game " + state.installed + " → " + state.remote;
    else if (state.installed || state.remote) v += " · Game " + (state.installed || state.remote);
    $("ver-line").textContent = v;
  }

  function renderProgress(m) {
    stage = m.stage;
    $("p-stage").textContent = STAGES[m.stage] || "Working";
    var f = m.total > 0 ? Math.min(1, m.done / m.total) : 0;
    $("p-pct").textContent = Math.floor(f * 100) + "%";
    $("p-fill").style.width = (f * 100).toFixed(2) + "%";
    $("p-bytes").textContent = m.total > 0 ? size(m.done) + " / " + size(m.total) : "";
    $("p-rate").textContent = m.stage === "downloading" && m.rate > 0 ? (m.rate / 1048576).toFixed(1) + " MB/s · " + eta(m.eta) + " left" : "";
  }

  function renderNews(items) {
    var list = $("news-list");
    list.innerHTML = "";
    (items && items.length ? items : DEFAULT_NEWS).forEach(function (n) {
      var li = document.createElement("li");
      li.style.setProperty("--c", COLORS[n.color] || COLORS.blue);
      var tag = document.createElement("span"); tag.className = "tag"; tag.textContent = n.tag || "News";
      var b = document.createElement("b"); b.textContent = n.title;
      var p = document.createElement("p"); p.textContent = n.text || "";
      li.appendChild(tag); li.appendChild(b); li.appendChild(p);
      list.appendChild(li);
    });
  }

  function showTab(name) {
    document.querySelectorAll(".tab").forEach(function (t) { t.classList.toggle("active", t.dataset.tab === name); });
    $("settings").hidden = name !== "settings";
    $("notices").hidden = name === "settings";
    $("notices").classList.toggle("wide", name === "news");
  }

  function handle(m) {
    switch (m.type) {
      case "state":
        busy = m.phase === "updating";
        var launcher = state.launcher;
        if (!busy) { state = m; state.launcher = launcher; stage = null; }
        else { state.installed = m.installed || state.installed; state.remote = m.remote || state.remote; }
        render();
        break;
      case "progress":
        if (!busy) busy = true;
        renderProgress(m);
        render();
        break;
      case "server":
        $("server").className = "server " + (m.online ? "on" : "off");
        $("server-text").innerHTML = m.online ? "<b>Online</b>" + (m.players != null ? " · " + m.players + (m.players === 1 ? " player" : " players") : "") : "<b>Offline</b>";
        break;
      case "settings":
        $("set-dir").textContent = m.dir;
        state.launcher = m.launcher;
        $("set-version").textContent = "Launcher " + m.launcher;
        render();
        break;
      case "news":
        renderNews(m.items);
        break;
    }
  }

  function bind() {
    $("drag").addEventListener("mousedown", function (e) {
      if (e.button === 0 && !e.target.closest("button")) send({ cmd: "window", action: "drag" });
    });
    $("btn-min").onclick = function () { send({ cmd: "window", action: "minimize" }); };
    $("btn-close").onclick = function () { send({ cmd: "window", action: "close" }); };
    document.querySelectorAll(".tab").forEach(function (t) { t.onclick = function () { showTab(t.dataset.tab); }; });
    $("primary").onclick = function () { send({ cmd: busy ? "pause" : "primary" }); };
    $("repair").onclick = function () { send({ cmd: "repair" }); };
    $("set-dir-change").onclick = function () { send({ cmd: "changeDir" }); };
    $("set-open").onclick = function () { send({ cmd: "openDir" }); };
  }

  // ---------- mock host for a plain browser ----------
  function createMockHost(scenario) {
    function emit(o) { setTimeout(function () { handle(o); }, 0); }
    var remote = "2026.10.09.1";
    return {
      post: function (c) {
        if (c.cmd !== "ready") return;
        emit({ type: "settings", dir: "C:\\Games\\RO3Emu\\Client", launcher: "1.0.0" });
        emit({ type: "server", online: scenario !== "offline", players: 7 });
        if (scenario === "offline") return emit({ type: "state", phase: "offline" });
        if (scenario === "downloading") {
          emit({ type: "state", phase: "install", remote: remote });
          emit({ type: "progress", stage: "downloading", done: 6.3e9, total: 14.1e9, rate: 31.4 * 1048576, eta: 254 });
          return;
        }
        var phase = { install: "install", ready: "ready", repair: "repair" }[scenario] || "update";
        emit({ type: "state", phase: phase, installed: phase === "install" ? null : phase === "update" ? "2026.10.08.2" : remote, remote: remote });
      },
    };
  }

  function start() {
    var params = new URLSearchParams(location.search);
    var wv = window.chrome && window.chrome.webview;
    if (wv) {
      host = { post: function (o) { wv.postMessage(o); } };
      wv.addEventListener("message", function (e) { handle(e.data); });
      document.addEventListener("contextmenu", function (e) { e.preventDefault(); });
    } else {
      host = createMockHost(params.get("mock") || "update");
    }
    bind();
    renderNews(null);
    render();
    if (params.get("tab")) showTab(params.get("tab"));
    send({ cmd: "ready" });
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start);
  else start();
})();
