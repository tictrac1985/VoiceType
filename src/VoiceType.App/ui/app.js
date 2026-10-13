'use strict';

/* =====================================================================
 *  الاتصال بالبرنامج
 *  الواجهة ترسل {id, method, params} وتستقبل {type:"result"} أو {type:"event"}.
 *  عند فتح الصفحة في متصفح عادي (للمعاينة) نستخدم بيانات تجريبية.
 * ===================================================================== */
const host = window.chrome && window.chrome.webview;
const pending = new Map();
const listeners = new Map();
let nextId = 1;

function call(method, params = {}, files = null) {
  const id = nextId++;
  const message = { id, method, params };
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject });
    if (!host) {
      Mock.handle(message).then(
        data => receive({ type: 'result', id, ok: true, data }),
        err => receive({ type: 'result', id, ok: false, error: err.message || String(err) }));
    } else if (files && host.postMessageWithAdditionalObjects) {
      host.postMessageWithAdditionalObjects(message, files);
    } else {
      host.postMessage(message);
    }
  });
}

function on(name, handler) {
  if (!listeners.has(name)) listeners.set(name, []);
  listeners.get(name).push(handler);
}

function receive(msg) {
  if (!msg) return;
  if (msg.type === 'result') {
    const p = pending.get(msg.id);
    if (!p) return;
    pending.delete(msg.id);
    msg.ok ? p.resolve(msg.data) : p.reject(new Error(msg.error || 'حدث خطأ.'));
  } else if (msg.type === 'event') {
    (listeners.get(msg.name) || []).forEach(h => { try { h(msg.data); } catch (e) { console.error(e); } });
  }
}

if (host) host.addEventListener('message', e => receive(e.data));

/* ===== أدوات ===== */
const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const icon = (name, cls = '') => `<svg class="${cls}"><use href="#i-${name}"/></svg>`;
const pct = f => `${Math.round((f || 0) * 100)}%`;

function toast(text, kind = 'ok', ms = 3800) {
  const el = document.createElement('div');
  el.className = `toast ${kind}`;
  el.innerHTML = `${icon(kind === 'bad' ? 'alert' : kind === 'info' ? 'info' : 'check')}<div>${esc(text)}</div>`;
  $('#toasts').appendChild(el);
  setTimeout(() => { el.style.transition = 'opacity .3s'; el.style.opacity = '0'; setTimeout(() => el.remove(), 300); }, ms);
}

async function run(button, work) {
  if (button) button.disabled = true;
  try { return await work(); }
  catch (e) { toast(e.message, 'bad', 6000); }
  finally { if (button) button.disabled = false; }
}

function fmtDuration(sec) {
  sec = Math.round(sec || 0);
  const h = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60), s = sec % 60;
  if (h) return `${h} س ${m} د`;
  if (m) return `${m} د ${s} ث`;
  return `${s} ث`;
}

function fmtTime(iso) {
  const d = new Date(iso);
  if (isNaN(d)) return '';
  return d.toLocaleTimeString('ar-EG-u-nu-latn', { hour: 'numeric', minute: '2-digit' });
}

const PRICING = {
  free: ['free', 'مجاني'],
  'free-tier': ['free-tier', 'مجاني بحدود'],
  trial: ['trial', 'تجربة مجانية'],
  paid: ['paid', 'مدفوع'],
};
const pricingTag = (p, label) => {
  const [cls, text] = PRICING[p] || ['', label || 'غير محدد'];
  return `<span class="tag ${cls}">${esc(label || text)}</span>`;
};

/* =====================================================================
 *  الحالة العامة
 * ===================================================================== */
let state = null;
let testing = false;

function setState(s) {
  if (!s) return;
  state = s;
  renderShared();
  renderDictation();
  renderSettings();
  renderFileEngines();
}

function engineByKey(key) {
  return state?.engines?.find(e => e.key === key) || null;
}

function engineLabel(key) {
  const e = engineByKey(key);
  if (!e) return key ? key.replace('|', ' · ') : null;
  return e.label;
}

function engineBadge(key) {
  const e = engineByKey(key);
  if (!key) return '<span class="muted">لم يُحدَّد</span>';
  if (!e) return `<span>${esc(engineLabel(key))}</span> <span class="tag bad">غير موجود</span>`;
  const where = e.kind === 'local' ? '<span class="tag free">على جهازك</span>' : pricingTag(e.pricing);
  const ready = e.ready ? '' : `<span class="tag bad">${esc(e.reason || 'غير جاهز')}</span>`;
  return `<span>${esc(e.label.replace(/^على جهازك · /, ''))}</span> ${where} ${ready}`;
}

function engineOptions(selected, { allowNone = false, noneText = 'بدون' } = {}) {
  const engines = state?.engines || [];
  const local = engines.filter(e => e.kind === 'local');
  const online = engines.filter(e => e.kind !== 'local');
  const opt = e => {
    const label = e.label.replace(/^على جهازك · /, '');
    const extra = e.kind === 'local' ? '' : ` — ${(PRICING[e.pricing] || [, ''])[1]}`;
    const reason = e.ready ? '' : ` (${e.reason || 'غير جاهز'})`;
    return `<option value="${esc(e.key)}" ${e.key === selected ? 'selected' : ''} ${e.ready ? '' : 'disabled'}>${esc(label + extra + reason)}</option>`;
  };
  let html = '';
  if (allowNone || !selected) html += `<option value="" ${!selected ? 'selected' : ''}>${esc(allowNone ? noneText : '— اختر نموذجًا —')}</option>`;
  if (local.length) html += `<optgroup label="على جهازك (مجاني، بدون إنترنت)">${local.map(opt).join('')}</optgroup>`;
  else html += `<optgroup label="على جهازك"><option disabled>لا توجد نماذج منزّلة بعد</option></optgroup>`;
  if (online.length) html += `<optgroup label="خدمات الإنترنت">${online.map(opt).join('')}</optgroup>`;
  return html;
}

const MIC_COLORS = [
  { id: 'gold', name: 'ذهبي', c: ['#F6CF7E', '#B9822F'] },
  { id: 'teal', name: 'فيروزي', c: ['#7FF3E1', '#14958B'] },
  { id: 'copper', name: 'نحاسي', c: ['#FFB37A', '#D9662A'] },
  { id: 'blue', name: 'أزرق', c: ['#3B82F6', '#6366F1'] },
  { id: 'purple', name: 'بنفسجي', c: ['#A855F7', '#EC4899'] },
  { id: 'green', name: 'أخضر', c: ['#10B981', '#0EA5E9'] },
  { id: 'dark', name: 'داكن', c: ['#4B5563', '#111827'] },
];
const THEMES = ['spotlight', 'orb', 'monolith'];

function shortEngine(key) {
  const label = engineLabel(key);
  return label ? label.replace(/^على جهازك · /, '') : '';
}

/* اسم النموذج وحده (بدون اسم الخدمة) للمساحات الضيقة */
function modelName(key) {
  const label = shortEngine(key);
  const parts = label.split(' · ');
  return parts.length > 1 ? parts.slice(1).join(' · ') : label;
}

function providerName(key) {
  const e = engineByKey(key);
  if (!e) return '';
  if (e.kind === 'local') return '';
  return shortEngine(key).split(' · ')[0];
}

function renderShared() {
  const micVisible = !!state.micVisible;
  ['#micToggle', '#setMicVisible'].forEach(sel => { const el = $(sel); if (el) el.checked = micVisible; });
  $('#setHotkey').textContent = state.hotkey;
  $('#hotkeyKeys').innerHTML = String(state.hotkey || '').split('+').map(k => `<kbd>${esc(k.trim())}</kbd>`).join('');
  const s = state.settings;
  $$('#micSizes [data-mic-size]').forEach(b => b.classList.toggle('on', b.dataset.micSize === (s.micSize || 'small')));
  $('#micColors').innerHTML = MIC_COLORS.map(m =>
    `<button data-mic-color="${m.id}" title="${m.name}" class="${m.id === s.micColor ? 'on' : ''}" style="background:radial-gradient(circle at 35% 30%,${m.c[0]},${m.c[1]} 70%)"></button>`).join('');
  if (s.theme && THEMES.includes(s.theme) && s.theme !== currentTheme()) applyTheme(s.theme, false);
  renderStatus();
}

