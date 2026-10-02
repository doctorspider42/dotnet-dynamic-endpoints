'use strict';
// Admin panel of DynamicEndpoints – served by MapDynamicEndpointsAdminUI(), talks to MapDynamicEndpointsAdmin().

const CONFIG = JSON.parse(document.getElementById('admin-config').textContent);
const API = CONFIG.apiPath.replace(/\/$/, '');
const PATH_BASE = (CONFIG.pathBase || '').replace(/\/$/, '');
const METHODS = ['GET', 'POST', 'PUT', 'PATCH', 'DELETE'];
const SOURCES = ['Route', 'Query', 'Header', 'Body', 'Form'];
const TYPES = ['String', 'Integer', 'Number', 'Boolean', 'Date', 'DateTime', 'Guid', 'Array', 'Object', 'File'];
const SCALARS = ['String', 'Integer', 'Number', 'Boolean', 'Date', 'DateTime', 'Guid'];
const FORMATS = ['', 'Email', 'Uri', 'Phone', 'Ipv4', 'Ipv6', 'Time'];

let endpoints = [];     // DynamicEndpointState[] – drafts of new endpoints included
let processors = [];
let validators = [];
let revisionsEnabled = false;  // the store keeps history and drafts
let draft = null;       // definition being edited
let draftErrors = {};   // validation errors keyed by path
let editing = null;     // { state, fromDraft } of the endpoint being edited
let snippetKind = 'curl';

