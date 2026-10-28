(() => {
  'use strict';
  const $app = document.getElementById('app');
  const KEY = 'tvToken';
  const store = {
    get() { try { return localStorage.getItem(KEY); } catch { return null; } },
    set(v) { try { v ? localStorage.setItem(KEY, v) : localStorage.removeItem(KEY); } catch { /* تخزين غير متاح */ } }
  };
  const params = new URLSearchParams(location.search);
  const SLIDE_SECONDS = Math.max(5, Math.min(120, Number(params.get('s')) || 10));
  const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const num = n => Math.round(Number(n) || 0).toLocaleString('en-US');
  const mil = n => { const v = Number(n) || 0; return v >= 1e6 ? (v / 1e6).toFixed(2) : num(v); };
  const milUnit = n => (Number(n) || 0) >= 1e6 ? 'مليون' : 'د.ع';
  let data = null, slide = 0, slideTimer = null, dataTimer = null, clockTimer = null;

  async function api(path, opts = {}) {
    const token = store.get();
    const r = await fetch(path, { ...opts, headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: 'Bearer ' + token } : {}) }, cache: 'no-store' });
    const body = await r.json().catch(() => ({}));
    if (!r.ok) { const e = new Error(body.error || 'تعذّر الاتصال'); e.status = r.status; throw e; }
    return body;
  }

  function stop() { [slideTimer, dataTimer, clockTimer].forEach(t => t && clearInterval(t)); slideTimer = dataTimer = clockTimer = null; }

  function loginView(msg) {
    stop();
    $app.innerHTML = `<div class="stage"><form class="login" id="f"><h1>شاشة المعمل</h1><p>سجّل دخول حساب الشاشة مرة واحدة</p>
      <input id="u" placeholder="اسم المستخدم" autocapitalize="none" required><input id="p" type="password" placeholder="كلمة المرور" required>
      <div class="err" id="e">${esc(msg || '')}</div><button>دخول</button></form></div>`;
    document.body.classList.add('show-cursor');
    document.getElementById('f').onsubmit = async ev => {
      ev.preventDefault();
      try {
        const r = await api('/api/manager/login', { method: 'POST', body: JSON.stringify({ username: u.value, password: p.value }) });
        store.set(r.token); document.body.classList.remove('show-cursor'); start();
      } catch (x) { document.getElementById('e').textContent = x.message; }
    };
  }

  const kpi = (l, v, c = '', u = '') => `<div class="k"><div class="l">${l}</div><div class="v ${c}">${v}<span class="u"> ${u}</span></div></div>`;
  const row = (a, b, c = '') => `<div class="row"><span>${esc(a)}</span><b class="${c}">${b}</b></div>`;

  function slides(s) {
    const max = Math.max(1, ...s.last10.map(d => d.sales));
    const onlineReps = s.reps.filter(r => r.online).length;
    const list = [];
    list.push(['ملخص اليوم', `<div class="grid g4">${kpi('مبيعات اليوم', mil(s.salesToday), '', milUnit(s.salesToday))}
        ${kpi('أوامر الإنتاج المكتملة', s.ordersDone, 'ok', 'من ' + s.ordersTotal)}${kpi('المندوبون المتصلون', onlineReps, '', 'من ' + s.reps.length)}${kpi('الحاضرون', s.present, 'ok', 'متأخرون ' + s.late)}</div>
      <div class="k" style="margin-top:1.1rem;flex:1"><div class="l" style="margin-bottom:.6rem">مبيعات آخر 10 أيام</div>
      <div class="bars">${s.last10.map(d => `<span title="${esc(d.date)}" style="height:${Math.max(2, d.sales / max * 100)}%"></span>`).join('')}</div></div>`]);
    list.push(['المندوبون والمخازن', `<div class="grid g2" style="flex:1"><div class="k"><div class="h">مبيعات المندوبين اليوم</div>
        ${s.reps.map(r => row((r.online ? '🟢 ' : '⚪ ') + r.name, num(r.salesToday), r.online ? 'ok' : '')).join('') || '<div class="l">لا مندوبين</div>'}</div>
      <div class="k"><div class="h">تنبيه المخزون 🔴</div>${s.lowStock.map(l => row(l.item, num(l.balance) + ' / ' + num(l.min), 'bad')).join('') || '<div class="row"><span>كل الأصناف كافية</span><b class="ok">✔</b></div>'}</div></div>`]);
    const pass = s.qcTotal ? Math.round(s.qcPassed / s.qcTotal * 100) : null;
    list.push(['الإنتاج والجودة', `<div class="grid g3">${kpi('أوامر مكتملة', s.ordersDone, 'ok', 'من ' + s.ordersTotal)}${kpi('فحوصات ناجحة', pass === null ? '—' : pass + '٪', pass === null || pass >= 95 ? 'ok' : 'warn', pass === null ? '' : s.qcPassed + ' من ' + s.qcTotal)}</div>
      <div class="k" style="margin-top:1.1rem;flex:1"><div class="h">أُنتج اليوم</div>${s.produced.map(p => row(p.name, num(p.packs))).join('') || '<div class="l">لم يُسجَّل إنتاج بعد</div>'}</div>`]);
    list.push(['الحضور اليوم', `<div class="grid g4">${kpi('حاضرون', s.present, 'ok')}${kpi('متأخرون', s.late, 'warn')}${kpi('غائبون', s.absent, 'bad')}${kpi('في إجازة', s.onLeave)}</div>
      <div class="k" style="margin-top:1.1rem;flex:1"><div class="h">الحضور حسب القسم</div>${s.departments.map(d =>
        `<div class="row"><span>${esc(d.name)}</span><span class="dep"><div class="bar"><i style="width:${d.total ? Math.round(d.present / d.total * 100) : 0}%"></i></div><b>${d.present} من ${d.total}</b></span></div>`).join('')}</div>`]);
    if (s.message) list.push(['', `<div class="msg">${esc(s.message)}</div>`]);
    return list;
  }

  function clock() {
    const el = document.getElementById('clk');
    if (el) el.textContent = new Date().toLocaleString('ar-IQ', { weekday: 'long', year: 'numeric', month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' });
  }

  function paint() {
    if (!data) return;
    const s = data.summary, list = slides(s);
    const stale = (Date.now() - new Date(data.updatedUtc).getTime()) > 5 * 60000;
    $app.innerHTML = `<div class="stage"><div class="clock" id="clk"></div>
      ${list.map(([title, body], i) => `<div class="slide ${i === slide % list.length ? 'on' : ''}" data-i="${i}"><div class="hd"><b>${esc(s.factoryName)}</b><span>${esc(title)}</span></div>${body}</div>`).join('')}
      <div class="dots">${list.map((_, i) => `<i class="${i === slide % list.length ? 'on' : ''}"></i>`).join('')}</div>
      ${stale ? '<div class="stale">⚠ آخر تحديث قديم — حاسبة المعمل قد تكون مطفأة</div>' : ''}</div>`;
    clock();
  }

  function rotate() {
    if (!data) return;
    const n = slides(data.summary).length;
    slide = (slide + 1) % n;
    const slidesEl = document.querySelectorAll('.slide'), dots = document.querySelectorAll('.dots i');
    slidesEl.forEach((el, i) => el.classList.toggle('on', i === slide));
    dots.forEach((el, i) => el.classList.toggle('on', i === slide));
  }

  async function load() {
    try { data = await api('/api/tv/summary'); paint(); }
    catch (e) {
      if (e.status === 401) { store.set(null); data = null; loginView(e.message); }
      else if (!data) $app.innerHTML = `<div class="stage"><div class="login"><h1>شاشة المعمل</h1><p>${esc(e.message)} — تُعاد المحاولة تلقائيًا</p></div></div>`;
    }
  }

  function start() {
    stop(); load();
    dataTimer = setInterval(load, 60000);
    slideTimer = setInterval(rotate, SLIDE_SECONDS * 1000);
    clockTimer = setInterval(clock, 15000);
  }

  document.addEventListener('keydown', e => { if (e.key === 'f' || e.key === 'F') { document.fullscreenElement ? document.exitFullscreen() : document.documentElement.requestFullscreen?.(); } });
  document.addEventListener('dblclick', () => { document.fullscreenElement ? document.exitFullscreen() : document.documentElement.requestFullscreen?.(); });
  store.get() ? start() : loginView();
})();