function renderStatus() {
  if (!state) return;
  const pill = $('#statusPill');
  const d = state.dictationState;
  const primary = state.settings.primary;
  pill.classList.toggle('rec', d === 'Recording');
  pill.classList.toggle('busy', d === 'Processing');
  pill.classList.toggle('warn', !primary && d === 'Idle');
  $('#statusText').textContent =
    d === 'Recording' ? 'يسجّل…' :
    d === 'Processing' ? 'يكتب…' :
    primary ? modelName(primary) : 'لم يتم اختيار نموذج';
}

/* ===== الثيمات ===== */
function currentTheme() { return document.documentElement.dataset.theme || 'spotlight'; }

function applyTheme(name, save = true) {
  if (!THEMES.includes(name)) return;
  document.documentElement.dataset.theme = name;
  try { localStorage.setItem('sada.theme', name); } catch (e) { /* غير متاح */ }
  $$('[data-theme-set]').forEach(b => b.classList.toggle('on', b.dataset.themeSet === name));
  stageSet('theme', name);
  if (save && state) {
    state.settings.theme = name;
    call('settings.save', { theme: name }).then(setState).catch(e => toast(e.message, 'bad'));
  }
}

document.addEventListener('click', e => {
  const t = e.target.closest('[data-theme-set]');
  if (t) { applyTheme(t.dataset.themeSet); $('#themePop').hidden = true; return; }
  if (e.target.closest('#themeBtn')) { $('#themePop').hidden = !$('#themePop').hidden; return; }
  if (!e.target.closest('#themePop')) $('#themePop').hidden = true;
});

/* ===== المسرح ثلاثي الأبعاد (stage3d.js) ===== */
window.__stage = window.__stage || { theme: currentTheme(), state: 'Idle', level: 0 };
function stageSet(key, value) {
  window.__stage[key] = value;
  const api = window.Stage3D;
  if (!api) return;
  if (key === 'theme') api.setTheme(value);
  else if (key === 'state') api.setState(value);
  else if (key === 'level') api.setLevel(value);
}

/* =====================================================================
 *  التنقل
 * ===================================================================== */
const pageLoaders = {};

function go(page) {
  if (!$(`#page-${page}`)) return;
  $$('.nav-item').forEach(b => b.classList.toggle('active', b.dataset.page === page));
  $$('.page').forEach(p => p.classList.toggle('active', p.id === `page-${page}`));
  $('.main').scrollTop = 0;
  pageLoaders[page]?.();
}

$$('.nav-item').forEach(b => b.addEventListener('click', () => go(b.dataset.page)));
document.addEventListener('click', e => {
  const t = e.target.closest('[data-go]');
  if (t) { e.preventDefault(); go(t.dataset.go); }
  const f = e.target.closest('[data-folder]');
  if (f) { e.preventDefault(); call('app.openFolder', { kind: f.dataset.folder }); }
});
on('app.navigate', page => go(page));

/* =====================================================================
 *  زر المايك العائم
 * ===================================================================== */
['#micToggle', '#setMicVisible'].forEach(sel => {
  $(sel).addEventListener('change', e => {
    const visible = e.target.checked;
    state.micVisible = visible;
    renderShared();
    call('mic.setVisible', { visible }).catch(err => toast(err.message, 'bad'));
  });
});
on('settings.changed', () => { refreshState().catch(() => {}); });

$('#writingModes').addEventListener('click', e => {
  const b = e.target.closest('[data-writing]');
  if (!b) return;
  if (b.dataset.writing !== 'normal' && state && !state.hasGeminiKey) { toast('الصياغة تحتاج مفتاح Gemini (مجاني).', 'bad'); return; }
  saveSetting({ writingMode: b.dataset.writing });
});
$('#micSizes').addEventListener('click', e => {
  const b = e.target.closest('[data-mic-size]');
  if (b) saveSetting({ micSize: b.dataset.micSize });
});
$('#micColors').addEventListener('click', e => {
  const b = e.target.closest('[data-mic-color]');
  if (b) saveSetting({ micColor: b.dataset.micColor });
});
async function saveSetting(values) {
  try { setState(await call('settings.save', values)); } catch (e) { toast(e.message, 'bad'); }
}
on('mic.visible', v => { if (state) { state.micVisible = v; renderShared(); } });

/* =====================================================================
 *  صفحة الإملاء
 * ===================================================================== */
function engineWhere(key) {
  const e = engineByKey(key);
  if (!key) return '';
  if (!e) return 'غير موجود';
  if (!e.ready) return e.reason || 'غير جاهز';
  return e.kind === 'local' ? 'على جهازك' : [providerName(key), (PRICING[e.pricing] || [, ''])[1]].filter(Boolean).join(' · ');
}

function renderDictation() {
  const s = state.settings;
  $('#noEngineBanner').hidden = !!s.primary;
  $('#gpuBanner').hidden = !(state.usesLocal && state.gpuSupported && !state.gpuInstalled);
  $('#engPrimary').textContent = s.primary ? modelName(s.primary) : 'لم يتم الاختيار';
  $('#engPrimaryWhere').textContent = engineWhere(s.primary);
  $('#engFallback').textContent = s.useFallback && s.fallback ? modelName(s.fallback) : 'متوقف';
  $('#engFallbackWhere').textContent = s.useFallback ? engineWhere(s.fallback) : '';
  $$('#writingModes [data-writing]').forEach(b => b.classList.toggle('on', b.dataset.writing === (s.writingMode || 'normal')));
  renderHistory();
  renderTester(state.dictationState);
}

function renderHistory() {
  const list = state.history || [];
  const el = $('#history');
  if (!list.length) {
    el.innerHTML = `<div class="empty">${icon('mic')}<div>لا توجد نصوص بعد</div></div>`;
    $('#clearHistory').hidden = true;
    renderStats();
    return;
  }
  el.innerHTML = list.map(h => `
    <div class="history-item">
      <div class="history-body">
        <div class="history-text">${esc(h.text)}</div>
        <div class="history-meta">
          <span>${esc(fmtTime(h.time))}</span>
          <span>${esc(h.engine)}</span>
          <span>${(h.seconds || 0).toFixed(1)} ث</span>
          ${h.inserted ? `<span class="tag ok">${icon('check')}كُتب</span>` : '<span class="tag">هنا فقط</span>'}
        </div>
        ${h.note ? `<div class="history-note">${icon('info')}<span>${esc(h.note)}</span></div>` : ''}
      </div>
      <div class="history-actions">
        <button class="btn ghost sm icon" data-copy="${esc(h.id)}" title="نسخ">${icon('copy')}</button>
        <button class="btn ghost sm icon danger-text" data-delete="${esc(h.id)}" title="حذف">${icon('trash')}</button>
      </div>
    </div>`).join('');
  $('#clearHistory').hidden = false;
  renderStats();
}

function renderStats() {
  const list = state.history || [];
  const counts = list.map(h => (h.text || '').split(/\s+/).filter(Boolean).length);
  const total = counts.reduce((a, b) => a + b, 0);
  $('#historyCount').textContent = list.length ? list.length.toLocaleString('ar-EG') : '';
  $('#statWords').textContent = total.toLocaleString('ar-EG');
  $('#statCount').textContent = list.length ? `${list.length.toLocaleString('ar-EG')} نص` : '';
  const recent = counts.slice(0, 24).reverse();
  const max = Math.max(1, ...recent);
  $('#statSpark').innerHTML = recent.map(c => `<i style="height:${Math.max(8, Math.round(c / max * 100))}%"></i>`).join('');
}

$('#history').addEventListener('click', async e => {
  const b = e.target.closest('[data-copy]');
  const d = e.target.closest('[data-delete]');
  if (b) {
    const item = (state.history || []).find(h => String(h.id) === b.dataset.copy);
    if (item) call('clipboard.copy', { text: item.text }).then(() => toast('تم النسخ'));
  } else if (d) {
    try { state.history = await call('history.remove', { id: Number(d.dataset.delete) }); renderHistory(); }
    catch (err) { toast(err.message, 'bad'); }
  }
});

$('#clearHistory').addEventListener('click', async e => {
  if (!confirmInline(e.currentTarget, 'تأكيد مسح الكل؟')) return;
  try { state.history = await call('history.clear'); renderHistory(); toast('تم مسح السجل.'); }
  catch (err) { toast(err.message, 'bad'); }
});

on('dictation.history', item => {
  if (!state) return;
  state.history = [item, ...(state.history || []).filter(h => h.id !== item.id)].slice(0, 50);
  renderHistory();
});

function renderTester(dState) {
  const tile = $('#stageTile');
  tile.classList.toggle('recording', dState === 'Recording');
  tile.classList.toggle('processing', dState === 'Processing');
  $('#stateText').textContent =
    dState === 'Recording' ? 'أسمعك…' :
    dState === 'Processing' ? 'يكتب…' :
    'جاهز';
  stageSet('state', dState);
  renderStatus();
  if (dState !== 'Recording') { setMeter(0); }
}