// ---------- helpers ----------
const $ = (sel) => document.querySelector(sel);
const esc = (v) => String(v ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const json = (v) => v === undefined || v === null ? '' : JSON.stringify(v, null, 2);
const when = (iso) => iso ? new Date(iso).toLocaleString() : '';
const stateOf = (id) => endpoints.find(x => x.definition.id === id);

function toast(message) {
  const t = $('#toast'); t.textContent = message; t.classList.add('show');
  clearTimeout(toast.timer); toast.timer = setTimeout(() => t.classList.remove('show'), 2200);
}

async function api(path, options = {}) {
  const response = await fetch(API + path, {
    ...options,
    headers: options.body ? { 'Content-Type': 'application/json' } : {},
    body: options.body ? JSON.stringify(options.body) : undefined,
  });
  const text = await response.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { data = text; }
  return { ok: response.ok, status: response.status, data };
}

function problemText(p) {
  if (!p || typeof p !== 'object') return String(p ?? 'Request failed');
  const errs = p.errors ? Object.entries(p.errors).map(([k, v]) => `${k}: ${v.join(' ')}`).join('\n') : '';
  return [p.title, p.detail, errs].filter(Boolean).join('\n');
}

async function copy(text) {
  try { await navigator.clipboard.writeText(text); }
  catch {
    const area = Object.assign(document.createElement('textarea'), { value: text });
    document.body.append(area); area.select(); document.execCommand('copy'); area.remove();
  }
  toast('Copied to clipboard');
}

// ---------- header ----------
function renderHeader() {
  document.title = CONFIG.title;
  $('#title').textContent = CONFIG.title;
  const links = [];
  if (CONFIG.swaggerUrl) links.push(`<a class="btn" href="${esc(CONFIG.swaggerUrl)}" target="_blank">Swagger UI ↗</a>`);
  if (CONFIG.openApiUrl) links.push(`<a class="btn hide-sm" href="${esc(CONFIG.openApiUrl)}" target="_blank">OpenAPI JSON ↗</a>`);
  $('#links').innerHTML = links.join('');
}

// ---------- list ----------
async function load() {
  const [list, procs, vals, drafts] = await Promise.all([api('/'), api('/processors'), api('/validators'), api('/drafts')]);
  if (!list.ok) {
    $('#list').innerHTML = `<div class="errors">The admin API at <code>${esc(API)}</code> answered ${list.status}. ${esc(problemText(list.data))}</div>`;
    return;
  }
  endpoints = list.data || [];
  processors = procs.data || [];
  validators = vals.data || [];
  revisionsEnabled = drafts.ok;
  renderList();
}

function renderList() {
  const filter = ($('#filter')?.value || '').trim().toLowerCase();
  const shown = endpoints.filter(({ definition: d }) => !filter ||
    [d.method, d.route, d.name, d.group, d.processor].some(v => (v || '').toLowerCase().includes(filter)));
  $('#count').textContent = endpoints.length ? `${endpoints.length} total` : '';
  if (!endpoints.length) {
    $('#list').innerHTML = `<div class="empty">No endpoints yet. Click <b>New endpoint</b> to publish your first one.</div>`;
    return;
  }
  $('#list').innerHTML = `<table>
    <thead><tr><th>Method</th><th>Route</th><th class="hide-sm">Name</th><th class="hide-sm">Group</th><th class="hide-sm">Processor</th><th>Status</th><th class="hide-sm">Rev.</th><th></th></tr></thead>
    <tbody>${shown.map(({ definition: d, status, errors, draft: dr }) => {
      const draftOnly = status === 'Draft';
      const pill = dr && !draftOnly ? ` <span class="pill" title="${esc(dr.comment || '')}">draft</span>` : '';
      const scheduled = dr?.publishAt ? ` <span class="pill warn" title="Scheduled">⏱ ${esc(when(dr.publishAt))}</span>` : '';
      return `
      <tr>
        <td><span class="method m-${d.method}">${d.method}</span></td>
        <td><code>${esc(d.route)}</code>${dr && dr.definition.route !== d.route ? ` <span class="muted">→ <code>${esc(dr.definition.route)}</code></span>` : ''}</td>
        <td class="hide-sm">${esc(d.name) || '<span class="muted">—</span>'}</td>
        <td class="hide-sm">${esc(d.group) || '<span class="muted">—</span>'}</td>
        <td class="hide-sm"><span class="badge">${esc(d.processor || 'default')}</span></td>
        <td><span class="status s-${status}" title="${esc((errors || []).join('\n'))}">${status}</span>${pill}${scheduled}</td>
        <td class="hide-sm muted">${draftOnly ? '—' : `r${d.revision}`}</td>
        <td class="actions">
          ${draftOnly ? '' : `<button class="small" onclick="openTester('${d.id}')" ${status !== 'Active' ? 'disabled' : ''}>▶ Try</button>`}
          <button class="small" onclick="openEditor('${d.id}')">${dr ? 'Edit draft' : 'Edit'}</button>
          ${dr ? `<button class="small primary" onclick="publish('${d.id}')">Publish</button>` : ''}
          ${revisionsEnabled && !draftOnly ? `<button class="small" onclick="openHistory('${d.id}')">History</button>` : ''}
          ${draftOnly ? '' : `<button class="small" onclick="toggle('${d.id}', ${!d.enabled})">${d.enabled ? 'Disable' : 'Enable'}</button>`}
          <button class="small danger" onclick="removeEndpoint('${d.id}')">Delete</button>
        </td>
      </tr>`; }).join('')}
    </tbody></table>`;
}

async function toggle(id, enable) {
  const r = await api(`/${id}/${enable ? 'enable' : 'disable'}`, { method: 'POST' });
  if (!r.ok) alert(problemText(r.data));
  else toast(enable ? 'Endpoint enabled' : 'Endpoint disabled');
  await load();
}

async function removeEndpoint(id) {
  const { definition: e, status } = stateOf(id);
  const what = status === 'Draft' ? `the draft of ${e.method} ${e.route}` : `${e.method} ${e.route} with its history`;
  if (!confirm(`Delete ${what}? This cannot be undone.`)) return;
  await api(`/${id}`, { method: 'DELETE' });
  toast('Endpoint deleted');
  await load();
}

async function publish(id) {
  const r = await api(`/${id}/publish`, { method: 'POST' });
  if (!r.ok) { alert(r.status === 409 ? `${problemText(r.data)}\n\nOpen the draft, review it and save it again on top of the current revision.` : problemText(r.data)); return false; }
  toast(`Published as revision ${r.data.revision}`);
  closeDrawer();
  await load();
  return true;
}

async function discardDraft(id) {
  if (!confirm('Discard the draft? The published revision stays as it is.')) return;
  await api(`/${id}/draft`, { method: 'DELETE' });
  toast('Draft discarded');
  closeDrawer();
  await load();
}

async function reloadFromStore() {
  await api('/reload', { method: 'POST' });
  await load();
  toast('Reloaded from the store');
}

// ---------- drawer ----------
function openDrawer(title) { $('#drawer-title').textContent = title; document.getElementById('shell').classList.add('open'); }
function closeDrawer() { document.getElementById('shell').classList.remove('open'); draft = null; editing = null; }
document.addEventListener('keydown', e => { if (e.key === 'Escape') closeDrawer(); });

// ---------- editor ----------
function openEditor(id) {
  const state = id ? stateOf(id) : null;
  const fromDraft = !!state?.draft;
  const existing = state ? (state.draft?.definition ?? state.definition) : null;
  draft = existing ? structuredClone(existing) : {
    method: 'GET', route: '/', name: '', description: '', group: '', processor: processors[0]?.name ?? '',
    processorConfig: processors[0]?.configurationExample ?? null, parameters: [], rules: [], validators: [], enabled: true,
  };
  draft.parameters ??= []; draft.rules ??= []; draft.validators ??= [];
  editing = { state, fromDraft, publishAt: state?.draft?.publishAt ?? null, comment: state?.draft?.comment ?? '' };
  draftErrors = {};
  openDrawer(!existing ? 'New endpoint' : `${fromDraft ? 'Edit draft of' : 'Edit'} ${existing.method} ${existing.route}`);
  renderEditor();
  renderEditorFoot();
}

function renderEditorFoot() {
  const { state, fromDraft } = editing;
  const published = state && state.status !== 'Draft';
  const info = !state ? (revisionsEnabled ? 'Save a draft, or publish right away' : 'Saving publishes the endpoint immediately')
    : fromDraft ? `draft based on ${state.draft.baseRevision ? `revision ${state.draft.baseRevision}` : 'nothing (new endpoint)'}`
    : `revision ${state.definition.revision}`;
  $('#drawer-foot').innerHTML = `
    <span class="muted" style="margin-right:auto;align-self:center">${info}</span>
    ${fromDraft ? `<button class="danger" onclick="discardDraft('${state.definition.id}')">Discard draft</button>` : ''}
    ${fromDraft && published ? `<button onclick="showDraftDiff('${state.definition.id}')">Changes</button>` : ''}
    <button onclick="closeDrawer()">Cancel</button>
    <button onclick="validateDraft()">Validate</button>
    ${revisionsEnabled ? `<button onclick="saveDraft(false)">Save draft</button>` : ''}
    <button class="primary" onclick="${fromDraft ? 'saveDraft(true)' : 'saveDefinition()'}">${state && !fromDraft ? 'Save & publish' : fromDraft ? 'Save & publish draft' : 'Create & publish'}</button>`;
}

function field(label, html, cls = '') { return `<label class="field ${cls}">${label}${html}</label>`; }
function errClass(path) { return draftErrors[path] ? 'invalid' : ''; }
// Merges a class="" passed in attrs with the error class, so an element never gets two class attributes.
function withClass(path, attrs) {
  let cls = errClass(path);
  attrs = attrs.replace(/class="([^"]*)"/, (_, c) => { cls += ' ' + c; return ''; });
  return `class="${cls.trim()}" data-path="${path}" ${attrs}`;
}
function input(path, value, attrs = '') {
  return `<input ${withClass(path, attrs)} value="${esc(value)}">`;
}
function select(path, value, options, attrs = '') {
  return `<select ${withClass(path, attrs)}>${options.map(o => `<option ${o === value ? 'selected' : ''}>${o}</option>`).join('')}</select>`;
}
function textarea(path, value, attrs = '') {
  return `<textarea ${withClass(path, attrs)} data-json="1" spellcheck="false">${esc(value)}</textarea>`;
}
function toLocalInput(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  return new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
}

