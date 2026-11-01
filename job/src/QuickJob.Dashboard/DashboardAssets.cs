namespace QuickJob.Dashboard;

/// <summary>看板的整张页面：HTML/CSS/JS 全在一根字符串里（无静态文件目录，NuGet 包直接带走）。
/// 数据面只认 JSON API 轮询（/stats /jobs /runs /executions /events），操作面走 POST。
/// JS 里全是 { }，所以不用插值 —— 字符串原样拿走。</summary>
internal static class DashboardAssets
{
    internal const string Html =
        """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>QuickJob Dashboard</title>
        <style>
          :root {
            --bg: #0d1117; --panel: #161b22; --panel2: #1c2330; --line: #263041;
            --ink: #d7dee9; --dim: #8b97a8; --acc: #4cc38a; --warn: #e3b341; --err: #f85149; --link: #58a6ff;
            font-size: 14px;
          }
          * { box-sizing: border-box; }
          body { margin: 0; background: var(--bg); color: var(--ink); font-family: "Segoe UI", system-ui, sans-serif; }
          header { display: flex; align-items: center; gap: 14px; padding: 10px 18px; background: var(--panel); border-bottom: 1px solid var(--line); }
          header h1 { font-size: 16px; margin: 0; letter-spacing: .4px; }
          header h1 span { color: var(--acc); }
          .chip { border: 1px solid var(--line); border-radius: 999px; padding: 2px 10px; font-size: 12px; color: var(--dim); }
          .chip.ok { color: var(--acc); border-color: var(--acc); }
          .chip.failed { color: var(--err); border-color: var(--err); }
          .space { flex: 1; }
          header a { color: var(--link); text-decoration: none; font-size: 13px; }
          main { padding: 14px 18px 40px; max-width: 1500px; margin: 0 auto; }
          .cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(120px, 1fr)); gap: 10px; margin-bottom: 14px; }
          .card { background: var(--panel); border: 1px solid var(--line); border-radius: 10px; padding: 10px 12px; }
          .card b { display: block; font-size: 20px; }
          .card small { color: var(--dim); }
          .grid { display: grid; grid-template-columns: 1fr 1fr; gap: 14px; }
          @media (max-width: 1100px) { .grid { grid-template-columns: 1fr; } }
          section { background: var(--panel); border: 1px solid var(--line); border-radius: 10px; margin-bottom: 14px; overflow: hidden; }
          section h2 { font-size: 13px; margin: 0; padding: 9px 12px; color: var(--dim); background: var(--panel2); border-bottom: 1px solid var(--line); letter-spacing: .5px; text-transform: uppercase; }
          table { width: 100%; border-collapse: collapse; font-size: 13px; }
          th { text-align: left; color: var(--dim); font-weight: 500; padding: 7px 10px; border-bottom: 1px solid var(--line); white-space: nowrap; }
          td { padding: 7px 10px; border-bottom: 1px solid var(--line); vertical-align: top; }
          tr:last-child td { border-bottom: none; }
          .mono { font-family: Consolas, "Cascadia Mono", monospace; font-size: 12px; }
          .dim { color: var(--dim); }
          .tag { display: inline-block; border-radius: 5px; padding: 1px 7px; font-size: 11px; border: 1px solid var(--line); color: var(--dim); }
          .tag.enabled { color: var(--acc); border-color: var(--acc); }
          .tag.paused { color: var(--warn); border-color: var(--warn); }
          .tag.failed { color: var(--err); border-color: var(--err); }
          .tag.completed { color: var(--acc); border-color: var(--acc); }
          .tag.retrying { color: var(--warn); border-color: var(--warn); }
          .tag.cancelled { color: var(--err); border-color: var(--err); }
          button { background: var(--panel2); color: var(--ink); border: 1px solid var(--line); border-radius: 7px; padding: 4px 10px; font-size: 12px; cursor: pointer; }
          button:hover { border-color: var(--acc); color: var(--acc); }
          button.danger:hover { border-color: var(--err); color: var(--err); }
          input, select { background: var(--bg); color: var(--ink); border: 1px solid var(--line); border-radius: 7px; padding: 5px 8px; font-size: 12px; }
          dialog { background: var(--panel); color: var(--ink); border: 1px solid var(--line); border-radius: 12px; padding: 16px; min-width: 380px; }
          dialog::backdrop { background: rgba(0,0,0,.55); }
          dialog h3 { margin: 0 0 10px; font-size: 14px; }
          dialog .row { margin-bottom: 10px; display: flex; gap: 8px; align-items: center; }
          dialog textarea { width: 100%; height: 90px; background: var(--bg); color: var(--ink); border: 1px solid var(--line); border-radius: 7px; font-family: Consolas, monospace; font-size: 12px; padding: 6px; }
          .errbar { color: var(--err); min-height: 18px; font-size: 12px; margin: 6px 0; }
          .events { max-height: 220px; overflow: auto; font-size: 12px; }
          .events div { padding: 3px 10px; border-bottom: 1px solid var(--line); font-family: Consolas, monospace; }
        </style>
        </head>
        <body>
        <header>
          <h1>Quick<span>Job</span> Dashboard</h1>
          <span class="chip" id="conn">connecting</span>
          <span class="chip" id="who"></span>
          <span class="space"></span>
          <a id="authlink" href="#"></a>
        </header>
        <main>
          <div class="cards" id="cards"></div>
          <section>
            <h2>Jobs</h2>
            <table id="jobs"><thead><tr>
              <th>Name</th><th>Schedule</th><th>Options</th><th>State</th><th style="width:1%">Actions</th>
            </tr></thead><tbody></tbody></table>
          </section>
          <div class="grid">
            <section>
              <h2>Running</h2>
              <table id="running"><thead><tr>
                <th>Job</th><th>Attempt</th><th>Started</th><th>Progress</th><th style="width:1%"></th>
              </tr></thead><tbody></tbody></table>
            </section>
            <section>
              <h2>Pending</h2>
              <table id="pending"><thead><tr>
                <th>Job</th><th>Trigger</th><th>Scheduled at</th><th style="width:1%"></th>
              </tr></thead><tbody></tbody></table>
            </section>
          </div>
          <section>
            <h2>Execution history</h2>
            <div style="padding: 8px 10px; display:flex; gap:8px; align-items:center;">
              <select id="jobpick"><option value="">all jobs</option></select>
              <button onclick="history()">refresh</button>
            </div>
            <table id="history"><thead><tr>
              <th>Job</th><th>Outcome</th><th>Attempt</th><th>Scheduled</th><th>Started</th><th>Finished</th><th>Error / Progress</th>
            </tr></thead><tbody></tbody></table>
          </section>
          <section>
            <h2>Event stream</h2>
            <div class="events" id="events"></div>
          </section>
        </main>
        <dialog id="dlg">
          <h3 id="dlgTitle">Schedule one run</h3>
          <div class="row"><span style="width:70px">Job</span><b id="dlgJob"></b></div>
          <div class="row" id="dlgAtRow"><span style="width:70px">When</span><input type="datetime-local" id="dlgAt" step="1"></div>
          <div class="row"><span style="width:70px">Payload</span></div>
          <textarea id="dlgPayload" placeholder='{"note": "optional json"} (empty = no payload)'></textarea>
          <div class="errbar" id="dlgErr"></div>
          <div style="text-align:right; display:flex; gap:8px; justify-content:flex-end;">
            <button onclick="dlg.close()">cancel</button>
            <button id="dlgGo" class="danger" onclick="dlgAction()">go</button>
          </div>
        </dialog>
        <script>
        (function () {
          "use strict";
          var readOnly = false;
          var $ = function (id) { return document.getElementById(id); };
          var dlg = $("dlg");
          var esc = function (v) { return (v === null || v === undefined ? "" : String(v)).replace(/[&<>"']/g, function (c) {
            return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c];
          }); };
          var jsq = function (v) { return esc(v).replace(/\\'/g, "&#39;"); };
          var when = function (iso) {
            if (!iso) return "&mdash;";
            var d = new Date(iso); return isNaN(d) ? "&mdash;" : esc(d.toISOString().replace("T", " ").replace("Z", ""));
          };
          var KIND = { 1: "Cron", 2: "Interval", 3: "Once", 4: "OnDemand" };
          var TRIGGER = { 1: "cron", 2: "interval", 3: "scheduled", 4: "manual", 5: "enqueued", 6: "retry" };
          var OVERLAP = { 1: "Skip", 2: "Queue", 3: "RunAnyway" };
          var EVENTNAME = { 1: "started", 2: "completed", 3: "failed", 4: "retrying", 5: "anomaly", 6: "skipped" };

          function api(path, body) {
            return fetch(path, body === undefined ? {} : {
              method: "POST",
              headers: { "content-type": "application/json" },
              body: JSON.stringify(body)
            }).then(function (r) {
              if (!r.ok && (r.status === 401 || r.status === 403)) throw new Error("login wall");
              return r.json();
            });
          }

          function get(path) {
            return api(path).then(null, function () {
              $("conn").textContent = "offline";
              $("conn").className = "chip failed";
              return null;
            });
          }

          function scheduleText(job) {
            if (job.kind === 1) return "cron " + esc(job.cron || "?");
            if (job.kind === 2) return "every " + esc(job.every || "?");
            if (job.kind === 3) return "once @ " + when(job.runAt);
            return "on demand";
          }

          function statsView(s) {
            var cells = [
              ["pending", s.pendingRuns], ["running", s.runningRuns],
              ["started", s.runsStarted], ["completed", s.runsCompleted],
              ["failed", s.runsFailed], ["retried", s.runsRetried],
              ["skipped", s.runsSkipped], ["cancelled", s.runsCancelled]
            ];
            $("cards").innerHTML = cells.map(function (c) {
              return '<div class="card"><b>' + c[1] + '</b><small>' + c[0] + '</small></div>';
            }).join("");
          }

          function jobsView(list) {
            list = list || [];
            var pick = $("jobpick");
            var keep = pick.value;
            pick.innerHTML = '<option value="">all jobs</option>' + list.map(function (j) {
              return '<option value="' + esc(j.name) + '">' + esc(j.name) + '</option>';
            }).join("");
            if (keep && list.some(function (j) { return j.name === keep; })) pick.value = keep;

            $("jobs").tBodies[0].innerHTML = list.map(function (j) {
              var o = j.options || {};
              var state = (o.enabled === false ? '<span class="tag">disabled</span> ' : "") +
                (j.paused ? '<span class="tag paused">paused</span>' : '<span class="tag enabled">live</span>');
              var actions = "";
              if (!readOnly) {
                actions =
                  '<button onclick="ui.pauseToggle(' + "'" + jsq(j.name) + "'," + (j.paused ? "1" : "0") + ')">' + (j.paused ? "resume" : "pause") + '</button> ' +
                  '<button onclick="ui.openDialog(' + "'" + jsq(j.name) + "','trigger')">trigger</button> ' +
                  (j.kind === 4 ? "" : '<button onclick="ui.openDialog(' + "'" + jsq(j.name) + "','schedule')">schedule once</button> ') +
                  '<button class="danger" onclick="ui.remove(' + "'" + jsq(j.name) + ')">remove</button>';
              }
              return "<tr>" +
                '<td class="mono"><b>' + esc(j.name) + "</b>" + (o.description ? '<br><span class="dim">' + esc(o.description) + "</span>" : "") + "</td>" +
                "<td>" + scheduleText(j) + (o.timeZoneId ? '<br><span class="dim">tz ' + esc(o.timeZoneId) + "</span>" : "") + "</td>" +
                '<td class="dim">' + (KIND[j.kind] || "?") + " &middot; conc " + (o.maxConcurrentPerJob || 1) +
                " &middot; overlap " + (OVERLAP[o.onOverlap] || "?") +
                " &middot; prio " + (o.priority || 0) +
                " &middot; retries " + (o.maxRetries || 0) +
                (o.timeout ? " &middot; timeout " + esc(o.timeout) : "") +
                (o.grace ? " &middot; grace " + esc(o.grace) : "") + "</td>" +
                "<td>" + state + "</td>" +
                "<td>" + actions + "</td></tr>";
            }).join("") || '<tr><td colspan="5" class="dim">no jobs registered — declare [QuickJob] static methods or use AddQuickJob DSL</td></tr>';
          }

          function runningView(list) {
            $("running").tBodies[0].innerHTML = (list || []).map(function (r) {
              return "<tr><td class=mono>" + esc(r.jobName) + "</td><td>" + r.attempt + "</td><td>" + when(r.startedAt) + "</td>" +
                '<td class="dim mono">' + esc(r.progress || "") + "</td>" +
                "<td>" + (!readOnly ? '<button class="danger" onclick="ui.cancel(' + "'" + esc(r.executionId) + ')">cancel</button>' : "") + "</td></tr>";
            }).join("") || '<tr><td colspan="5" class="dim">nothing in flight</td></tr>';
          }

          function pendingView(list) {
            $("pending").tBodies[0].innerHTML = (list || []).map(function (r) {
              return "<tr><td class=mono>" + esc(r.jobName) + "</td><td class=dim>" + (TRIGGER[r.trigger] || r.trigger) + "</td><td>" + when(r.scheduledAt) + "</td>" +
                "<td>" + (!readOnly ? '<button class="danger" onclick="ui.cancel(' + "'" + esc(r.executionId) + ')">drop</button>' : "") + "</td></tr>";
            }).join("") || '<tr><td colspan="4" class="dim">queue empty</td></tr>';
          }

          function history(rows) {
            $("history").tBodies[0].innerHTML = (rows || []).map(function (e) {
              var text = e.outcome || "";
              var cls = text.indexOf("Completed") === 0 ? "completed" :
                        text.indexOf("Failed") === 0 ? "failed" :
                        text.indexOf("Retrying") === 0 ? "retrying" :
                        text.indexOf("Skipped") === 0 || text.indexOf("Cancelled") === 0 ? "cancelled" : "";
              return "<tr><td class=mono>" + esc(e.jobName) + "</td>" +
                '<td><span class="tag ' + cls + '">' + esc(text) + "</span></td><td>" + e.attempt + "</td>" +
                "<td>" + when(e.scheduledAt) + "</td><td>" + when(e.startedAt) + "</td><td>" + when(e.finishedAt) + "</td>" +
                '<td class="mono dim" style="max-width:320px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap;">' + esc(e.error || e.progress || "") + "</td></tr>";
            }).join("") || '<tr><td colspan="7" class="dim">no history</td></tr>';
          }

          function loadHistory() {
            var job = $("jobpick").value;
            var qs = job ? "?job=" + encodeURIComponent(job) : "?take=80";
            get("/executions" + qs).then(history);
          }

          function eventsView(list) {
            $("events").innerHTML = (list || []).map(function (e) {
              var cls = e.kind === 2 ? "completed" : e.kind === 3 ? "failed" : e.kind === 4 ? "retrying" : e.kind === 6 ? "cancelled" : e.kind === 5 ? "failed" : "";
              return '<div><span class="tag ' + cls + '">' + (EVENTNAME[e.kind] || "?") + "</span> " + when(e.at) + " &middot; " +
                esc(e.jobName || e.message || "?") +
                (e.error ? ' <span class="dim">' + esc(String(e.error).slice(0, 120)) + "</span>" : "") +
                (e.attempt ? ' <span class="dim">attempt ' + e.attempt + "</span>" : "") +
                (e.delay ? ' <span class="dim">delay ' + esc(e.delay) + "</span>" : "") +
                "</div>";
            }).join("") || '<div class="dim">no events yet</div>';
          }

          var busy = false;
          function tick() {
            if (busy) return;
            busy = true;
            Promise.all([
              get("/stats"), get("/jobs"), get("/runs/pending"), get("/runs/running"), get("/events?take=40")
            ]).then(function (r) {
              if (r[0]) statsView(r[0]);
              if (r[1]) jobsView(r[1]);
              if (r[2]) pendingView(r[2]);
              if (r[3]) runningView(r[3]);
              if (r[4]) eventsView(r[4]);
              $("conn").textContent = "polling 2s";
              $("conn").className = "chip ok";
              busy = false;
            });
          }

          fetch("/me").then(function (r) { return r.json(); }).then(function (me) {
            readOnly = !!me.readOnly;
            var who = $("who"), link = $("authlink");
            if (me.authMode && me.authMode !== "Anonymous") {
              who.textContent = me.authenticated ? (me.name || "signed in") : "not signed in";
              who.className = "chip" + (me.authenticated ? " ok" : " failed");
              link.textContent = me.authenticated ? "sign out" : "sign in";
              link.href = me.authenticated ? "/logout" : "/login";
            } else {
              who.textContent = "anonymous";
            }
            tick();
            setInterval(tick, 2000);
            $("jobpick").onchange = loadHistory;
            loadHistory();
          }).catch(function () {
            tick();
            setInterval(tick, 4000);
          });

          window.history = loadHistory;

          window.ui = {
            pauseToggle: function (job, paused) {
              api(paused ? "/resume" : "/pause", { job: job });
            },
            openDialog: function (job, mode) {
              dlg.dataset.mode = mode;
              dlg.dataset.job = job;
              $("dlgTitle").textContent = (mode === "trigger" ? "Trigger now — " : "Schedule one run — ") + job;
              $("dlgJob").textContent = job;
              $("dlgAtRow").style.display = mode === "trigger" ? "none" : "flex";
              $("dlgPayload").value = "";
              $("dlgErr").textContent = "";
              dlg.showModal();
            },
            remove: function (job) {
              if (!confirm("Remove registration of '" + job + "'? Due runs already dispatched finish or drop.")) return;
              api("/remove", { job: job });
            },
            cancel: function (runId) {
              api("/cancel", { runId: runId });
            }
          };

          window.dlgAction = function () {
            var mode = dlg.dataset.mode, job = dlg.dataset.job;
            var payload = null;
            var text = $("dlgPayload").value.trim();
            if (text.length > 0) {
              try { payload = JSON.parse(text); }
              catch (err) { $("dlgErr").textContent = "payload is not JSON: " + err.message; return; }
            }
            if (mode === "trigger") {
              api("/trigger", { job: job, payload: payload }).then(function () { dlg.close(); });
            } else {
              var at = $("dlgAt").value;
              if (!at) { $("dlgErr").textContent = "pick a moment"; return; }
              api("/schedule", { job: job, at: new Date(at).toISOString(), payload: payload }).then(function () { dlg.close(); });
            }
          };
        })();
        </script>
        </body>
        </html>
        """;
}