$('#testMeter').innerHTML = '<i></i>'.repeat(22);

function setMeter(level) {
  stageSet('level', level);
  const bars = $$('#testMeter i');
  const n = bars.length;
  bars.forEach((b, i) => {
    const center = 1 - Math.abs(i - (n - 1) / 2) / (n / 2);
    const v = Math.min(1, level * 9) * (0.45 + 0.55 * center) * (0.75 + Math.random() * 0.5);
    b.style.height = `${4 + v * 28}px`;
  });
}

$('#testMic').addEventListener('click', async () => {
  const s = state?.dictationState;
  if (s === 'Processing') return;
  if (s === 'Recording') {
    if (!testing) { toast('زر المايك العائم يسجّل الآن. أوقفه أولًا.', 'info'); return; }
    call('dictation.testStop').catch(e => toast(e.message, 'bad'));
    return;
  }
  if (!state.settings.primary) { toast('اختر نموذجًا أولًا من صفحة النماذج.', 'bad'); return; }
  testing = true;
  $('#testResult').hidden = true;
  try { await call('dictation.testStart'); }
  catch (e) { testing = false; toast(e.message, 'bad'); }
});

on('dictation.state', s => {
  if (!state) return;
  state.dictationState = s;
  renderTester(s);
  if (s === 'Idle') setTimeout(() => { if (state.dictationState === 'Idle') { testing = false; renderTester('Idle'); } }, 50);
});
on('dictation.level', level => setMeter(level));
on('dictation.live', ({ text, interim }) => {
  if (!testing) return;
  const box = $('#testResult');
  box.hidden = false;
  box.classList.remove('error');
  $('#testText').innerHTML = esc(text || '') + (interim ? `<span class="interim">${text ? ' ' : ''}${esc(interim)}</span>` : '');
  $('#testCopy').hidden = !text;
});
on('dictation.test', ({ text, error }) => {
  const box = $('#testResult');
  box.hidden = false;
  box.classList.toggle('error', !!error);
  $('#testText').textContent = error || text || '';
  $('#testCopy').hidden = !!error;
});
$('#testCopy').addEventListener('click', () => call('clipboard.copy', { text: $('#testText').textContent }).then(() => toast('تم النسخ')));

/* =====================================================================
 *  صفحة تفريغ الملفات
 * ===================================================================== */
let currentFile = null;
let fileBusy = false;
let lastResult = null;

function renderFileEngines() {
  const sel = $('#fileEngine');
  const chosen = sel.value || state.settings.filesEngine || state.settings.primary;
  sel.innerHTML = engineOptions(chosen);
}

function setFile(info) {
  if (!info) return;
  currentFile = info;
  $('#fileName').textContent = info.name;
  $('#fileSize').textContent = `${info.sizeMb} ميجابايت`;
  $('#fileCard').hidden = false;
  $('#dropzone').classList.add('compact');
  $('#dropzone h3').textContent = 'اسحب ملفًا آخر إلى هنا لتغييره';
  $('#fileProgress').hidden = true;
}

$('#pickFile').addEventListener('click', e => { e.stopPropagation(); run(e.currentTarget, async () => setFile(await call('files.pick'))); });
$('#changeFile').addEventListener('click', e => run(e.currentTarget, async () => setFile(await call('files.pick'))));
$('#dropzone').addEventListener('click', e => { if ($('#dropzone').classList.contains('compact')) $('#pickFile').click(); });

// منع المتصفح من فتح الملف المسحوب في أي مكان بالصفحة
['dragover', 'drop'].forEach(t => document.addEventListener(t, e => e.preventDefault()));
const dz = $('#dropzone');
dz.addEventListener('dragenter', e => { e.preventDefault(); dz.classList.add('over'); });
dz.addEventListener('dragover', e => { e.preventDefault(); dz.classList.add('over'); });
dz.addEventListener('dragleave', e => { if (!dz.contains(e.relatedTarget)) dz.classList.remove('over'); });
dz.addEventListener('drop', async e => {
  e.preventDefault();
  dz.classList.remove('over');
  const files = e.dataTransfer?.files;
  if (!files || !files.length) return;
  try {
    const info = host ? await call('files.dropped', {}, files) : await Mock.dropped(files[0]);
    if (info) setFile(info); else toast('تعذّر قراءة الملف. استخدم زر «اختر ملفًا».', 'bad');
  } catch (err) { toast(err.message, 'bad'); }
});

$('#startFile').addEventListener('click', async () => {
  if (!currentFile || fileBusy) return;
  const engine = $('#fileEngine').value;
  if (!engine) { toast('اختر نموذجًا للتفريغ.', 'bad'); return; }
  fileBusy = true;
  $('#startFile').disabled = true;
  $('#changeFile').disabled = true;
  $('#fileProgress').hidden = false;
  setFileProgress(0, 'جارٍ التحضير…');
  try {
    const r = await call('files.transcribe', { path: currentFile.path, engine });
    lastResult = r;
    showResult(r);
    $('#summaryBox').hidden = true;
    toast('انتهى التفريغ وحُفظت نسخة تلقائيًا.');
    if (autoSummarize) { autoSummarize = false; summarize(); }
  } catch (e) {
    toast(e.message, 'bad', 7000);
  } finally {
    fileBusy = false;
    $('#startFile').disabled = false;
    $('#changeFile').disabled = false;
    $('#fileProgress').hidden = true;
  }
});

function setFileProgress(fraction, stage) {
  $('#fileBar').style.width = pct(fraction);
  $('#filePercent').textContent = pct(fraction);
  if (stage) $('#fileStage').textContent = stage;
}
on('file.progress', p => setFileProgress(p.fraction, p.stage));
$('#cancelFile').addEventListener('click', () => call('files.cancel'));

function showResult(r) {
  $('#resultCard').hidden = false;
  $('#resultText').value = r.text || '';
  const words = (r.text || '').split(/\s+/).filter(Boolean).length;
  $('#resultChips').innerHTML = [
    `<span class="tag">مدة الملف: ${esc(fmtDuration(r.durationSeconds))}</span>`,
    `<span class="tag">وقت التفريغ: ${esc(fmtDuration(r.elapsedSeconds))}</span>`,
    `<span class="tag">${words.toLocaleString('en-US')} كلمة</span>`,
    `<span class="tag free-tier">${esc(r.engine)}</span>`,
  ].join('');
  $('#exportSrt').title = r.hasTimestamps ? '' : 'التوقيتات تقريبية مع خدمات الإنترنت';
  setTimeout(() => $('#resultCard').scrollIntoView({ behavior: 'smooth', block: 'start' }), 60);
}

$('#copyResult').addEventListener('click', () => call('clipboard.copy', { text: $('#resultText').value }).then(() => toast('تم نسخ النص كاملًا')));
$$('[data-export]').forEach(b => b.addEventListener('click', e => run(e.currentTarget, async () => {
  const r = await call('files.export', { format: b.dataset.export, text: $('#resultText').value });
  if (r?.path) {
    toast('تم الحفظ');
    call('files.showInFolder', { path: r.path });
  }
})));
$('#showSaved').addEventListener('click', () => lastResult?.savedTo && call('files.showInFolder', { path: lastResult.savedTo }));

/* ===== تسجيل الاجتماعات ===== */
let meetingOn = false;
let autoSummarize = false;
const two = n => String(n).padStart(2, '0');

function renderMeeting(on) {
  meetingOn = on;
  $('#meetingTile').classList.toggle('recording', on);
  $('#meetingTimer').hidden = !on;
  $('#meetingLevel').hidden = !on;
  $('#meetingCancel').hidden = !on;
  $('#meetingMicRow').hidden = on;
  $('#meetingBtnText').textContent = on ? 'إيقاف وتفريغ' : 'تسجيل';
  if (!on) $('#meetingTimer').textContent = '00:00';
}

$('#meetingBtn').addEventListener('click', e => run(e.currentTarget, async () => {
  if (!meetingOn) {
    await call('meeting.start', { mic: $('#meetingMic').checked });
    renderMeeting(true);
    return;
  }
  $('#meetingBtnText').textContent = 'جارٍ التجهيز…';
  const info = await call('meeting.stop');
  renderMeeting(false);
  setFile(info);
  autoSummarize = true;
  $('#startFile').click();
}));
$('#meetingCancel').addEventListener('click', e => {
  if (!confirmInline(e.currentTarget, 'تأكيد حذف التسجيل؟')) return;
  call('meeting.cancel').then(() => renderMeeting(false));
});
on('meeting.tick', ({ seconds, level }) => {
  if (!meetingOn) renderMeeting(true);
  const h = Math.floor(seconds / 3600), m = Math.floor((seconds % 3600) / 60), sec = seconds % 60;
  $('#meetingTimer').textContent = (h ? h + ':' : '') + two(m) + ':' + two(sec);
  $('#meetingLevel i').style.width = `${Math.min(100, Math.round(level * 160))}%`;
});