function renderEditor() {
  const d = draft;
  const proc = processors.find(p => p.name.toLowerCase() === (d.processor || '').toLowerCase());
  const errorList = Object.entries(draftErrors);
  const paramNames = d.parameters.map(p => p.name).filter(Boolean);
  const requestValidators = validators.filter(v => v.forRequests);
  const { state, fromDraft } = editing;

  $('#drawer-body').innerHTML = `
    ${errorList.length ? `<div class="errors"><b>Fix these problems:</b><ul>${errorList.map(([k, v]) => `<li><code>${esc(k)}</code>: ${esc(v.join(' '))}</li>`).join('')}</ul></div>` : ''}
    <div id="validation-ok"></div>
    ${fromDraft && state.status !== 'Draft' ? `<div class="notice">✎ You are editing the <b>draft</b>. Revision ${state.definition.revision} keeps serving requests until the draft is published.</div>` : ''}

    <section class="card">
      <h3>Endpoint</h3>
      <div class="grid">
        ${field('Method', select('method', d.method, METHODS))}
        ${field('Route', input('route', d.route, 'placeholder="/orders/{id}" class="mono"'), 'half')}
        ${field('Name', input('name', d.name, 'placeholder="Create order"'))}
        ${field('Description', input('description', d.description), 'wide')}
        ${field('Group <span class="muted">(section in Swagger UI)</span>', input('group', d.group, 'placeholder="Dynamic" list="groups"'), 'half')}
        <datalist id="groups">${[...new Set(endpoints.map(e => e.definition.group).filter(Boolean))].map(g => `<option value="${esc(g)}">`).join('')}</datalist>
        <label class="check"><input type="checkbox" data-path="enabled" ${d.enabled ? 'checked' : ''}> Enabled</label>
      </div>
      <p class="hint">Route parameters like <code>{id}</code> need a parameter with source <b>Route</b> — <a href="#" onclick="syncRouteParams();return false">add missing ones</a>.</p>
    </section>

    <section class="card">
      <h3>Processor</h3>
      <div class="grid">
        ${field('Processor', select('processor', proc?.name ?? d.processor, processors.map(p => p.name)))}
        <div class="half" style="align-self:end">${proc?.description ? `<span class="muted">${esc(proc.description)}</span>` : ''}</div>
        ${field(`Configuration (JSON) ${proc?.configurationExample ? `<a href="#" onclick="useExample();return false">use example</a>` : ''}`,
          textarea('processorConfig', json(d.processorConfig), 'rows="4"'), 'wide')}
      </div>
    </section>

    <section class="card">
      <h3>Parameters <span class="muted">(${d.parameters.length})</span><span class="spacer"></span><button class="small" onclick="addParameter()">＋ Add parameter</button></h3>
      ${d.parameters.length ? d.parameters.map(renderParameter).join('') : '<p class="muted">No parameters. Requests are passed to the processor as-is.</p>'}
    </section>

    <section class="card">
      <h3>Business rules <span class="muted">(JsonLogic)</span><span class="spacer"></span><button class="small" onclick="addRule()">＋ Add rule</button></h3>
      ${d.rules.length ? d.rules.map((r, i) => `
        <div class="item">
          <div class="item-head">Rule ${i + 1}<span class="spacer"></span><button class="small danger" onclick="removeAt('rules', ${i})">Remove</button></div>
          <div class="grid">
            ${field('Error message', input(`rules[${i}].message`, r.message), 'half')}
            ${field('Reported for parameter', select(`rules[${i}].parameter`, r.parameter ?? '', ['', ...paramNames]))}
            ${field('Condition — must be truthy', textarea(`rules[${i}].condition`, json(r.condition), 'rows="3"'), 'wide')}
          </div>
        </div>`).join('') : ''}
      <p class="hint">Rules run after all parameters passed validation. Example: <code>{"&lt;": [{"var": "from"}, {"var": "to"}]}</code>. Operators: <a href="https://jsonlogic.com/operations.html" target="_blank">jsonlogic.com</a>.</p>
    </section>

    <section class="card">
      <h3>Custom validators <span class="muted">(whole request)</span><span class="spacer"></span>
        <button class="small" onclick="addRequestValidator()" ${requestValidators.length ? '' : 'disabled title="No request-level validators registered"'}>＋ Add validator</button></h3>
      ${d.validators.map((v, i) => {
        const descriptor = validators.find(x => x.name.toLowerCase() === (v.name || '').toLowerCase());
        return `
        <div class="item">
          <div class="item-head">${esc(v.name)}<span class="muted" style="font-weight:400">${descriptor?.description ? ' — ' + esc(descriptor.description) : ''}</span>
            <span class="spacer"></span><button class="small danger" onclick="removeAt('validators', ${i})">Remove</button></div>
          <div class="grid">
            ${field('Validator', select(`validators[${i}].name`, descriptor?.name ?? v.name, requestValidators.map(x => x.name), 'data-rerender="1"'))}
            ${field('Configuration (JSON)', textarea(`validators[${i}].config`, json(v.config), 'rows="3"'), 'wide')}
          </div>
        </div>`; }).join('')}
      <p class="hint">Code validators (C# or FluentValidation) registered by developers. They run last, only when parameters and rules passed, so they may hit the database.</p>
    </section>

    <section class="card">
      <h3>Security &amp; documentation</h3>
      <div class="grid">
        <label class="check"><input type="checkbox" data-path="allowAnonymous" ${d.allowAnonymous ? 'checked' : ''}> Allow anonymous</label>
        <label class="check"><input type="checkbox" data-path="requireAuthorization" ${d.requireAuthorization ? 'checked' : ''}> Require auth</label>
        ${field('Authorization policy', input('authorizationPolicy', d.authorizationPolicy))}
        ${field('Rate limiting policy', input('rateLimitingPolicy', d.rateLimitingPolicy))}
        ${field('Response schema (JSON Schema, documentation only)', textarea('responseSchema', json(d.responseSchema), 'rows="3"'), 'wide')}
        ${field('Request example (JSON, documentation and “Try”)', textarea('requestExample', json(d.requestExample), 'rows="3"'), 'wide')}
      </div>
    </section>

    ${revisionsEnabled ? `
    <section class="card">
      <h3>Publishing <span class="muted">(drafts)</span></h3>
      <div class="grid">
        ${field('Publish draft at <span class="muted">(empty: by hand)</span>', `<input type="datetime-local" id="publish-at" value="${esc(toLocalInput(editing.publishAt))}">`)}
        ${field('Comment <span class="muted">(kept in the history)</span>', `<input id="draft-comment" value="${esc(editing.comment)}" placeholder="What changes and why">`, 'half')}
      </div>
      <p class="hint"><b>Save draft</b> stores the change without routing it. It goes live when you publish it, or at the set time.</p>
    </section>` : ''}`;

  for (const el of document.querySelectorAll('#drawer-body [data-path]')) {
    el.addEventListener(el.tagName === 'SELECT' || el.type === 'checkbox' ? 'change' : 'input', onFieldChange);
  }
  $('#publish-at')?.addEventListener('input', e => { editing.publishAt = e.target.value ? new Date(e.target.value).toISOString() : null; });
  $('#draft-comment')?.addEventListener('input', e => { editing.comment = e.target.value; });
}

