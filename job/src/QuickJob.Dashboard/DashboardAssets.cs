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
          :root[data-theme="light"] {
            --bg: #ffffff; --panel: #f6f8fa; --panel2: #eef1f5; --line: #d0d7de;
            --ink: #1f2328; --dim: #656d76; --acc: #1a7f37; --warn: #9a6700; --err: #cf222e; --link: #0969da;
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
          td.act { white-space: nowrap; }
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
          button { background: var(--panel2); color: var(--ink); border: 1px solid var(--line); border-radius: 7px; padding: 4px 10px; font-size: 12px; cursor: pointer; white-space: nowrap; }
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
          <button id="themebtn" data-i18n-title="thTheme" title="toggle light/dark">dark</button>
          <button id="langbtn" data-i18n-title="thLang" title="switch language">zh</button>
          <a id="authlink" href="#"></a>
        </header>
        <main>
          <div class="cards" id="cards"></div>
          <section>
            <h2 data-i18n="tagJobs">Jobs</h2>
            <table id="jobs"><thead><tr>
              <th data-i18n="thName">Name</th><th data-i18n="thSchedule">Schedule</th><th data-i18n="thOptions">Options</th><th data-i18n="thState">State</th><th style="width:1%" data-i18n="thActions">Actions</th>
            </tr></thead><tbody></tbody></table>
          </section>
          <div class="grid">
            <section>
              <h2 data-i18n="tagRunning">Running</h2>
              <table id="running"><thead><tr>
                <th data-i18n="thJob">Job</th><th data-i18n="thAttempt">Attempt</th><th data-i18n="thStarted">Started</th><th data-i18n="thProgress">Progress</th><th style="width:1%"></th>
              </tr></thead><tbody></tbody></table>
            </section>
            <section>
              <h2 data-i18n="tagPending">Pending</h2>
              <table id="pending"><thead><tr>
                <th data-i18n="thJob">Job</th><th data-i18n="thTrigger">Trigger</th><th data-i18n="thScheduledAt">Scheduled at</th><th style="width:1%"></th>
              </tr></thead><tbody></tbody></table>
            </section>
          </div>
          <section>
            <h2 data-i18n="tagHistory">Execution history</h2>
            <div style="padding: 8px 10px; display:flex; gap:8px; align-items:center;">
              <select id="jobpick"><option value="" data-i18n="allJobs">all jobs</option></select>
              <button onclick="loadHistory()" data-i18n="refresh">refresh</button>
            </div>
            <table id="history"><thead><tr>
              <th data-i18n="thJob">Job</th><th data-i18n="thOutcome">Outcome</th><th data-i18n="thAttempt">Attempt</th><th data-i18n="thScheduled">Scheduled</th><th data-i18n="thStarted">Started</th><th data-i18n="thFinished">Finished</th><th data-i18n="thErrorProgress">Error / Progress</th>
            </tr></thead><tbody></tbody></table>
          </section>
          <section>
            <h2 data-i18n="tagEvents">Event stream</h2>
            <div class="events" id="events"></div>
          </section>
        </main>
        <dialog id="dlg">
          <h3 id="dlgTitle">Schedule one run</h3>
          <div class="row"><span style="width:70px" data-i18n="dlgJob">Job</span><b id="dlgJob"></b></div>
          <div class="row" id="dlgAtRow"><span style="width:70px" data-i18n="dlgWhen">When</span><input type="datetime-local" id="dlgAt" step="1"></div>
          <div class="row"><span style="width:70px" data-i18n="dlgPayload">Payload</span></div>
          <textarea id="dlgPayload" data-i18n-ph="dlgPh" placeholder='{"note": "optional json"} (empty = no payload)'></textarea>
          <div class="errbar" id="dlgErr"></div>
          <div style="text-align:right; display:flex; gap:8px; justify-content:flex-end;">
            <button onclick="dlg.close()" data-i18n="btnCancel">cancel</button>
            <button id="dlgGo" class="danger" onclick="dlgAction()" data-i18n="btnGo">go</button>
          </div>
        </dialog>
        <script>
        (function () {
          "use strict";
          var ROOT = "@ROOT@"; // 服务端发版时替换成挂载根 —— fetch 面与登录链路全走它
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
          var KIND = { 1: "kCron", 2: "kInterval", 3: "kOnce", 4: "kOnDemand" };
          var TRIGGER = { 1: "trgCron", 2: "trgInterval", 3: "trgScheduled", 4: "trgManual", 5: "trgEnqueued", 6: "trgRetry" };
          var OVERLAP = { 1: "ovSkip", 2: "ovQueue", 3: "ovRunAny" };
          var EVENTNAME = { 1: "evStarted", 2: "evCompleted", 3: "evFailed", 4: "evRetrying", 5: "evAnomaly", 6: "evSkipped" };

          // ---- i18n:中英双词典,页面文案一律走 T()/TF() 取词,静态骨架用 data-i18n 标注。
          var connState = "connecting", connSeconds = "2s";
          var meData = null, currentTheme = "dark";
          var I = {
            en: {
              thTheme: "toggle light/dark", thLang: "switch language",
              themeDark: "dark", themeLight: "light",
              connConnecting: "connecting", connOffline: "offline", connPolling: "polling {x}",
              anonymous: "anonymous", signedIn: "signed in", notSignedIn: "not signed in", signOut: "sign out", signIn: "sign in",
              tagJobs: "Jobs", tagRunning: "Running", tagPending: "Pending", tagHistory: "Execution history", tagEvents: "Event stream",
              thName: "Name", thSchedule: "Schedule", thOptions: "Options", thState: "State", thActions: "Actions",
              thJob: "Job", thAttempt: "Attempt", thStarted: "Started", thProgress: "Progress",
              thTrigger: "Trigger", thScheduledAt: "Scheduled at",
              thOutcome: "Outcome", thScheduled: "Scheduled", thFinished: "Finished", thErrorProgress: "Error / Progress",
              allJobs: "all jobs", refresh: "refresh",
              kCron: "Cron", kInterval: "Interval", kOnce: "Once", kOnDemand: "OnDemand",
              trgCron: "cron", trgInterval: "interval", trgScheduled: "scheduled", trgManual: "manual", trgEnqueued: "enqueued", trgRetry: "retry",
              ovSkip: "Skip", ovQueue: "Queue", ovRunAny: "RunAnyway",
              evStarted: "started", evCompleted: "completed", evFailed: "failed", evRetrying: "retrying", evAnomaly: "anomaly", evSkipped: "skipped",
              outCompleted: "Completed", outFailed: "Failed", outRetrying: "Retrying", outSkipped: "Skipped", outCancelled: "Cancelled",
              stPending: "pending", stRunning: "running", stStarted: "started", stCompleted: "completed",
              stFailed: "failed", stRetried: "retried", stSkipped: "skipped", stCancelled: "cancelled",
              cronX: "cron {x}", everyX: "every {x}", onceAt: "once @ {x}", onDemand: "on demand",
              concN: "conc {x}", overlapX: "overlap {x}", prioN: "prio {x}", retriesN: "retries {x}",
              timeoutX: "timeout {x}", graceX: "grace {x}", tzX: "tz {x}",
              disabled: "disabled", paused: "paused", live: "live",
              btnPause: "pause", btnResume: "resume", btnTrigger: "trigger", btnScheduleOnce: "schedule once",
              btnRemove: "remove", btnCancel: "cancel", btnDrop: "drop", btnGo: "go",
              dlgTitleTrigger: "Trigger now — {x}", dlgTitleSchedule: "Schedule one run — {x}",
              dlgJob: "Job", dlgWhen: "When", dlgPayload: "Payload",
              dlgPh: '{"note": "optional json"} (empty = no payload)',
              dlgErrJson: "payload is not JSON: {x}", dlgErrPick: "pick a moment",
              confirmRemove: "Remove registration of '{x}'? Due runs already dispatched finish or drop.",
              noJobs: "no jobs registered — declare [QuickJob] static methods or use AddQuickJob DSL",
              nothingInFlight: "nothing in flight", queueEmpty: "queue empty", noHistory: "no history", noEvents: "no events yet",
              attemptN: "attempt {x}", delayX: "delay {x}"
            },
            zh: {
              thTheme: "切换浅色/深色", thLang: "切换语言",
              themeDark: "深色", themeLight: "浅色",
              connConnecting: "连接中", connOffline: "离线", connPolling: "轮询中 {x}",
              anonymous: "匿名", signedIn: "已登录", notSignedIn: "未登录", signOut: "登出", signIn: "登录",
              tagJobs: "任务", tagRunning: "执行中", tagPending: "排队中", tagHistory: "执行历史", tagEvents: "事件流",
              thName: "名称", thSchedule: "调度", thOptions: "选项", thState: "状态", thActions: "操作",
              thJob: "任务", thAttempt: "尝试", thStarted: "开始", thProgress: "进度",
              thTrigger: "触发", thScheduledAt: "计划时刻",
              thOutcome: "结果", thScheduled: "计划", thFinished: "结束", thErrorProgress: "错误 / 进度",
              allJobs: "全部任务", refresh: "刷新",
              kCron: "Cron", kInterval: "间隔", kOnce: "单次", kOnDemand: "按需",
              trgCron: "cron", trgInterval: "间隔", trgScheduled: "预定", trgManual: "手动", trgEnqueued: "入队", trgRetry: "重试",
              ovSkip: "跳过", ovQueue: "排队", ovRunAny: "照跑",
              evStarted: "已启动", evCompleted: "已完成", evFailed: "已失败", evRetrying: "重试中", evAnomaly: "异常", evSkipped: "已跳过",
              outCompleted: "完成", outFailed: "失败", outRetrying: "重试中", outSkipped: "跳过", outCancelled: "取消",
              stPending: "排队", stRunning: "执行中", stStarted: "已启动", stCompleted: "已完成",
              stFailed: "已失败", stRetried: "已重试", stSkipped: "已跳过", stCancelled: "已取消",
              cronX: "cron {x}", everyX: "每 {x}", onceAt: "单次 @ {x}", onDemand: "按需",
              concN: "并发 {x}", overlapX: "重叠 {x}", prioN: "优先 {x}", retriesN: "重试 {x}",
              timeoutX: "超时 {x}", graceX: "宽限 {x}", tzX: "时区 {x}",
              disabled: "停用", paused: "暂停", live: "生效中",
              btnPause: "暂停", btnResume: "恢复", btnTrigger: "立即触发", btnScheduleOnce: "定时一次",
              btnRemove: "移除", btnCancel: "取消", btnDrop: "丢弃", btnGo: "执行",
              dlgTitleTrigger: "立即触发 — {x}", dlgTitleSchedule: "定时一次 — {x}",
              dlgJob: "任务", dlgWhen: "时刻", dlgPayload: "载荷",
              dlgPh: '{"note": "可选 json"}(空 = 无载荷)',
              dlgErrJson: "载荷不是合法 JSON:{x}", dlgErrPick: "请先选时刻",
              confirmRemove: "移除登记任务 '{x}'?已派发的到期运行会跑完或丢弃。",
              noJobs: "无已登记任务 — 用 [QuickJob] 静态方法特性或 AddQuickJob DSL 登记",
              nothingInFlight: "无执行中", queueEmpty: "队列空", noHistory: "暂无历史", noEvents: "暂无事件",
              attemptN: "尝试 {x}", delayX: "延迟 {x}"
            }
          };
          var lang = "en";
          function T(k) {
            var d = I[lang] || I.en;
            var v = d[k];
            if (v === undefined) v = I.en[k];
            return v === undefined ? k : v;
          }
          function TF(k, x) { return T(k).replace("{x}", x === null || x === undefined ? "" : String(x)); }
          function drawConn() {
            var c = $("conn");
            if (connState === "offline") { c.textContent = T("connOffline"); c.className = "chip failed"; }
            else if (connState === "polling") { c.textContent = TF("connPolling", connSeconds); c.className = "chip ok"; }
            else { c.textContent = T("connConnecting"); c.className = "chip"; }
          }
          function drawWho() {
            var me = meData;
            if (!me) return;
            var who = $("who"), link = $("authlink");
            if (me.authMode && me.authMode !== "Anonymous") {
              who.textContent = me.authenticated ? (me.name || T("signedIn")) : T("notSignedIn");
              who.className = "chip" + (me.authenticated ? " ok" : " failed");
              link.textContent = me.authenticated ? T("signOut") : T("signIn");
              link.href = ROOT + (me.authenticated ? "/logout" : "/login");
            } else {
              who.textContent = T("anonymous");
            }
          }
          function scanStatic() {
            var i, el, nods = document.querySelectorAll("[data-i18n]");
            for (i = 0; i < nods.length; i++) nods[i].textContent = T(nods[i].getAttribute("data-i18n"));
            nods = document.querySelectorAll("[data-i18n-title]");
            for (i = 0; i < nods.length; i++) nods[i].title = T(nods[i].getAttribute("data-i18n-title"));
            nods = document.querySelectorAll("[data-i18n-ph]");
            for (i = 0; i < nods.length; i++) nods[i].placeholder = T(nods[i].getAttribute("data-i18n-ph"));
          }
          function applyLang(l) {
            lang = l;
            try { localStorage.setItem("qj.lang", l); } catch (e) {}
            document.documentElement.lang = l === "zh" ? "zh-CN" : "en";
            $("langbtn").textContent = l === "zh" ? "en" : "zh";
            scanStatic();
            drawConn();
            drawWho();
            if (currentTheme) applyTheme(currentTheme);
            tick();
            loadHistory();
          }

          // theme: localStorage 记忆 → 首访看 prefers-color-scheme → 都没有就暗板。
          // 按钮文案 = 可切去的模式(与 pause/resume 标签同语义)。
          function applyTheme(t) {
            currentTheme = t;
            document.documentElement.setAttribute("data-theme", t);
            try { localStorage.setItem("qj.theme", t); } catch (e) {}
            $("themebtn").textContent = t === "light" ? T("themeDark") : T("themeLight");
          }
          var firstTheme = null;
          try { firstTheme = localStorage.getItem("qj.theme"); } catch (e) {}
          if (firstTheme !== "light" && firstTheme !== "dark")
            firstTheme = (window.matchMedia && matchMedia("(prefers-color-scheme: light)").matches) ? "light" : "dark";
          applyTheme(firstTheme);

          // lang: localStorage 记忆 → 首访看 navigator.language(zh 开头)→ 都没有就英文。
          // 语言按钮文案 = 可切去的语言码(与主题按钮同语义)。
          var firstLang = null;
          try { firstLang = localStorage.getItem("qj.lang"); } catch (e) {}
          if (firstLang !== "zh" && firstLang !== "en")
            firstLang = (navigator.language || "").indexOf("zh") === 0 ? "zh" : "en";
          applyLang(firstLang);
          $("themebtn").onclick = function () {
            applyTheme(currentTheme === "dark" ? "light" : "dark");
          };
          $("langbtn").onclick = function () {
            applyLang(lang === "zh" ? "en" : "zh");
          };

          function api(path, body) {
            return fetch(ROOT + path, body === undefined ? {} : {
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
              connState = "offline";
              drawConn();
              return null;
            });
          }

          function scheduleText(job) {
            if (job.kind === 1) return TF("cronX", esc(job.cron || "?"));
            if (job.kind === 2) return TF("everyX", esc(job.every || "?"));
            if (job.kind === 3) return TF("onceAt", when(job.runAt));
            return T("onDemand");
          }

          function statsView(s) {
            var cells = [
              ["stPending", s.pendingRuns], ["stRunning", s.runningRuns],
              ["stStarted", s.runsStarted], ["stCompleted", s.runsCompleted],
              ["stFailed", s.runsFailed], ["stRetried", s.runsRetried],
              ["stSkipped", s.runsSkipped], ["stCancelled", s.runsCancelled]
            ];
            $("cards").innerHTML = cells.map(function (c) {
              return '<div class="card"><b>' + c[1] + '</b><small>' + T(c[0]) + '</small></div>';
            }).join("");
          }

          function jobsView(list) {
            list = list || [];
            var pick = $("jobpick");
            var keep = pick.value;
            pick.innerHTML = '<option value="">' + T("allJobs") + '</option>' + list.map(function (j) {
              return '<option value="' + esc(j.name) + '">' + esc(j.name) + '</option>';
            }).join("");
            if (keep && list.some(function (j) { return j.name === keep; })) pick.value = keep;

            $("jobs").tBodies[0].innerHTML = list.map(function (j) {
              var o = j.options || {};
              var state = (o.enabled === false ? '<span class="tag">' + T("disabled") + '</span> ' : "") +
                (j.paused ? '<span class="tag paused">' + T("paused") + '</span>' : '<span class="tag enabled">' + T("live") + '</span>');
              var actions = "";
              if (!readOnly) {
                actions =
                  '<button onclick="ui.pauseToggle(' + "'" + jsq(j.name) + "'," + (j.paused ? "1" : "0") + ')">' + (j.paused ? T("btnResume") : T("btnPause")) + '</button> ' +
                  '<button onclick="ui.openDialog(' + "'" + jsq(j.name) + "','" + "trigger" + "')\">" + T("btnTrigger") + "</button> " +
                  (j.kind === 4 ? "" : '<button onclick="ui.openDialog(' + "'" + jsq(j.name) + "','" + "schedule" + "')\">" + T("btnScheduleOnce") + "</button> ") +
                  '<button class="danger" onclick="ui.remove(' + "'" + jsq(j.name) + '\')">' + T("btnRemove") + "</button>";
              }
              return "<tr>" +
                '<td class="mono"><b>' + esc(j.name) + "</b>" + (o.description ? '<br><span class="dim">' + esc(o.description) + "</span>" : "") + "</td>" +
                "<td>" + scheduleText(j) + (o.timeZoneId ? '<br><span class="dim">' + TF("tzX", esc(o.timeZoneId)) + "</span>" : "") + "</td>" +
                '<td class="dim">' + (KIND[j.kind] ? T(KIND[j.kind]) : "?") + " &middot; " + TF("concN", (o.maxConcurrentPerJob || 1)) +
                " &middot; " + TF("overlapX", (OVERLAP[o.onOverlap] ? T(OVERLAP[o.onOverlap]) : "?")) +
                " &middot; " + TF("prioN", (o.priority || 0)) +
                " &middot; " + TF("retriesN", (o.maxRetries || 0)) +
                (o.timeout ? " &middot; " + TF("timeoutX", esc(o.timeout)) : "") +
                (o.grace ? " &middot; " + TF("graceX", esc(o.grace)) : "") + "</td>" +
                "<td>" + state + "</td>" +
                '<td class="act">' + actions + "</td></tr>";
            }).join("") || '<tr><td colspan="5" class="dim">' + T("noJobs") + "</td></tr>";
          }

          function runningView(list) {
            $("running").tBodies[0].innerHTML = (list || []).map(function (r) {
              return "<tr><td class=mono>" + esc(r.jobName) + "</td><td>" + r.attempt + "</td><td>" + when(r.startedAt) + "</td>" +
                '<td class="dim mono">' + esc(r.progress || "") + "</td>" +
                '<td class="act">' + (!readOnly ? '<button class="danger" onclick="ui.cancel(' + "'" + esc(r.executionId) + '\')">' + T("btnCancel") + "</button>" : "") + "</td></tr>";
            }).join("") || '<tr><td colspan="5" class="dim">' + T("nothingInFlight") + "</td></tr>";
          }

          function pendingView(list) {
            $("pending").tBodies[0].innerHTML = (list || []).map(function (r) {
              return "<tr><td class=mono>" + esc(r.jobName) + "</td><td class=dim>" + (TRIGGER[r.trigger] ? T(TRIGGER[r.trigger]) : esc(String(r.trigger))) + "</td><td>" + when(r.scheduledAt) + "</td>" +
                '<td class="act">' + (!readOnly ? '<button class="danger" onclick="ui.cancel(' + "'" + esc(r.executionId) + '\')">' + T("btnDrop") + "</button>" : "") + "</td></tr>";
            }).join("") || '<tr><td colspan="4" class="dim">' + T("queueEmpty") + "</td></tr>";
          }

          function history(rows) {
            $("history").tBodies[0].innerHTML = (rows || []).map(function (e) {
              // 后端 outcome 是显示文本(可能带后缀,如 "Retrying #2")——语义键与样式类从词头判出,
              // 显示文本走词典 + 尾件原样;翻译不参与判定,切语言不会破样式。
              var raw = e.outcome || "";
              var tail = raw.replace(/^[A-Za-z]+/, "");
              var key = null, cls = "";
              if (raw.indexOf("Completed") === 0) { key = "outCompleted"; cls = "completed"; }
              else if (raw.indexOf("Failed") === 0) { key = "outFailed"; cls = "failed"; }
              else if (raw.indexOf("Retrying") === 0) { key = "outRetrying"; cls = "retrying"; }
              else if (raw.indexOf("Skipped") === 0) { key = "outSkipped"; cls = "cancelled"; }
              else if (raw.indexOf("Cancelled") === 0) { key = "outCancelled"; cls = "cancelled"; }
              var shown = key ? esc(T(key)) + esc(tail) : esc(raw);
              return "<tr><td class=mono>" + esc(e.jobName) + "</td>" +
                '<td><span class="tag ' + cls + '">' + shown + "</span></td><td>" + e.attempt + "</td>" +
                "<td>" + when(e.scheduledAt) + "</td><td>" + when(e.startedAt) + "</td><td>" + when(e.finishedAt) + "</td>" +
                '<td class="mono dim" style="max-width:320px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap;">' + esc(e.error || e.progress || "") + "</td></tr>";
            }).join("") || '<tr><td colspan="7" class="dim">' + T("noHistory") + "</td></tr>";
          }

          function loadHistory() {
            var job = $("jobpick").value;
            var qs = job ? "?job=" + encodeURIComponent(job) : "?take=80";
            get("/executions" + qs).then(history);
          }

          function eventsView(list) {
            $("events").innerHTML = (list || []).map(function (e) {
              var cls = e.kind === 2 ? "completed" : e.kind === 3 ? "failed" : e.kind === 4 ? "retrying" : e.kind === 6 ? "cancelled" : e.kind === 5 ? "failed" : "";
              return '<div><span class="tag ' + cls + '">' + (EVENTNAME[e.kind] ? T(EVENTNAME[e.kind]) : "?") + "</span> " + when(e.at) + " &middot; " +
                esc(e.jobName || e.message || "?") +
                (e.error ? ' <span class="dim">' + esc(String(e.error).slice(0, 120)) + "</span>" : "") +
                (e.attempt ? ' <span class="dim">' + TF("attemptN", e.attempt) + "</span>" : "") +
                (e.delay ? ' <span class="dim">' + TF("delayX", esc(e.delay)) + "</span>" : "") +
                "</div>";
            }).join("") || '<div class="dim">' + T("noEvents") + "</div>";
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
              connState = "polling";
              connSeconds = "2s";
              drawConn();
              busy = false;
            });
          }

          fetch(ROOT + "/me").then(function (r) { return r.json(); }).then(function (me) {
            readOnly = !!me.readOnly;
            meData = me;
            drawWho();
            tick();
            setInterval(tick, 2000);
            $("jobpick").onchange = loadHistory;
            loadHistory();
          }).catch(function () {
            tick();
            setInterval(tick, 4000);
          });

          window.loadHistory = loadHistory; // history 是只读原生 History —— 严格模式赋值会抛 TypeError

          window.ui = {
            pauseToggle: function (job, paused) {
              api(paused ? "/resume" : "/pause", { job: job });
            },
            openDialog: function (job, mode) {
              dlg.dataset.mode = mode;
              dlg.dataset.job = job;
              $("dlgTitle").textContent = TF(mode === "trigger" ? "dlgTitleTrigger" : "dlgTitleSchedule", job);
              $("dlgJob").textContent = job;
              $("dlgAtRow").style.display = mode === "trigger" ? "none" : "flex";
              $("dlgPayload").value = "";
              $("dlgErr").textContent = "";
              dlg.showModal();
            },
            remove: function (job) {
              if (!confirm(TF("confirmRemove", job))) return;
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
              catch (err) { $("dlgErr").textContent = TF("dlgErrJson", err.message); return; }
            }
            if (mode === "trigger") {
              api("/trigger", { job: job, payload: payload }).then(function () { dlg.close(); });
            } else {
              var at = $("dlgAt").value;
              if (!at) { $("dlgErr").textContent = T("dlgErrPick"); return; }
              api("/schedule", { job: job, at: new Date(at).toISOString(), payload: payload }).then(function () { dlg.close(); });
            }
          };
        })();
        </script>
        </body>
        </html>
        """;
}