async function summarize(button) {
  const text = $('#resultText').value.trim();
  if (!text) return;
  const box = $('#summaryBox');
  box.hidden = false;
  $('#summaryText').innerHTML = '<span class="spinner"></span>';
  try {
    const r = await call('files.summarize', { text });
    $('#summaryText').textContent = r.summary;
  } catch (e) {
    $('#summaryText').textContent = e.message;
  } finally {
    if (button) button.disabled = false;
  }
}
$('#summarizeBtn').addEventListener('click', e => { e.currentTarget.disabled = true; summarize(e.currentTarget); });
$('#copySummary').addEventListener('click', () => call('clipboard.copy', { text: $('#summaryText').textContent }).then(() => toast('تم النسخ')));

/* =====================================================================
 *  صفحة النماذج: على الجهاز
 * ===================================================================== */
const downloads = {}; // key -> {fraction, text}
let localData = null;

$$('.tab').forEach(t => t.addEventListener('click', () => showTab(t.dataset.tab)));
function showTab(name) {
  $$('.tab').forEach(t => t.classList.toggle('active', t.dataset.tab === name));
  $$('.tab-panel').forEach(p => p.classList.toggle('active', p.id === `tab-${name}`));
  if (name === 'online') loadProviders(); else loadLocal();
}
pageLoaders.models = () => { const active = $('.tab.active')?.dataset.tab || 'local'; active === 'online' ? loadProviders() : loadLocal(); };

async function loadLocal() {
  try { localData = await call('local.list'); renderLocal(); }
  catch (e) { toast(e.message, 'bad'); }
}

function renderLocal() {
  if (!localData) return;
  const g = localData.gpu || {};
  const gpuKey = 'gpu';
  const gpuDl = downloads[gpuKey];
  $('#gpuCard').innerHTML = `
    <div class="gpu-icon ${g.installed && g.driver ? 'on' : ''}">${icon('bolt')}</div>
    <div class="grow">
      <b>تسريع كرت الشاشة ${g.installed ? '<span class="tag ok">مفعّل</span>' : '<span class="tag warn">غير مفعّل</span>'}</b>
      <span class="muted sm">${esc(g.status || '')}</span>
      ${gpuDl ? progressHtml(gpuDl, gpuKey) : ''}
    </div>
    ${!g.installed && g.supported !== false && !gpuDl ? `<button class="btn primary" id="gpuInstall">${icon('download')}تفعيل (تنزيل ~40 ميجا)</button>` : ''}`;
  $('#gpuInstall')?.addEventListener('click', e => run(e.currentTarget, async () => {
    const r = await call('gpu.install');
    downloads[r.key] = { fraction: 0, text: '' };
    renderLocal();
  }));

  $('#localModels').innerHTML = localData.models.map(m => {
    const dl = downloads[m.file];
    const inUse = state && [state.settings.primary, state.settings.fallback].includes(`local|${m.file}`) ||
      (state?.engines || []).some(e => e.kind === 'local' && e.key.endsWith(`|${m.file}`) && [state.settings.primary, state.settings.fallback].includes(e.key));
    return `
    <div class="card model ${m.installed ? 'installed' : ''}" data-file="${esc(m.file)}">
      <div class="model-top">
        <span class="model-name">${esc(m.name)}</span>
        ${m.recommended ? '<span class="tag rec">موصى به</span>' : ''}
        ${m.installed ? `<span class="tag ok">${icon('check')}منزّل</span>` : ''}
        ${inUse ? '<span class="tag free-tier">مستخدم الآن</span>' : ''}
      </div>
      <div class="model-desc">${esc(m.description)}</div>
      ${dl ? progressHtml(dl, m.file) : ''}
      <div class="model-foot">
        <span class="size">${Number(m.sizeMb).toLocaleString('en-US')} MB</span>
        <span class="spacer"></span>
        ${m.installed
          ? `<button class="btn ghost sm danger-text" data-remove="${esc(m.file)}">${icon('trash')}حذف</button>`
          : dl ? '' : `<button class="btn ${m.recommended ? 'primary' : ''} sm" data-download="${esc(m.file)}">${icon('download')}تنزيل</button>`}
      </div>
    </div>`;
  }).join('');

  const folder = $('#openModels');
  folder.textContent = localData.folder;
  folder.dataset.folder = 'models';
}

function progressHtml(dl, key) {
  return `<div class="progress-box" data-progress="${esc(key)}">
    <div class="progress-top"><span class="dl-text">${esc(dl.text || 'جارٍ البدء…')}</span><b class="dl-pct">${dl.fraction == null ? '' : pct(dl.fraction)}</b></div>
    <div class="bar ${dl.fraction == null ? 'indeterminate' : ''}"><i style="width:${pct(dl.fraction)}"></i></div>
    <button class="btn ghost sm danger-text" data-cancel="${esc(key)}">إلغاء</button>
  </div>`;
}

$('#tab-local').addEventListener('click', async e => {
  const d = e.target.closest('[data-download]');
  const r = e.target.closest('[data-remove]');
  const c = e.target.closest('[data-cancel]');
  if (d) {
    run(d, async () => {
      const res = await call('local.download', { file: d.dataset.download });
      downloads[res.key] = { fraction: 0, text: '' };
      renderLocal();
    });
  } else if (r) {
    if (!confirmInline(r, 'تأكيد الحذف؟')) return;
    run(r, async () => { localData = await call('local.remove', { file: r.dataset.remove }); toast('تم حذف النموذج'); await refreshState(); renderLocal(); });
  } else if (c) {
    call('local.cancel', { key: c.dataset.cancel });
  }
});

// زر يحتاج ضغطة ثانية للتأكيد (بدلًا من نوافذ التأكيد)
function confirmInline(btn, text) {
  if (btn.dataset.armed) return true;
  btn.dataset.armed = '1';
  const old = btn.innerHTML;
  btn.innerHTML = esc(text);
  setTimeout(() => { if (btn.isConnected) { delete btn.dataset.armed; btn.innerHTML = old; } }, 3000);
  return false;
}

on('download.progress', p => {
  const dl = downloads[p.key] || (downloads[p.key] = {});
  dl.fraction = p.fraction; dl.text = p.text;
  const box = document.querySelector(`[data-progress="${CSS.escape(p.key)}"]`);
  if (box) {
    $('.dl-text', box).textContent = p.text || '';
    $('.dl-pct', box).textContent = p.fraction == null ? '' : pct(p.fraction);
    $('.bar', box).classList.toggle('indeterminate', p.fraction == null);
    $('.bar i', box).style.width = pct(p.fraction);
  } else if (p.key === urlKey) {
    $('#urlProgressText').textContent = p.text || '';
    $('#urlBar').style.width = pct(p.fraction);
  } else if (localData) {
    renderLocal();
  }
});
on('download.done', d => {
  delete downloads[d.key];
  if (d.key === urlKey) { urlKey = null; $('#urlProgress').hidden = true; $('#modelUrl').value = ''; }
  setState(d.state);
  toast(d.key === 'gpu' ? 'تم تفعيل تسريع كرت الشاشة.' : 'اكتمل تنزيل النموذج وأصبح جاهزًا.');
  loadLocal();
});
on('download.error', d => {
  delete downloads[d.key];
  if (d.key === urlKey) { urlKey = null; $('#urlProgress').hidden = true; }
  toast(d.message, 'bad', 6000);
  if (localData) renderLocal();
});

let urlKey = null;
$('#addModelUrl').addEventListener('click', e => run(e.currentTarget, async () => {
  const url = $('#modelUrl').value.trim();
  if (!url) { toast('الصق رابط النموذج أولًا.', 'bad'); return; }
  const r = await call('local.download', { url });
  urlKey = r.key;
  downloads[r.key] = { fraction: 0, text: '' };
  $('#urlProgress').hidden = false;
  $('#urlProgressText').textContent = `جارٍ تنزيل ${r.key}…`;
  $('#urlBar').style.width = '0';
}));
$('#addModelFile').addEventListener('click', e => run(e.currentTarget, async () => {
  const r = await call('local.addFile');
  if (r?.file) { toast(`تمت إضافة ${r.file}`); await refreshState(); loadLocal(); }
}));