function renderParameter(p, i) {
  const base = `parameters[${i}]`;
  const t = p.type || 'String';
  const it = p.itemType || 'String';
  const scalarType = t === 'Array' ? it : t;
  const showText = scalarType === 'String';
  const showNumber = scalarType === 'Integer' || scalarType === 'Number';
  const showEnum = SCALARS.includes(scalarType) && scalarType !== 'Boolean';
  // Only validators that accept this parameter's type (checked again on save).
  const parameterValidators = validators.filter(v => v.forParameters && (!v.parameterTypes?.length || v.parameterTypes.includes(t)));
  return `
    <div class="item">
      <div class="item-head"><code>${esc(p.name) || 'unnamed'}</code> <span class="muted">${esc(p.source)} · ${esc(t)}${t === 'Array' ? ` of ${esc(it)}` : ''}${p.required ? ' · required' : ''}</span>
        <span class="spacer"></span><button class="small danger" onclick="removeAt('parameters', ${i})">Remove</button></div>
      <div class="grid">
        ${field('Name', input(`${base}.name`, p.name, 'class="mono"'))}
        ${field('Source', select(`${base}.source`, p.source, SOURCES, 'data-rerender="1"'))}
        ${field('Type', select(`${base}.type`, t, TYPES, 'data-rerender="1"'))}
        ${t === 'Array' ? field('Item type', select(`${base}.itemType`, it, SCALARS.concat(p.source === 'Body' ? ['Object'] : p.source === 'Form' ? ['File'] : []), 'data-rerender="1"')) : ''}
        ${p.source !== 'Route' ? field(p.source === 'Header' ? 'Header name' : 'Name in request', input(`${base}.sourceName`, p.sourceName, `placeholder="${esc(p.name || '')}"`)) : ''}
        <label class="check"><input type="checkbox" data-path="${base}.required" data-rerender="1" ${p.required || p.source === 'Route' ? 'checked' : ''} ${p.source === 'Route' ? 'disabled' : ''}> Required</label>
        ${field('Description', input(`${base}.description`, p.description), 'wide')}
        ${showText ? field('Min length', input(`${base}.minLength`, p.minLength, 'type="number" min="0" data-number="int"')) : ''}
        ${showText ? field('Max length', input(`${base}.maxLength`, p.maxLength, 'type="number" min="0" data-number="int"')) : ''}
        ${showNumber ? field('Minimum', input(`${base}.minimum`, p.minimum, 'type="number" step="any" data-number="dec"')) : ''}
        ${showNumber ? field('Maximum', input(`${base}.maximum`, p.maximum, 'type="number" step="any" data-number="dec"')) : ''}
        ${showText ? field('Format', select(`${base}.format`, p.format ?? '', FORMATS)) : ''}
        ${showText ? field('Pattern (regex)', input(`${base}.pattern`, p.pattern, 'class="mono" placeholder="^[a-z]+$"'), 'half') : ''}
        ${showEnum ? field('Allowed values (comma separated)', input(`${base}.allowedValues`, (p.allowedValues || []).join(', '), 'data-list="1"'), 'half') : ''}
        ${t === 'File' || it === 'File' ? field('Max file size (bytes)', input(`${base}.maxFileSize`, p.maxFileSize, 'type="number" min="1" data-number="int"')) : ''}
        ${t === 'File' || it === 'File' ? field('Allowed content types (comma separated)', input(`${base}.allowedContentTypes`, (p.allowedContentTypes || []).join(', '), 'data-list="1" placeholder="application/pdf, image/*"'), 'half') : ''}
        ${t === 'Array' ? field('Min items', input(`${base}.minItems`, p.minItems, 'type="number" min="0" data-number="int"')) : ''}
        ${t === 'Array' ? field('Max items', input(`${base}.maxItems`, p.maxItems, 'type="number" min="0" data-number="int"')) : ''}
        ${!p.required && p.source !== 'Route' && t !== 'File' && it !== 'File' ? field('Default (JSON)', input(`${base}.default`, json(p.default), 'class="mono" data-json="1" placeholder="e.g. 10 or &quot;abc&quot;"')) : ''}
        ${field('Example (JSON)', input(`${base}.example`, json(p.example), 'class="mono" data-json="1"'))}
        ${t === 'Object' || t === 'Array' ? field('Custom JSON Schema (optional)', textarea(`${base}.schema`, json(p.schema), 'rows="3"'), 'wide') : ''}
        ${parameterValidators.length ? `<div class="wide"><span class="muted" style="font-size:12px">Custom validators</span><div style="display:flex;gap:14px;flex-wrap:wrap;margin-top:4px">
          ${parameterValidators.map(v => `<label class="check" title="${esc(v.description || '')}"><input type="checkbox" ${(p.validators || []).some(x => x.name.toLowerCase() === v.name.toLowerCase()) ? 'checked' : ''}
            onchange="toggleParameterValidator(${i}, '${esc(v.name)}', this.checked)"> ${esc(v.name)}</label>`).join('')}</div></div>` : ''}
      </div>
    </div>`;
}

