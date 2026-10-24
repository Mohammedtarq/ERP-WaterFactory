(() => {
  'use strict';
  const $app = document.getElementById('app');
  const KEY = 'managerToken';
  const store = {
    get() { try { return localStorage.getItem(KEY); } catch { return null; } },
    set(v) { try { v ? localStorage.setItem(KEY, v) : localStorage.removeItem(KEY); } catch { /* تخزين غير متاح */ } }
  };
  const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const num = n => Math.round(Number(n) || 0).toLocaleString('en-US');
  const TABS = [['home', '🏠', 'الرئيسية'], ['reps', '🚚', 'المندوبون'], ['stock', '📦', 'المخازن'], ['debts', '💰', 'الذمم'], ['hr', '👥', 'الموارد']];
  let tab = 'home', data = null, timer = null;

  const ago = iso => {
    if (!iso) return 'غير متصل';
    const s = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
    if (s < 60) return 'قبل أقل من دقيقة';
    if (s < 3600) return `قبل ${Math.floor(s / 60)} د`;
    if (s < 86400) return `قبل ${Math.floor(s / 3600)} ساعة`;
    return `قبل ${Math.floor(s / 86400)} يوم`;
  };
  const dot = iso => !iso ? '🔴' : (Date.now() - new Date(iso).getTime()) < 600000 ? '🟢' : (Date.now() - new Date(iso).getTime()) < 6 * 3600000 ? '🟠' : '🔴';

  async function api(path, opts = {}) {
    const token = store.get();
    const r = await fetch(path, { ...opts, headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: 'Bearer ' + token } : {}) }, cache: 'no-store' });
    const body = await r.json().catch(() => ({}));
    if (!r.ok) { const e = new Error(body.error || 'تعذّر الاتصال'); e.status = r.status; throw e; }
    return body;
  }

  function loginView(msg) {
    stop();
    $app.innerHTML = `<form class="login" id="f"><h1>لوحة المدير</h1><p>عرض فقط — لا تعديل ولا حذف</p>
      <input id="u" autocomplete="username" placeholder="اسم المستخدم" autocapitalize="none" required>
      <input id="p" type="password" autocomplete="current-password" placeholder="كلمة المرور" required>
      <div class="err" id="e">${esc(msg || '')}</div><button>دخول</button></form>`;
    document.getElementById('f').onsubmit = async ev => {
      ev.preventDefault();
      const e = document.getElementById('e'); e.textContent = '';
      try {
        const r = await api('/api/manager/login', { method: 'POST', body: JSON.stringify({ username: u.value, password: p.value }) });
        store.set(r.token); start();
      } catch (x) { e.textContent = x.message; }
    };
  }

  const kpi = (l, v, c = '') => `<div class="k"><div class="l">${l}</div><div class="v ${c}">${num(v)}</div></div>`;
  const row = (a, b, c = '') => `<div class="row"><span>${esc(a)}</span><span class="n ${c}">${b}</span></div>`;

  function home(s) {
    const t = s.today, max = Math.max(1, ...s.last30.map(d => d.sales));
    return `<div class="grid">${kpi('مبيعات اليوم', t.sales)}${kpi('المحصَّل اليوم', t.collected, 'ok')}${kpi('مبيعات آجلة', t.creditSales, 'warn')}${kpi('مصروفات اليوم', t.expenses, 'bad')}</div>
      <div class="card"><div class="h">الصناديق</div>${s.boxes.map(b => row(b.name, num(b.balance))).join('') || '<div class="muted">لا صناديق</div>'}</div>
      <div class="card"><div class="h">مبيعات آخر 30 يومًا</div><div class="chart">${s.last30.map(d => `<span title="${esc(d.date)}: ${num(d.sales)}" style="height:${Math.max(2, d.sales / max * 100)}%"></span>`).join('')}</div></div>
      <div class="card"><div class="h">تنبيهات</div>${row('حركات المندوبين بانتظار الاعتماد', s.alerts.pendingRepRequests, s.alerts.pendingRepRequests ? 'warn' : 'ok')}
        ${row('طلبات صرف بانتظار الموافقة', s.alerts.pendingPayments, s.alerts.pendingPayments ? 'warn' : 'ok')}${row('منتجات تحت حد التنبيه', s.alerts.lowStock, s.alerts.lowStock ? 'bad' : 'ok')}</div>`;
  }
  const reps = s => s.reps.length ? s.reps.map(r => `<div class="card"><div class="row"><b>${esc(r.name)}</b><span class="${dot(r.lastSeenUtc) === '🟢' ? 'ok' : dot(r.lastSeenUtc) === '🟠' ? 'warn' : 'bad'}">${dot(r.lastSeenUtc)} ${ago(r.lastSeenUtc)}</span></div>
      ${row('مبيعات اليوم', num(r.salesToday))}${row('النقد معه', num(r.cashInWallet))}${row('حركات معلّقة', r.pendingRequests ? r.pendingRequests : '0 ✔', r.pendingRequests ? 'warn' : 'ok')}<div class="muted">${esc(r.status)}</div></div>`).join('')
    : '<div class="card muted">لا مندوبين</div>';
  const stock = s => `<div class="card"><div class="h">تحت حد التنبيه 🔴</div>${s.lowStock.map(l => `<div class="row"><span>${esc(l.item)}<div class="muted">${esc(l.warehouse)}</div></span><span class="n bad">${num(l.balance)} / ${num(l.min)}</span></div><div class="bar"><i style="width:${Math.min(100, l.min ? l.balance / l.min * 100 : 0)}%"></i></div>`).join('') || '<div class="muted ok">لا نقص ✔</div>'}</div>
      <div class="card"><div class="h">أرصدة المنتجات التامة (بالقطعة)</div>${s.stock.map(i => row(i.item, num(i.pieces))).join('') || '<div class="muted">لا أرصدة</div>'}</div>`;
  const debts = s => `<div class="card"><div class="h">أكبر الديون على الزبائن 💰</div>${s.debtors.map(d => row(d.name, num(d.amount), 'bad')).join('') || '<div class="muted ok">لا ديون ✔</div>'}
      <div class="row"><b>مجموع الذمم</b><b class="n">${num(s.totalReceivables)}</b></div></div>`;
  const hr = s => { const h = s.hr; return `<div class="grid">${kpi('حاضرون', h.present, 'ok')}${kpi('غائبون', h.absent, 'bad')}${kpi('متأخرون', h.late, 'warn')}${kpi('في إجازة', h.onLeave)}</div>
      <div class="card"><div class="h">الغائبون اليوم</div>${h.absentToday.map(p => row(p.name, esc(p.detail))).join('') || '<div class="muted ok">لا غياب ✔</div>'}</div>
      <div class="card"><div class="h">في إجازة</div>${h.onLeaveToday.map(p => row(p.name, esc(p.detail))).join('') || '<div class="muted">لا أحد</div>'}</div>
      <div class="card"><div class="h">علاوات قادمة (30 يومًا)</div>${row(h.raisesDue + ' موظف', '<span class="tag">تنبيه</span>')}</div>
      <div class="card muted">الرواتب والسلف غير معروضة في هذه اللوحة.</div>`; };
  const VIEWS = { home, reps, stock, debts, hr };

  function paint() {
    if (!data) return;
    const s = data.summary;
    const stale = (Date.now() - new Date(data.updatedUtc).getTime()) > 5 * 60000;
    $app.innerHTML = `<div class="top"><span class="ro">قراءة فقط</span><b>${esc(s.factoryName)}</b>
      <small>${stale ? '🟠' : '🟢'} آخر تحديث ${ago(data.updatedUtc)}${stale ? ' — حاسبة المعمل قد تكون مطفأة' : ''}</small><button class="out" id="o">خروج</button></div>
      <div class="body">${VIEWS[tab](s)}</div>
      <nav>${TABS.map(([k, i, l]) => `<button data-t="${k}" class="${k === tab ? 'on' : ''}"><b>${i}</b>${l}</button>`).join('')}</nav>`;
    document.querySelectorAll('nav button').forEach(b => b.onclick = () => { tab = b.dataset.t; paint(); scrollTo(0, 0); });
    document.getElementById('o').onclick = async () => { try { await api('/api/manager/logout', { method: 'POST' }); } catch { /* خروج محلي كافٍ */ } store.set(null); data = null; loginView(); };
  }

  async function load() {
    try { data = await api('/api/manager/summary'); paint(); }
    catch (e) {
      if (e.status === 401) { store.set(null); data = null; loginView(e.message); }
      else if (!data) $app.innerHTML = `<div class="login"><h1>لوحة المدير</h1><p>${esc(e.message)}</p><button onclick="location.reload()">إعادة المحاولة</button></div>`;
    }
  }
  function start() { stop(); load(); timer = setInterval(load, 60000); }
  function stop() { if (timer) clearInterval(timer); timer = null; }
  document.addEventListener('visibilitychange', () => { if (!document.hidden && store.get()) load(); });

  if ('serviceWorker' in navigator) navigator.serviceWorker.register('/manager/sw.js', { scope: '/manager/' }).catch(() => {});
  store.get() ? start() : loginView();
})();