/* =====================================================================
 *  صفحة النماذج: خدمات الإنترنت
 * ===================================================================== */
let providers = [];
const fetched = {}; // id -> [models]
const providerMsg = {}; // id -> {cls, text}

async function loadProviders() {
  try { providers = await call('providers.list'); renderProviders(); }
  catch (e) { toast(e.message, 'bad'); }
}

const LOGO_COLORS = ['#4f5bea', '#0ea5e9', '#10b981', '#f59e0b', '#ef4444', '#8b5cf6', '#ec4899', '#14b8a6', '#6366f1'];
function logoColor(id) { let h = 0; for (const c of id) h = (h * 31 + c.charCodeAt(0)) >>> 0; return LOGO_COLORS[h % LOGO_COLORS.length]; }

function renderProviders() {
  const order = { free: 0, 'free-tier': 1, trial: 2, unknown: 3, paid: 4 };
  const list = [...providers].sort((a, b) => (b.hasKey - a.hasKey) || ((order[a.pricing] ?? 3) - (order[b.pricing] ?? 3)));
  $('#providers').innerHTML = list.map(p => {
    const ready = !p.needsKey || p.hasKey;
    const msg = providerMsg[p.id];
    const keys = p.keys || [];
    const addRow = (placeholder, label) => `<div class="key-row">
            <input type="password" placeholder="${placeholder}" data-keyinput="${esc(p.id)}" autocomplete="off" spellcheck="false">
            <button class="btn primary" data-savekey="${esc(p.id)}">${icon('check')}${label}</button>
            ${p.keyUrl ? `<button class="btn" data-url="${esc(p.keyUrl)}">${icon('link')}احصل على مفتاح</button>` : ''}
          </div>`;
    const ownerKey = p.keyFromName ? `مفتاح ${esc(p.keyFromName)}` : 'المفتاح';
    const keyPart = !p.needsKey
      ? `<div class="key-saved">${icon('check')}لا تحتاج مفتاحًا</div>`
      : !keys.length
        ? addRow(p.keyFromName ? `الصق مفتاح ${esc(p.keyFromName)} هنا` : 'الصق المفتاح هنا', 'حفظ')
        : `<div class="key-list">
            ${keys.map((k, i) => `
            <div class="key-item">
              <div class="key-saved">${icon('key')}${keys.length > 1 ? `${ownerKey} ${i + 1}` : (p.keyFromName ? `يستخدم مفتاح ${esc(p.keyFromName)} المحفوظ` : 'المفتاح محفوظ')} <code>${esc(k.masked)}</code>
                ${k.status ? `<span class="tag ${k.status === 'يعمل' ? 'ok' : 'warn'}">${esc(k.status)}</span>` : ''}</div>
              <button class="btn sm" data-test="${esc(k.slot)}">${icon('refresh')}اختبار</button>
              <button class="btn ghost sm danger-text" data-unkey="${esc(k.slot)}">${icon('trash')}إزالة</button>
            </div>`).join('')}
            <details class="add-key">
              <summary title="يُستخدم تلقائيًا إذا تجاوز الأول حده">${icon('plus')}إضافة مفتاح آخر</summary>
              ${addRow(p.keyFromName ? `الصق مفتاح ${esc(p.keyFromName)} آخر` : 'الصق المفتاح الإضافي هنا', 'إضافة')}
            </details>
          </div>`;
    const list = fetched[p.id];
    return `
    <div class="card provider ${ready ? 'ready' : ''}" data-provider="${esc(p.id)}">
      <div class="provider-head">
        <div class="provider-logo" style="background:${logoColor(p.id)}">${esc((p.name || p.id).trim()[0].toUpperCase())}</div>
        <div class="provider-title"><b>${esc(p.name)}</b><p>${esc(p.description || '')}</p></div>
        ${pricingTag(p.pricing, p.pricingLabel)}
        ${ready ? `<span class="tag ok">${icon('check')}جاهز</span>` : ''}
      </div>
      ${p.pricingNote ? `<div class="provider-note">${esc(p.pricingNote)}</div>` : ''}
      ${keyPart}
      ${msg ? `<div class="status-msg ${msg.cls}">${esc(msg.text)}</div>` : ''}
      <div class="provider-models">
        <div class="card-head"><b class="sm">النماذج</b>${ready && p.models.length ? '<span class="muted sm">انقر على نموذج لاستخدامه في الإملاء</span>' : ''}</div>
        <div class="model-chips">
          ${p.models.length ? p.models.map(m => {
            const key = `${p.id}|${m.id}`;
            const active = state?.settings?.primary === key;
            return `<span class="model-chip ${active ? 'active' : ''}">
              <button class="chip-use" data-use="${esc(m.id)}" ${ready ? '' : 'disabled'} title="${active ? 'هذا هو نموذج الإملاء الحالي' : 'استخدمه للإملاء'}">${active ? icon('check') : ''}${esc(m.id)}</button>
              ${m.builtIn ? '' : `<button class="chip-x" title="حذف" data-delmodel="${esc(m.id)}">${icon('x')}</button>`}
            </span>`;
          }).join('') : '<span class="muted sm">لا توجد نماذج. أضف اسم نموذج.</span>'}
        </div>
        <div class="add-model">
          ${list ? `<select data-fetchedsel>${list.map(m => `<option>${esc(m)}</option>`).join('')}</select>
                    <button class="btn sm" data-addfetched>${icon('plus')}إضافة المختار</button>
                    ${list.length > 1 ? `<button class="btn ghost sm" data-addall>${icon('plus')}إضافة الكل (${list.length})</button>` : ''}
                    <button class="btn ghost sm" data-closefetch>إغلاق</button>`
                 : `<input placeholder="اسم نموذج آخر (اختياري)" data-modelname>
                    <button class="btn sm" data-addmodel>${icon('plus')}إضافة</button>
                    ${p.canListModels && ready ? `<button class="btn ghost sm" data-fetch>${icon('refresh')}عرض النماذج المتاحة</button>` : ''}`}
        </div>
      </div>
    </div>`;
  }).join('');
}

$('#providers').addEventListener('click', async e => {
  const card = e.target.closest('[data-provider]');
  if (!card) return;
  const id = card.dataset.provider;
  const t = e.target.closest('button');
  if (!t) return;

  if (t.dataset.url) { call('app.openUrl', { url: t.dataset.url }); return; }

  if (t.dataset.savekey !== undefined) {
    const key = $('[data-keyinput]', card).value.trim();
    if (!key) { toast('الصق المفتاح أولًا.', 'bad'); return; }
    t.innerHTML = '<span class="spinner" style="width:16px;height:16px;border-width:2px"></span> جارٍ الاختبار…';
    await run(t, async () => {
      const r = await call('providers.saveKey', { id, key });
      providers = r.providers;
      providerMsg[id] = statusMsg(r);
      setState(r.state);
      renderProviders();
      toast(r.status === 'Ok' ? 'تم حفظ المفتاح وهو يعمل.' : r.message, r.status === 'Ok' ? 'ok' : r.status === 'Warning' ? 'info' : 'bad', 6000);
    });
    renderProviders();
    return;
  }
  if (t.dataset.test !== undefined) {
    await run(t, async () => { const r = await call('providers.testKey', { id, slot: t.dataset.test }); providers = r.providers || providers; providerMsg[id] = statusMsg(r); renderProviders(); });
    return;
  }
  if (t.dataset.unkey !== undefined) {
    if (!confirmInline(t, 'تأكيد الإزالة؟')) return;
    await run(t, async () => { const r = await call('providers.removeKey', { id, slot: t.dataset.unkey }); providers = r.providers; delete providerMsg[id]; setState(r.state); renderProviders(); });
    return;
  }
  if (t.dataset.fetch !== undefined) {
    await run(t, async () => {
      const list = await call('providers.fetchModels', { id });
      if (!list?.length) { toast('لا توجد نماذج متاحة الآن لهذه الخدمة.', 'info'); return; }
      fetched[id] = list; renderProviders();
    });
    return;
  }
  const addModel = async model => {
    if (!model) { toast('اكتب اسم النموذج.', 'bad'); return; }
    await run(t, async () => { const r = await call('providers.addModel', { id, model }); providers = r.providers; delete fetched[id]; setState(r.state); renderProviders(); toast(`تمت إضافة ${model}`); });
  };
  if (t.dataset.addmodel !== undefined) return addModel($('[data-modelname]', card).value.trim());
  if (t.dataset.addfetched !== undefined) return addModel($('[data-fetchedsel]', card).value);
  if (t.dataset.closefetch !== undefined) { delete fetched[id]; renderProviders(); return; }
  if (t.dataset.addall !== undefined) {
    const known = new Set((providers.find(x => x.id === id)?.models || []).map(m => m.id));
    const toAdd = fetched[id].filter(m => !known.has(m));
    await run(t, async () => {
      let r = null;
      for (const model of toAdd) r = await call('providers.addModel', { id, model });
      if (r) { providers = r.providers; setState(r.state); }
      delete fetched[id]; renderProviders();
      toast(toAdd.length ? `تمت إضافة ${toAdd.length} نموذج` : 'كل النماذج مضافة بالفعل', 'ok');
    });
    return;
  }
  if (t.dataset.use) {
    const key = `${id}|${t.dataset.use}`;
    if (state?.settings?.primary === key) return;
    await run(t, async () => {
      setState(await call('settings.save', { primary: key }));
      renderProviders();
      toast(`الإملاء الآن بـ ${t.dataset.use}. جرّبه من صفحة «الإملاء».`);
    });
    return;
  }
  if (t.dataset.delmodel) {
    await run(t, async () => { const r = await call('providers.removeModel', { id, model: t.dataset.delmodel }); providers = r.providers; setState(r.state); renderProviders(); });
  }
});
$('#providers').addEventListener('keydown', e => {
  if (e.key !== 'Enter') return;
  const card = e.target.closest('[data-provider]');
  if (e.target.matches('[data-keyinput]')) $('[data-savekey]', card)?.click();
  if (e.target.matches('[data-modelname]')) $('[data-addmodel]', card)?.click();
});