// Writes an input value into the draft object, following paths like "parameters[0].minLength".
function onFieldChange(e) {
  const el = e.target;
  const path = el.dataset.path;
  let value;
  if (el.type === 'checkbox') value = el.checked;
  else if (el.dataset.number) value = el.value === '' ? null : Number(el.value);
  else if (el.dataset.json) {
    if (el.value.trim() === '') { value = null; el.classList.remove('invalid'); }
    else {
      try { value = JSON.parse(el.value); el.classList.remove('invalid'); }
      catch {
        // Lenient for single-line inputs: unquoted text becomes a string.
        if (el.tagName === 'INPUT') value = el.value;
        else { el.classList.add('invalid'); return; }
      }
    }
  }
  else if (el.dataset.list) value = el.value.split(',').map(s => s.trim()).filter(Boolean);
  else value = el.value;

  setPath(draft, path, value);

  if (path.endsWith('.allowedValues')) coerceAllowedValues(path);
  if (path === 'processor') {
    // Configurations are processor specific – start from the new processor's example.
    const proc = processors.find(p => p.name === value);
    draft.processorConfig = proc?.configurationExample ? structuredClone(proc.configurationExample) : null;
    renderEditor();
  }
  const vm = path.match(/^validators\[(\d+)\]\.name$/);
  if (vm) {
    const v = validators.find(x => x.name === value);
    draft.validators[+vm[1]].config = v?.configurationExample ? structuredClone(v.configurationExample) : null;
  }
  if (el.dataset.rerender) {
    const m = path.match(/^parameters\[(\d+)\]\.source$/);
    if (m && value === 'Route') { draft.parameters[+m[1]].required = true; }
    renderEditor();
  }
}

function coerceAllowedValues(path) {
  const i = +path.match(/\[(\d+)\]/)[1];
  const p = draft.parameters[i];
  const type = p.type === 'Array' ? (p.itemType || 'String') : p.type;
  if (type === 'Integer' || type === 'Number') p.allowedValues = p.allowedValues.map(v => isNaN(Number(v)) ? v : Number(v));
}

function setPath(obj, path, value) {
  const parts = path.replace(/\[(\d+)\]/g, '.$1').split('.');
  let target = obj;
  for (const part of parts.slice(0, -1)) target = target[part];
  target[parts.at(-1)] = value;
}

function addParameter() {
  draft.parameters.push({ name: '', source: draft.method === 'GET' || draft.method === 'DELETE' ? 'Query' : 'Body', type: 'String', required: false });
  renderEditor();
  const inputs = document.querySelectorAll('#drawer-body [data-path$=".name"]');
  inputs[inputs.length - 1]?.focus();
}

function addRule() {
  const names = draft.parameters.map(p => p.name).filter(Boolean);
  draft.rules.push({
    message: '', parameter: names[1] ?? null,
    condition: names.length >= 2 ? { '<': [{ var: names[0] }, { var: names[1] }] } : { '!!': [{ var: names[0] ?? 'field' }] },
  });
  renderEditor();
}

function toggleParameterValidator(i, name, on) {
  const p = draft.parameters[i];
  const rest = (p.validators || []).filter(v => v.name.toLowerCase() !== name.toLowerCase());
  p.validators = on ? [...rest, { name }] : rest;
}

function addRequestValidator() {
  const v = validators.find(x => x.forRequests);
  draft.validators.push({ name: v.name, config: v.configurationExample ? structuredClone(v.configurationExample) : null });
  renderEditor();
}

function removeAt(collection, i) { draft[collection].splice(i, 1); renderEditor(); }

function useExample() {
  const proc = processors.find(p => p.name.toLowerCase() === (draft.processor || '').toLowerCase());
  draft.processorConfig = structuredClone(proc.configurationExample);
  renderEditor();
}

function syncRouteParams() {
  const names = [...draft.route.matchAll(/\{\*{0,2}([A-Za-z_][A-Za-z0-9_]*)[^}]*\}/g)].map(m => m[1]);
  let added = 0;
  for (const name of names) {
    if (!draft.parameters.some(p => p.source === 'Route' && (p.sourceName || p.name) === name)) {
      draft.parameters.push({ name, source: 'Route', type: 'String', required: true });
      added++;
    }
  }
  renderEditor();
  toast(added ? `Added ${added} route parameter(s)` : 'All route parameters are defined');
}

function cleanDraft() {
  const d = structuredClone(draft);
  d.parameters = d.parameters.map(p => {
    const c = { ...p };
    for (const k of Object.keys(c)) if (c[k] === '' || c[k] === null || (Array.isArray(c[k]) && !c[k].length)) delete c[k];
    if (c.type !== 'Array') delete c.itemType;
    if (c.source === 'Route') c.required = true;
    return c;
  });
  d.rules = d.rules.map(r => ({ ...r, parameter: r.parameter || null }));
  for (const k of ['name', 'description', 'group', 'authorizationPolicy', 'rateLimitingPolicy']) if (!d[k]) d[k] = null;
  return d;
}

function showErrors(r) {
  draftErrors = r.data?.errors ?? { request: [problemText(r.data)] };
  renderEditor();
  $('#drawer-body').scrollTop = 0;
}

async function validateDraft() {
  const r = await api('/validate', { method: 'POST', body: cleanDraft() });
  if (!r.ok) { showErrors(r); return; }
  draftErrors = r.data.isValid ? {} : r.data.errors;
  renderEditor();
  if (r.data.isValid) $('#validation-ok').innerHTML = '<div class="success">✓ Definition is valid.</div>';
  $('#drawer-body').scrollTop = 0;
}

// Publishes the definition directly (no draft involved).
async function saveDefinition() {
  const body = cleanDraft();
  const r = body.id
    ? await api(`/${body.id}`, { method: 'PUT', body })
    : await api('/', { method: 'POST', body });
  if (r.ok) {
    toast(body.id ? 'Saved & published' : 'Created & published');
    closeDrawer();
    await load();
    return;
  }
  showErrors(r);
}

// Saves the draft – based on the revision the editor started from – and optionally publishes it right away.
async function saveDraft(publishNow) {
  const definition = cleanDraft();
  const { state } = editing;
  definition.revision = state?.draft?.baseRevision ?? (state && state.status !== 'Draft' ? state.definition.revision : 0);
  const body = { definition, publishAt: publishNow ? null : editing.publishAt, comment: editing.comment || null };
  const r = definition.id
    ? await api(`/${definition.id}/draft`, { method: 'PUT', body })
    : await api('/drafts', { method: 'POST', body });
  if (!r.ok) { showErrors(r); return; }
  if (publishNow) { await publish(r.data.endpointId); return; }
  toast(r.data.publishAt ? `Draft saved, publishing ${when(r.data.publishAt)}` : 'Draft saved – not published yet');
  closeDrawer();
  await load();
}

// ---------- history & diffs ----------
async function openHistory(id) {
  const state = stateOf(id);
  const d = state.definition;
  openDrawer(`History of ${d.method} ${d.route}`);
  $('#drawer-foot').innerHTML = `<button onclick="closeDrawer()">Close</button>`;
  const r = await api(`/${id}/revisions`);
  if (!r.ok) { $('#drawer-body').innerHTML = `<div class="errors">${esc(problemText(r.data))}</div>`; return; }
  const revisions = r.data;
  $('#drawer-body').innerHTML = `
    ${state.draft ? `<div class="notice">✎ There is an unpublished draft${state.draft.publishAt ? `, scheduled for <b>${esc(when(state.draft.publishAt))}</b>` : ''}.
      <span class="spacer"></span>
      <button class="small" onclick="showDraftDiff('${id}')">Changes</button>
      <button class="small primary" onclick="publish('${id}')">Publish</button></div>` : ''}
    <table>
      <thead><tr><th>Rev.</th><th>What</th><th class="hide-sm">When</th><th>Comment</th><th></th></tr></thead>
      <tbody>${revisions.map((rev, i) => `
        <tr>
          <td><b>r${rev.revision}</b>${rev.revision === d.revision ? ' <span class="pill">published</span>' : ''}</td>
          <td>${esc(rev.kind)}${rev.sourceRevision ? ` <span class="muted">(r${rev.sourceRevision})</span>` : ''}</td>
          <td class="hide-sm muted">${esc(when(rev.savedAt))}</td>
          <td>${esc(rev.comment) || '<span class="muted">—</span>'}</td>
          <td class="actions">
            <button class="small" onclick="showRevision('${id}', ${rev.revision})">View</button>
            ${i < revisions.length - 1 ? `<button class="small" onclick="showDiff('${id}', ${revisions[i + 1].revision}, ${rev.revision})">Diff ↓</button>` : ''}
            ${rev.revision !== d.revision ? `<button class="small" onclick="showDiff('${id}', ${rev.revision}, ${d.revision})">vs published</button>
            <button class="small danger" onclick="rollback('${id}', ${rev.revision})">Roll back</button>` : ''}
          </td>
        </tr>`).join('')}
      </tbody></table>
    <div id="history-detail" style="margin-top:16px"></div>`;
}

async function showRevision(id, revision) {
  const r = await api(`/${id}/revisions/${revision}`);
  $('#history-detail').innerHTML = `<section class="card"><h3>Revision ${revision}</h3><pre class="response">${esc(json(r.data?.definition ?? r.data))}</pre></section>`;
}

async function showDiff(id, from, to) {
  const r = await api(`/${id}/diff?from=${from}&to=${to}`);
  renderDiff(`Changes from r${from} to r${to}`, r, '#history-detail');
}

async function showDraftDiff(id) {
  const r = await api(`/${id}/draft/diff`);
  const target = document.getElementById('history-detail') ? '#history-detail' : '#drawer-body';
  if (target === '#drawer-body') {
    // From the editor: show the changes on top, the form stays below.
    const box = document.getElementById('draft-diff') ?? Object.assign(document.createElement('div'), { id: 'draft-diff' });
    $('#drawer-body').prepend(box);
    renderDiff('What publishing the saved draft changes', r, '#draft-diff');
    $('#drawer-body').scrollTop = 0;
    return;
  }
  renderDiff('What publishing the draft changes', r, target);
}

function renderDiff(title, r, target) {
  const value = (v) => v === null || v === undefined ? '' : esc(typeof v === 'string' ? v : JSON.stringify(v, null, 2));
  $(target).innerHTML = !r.ok ? `<div class="errors">${esc(problemText(r.data))}</div>` : `
    <section class="card"><h3>${esc(title)}</h3>
      ${r.data.length ? `<table class="diff"><thead><tr><th>Path</th><th>Change</th><th>Before</th><th>After</th></tr></thead><tbody>
        ${r.data.map(c => `<tr class="d-${c.kind}"><td><code>${esc(c.path)}</code></td><td>${esc(c.kind)}</td>
          <td class="value from">${value(c.from)}</td><td class="value to">${value(c.to)}</td></tr>`).join('')}
      </tbody></table>` : '<p class="muted">No differences.</p>'}
    </section>`;
}

async function rollback(id, revision) {
  if (!confirm(`Publish the content of revision ${revision} again, as a new revision?`)) return;
  const r = await api(`/${id}/revisions/${revision}/rollback`, { method: 'POST' });
  if (!r.ok) { alert(problemText(r.data)); return; }
  toast(`Rolled back – now revision ${r.data.revision}`);
  await load();
  await openHistory(id);
}

// ---------- example values ----------
const FORMAT_SCHEMA = { Email: 'email', Uri: 'uri', Phone: 'phone', Ipv4: 'ipv4', Ipv6: 'ipv6', Time: 'time' };

// The parameter as a JSON Schema, so parameters and custom schemas share one example generator.
function schemaOf(p, type = p.type) {
  const s = {};
  switch (type) {
    case 'Integer': s.type = 'integer'; break;
    case 'Number': s.type = 'number'; break;
    case 'Boolean': s.type = 'boolean'; break;
    case 'Date': s.type = 'string'; s.format = 'date'; break;
    case 'DateTime': s.type = 'string'; s.format = 'date-time'; break;
    case 'Guid': s.type = 'string'; s.format = 'uuid'; break;
    case 'Object': return { type: 'object', ...(p.schema || {}) };
    case 'Array': return {
      type: 'array', minItems: p.minItems, maxItems: p.maxItems,
      items: p.schema?.items ?? schemaOf({ ...p, schema: null, minItems: undefined, maxItems: undefined }, p.itemType || 'String'),
    };
    default: s.type = 'string'; s.format = FORMAT_SCHEMA[p.format];
  }
  if (p.minLength != null) s.minLength = p.minLength;
  if (p.maxLength != null) s.maxLength = p.maxLength;
  if (p.minimum != null) s.minimum = p.minimum;
  if (p.maximum != null) s.maximum = p.maximum;
  if (p.allowedValues?.length) s.enum = p.allowedValues;
  return s;
}