function statusMsg(r) {
  const cls = r.status === 'Ok' ? 'ok' : r.status === 'Warning' ? '' : 'bad';
  return { cls, text: r.message || (r.status === 'Ok' ? 'المفتاح يعمل.' : '') };
}

/* =====================================================================
 *  الإعدادات
 * ===================================================================== */
function renderSettings() {
  const s = state.settings;
  $('#setPrimary').innerHTML = engineOptions(s.primary);
  $('#setFallback').innerHTML = engineOptions(s.fallback, { allowNone: true, noneText: 'بدون بديل' });
  $('#setFiles').innerHTML = engineOptions(s.filesEngine || s.primary);
  const mics = state.microphones || [];
  $('#setMicDevice').innerHTML = `<option value="-1">الميكروفون الافتراضي في ويندوز</option>` +
    mics.map((m, i) => `<option value="${i}">${esc(m)}</option>`).join('');
  $('#setMicDevice').value = String(s.microphoneDevice ?? -1);
  $('#setMicColor').innerHTML = MIC_COLORS.map(m => `<option value="${m.id}">${m.name}</option>`).join('');
  $$('[data-setting]').forEach(el => {
    const name = el.dataset.setting;
    if (['primary', 'fallback', 'filesEngine', 'microphoneDevice'].includes(name)) return;
    const value = name === 'startWithWindows' ? state.startWithWindows : s[name];
    if (el.type === 'checkbox') el.checked = !!value; else el.value = value ?? '';
  });
  $$('[data-theme-set]').forEach(b => b.classList.toggle('on', b.dataset.themeSet === currentTheme()));
  renderProfiles();
  $('#dataFolder').textContent = state.dataFolder;
  $('#versionText').textContent = state.version ? `الإصدار ${state.version}` : '';
}

$$('[data-setting]').forEach(el => el.addEventListener('change', async () => {
  const name = el.dataset.setting;
  let value = el.type === 'checkbox' ? el.checked : el.value;
  if (name === 'microphoneDevice') value = Number(value);
  try {
    setState(await call('settings.save', { [name]: value }));
    toast('تم الحفظ', 'ok', 1600);
  } catch (e) { toast(e.message, 'bad'); }
}));

/* ===== حسب البرنامج ===== */
const STYLES = [['default', 'عادي'], ['chat', 'محادثة: بدون نقطة'], ['document', 'مستند: كل جملة في سطر'], ['code', 'كود: بدون إضافات']];
const WRITINGS = [['', 'الكتابة: حسب الاختيار العام'], ['normal', 'كما قلته'], ['formal', 'صياغة رسمية'], ['email', 'إيميل'], ['english', 'ترجمة للإنجليزي']];
let lastMatchInput = null;
let profilesTimer = null;

function renderProfiles() {
  const box = $('#profiles');
  if (box.contains(document.activeElement)) return; // لا نعيد الرسم أثناء الكتابة
  const opts = (list, value) => list.map(([v, t]) => `<option value="${v}" ${v === (value ?? '') ? 'selected' : ''}>${t}</option>`).join('');
  box.innerHTML = (state.profiles || []).map(p => `
    <div class="profile ${p.enabled ? '' : 'off'}" data-id="${esc(p.id)}">
      <input class="p-name" value="${esc(p.name)}" placeholder="الاسم">
      <select class="p-style">${opts(STYLES, p.style)}</select>
      <select class="p-writing">${opts(WRITINGS, p.writing)}</select>
      <label class="switch" title="تشغيل / إيقاف"><input type="checkbox" class="p-enabled" ${p.enabled ? 'checked' : ''}><span></span></label>
      <button class="btn ghost sm icon danger-text p-delete" title="حذف">${icon('trash')}</button>
      <input class="p-match" value="${esc((p.match || []).join(', '))}" placeholder="WhatsApp, WINWORD, title:Gmail" spellcheck="false">
    </div>`).join('');
  const recent = state.recentApps || [];
  $('#recentApps').innerHTML = recent.length
    ? `<span>آخر البرامج:</span>${recent.map(a => `<button data-app="${esc(a)}" title="إضافة لآخر خانة تعديل">${esc(a)}</button>`).join('')}`
    : '';
}

function collectProfiles() {
  return $$('#profiles .profile').map(row => ({
    id: row.dataset.id,
    name: $('.p-name', row).value.trim() || 'بدون اسم',
    style: $('.p-style', row).value,
    writing: $('.p-writing', row).value || null,
    enabled: $('.p-enabled', row).checked,
    match: $('.p-match', row).value.split(/[,،]/).map(x => x.trim()).filter(Boolean),
  }));
}

function saveProfiles(now = false) {
  clearTimeout(profilesTimer);
  profilesTimer = setTimeout(async () => {
    try { const s = await call('profiles.save', { profiles: collectProfiles() }); state.profiles = s.profiles; toast('تم الحفظ', 'ok', 1400); }
    catch (e) { toast(e.message, 'bad'); }
  }, now ? 0 : 600);
}

$('#profiles').addEventListener('input', () => saveProfiles());
$('#profiles').addEventListener('change', e => {
  const row = e.target.closest('.profile');
  if (e.target.classList.contains('p-enabled')) row.classList.toggle('off', !e.target.checked);
  saveProfiles(true);
});
$('#profiles').addEventListener('focusin', e => { if (e.target.classList.contains('p-match')) lastMatchInput = e.target; });
$('#profiles').addEventListener('click', e => {
  const del = e.target.closest('.p-delete');
  if (!del) return;
  if (!confirmInline(del, 'تأكيد؟')) return;
  del.closest('.profile').remove();
  saveProfiles(true);
});
$('#recentApps').addEventListener('click', e => {
  const b = e.target.closest('[data-app]');
  if (!b) return;
  const input = lastMatchInput && lastMatchInput.isConnected ? lastMatchInput : $$('#profiles .p-match').pop();
  if (!input) return;
  const items = input.value.split(/[,،]/).map(x => x.trim()).filter(Boolean);
  if (!items.includes(b.dataset.app)) items.push(b.dataset.app);
  input.value = items.join(', ');
  saveProfiles(true);
});
$('#addProfile').addEventListener('click', () => {
  const list = collectProfiles();
  list.push({ id: Math.random().toString(16).slice(2, 10), name: 'إعداد جديد', style: 'chat', writing: null, enabled: true, match: [] });
  call('profiles.save', { profiles: list }).then(s => { state.profiles = s.profiles; renderProfiles(); $$('#profiles .p-name').pop()?.focus(); });
});
$('#resetProfiles').addEventListener('click', e => {
  if (!confirmInline(e.currentTarget, 'تأكيد الإرجاع؟')) return;
  call('profiles.reset').then(s => { state.profiles = s.profiles; renderProfiles(); toast('رجعت الإعدادات الافتراضية'); });
});

/* =====================================================================
 *  البداية
 * ===================================================================== */
async function refreshState() { setState(await call('app.state')); }

/* ===== التعلّم من Gemini ===== */
let learning = null;
pageLoaders.settings = () => loadLearning();
on('learning.updated', () => { if ($('#page-settings').classList.contains('active')) loadLearning(); });

async function loadLearning() {
  try { learning = await call('learning.get'); renderLearning(); } catch (e) { toast(e.message, 'bad'); }
}

function renderLearning() {
  if (!learning) return;
  const rules = learning.rules || [];
  const terms = learning.terms || [];
  $('#learnStats').innerHTML = [
    `<span class="tag ok">${learning.activeCount} تصحيح مُطبَّق</span>`,
    `<span class="tag">${rules.length - learning.activeCount} في انتظار التأكيد</span>`,
    `<span class="tag free-tier">${learning.termCount} كلمة إنجليزية</span>`,
    learning.localModel ? `<span class="tag">يتعلّم: ${esc(learning.localModel.replace(/^على جهازك · /, ''))}</span>` : '<span class="tag warn">نزّل نموذجًا على جهازك ليتعلّم</span>',
  ].join('');
  $('#learnRules').innerHTML = rules.length
    ? rules.map(r => `<div class="learn-rule ${r.active ? '' : 'pending'}" title="${r.active ? 'يُطبَّق' : 'يحتاج تكرارًا آخر قبل التطبيق'}">
        <span class="from">${esc(r.from)}</span><span class="arrow">←</span><span class="to">${esc(r.to)}</span>
        <span class="muted sm">×${r.count}</span><span class="spacer"></span>
        <button class="btn ghost sm icon danger-text" data-unrule="${esc(r.from)}" data-to="${esc(r.to)}" title="حذف">${icon('trash')}</button>
      </div>`).join('')
    : '<div class="muted sm">لم يتعلّم تصحيحات بعد. استخدم الإملاء مع Gemini وسيبدأ التعلّم تلقائيًا.</div>';
  $('#learnTerms').innerHTML = terms.map(t => `<span class="model-chip"><button class="chip-use" disabled>${esc(t.word)}</button><button class="chip-x" title="حذف" data-unterm="${esc(t.word)}">${icon('x')}</button></span>`).join('');
}

$('#learningCard').addEventListener('click', async e => {
  const rule = e.target.closest('[data-unrule]');
  const term = e.target.closest('[data-unterm]');
  try {
    if (rule) { learning = await call('learning.removeRule', { from: rule.dataset.unrule, to: rule.dataset.to }); renderLearning(); }
    else if (term) { learning = await call('learning.removeTerm', { word: term.dataset.unterm }); renderLearning(); }
  } catch (err) { toast(err.message, 'bad'); }
});
$('#learnClear').addEventListener('click', async e => {
  if (!confirmInline(e.currentTarget, 'تأكيد المسح؟')) return;
  try { learning = await call('learning.clear'); renderLearning(); toast('تم مسح ما تعلّمه.'); } catch (err) { toast(err.message, 'bad'); }
});

$('#desktopShortcut').addEventListener('click', e => run(e.currentTarget, async () => {
  await call('app.desktopShortcut');
  toast('تم إنشاء الاختصار على سطح المكتب.');
}));

async function start() {
  try { await refreshState(); }
  catch (e) { toast(e.message, 'bad', 8000); }
  const initial = new URLSearchParams(location.search).get('page');
  if (initial) go(initial);
}

/* =====================================================================
 *  بيانات تجريبية للمعاينة في متصفح عادي (لا تُستخدم داخل البرنامج)
 * ===================================================================== */