function fromSchema(s, depth = 0) {
  if (!s || depth > 6) return null;
  if (s.example !== undefined) return s.example;
  if (Array.isArray(s.examples) && s.examples.length) return s.examples[0];
  if (s.const !== undefined) return s.const;
  if (s.default !== undefined) return s.default;
  if (Array.isArray(s.enum) && s.enum.length) return s.enum[0];
  const type = Array.isArray(s.type) ? s.type.find(t => t !== 'null') : s.type ?? (s.properties ? 'object' : s.items ? 'array' : 'string');
  switch (type) {
    case 'object': return Object.fromEntries(Object.entries(s.properties || {}).map(([k, v]) => [k, fromSchema(v, depth + 1)]));
    case 'array': {
      const count = Math.min(Math.max(s.minItems ?? 1, 1), s.maxItems ?? 3, 3);
      return Array.from({ length: Math.max(count, s.minItems ?? 0) }, () => fromSchema(s.items, depth + 1));
    }
    case 'integer': return Math.ceil(number(s, 1));
    case 'number': return number(s, 1.5);
    case 'boolean': return true;
    case 'null': return null;
    default: return text(s);
  }
}

function number(s, fallback) {
  const min = s.minimum ?? (s.exclusiveMinimum != null ? s.exclusiveMinimum + 1 : null);
  const max = s.maximum ?? (s.exclusiveMaximum != null ? s.exclusiveMaximum - 1 : null);
  if (min != null && max != null) return min <= fallback && fallback <= max ? fallback : min;
  if (min != null) return Math.max(min, fallback);
  if (max != null) return Math.min(max, fallback);
  return fallback;
}

function text(s) {
  const now = new Date();
  const byFormat = {
    email: 'user@example.com', uri: 'https://example.com', phone: '+48123456789', ipv4: '192.168.0.1', ipv6: '2001:db8::1',
    time: '12:30:00', date: now.toISOString().slice(0, 10), 'date-time': now.toISOString(), uuid: crypto.randomUUID(),
  }[s.format];
  if (byFormat) return byFormat;
  let value = 'text';
  while (value.length < (s.minLength ?? 0)) value += 'x';
  return s.maxLength != null ? value.slice(0, Math.max(s.maxLength, 0)) : value;
}

function sample(p) {
  if (p.example !== undefined && p.example !== null) return p.example;
  if (p.default !== undefined && p.default !== null) return p.default;
  return fromSchema(schemaOf(p));
}
function isFile(p) { return p.type === 'File' || (p.type === 'Array' && p.itemType === 'File'); }
function sampleText(p) { const v = sample(p); return Array.isArray(v) ? v.join(',') : typeof v === 'object' && v !== null ? JSON.stringify(v) : (v ?? ''); }
function sampleBody(d) {
  if (d.requestExample && Object.keys(d.requestExample).length) return d.requestExample;
  return Object.fromEntries(d.parameters.filter(p => p.source === 'Body').map(p => [p.sourceName || p.name, sample(p)]));
}

// ---------- tester ----------
function openTester(id) {
  const d = stateOf(id).definition;
  const nonBody = d.parameters.filter(p => p.source !== 'Body');
  const hasBody = !(d.method === 'GET' || d.method === 'DELETE' || d.parameters.some(p => p.source === 'Form'));
  openDrawer(`Try ${d.method} ${d.route}`);
  $('#drawer-body').innerHTML = `
    <section class="card">
      <h3><span class="method m-${d.method}">${d.method}</span> <code>${esc(d.route)}</code><span class="spacer"></span>
        <button class="small" onclick="fillExample('${id}')" title="Generated from the parameters, their constraints and examples">↻ Example values</button></h3>
      ${d.description ? `<p class="muted">${esc(d.description)}</p>` : ''}
      ${nonBody.length ? `<div class="grid">${nonBody.map(p => field(
        `${esc(p.sourceName || p.name)} <span class="muted">(${p.source.toLowerCase()}, ${p.type}${p.required || p.source === 'Route' ? ', required' : ''})</span>`,
        isFile(p)
          ? `<input type="file" data-test="${esc(p.sourceName || p.name)}" data-source="${p.source}" ${p.type === 'Array' ? 'multiple' : ''}>`
          : `<input data-test="${esc(p.sourceName || p.name)}" data-source="${p.source}" value="${esc(sampleText(p))}" class="mono"${p.pattern ? ` placeholder="${esc(p.pattern)}"` : ''}>`)).join('')}</div>` : ''}
      ${hasBody ? field('Body (JSON)', `<textarea id="test-body" rows="8" spellcheck="false">${esc(json(sampleBody(d)))}</textarea>`) : ''}
      <p class="hint">Array query parameters: separate values with commas. Clear a field to omit it.</p>
    </section>
    <section class="card"><h3>Response <span id="test-status"></span></h3><pre class="response" id="test-response">Click “Send”.</pre></section>
    <section class="card"><h3>Code <span class="spacer"></span>
        <span class="tabs">${['curl', 'httpie', 'csharp'].map(k => `<button class="small ${k === snippetKind ? 'active' : ''}" data-snippet="${k}" onclick="showSnippet('${id}', '${k}')">${{ curl: 'curl', httpie: 'HTTPie', csharp: 'C# HttpClient' }[k]}</button>`).join('')}</span>
        <button class="small" onclick="copy(document.getElementById('snippet').textContent)">⧉ Copy</button></h3>
      <pre class="snippet" id="snippet"></pre></section>`;
  $('#drawer-foot').innerHTML = `${CONFIG.swaggerUrl ? `<a class="btn" href="${esc(CONFIG.swaggerUrl)}" target="_blank" style="margin-right:auto">Open in Swagger UI ↗</a>` : '<span style="margin-right:auto"></span>'}
    <button onclick="copy(snippet('${id}', 'curl'))">Copy as curl</button>
    <button onclick="copy(snippet('${id}', 'httpie'))" class="hide-sm">Copy as HTTPie</button>
    <button onclick="copy(snippet('${id}', 'csharp'))" class="hide-sm">Copy as C#</button>
    <button onclick="closeDrawer()">Close</button><button class="primary" onclick="sendTest('${id}')">Send ▶</button>`;
  for (const el of document.querySelectorAll('#drawer-body [data-test], #test-body')) el.addEventListener('input', () => showSnippet(id));
  showSnippet(id);
}

function fillExample(id) {
  const d = stateOf(id).definition;
  for (const el of document.querySelectorAll('[data-test]')) {
    const p = d.parameters.find(x => (x.sourceName || x.name) === el.dataset.test);
    if (p && el.type !== 'file') el.value = sampleText(p);
  }
  if ($('#test-body')) $('#test-body').value = json(sampleBody(d));
  showSnippet(id);
}