const Mock = (() => {
  if (host) return null;
  const wait = ms => new Promise(r => setTimeout(r, ms));
  const emit = (name, data) => receive({ type: 'event', name, data });
  const now = Date.now();
  const s = {
    version: '1.0.0',
    dataFolder: 'D:\\my pro\\مساعد الإملاء\\VoiceType App\\data',
    micVisible: true,
    hotkey: 'Ctrl + Alt + Shift + M',
    dictationState: 'Idle',
    startWithWindows: false,
    usesLocal: true, gpuSupported: true, gpuInstalled: false,
    microphones: ['Microphone Array (Realtek Audio)', 'Headset (WH-1000XM4)'],
    hasGeminiKey: true,
    recentApps: ['WINWORD', 'chrome', 'WhatsApp', 'Code'],
    profiles: [
      { id: 'chat', name: 'المحادثات', style: 'chat', writing: null, enabled: true, match: ['WhatsApp', 'Telegram', 'Messenger', 'title:WhatsApp'] },
      { id: 'docs', name: 'المستندات', style: 'document', writing: null, enabled: true, match: ['WINWORD', 'notepad', 'title:Google Docs'] },
      { id: 'mail', name: 'البريد', style: 'document', writing: 'email', enabled: true, match: ['OUTLOOK', 'title:Gmail'] },
      { id: 'code', name: 'الأكواد', style: 'code', writing: null, enabled: true, match: ['Code', 'Cursor', 'WindowsTerminal'] },
    ],
    history: [
      { id: 3, time: new Date(now - 60000).toISOString(), text: 'تمام يا أستاذ أحمد، هبعتلك الملف النهارده بالليل إن شاء الله.', engine: 'Google Gemini', inserted: true, seconds: 1.8, note: 'Google Gemini Transcribe (المفتاح 1): تجاوزت الحد المسموح لدى المزود. فاستُخدم مفتاح آخر.' },
      { id: 2, time: new Date(now - 600000).toISOString(), text: 'محتاجين نراجع الـ API قبل ما نرفع الـ update على السيرفر.', engine: 'Google Gemini', inserted: true, seconds: 2.1 },
      { id: 1, time: new Date(now - 3600000).toISOString(), text: 'اجتماع بكرة الساعة عشرة الصبح في المكتب.', engine: 'على جهازك · Whisper large-v3', inserted: false, seconds: 8.7 },
    ],
    settings: { language: 'ar', textMode: 'General', lineBreaks: 'Sentences', learnFromOnline: true, fixMixedDirection: true, arabicStartsRight: true, liveTyping: false, writingMode: 'normal', useAppProfiles: true, primary: 'gemini|gemini-3.5-transcribe', fallback: 'local|ggml-large-v3-q5_0.bin', useFallback: true, filesEngine: 'gemini|gemini-3.5-transcribe', sounds: true, microphoneDevice: -1, micSize: 'small', micColor: 'gold', micAlwaysOnTop: true, micDimWhenIdle: false, theme: new URLSearchParams(location.search).get('theme') || 'spotlight' },
    engines: [
      { key: 'local|ggml-large-v3-q5_0.bin', label: 'على جهازك · Whisper large-v3 (مضغوط)', kind: 'local', pricing: 'free', ready: true },
      { key: 'gemini|gemini-3.5-transcribe', label: 'Google Gemini · gemini-3.5-transcribe', kind: 'online', pricing: 'free-tier', ready: true },
      { key: 'groq|whisper-large-v3-turbo', label: 'Groq · whisper-large-v3-turbo', kind: 'online', pricing: 'free-tier', ready: false, reason: 'أضف المفتاح أولًا' },
      { key: 'openai|gpt-4o-transcribe', label: 'OpenAI · gpt-4o-transcribe', kind: 'online', pricing: 'paid', ready: false, reason: 'أضف المفتاح أولًا' },
    ],
  };
  const local = {
    folder: 'D:\\my pro\\مساعد الإملاء\\VoiceType App\\data\\models',
    gpu: { supported: true, installed: false, driver: true, status: 'يعمل على المعالج (CPU). يمكنك تفعيل تسريع كرت الشاشة لسرعة أكبر.' },
    models: [
      { file: 'ggml-large-v3-turbo-q5_0.bin', name: 'Whisper Turbo (مضغوط)', sizeMb: 574, description: 'أفضل توازن بين الدقة والسرعة. مناسب لمعظم الأجهزة.', recommended: true, installed: false },
      { file: 'ggml-large-v3-turbo-q8_0.bin', name: 'Whisper Turbo (دقة أعلى)', sizeMb: 874, description: 'دقة أعلى قليلًا وحجم أكبر.', recommended: false, installed: false },
      { file: 'ggml-large-v3-turbo.bin', name: 'Whisper Turbo (كامل)', sizeMb: 1624, description: 'النسخة الكاملة بدون ضغط.', recommended: false, installed: false },
      { file: 'ggml-large-v3-q5_0.bin', name: 'Whisper large-v3 (مضغوط)', sizeMb: 1081, description: 'الأدق للعربية لكنه أبطأ.', recommended: false, installed: true },
    ],
  };
  const providers = [
    { id: 'gemini', name: 'Google Gemini', description: 'نموذج Google لتفريغ الكلام، ممتاز للعامية المصرية.', pricing: 'free-tier', pricingLabel: 'مجاني بحدود', pricingNote: 'مجاني مع حد للطلبات في الدقيقة.', keyUrl: 'https://aistudio.google.com/apikey', needsKey: true, hasKey: true, masked: 'AIza••••x9Qk',
      keys: [{ slot: 'gemini', masked: 'AIza••••x9Qk', status: 'تجاوز الحد المجاني — يُتخطى حتى 14:20' }, { slot: 'gemini#2', masked: 'AIza••••m3Tq', status: 'يعمل' }], canListModels: true, models: [{ id: 'gemini-3.5-transcribe', builtIn: true }] },
    { id: 'openrouter-free', name: 'OpenRouter — نماذج مجانية', description: 'يستخدم نفس مفتاح OpenRouter. يعرض النماذج المجانية التي تفهم الصوت، وتستطيع التبديل بينها.', pricing: 'free-tier', pricingLabel: 'مجاني بحدود', pricingNote: 'مجاني: 50 طلبًا في اليوم و20 في الدقيقة.', keyUrl: 'https://openrouter.ai/settings/keys', needsKey: true, hasKey: true, keyFromName: 'OpenRouter', masked: 'sk-o••••7f2a', keys: [{ slot: 'openrouter', masked: 'sk-o••••7f2a', status: null }], canListModels: true, models: [{ id: 'nvidia/nemotron-3-nano-omni-30b-a3b-reasoning:free', builtIn: true }, { id: 'thinkingmachines/inkling:free', builtIn: true }, { id: 'thinkingmachines/inkling-small:free', builtIn: true }] },
    { id: 'groq', name: 'Groq', description: 'Whisper سريع جدًا على الإنترنت.', pricing: 'free-tier', pricingLabel: 'مجاني بحدود', pricingNote: 'حد يومي مجاني.', keyUrl: 'https://console.groq.com/keys', needsKey: true, hasKey: false, canListModels: true, models: [{ id: 'whisper-large-v3-turbo', builtIn: true }, { id: 'whisper-large-v3', builtIn: true }] },
    { id: 'huggingface', name: 'Hugging Face', description: 'نماذج مفتوحة عبر واجهة Hugging Face.', pricing: 'free-tier', pricingLabel: 'مجاني بحدود', keyUrl: 'https://huggingface.co/settings/tokens', needsKey: true, hasKey: false, canListModels: false, models: [{ id: 'openai/whisper-large-v3', builtIn: true }] },
    { id: 'openai', name: 'OpenAI', description: 'نماذج OpenAI للتفريغ.', pricing: 'paid', pricingLabel: 'مدفوع', keyUrl: 'https://platform.openai.com/api-keys', needsKey: true, hasKey: false, canListModels: true, models: [{ id: 'gpt-4o-transcribe', builtIn: true }] },
  ];
  const sampleText = 'السلام عليكم ورحمة الله. النهارده هنتكلم عن خطة المشروع للشهر الجاي، وإزاي هنقسم الشغل على الفريق.\n\nأول حاجة محتاجين نخلص الواجهة الجديدة قبل يوم عشرين، وبعدها نبدأ الاختبارات مع العملاء. لو في أي مشكلة في الـ API هنرجع للفريق التقني على طول.\n\nتاني حاجة، الميزانية: اتفقنا إن المصاريف تفضل زي ما هي لحد نهاية الربع، ونراجعها تاني في الاجتماع الجاي.';
  return {
    async dropped(file) { return { path: 'C:\\' + file.name, name: file.name, sizeMb: +(file.size / 1048576).toFixed(1) }; },
    async handle({ method, params }) {
      await wait(120);
      switch (method) {
        case 'app.state': return structuredClone(s);
        case 'settings.save': Object.assign(s.settings, params); if ('startWithWindows' in params) s.startWithWindows = params.startWithWindows; return structuredClone(s);
        case 'mic.setVisible': s.micVisible = params.visible; return null;
        case 'history.remove': s.history = s.history.filter(h => h.id !== params.id); return structuredClone(s.history);
        case 'history.clear': s.history = []; return [];
        case 'profiles.save': s.profiles = params.profiles; return structuredClone(s);
        case 'profiles.reset': return structuredClone(s);
        case 'meeting.start': (async () => { for (let i = 0; i < 400 && meetingOn !== null; i++) { await wait(1000); if (!meetingOn) break; emit('meeting.tick', { seconds: i + 1, level: Math.random() * 0.5 }); } })(); return { recording: true };
        case 'meeting.stop': return { path: 'C:\\اجتماع 2026-10-13 10-30.wav', name: 'اجتماع 2026-10-13 10-30.wav', sizeMb: 24.6, seconds: 812 };
        case 'files.summarize': await wait(600); return { summary: 'الملخص:\nناقش الفريق خطة الشهر الجاي وتقسيم الشغل.\n\nأهم النقاط:\n- الواجهة الجديدة قبل يوم ٢٠\n- مراجعة الـ API\n\nالقرارات:\n- الميزانية ثابتة لنهاية الربع\n\nالمهام:\n- إنهاء الواجهة — أحمد — يوم ٢٠\n- مراجعة الـ API — الفريق التقني — غير محدد', savedTo: 'x' };
        case 'learning.get': return { enabled: true, localModel: 'على جهازك · Whisper Turbo (دقة أعلى)', activeCount: 2, minCount: 2, termCount: 14,
          rules: [{ from: 'ورك سبيس', to: 'Workspace', count: 4, active: true }, { from: 'جيت هب', to: 'GitHub', count: 2, active: true }, { from: 'ابديت', to: 'update', count: 1, active: false }],
          terms: ['Workspace', 'API', 'GitHub', 'update', 'Python', 'Gemini', 'bug', 'meeting'].map((w, i) => ({ word: w, count: 9 - i })) };
        case 'local.list': return structuredClone(local);
        case 'providers.list': return structuredClone(providers);
        case 'files.pick': return { path: 'C:\\محاضرة الاجتماع.mp3', name: 'محاضرة الاجتماع.mp3', sizeMb: 18.4 };
        case 'files.transcribe':
          for (let i = 0; i <= 10; i++) { emit('file.progress', { fraction: i / 10, stage: `جارٍ التفريغ… الجزء ${Math.min(i + 1, 10)} من 10` }); await wait(150); }
          return { text: sampleText, segments: 12, hasTimestamps: false, durationSeconds: 1265, elapsedSeconds: 94, engine: 'Google Gemini · gemini-3.5-transcribe', savedTo: 'x' };
        case 'dictation.testStart':
          s.dictationState = 'Recording'; emit('dictation.state', 'Recording');
          (async () => { for (let i = 0; i < 400 && s.dictationState === 'Recording'; i++) { emit('dictation.level', Math.random() * 0.1); await wait(60); } })();
          return null;
        case 'dictation.testStop':
          s.dictationState = 'Processing'; emit('dictation.state', 'Processing'); await wait(900);
          s.dictationState = 'Idle'; emit('dictation.test', { text: 'ده اختبار سريع للإملاء بالصوت.', error: null }); emit('dictation.state', 'Idle');
          return null;
        case 'local.download':
          (async () => { const key = params.file || 'model.bin'; for (let i = 1; i <= 20; i++) { await wait(200); emit('download.progress', { key, fraction: i / 20, text: `${i * 28} / 574 MB` }); } })();
          return { key: params.file || 'model.bin' };
        case 'providers.fetchModels': return ['gemini-3.5-transcribe', 'gemini-3.5-flash', 'gemini-3.5-pro'];
        default: return null;
      }
    },
  };
})();

/* لقطات المعاينة: ?page=files&demo=result */
if (!host) {
  const q = new URLSearchParams(location.search);
  setTimeout(async () => {
    if (q.get('demo') === 'result') { setFile(await call('files.pick')); $('#startFile').click(); }
    if (q.get('demo') === 'summary') { setFile(await call('files.pick')); autoSummarize = true; $('#startFile').click(); }
    if (q.get('demo') === 'meeting') { $('#meetingBtn').click(); }
    if (q.get('demo') === 'test') { $('#testMic').click(); }
    if (q.get('demo') === 'download') { $('[data-download]')?.click(); }
    if (q.get('tab')) showTab(q.get('tab'));
  }, 400);
}

start();