// The request as entered in the form – shared by "Send" and the code snippets.
function buildRequest(d) {
  let path = d.route;
  const query = new URLSearchParams();
  const headers = {};
  const fields = [], files = [];
  const isForm = d.parameters.some(p => p.source === 'Form');
  for (const el of document.querySelectorAll('[data-test]')) {
    const name = el.dataset.test, value = el.value;
    if (el.type === 'file') { for (const file of el.files) files.push({ name, file }); if (!el.files.length) files.push({ name, file: null }); continue; }
    if (value === '') continue;
    if (el.dataset.source === 'Form') { value.split(',').forEach(v => fields.push({ name, value: v.trim() })); continue; }
    if (el.dataset.source === 'Route') path = path.replace(new RegExp(`\\{\\*{0,2}${name}[^}]*\\}`, 'i'), encodeURIComponent(value));
    else if (el.dataset.source === 'Query') value.split(',').forEach(v => query.append(name, v.trim()));
    else headers[name] = value;
  }
  const bodyEl = $('#test-body');
  const body = bodyEl && bodyEl.value.trim() ? bodyEl.value.trim() : null;
  if (body) headers['Content-Type'] = 'application/json';
  const url = PATH_BASE + path + (query.size ? `?${query}` : '');
  return { method: d.method, url, absoluteUrl: location.origin + url, headers, body, isForm, fields, files };
}

async function sendTest(id) {
  const d = stateOf(id).definition;
  const req = buildRequest(d);
  let body;
  if (req.isForm) {
    // The browser sets the multipart Content-Type (with its boundary) for FormData bodies.
    body = new FormData();
    for (const f of req.fields) body.append(f.name, f.value);
    for (const f of req.files) if (f.file) body.append(f.name, f.file);
  } else body = req.body ?? undefined;
  const started = performance.now();
  const response = await fetch(req.url, { method: req.method, headers: req.headers, body });
  const ms = Math.round(performance.now() - started);
  const text = await response.text();
  let pretty = text;
  try { pretty = JSON.stringify(JSON.parse(text), null, 2); } catch { }
  $('#test-status').innerHTML = `<span class="status ${response.ok ? 's-Active' : 's-Invalid'}">${response.status}</span> <span class="muted">${ms} ms · ${esc(req.method)} ${esc(req.url)}</span>`;
  $('#test-response').textContent = pretty || '(empty body)';
}

function showSnippet(id, kind) {
  if (kind) snippetKind = kind;
  for (const b of document.querySelectorAll('[data-snippet]')) b.classList.toggle('active', b.dataset.snippet === snippetKind);
  const el = document.getElementById('snippet');
  if (el) el.textContent = snippet(id, snippetKind);
}

const sh = (v) => `'${String(v).replace(/'/g, `'\\''`)}'`;
const compact = (body) => { try { return JSON.stringify(JSON.parse(body)); } catch { return body; } };
const fileName = (f) => f.file?.name ?? `path/to/${f.name}`;

function snippet(id, kind) {
  const req = buildRequest(stateOf(id).definition);
  return { curl, httpie, csharp: csharpSnippet }[kind](req);
}

function curl(req) {
  const lines = [`curl${req.method === 'GET' ? '' : ` -X ${req.method}`} ${sh(req.absoluteUrl)}`];
  for (const [k, v] of Object.entries(req.headers)) lines.push(`-H ${sh(`${k}: ${v}`)}`);
  if (req.isForm) {
    for (const f of req.fields) lines.push(`-F ${sh(`${f.name}=${f.value}`)}`);
    for (const f of req.files) lines.push(`-F ${sh(`${f.name}=@${fileName(f)}`)}`);
  } else if (req.body) lines.push(`--data-raw ${sh(compact(req.body))}`);
  return lines.join(' \\\n  ');
}

function httpie(req) {
  const parts = [`http${req.isForm ? ' --form' : ''} ${req.method} ${sh(req.absoluteUrl)}`];
  for (const [k, v] of Object.entries(req.headers)) if (!(req.body && k === 'Content-Type')) parts.push(sh(`${k}:${v}`));
  if (req.isForm) {
    for (const f of req.fields) parts.push(sh(`${f.name}=${f.value}`));
    for (const f of req.files) parts.push(sh(`${f.name}@${fileName(f)}`));
  } else if (req.body) parts.push(`--raw ${sh(compact(req.body))}`);
  return parts.join(' \\\n  ');
}

function csharpSnippet(req) {
  const str = (v) => `"${String(v).replace(/\\/g, '\\\\').replace(/"/g, '\\"')}"`;
  const method = req.method[0] + req.method.slice(1).toLowerCase();
  const lines = [
    'using var client = new HttpClient();',
    `using var request = new HttpRequestMessage(HttpMethod.${method}, ${str(req.absoluteUrl)});`,
  ];
  for (const [k, v] of Object.entries(req.headers)) if (k !== 'Content-Type') lines.push(`request.Headers.TryAddWithoutValidation(${str(k)}, ${str(v)});`);
  if (req.isForm) {
    lines.push('using var form = new MultipartFormDataContent();');
    for (const f of req.fields) lines.push(`form.Add(new StringContent(${str(f.value)}), ${str(f.name)});`);
    for (const f of req.files) lines.push(`form.Add(new StreamContent(File.OpenRead(${str(fileName(f))})), ${str(f.name)}, ${str(f.file?.name ?? f.name)});`);
    lines.push('request.Content = form;');
  } else if (req.body) {
    let pretty = req.body;
    try { pretty = JSON.stringify(JSON.parse(req.body), null, 2); } catch { }
    // A raw string literal needs more quotes than the longest run inside it.
    const quotes = '"'.repeat(Math.max(3, ...[...pretty.matchAll(/"+/g)].map(m => m[0].length + 1)));
    lines.push(`request.Content = new StringContent(${quotes}`);
    lines.push(...pretty.split('\n').map(l => '    ' + l));
    lines.push(`    ${quotes}, System.Text.Encoding.UTF8, "application/json");`);
  }
  lines.push('using var response = await client.SendAsync(request);');
  lines.push('Console.WriteLine($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");');
  return lines.join('\n');
}

renderHeader();
load();
