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
const ALGORITHMS = ['FixedWindow', 'SlidingWindow', 'TokenBucket', 'Concurrency'];
const PARTITIONS = ['IpAddress', 'User', 'Header', 'Endpoint'];
const PERIODS = ['Hour', 'Day', 'Week', 'Month'];
const IMPORT_MODES = {
  create: 'Create – only adds endpoints that don\'t exist yet',
  upsert: 'Upsert – adds new endpoints and replaces existing ones',
  sync: 'Sync – like upsert, and deletes endpoints missing from the file',
};

let endpoints = [];     // DynamicEndpointState[] – drafts of new endpoints included
let processors = [];
let validators = [];
let revisionsEnabled = false;  // the store keeps history and drafts
let draft = null;       // definition being edited
let draftErrors = {};   // validation errors keyed by path
let editing = null;     // { state, fromDraft } of the endpoint being edited
let snippetKind = 'curl';
// What the server supports (GET /info; probed on older servers). tenancy: null or { routePrefix, routeParameter, header }.
// crud: the ef-crud processor of DynamicEndpoints.EntityFrameworkCore ("crud" in /info features).
let features = { tenancy: null, tenant: null, audit: false, formats: ['json'], crud: false };
let tenants = [];
let tenantFilter = '';  // '' all, '*' shared only, or a tenant
let selected = new Set();
let tester = null;      // state of the "Try" console

// ---------- helpers ----------
const $ = (sel) => document.querySelector(sel);
const esc = (v) => String(v ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const json = (v) => v === undefined || v === null ? '' : JSON.stringify(v, null, 2);
const when = (iso) => iso ? new Date(iso).toLocaleString() : '';
const stateOf = (id) => endpoints.find(x => x.definition.id === id);
const enc = encodeURIComponent;
const tenantsShown = () => !!features.tenancy && !features.tenant;

function toast(message) {
  const t = $('#toast'); t.textContent = message; t.classList.add('show');
  clearTimeout(toast.timer); toast.timer = setTimeout(() => t.classList.remove('show'), 2200);
}

// options.body is sent as JSON; options.raw as is, with options.contentType.
async function api(path, options = {}) {
  const raw = options.raw !== undefined;
  const response = await fetch(API + path, {
    ...options,
    headers: raw ? { 'Content-Type': options.contentType || 'application/json' } : options.body ? { 'Content-Type': 'application/json' } : {},
    body: raw ? options.raw : options.body ? JSON.stringify(options.body) : undefined,
  });
  const text = await response.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { data = text; }
  return { ok: response.ok, status: response.status, data, text };
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

function download(name, text, type) {
  const url = URL.createObjectURL(new Blob([text], { type }));
  const a = Object.assign(document.createElement('a'), { href: url, download: name });
  document.body.append(a); a.click(); a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

// Reads a picked file into a textarea.
function readFileInto(input, target, after) {
  const file = input.files[0];
  if (!file) return;
  const reader = new FileReader();
  reader.onload = () => { $(target).value = reader.result; $(target).dataset.fileName = file.name; after?.(); };
  reader.readAsText(file);
}

// JSON or YAML, by file name or by the first character.
function contentTypeOf(text, fileName) {
  if (/\.ya?ml$/i.test(fileName || '')) return 'application/yaml';
  if (/\.json$/i.test(fileName || '')) return 'application/json';
  return /^\s*[[{]/.test(text) || !features.formats.includes('yaml') ? 'application/json' : 'application/yaml';
}

function tenantPill(t) { return t ? `<span class="pill tenant" title="Tenant">${esc(t)}</span>` : '<span class="muted">shared</span>'; }

// ---------- header ----------
function linkFor(general, perTenant) {
  const tenant = features.tenant || (tenantFilter && tenantFilter !== '*' ? tenantFilter : null);
  return tenant && perTenant ? { url: perTenant.replace(/\{tenant\}/g, enc(tenant)), tenant } : { url: general, tenant: null };
}
const swaggerLink = () => linkFor(CONFIG.swaggerUrl, CONFIG.tenantSwaggerUrl);

function renderHeader() {
  document.title = CONFIG.title;
  $('#title').textContent = CONFIG.title;
  const links = [];
  if (features.tenant) links.push(`<span class="pill tenant" title="This panel manages the endpoints of one tenant">tenant: ${esc(features.tenant)}</span>`);
  const swagger = swaggerLink(), openApi = linkFor(CONFIG.openApiUrl, CONFIG.tenantOpenApiUrl);
  if (swagger.url) links.push(`<a class="btn" href="${esc(swagger.url)}" target="_blank">Swagger UI${swagger.tenant ? ` (${esc(swagger.tenant)})` : ''} ↗</a>`);
  if (openApi.url) links.push(`<a class="btn hide-sm" href="${esc(openApi.url)}" target="_blank">OpenAPI JSON${openApi.tenant ? ` (${esc(openApi.tenant)})` : ''} ↗</a>`);
  $('#links').innerHTML = links.join('');
}

// ---------- capabilities ----------
async function detect() {
  const r = await api('/info');
  if (r.ok && r.data && typeof r.data === 'object') {
    features = {
      tenancy: r.data.tenancy ?? null, tenant: r.data.tenant ?? null, audit: !!r.data.auditLog,
      formats: r.data.formats?.length ? r.data.formats.map(f => f.toLowerCase()) : ['json'],
      crud: (r.data.features || []).some(f => String(f).toLowerCase() === 'crud'),
    };
    if (features.crud) await loadCrudEntities();
    return;
  }
  // An older server without /info: the audit log answers or it doesn't; tenancy shows once an endpoint has a tenant.
  const audit = await api('/audit?limit=1');
  features = { tenancy: null, tenant: null, audit: audit.ok, formats: ['json'], legacy: true };
}

// ---------- list ----------
async function load() {
  const byTenant = tenantFilter && tenantFilter !== '*' ? `/?tenant=${enc(tenantFilter)}` : '/';
  const [list, procs, vals, drafts, tenantList] = await Promise.all([api(byTenant), api('/processors'), api('/validators'), api('/drafts'),
    tenantsShown() ? api('/tenants') : Promise.resolve(null)]);
  if (!list.ok) {
    $('#list').innerHTML = `<div class="errors">The admin API at <code>${esc(API)}</code> answered ${list.status}. ${esc(problemText(list.data))}</div>`;
    return;
  }
  endpoints = list.data || [];
  processors = procs.data || [];
  validators = vals.data || [];
  revisionsEnabled = drafts.ok;
  if (features.legacy && !features.tenancy && endpoints.some(e => e.definition.tenant)) features.tenancy = {};
  if (tenantList?.ok) tenants = tenantList.data || [];
  const ids = new Set(endpoints.map(e => e.definition.id));
  selected = new Set([...selected].filter(id => ids.has(id)));
  renderTools();
  renderHeader();
  renderList();
}

function renderTools() {
  const filter = $('#tenant-filter');
  filter.hidden = !tenantsShown();
  if (tenantsShown()) {
    const known = tenants.includes(tenantFilter) || !tenantFilter || tenantFilter === '*' ? tenants : [...tenants, tenantFilter];
    filter.innerHTML = [['', 'All tenants'], ['*', 'Shared only'], ...known.map(t => [t, t])]
      .map(([v, l]) => `<option value="${esc(v)}" ${v === tenantFilter ? 'selected' : ''}>${esc(l)}</option>`).join('');
  }
  $('#tools').innerHTML = `
    ${features.audit ? '<button onclick="openAuditLog()" title="Who changed what, and when">Audit log</button>' : ''}
    <button onclick="openExport()" title="Download definitions as JSON${features.formats.includes('yaml') ? ' or YAML' : ''}">⇩ Export</button>
    <button onclick="openImport()" title="Import exported definitions">⇧ Import</button>
    <button onclick="openOpenApiImport()" title="Create endpoint skeletons from an OpenAPI document" class="hide-sm">OpenAPI import</button>
    ${features.crud ? '<button onclick="openCrudScaffold()" title="Generate CRUD endpoints for an entity of the EF Core model" class="hide-sm">Scaffold CRUD</button>' : ''}`;
}

function setTenantFilter(value) {
  tenantFilter = value;
  selected.clear();
  load();
}

function shownEndpoints() {
  const filter = ($('#filter')?.value || '').trim().toLowerCase();
  return endpoints.filter(({ definition: d }) => (tenantFilter !== '*' || !d.tenant) && (!filter ||
    [d.method, d.route, d.name, d.group, d.processor, d.tenant].some(v => (v || '').toLowerCase().includes(filter))));
}

function renderList() {
  const shown = shownEndpoints();
  const withTenant = !!features.tenancy && !features.tenant;
  $('#count').textContent = endpoints.length ? `${endpoints.length} total` : '';
  renderSelection();
  if (!endpoints.length) {
    $('#list').innerHTML = `<div class="empty">No endpoints${tenantFilter ? ' for this tenant filter' : ''} yet. Click <b>New endpoint</b> to publish your first one, or <a href="#" onclick="openImport();return false">import</a> some.</div>`;
    return;
  }
  const allChecked = shown.length && shown.every(s => selected.has(s.definition.id));
  $('#list').innerHTML = `<table>
    <thead><tr><th class="check"><input type="checkbox" title="Select all shown" ${allChecked ? 'checked' : ''} onchange="selectAll(this.checked)"></th>
      <th>Method</th><th>Route</th><th class="hide-sm">Name</th>${withTenant ? '<th class="hide-sm">Tenant</th>' : ''}<th class="hide-sm">Group</th><th class="hide-sm">Processor</th><th>Status</th><th class="hide-sm">Rev.</th><th></th></tr></thead>
    <tbody>${shown.map(({ definition: d, status, errors, draft: dr }) => {
      const draftOnly = status === 'Draft';
      const pill = dr && !draftOnly ? ` <span class="pill" title="${esc(dr.comment || '')}">draft</span>` : '';
      const scheduled = dr?.publishAt ? ` <span class="pill warn" title="Scheduled">⏱ ${esc(when(dr.publishAt))}</span>` : '';
      const extras = [d.caching ? '<span class="pill" title="Response caching">cache</span>' : '', d.rateLimit ? '<span class="pill" title="Rate limit">limit</span>' : ''].join(' ');
      return `
      <tr>
        <td class="check"><input type="checkbox" ${selected.has(d.id) ? 'checked' : ''} onchange="toggleSelected('${d.id}', this.checked)"></td>
        <td><span class="method m-${d.method}">${d.method}</span></td>
        <td><code>${esc(d.route)}</code>${dr && dr.definition.route !== d.route ? ` <span class="muted">→ <code>${esc(dr.definition.route)}</code></span>` : ''} ${extras}</td>
        <td class="hide-sm">${esc(d.name) || '<span class="muted">—</span>'}</td>
        ${withTenant ? `<td class="hide-sm">${tenantPill(d.tenant)}</td>` : ''}
        <td class="hide-sm">${esc(d.group) || '<span class="muted">—</span>'}</td>
        <td class="hide-sm"><span class="badge">${esc(d.processor || 'default')}</span></td>
        <td><span class="status s-${status}" title="${esc((errors || []).join('\n'))}">${status}</span>${pill}${scheduled}</td>
        <td class="hide-sm muted">${draftOnly ? '—' : `r${d.revision}`}</td>
        <td class="actions">
          ${draftOnly ? '' : `<button class="small" onclick="openTester('${d.id}')" ${status !== 'Active' ? 'disabled' : ''}>▶ Try</button>`}
          <button class="small" onclick="openEditor('${d.id}')">${dr ? 'Edit draft' : 'Edit'}</button>
          ${dr ? `<button class="small primary" onclick="publish('${d.id}')">Publish</button>` : ''}
          ${revisionsEnabled && !draftOnly ? `<button class="small" onclick="openHistory('${d.id}')">History</button>` : ''}
          ${features.audit && !draftOnly ? `<button class="small" onclick="openAudit('${d.id}')">Audit</button>` : ''}
          ${draftOnly ? '' : `<button class="small" onclick="toggle('${d.id}', ${!d.enabled})">${d.enabled ? 'Disable' : 'Enable'}</button>`}
          <button class="small danger" onclick="removeEndpoint('${d.id}')">Delete</button>
        </td>
      </tr>`; }).join('')}
    </tbody></table>`;
}

function renderSelection() {
  const bar = $('#selection');
  bar.hidden = !selected.size;
  bar.innerHTML = selected.size ? `<span><b>${selected.size}</b> selected</span><span class="spacer"></span>
    <button class="small" onclick="openExport('selected')">⇩ Export selected</button>
    <button class="small" onclick="selected.clear();renderList()">Clear selection</button>` : '';
}

function toggleSelected(id, on) { on ? selected.add(id) : selected.delete(id); renderList(); }
function selectAll(on) { for (const s of shownEndpoints()) on ? selected.add(s.definition.id) : selected.delete(s.definition.id); renderList(); }

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
function closeDrawer() { document.getElementById('shell').classList.remove('open'); draft = null; editing = null; tester = null; }
document.addEventListener('keydown', e => { if (e.key === 'Escape') closeDrawer(); });

// ---------- editor ----------
function openEditor(id) {
  const state = id ? stateOf(id) : null;
  const fromDraft = !!state?.draft;
  const existing = state ? (state.draft?.definition ?? state.definition) : null;
  draft = existing ? structuredClone(existing) : {
    method: 'GET', route: '/', name: '', description: '', group: '', processor: processors[0]?.name ?? '',
    processorConfig: processors[0]?.configurationExample ? structuredClone(processors[0].configurationExample) : null,
    parameters: [], rules: [], validators: [], enabled: true,
    tenant: tenantsShown() && tenantFilter && tenantFilter !== '*' ? tenantFilter : null,
  };
  draft.parameters ??= []; draft.rules ??= []; draft.validators ??= [];
  editing = { state, fromDraft, publishAt: state?.draft?.publishAt ?? null, comment: state?.draft?.comment ?? '', configView: 'form' };
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
    <button onclick="showDraftSnippets()" class="hide-sm" title="Example request of the unsaved definition">Code</button>
    <button onclick="validateDraft()">Validate</button>
    ${revisionsEnabled ? `<button onclick="saveDraft(false)">Save draft</button>` : ''}
    <button class="primary" onclick="${fromDraft ? 'saveDraft(true)' : 'saveDefinition()'}">${state && !fromDraft ? 'Save & publish' : fromDraft ? 'Save & publish draft' : 'Create & publish'}</button>`;
}

// Server validation errors appear below the field whose data-path they are keyed by.
function field(label, html, cls = '', extraErrors = null) {
  const path = html.match(/data-path="([^"]+)"/)?.[1];
  const errs = [...(path && draft ? draftErrors[path] ?? [] : []), ...(extraErrors ?? [])];
  return `<label class="field ${cls}">${label}${html}${errs.length ? `<span class="field-error">${esc(errs.join(' '))}</span>` : ''}</label>`;
}
function sectionErrors(path) {
  const errs = draftErrors[path];
  return errs?.length ? `<p class="field-error" style="margin:0 0 10px">${esc(errs.join(' '))}</p>` : '';
}
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
  // Options are values, or [value, label] pairs.
  return `<select ${withClass(path, attrs)}>${options.map(o => {
    const [v, l] = Array.isArray(o) ? o : [o, o];
    return `<option value="${esc(v)}" ${v === value ? 'selected' : ''}>${esc(l)}</option>`;
  }).join('')}</select>`;
}
function textarea(path, value, attrs = '') {
  return `<textarea ${withClass(path, attrs)} data-json="1" spellcheck="false">${esc(value)}</textarea>`;
}
function checkbox(path, checked, label, attrs = '') {
  return `<label class="check"><input type="checkbox" ${withClass(path, attrs)} ${checked ? 'checked' : ''}> <span>${label}</span></label>`;
}
function toLocalInput(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  return new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
}

function focusField(path) {
  const el = document.querySelector(`#drawer-body [data-path="${CSS.escape(path)}"]`) ?? document.querySelector(`#drawer-body [data-section="${CSS.escape(path.split(/[.[]/)[0])}"]`);
  el?.scrollIntoView({ block: 'center' });
  el?.focus?.();
}

function renderEditor() {
  const d = draft;
  const proc = processors.find(p => p.name.toLowerCase() === (d.processor || '').toLowerCase());
  const errorList = Object.entries(draftErrors);
  const paramNames = d.parameters.map(p => p.name).filter(Boolean);
  const requestValidators = validators.filter(v => v.forRequests);
  const { state, fromDraft } = editing;

  $('#drawer-body').innerHTML = `
    ${errorList.length ? `<div class="errors"><b>Fix these problems:</b><ul>${errorList.map(([k, v]) => `<li><a href="#" onclick="focusField('${esc(k)}');return false"><code>${esc(k)}</code></a>: ${esc(v.join(' '))}</li>`).join('')}</ul></div>` : ''}
    <div id="validation-ok"></div>
    ${fromDraft && state.status !== 'Draft' ? `<div class="notice">✎ You are editing the <b>draft</b>. Revision ${state.definition.revision} keeps serving requests until the draft is published.</div>` : ''}

    <section class="card">
      <h3>Endpoint</h3>
      <div class="grid">
        ${field('Method', select('method', d.method, METHODS, 'data-rerender="1"'))}
        ${field('Route', input('route', d.route, 'placeholder="/orders/{id}" class="mono"'), 'half')}
        ${field('Name', input('name', d.name, 'placeholder="Create order"'))}
        ${field('Description', input('description', d.description), 'wide')}
        ${field('Group <span class="muted">(section in Swagger UI)</span>', input('group', d.group, 'placeholder="Dynamic" list="groups"'), 'half')}
        <datalist id="groups">${[...new Set(endpoints.map(e => e.definition.group).filter(Boolean))].map(g => `<option value="${esc(g)}">`).join('')}</datalist>
        ${tenantsShown() ? `${field('Tenant <span class="muted">(empty: shared by all tenants)</span>', input('tenant', d.tenant, 'placeholder="shared" list="tenant-list" class="mono"'))}
          <datalist id="tenant-list">${tenants.map(t => `<option value="${esc(t)}">`).join('')}</datalist>` : ''}
        <label class="check"><input type="checkbox" data-path="enabled" ${d.enabled ? 'checked' : ''}> Enabled</label>
      </div>
      <p class="hint">Route parameters like <code>{id}</code> need a parameter with source <b>Route</b> — <a href="#" onclick="syncRouteParams();return false">add missing ones</a>.${features.tenant ? ` The endpoint belongs to tenant <b>${esc(features.tenant)}</b>.` : ''}</p>
    </section>

    ${renderProcessorSection(proc)}

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

    ${renderCachingSection()}
    ${renderRateLimitSection()}

    <section class="card">
      <h3>Security &amp; documentation</h3>
      <div class="grid">
        <label class="check"><input type="checkbox" data-path="allowAnonymous" ${d.allowAnonymous ? 'checked' : ''}> Allow anonymous</label>
        <label class="check"><input type="checkbox" data-path="requireAuthorization" ${d.requireAuthorization ? 'checked' : ''}> Require auth</label>
        ${field('Authorization policy', input('authorizationPolicy', d.authorizationPolicy))}
        ${field('Rate limiting policy <span class="muted">(named, or a limit below)</span>', input('rateLimitingPolicy', d.rateLimitingPolicy))}
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

// ---------- processor configuration ----------
// Forms for the built-in processors; every other processor (and "Edit as JSON") gets the JSON editor.
const HEADERS_HINT = 'One per line: <code>Name: value</code>. Values may use <code>{param}</code> and <code>{config:Key}</code> for secrets.';
const PROCESSOR_FORMS = {
  'http-forward': [
    { key: 'url', label: 'Target URL <span class="muted">({param} placeholders)</span>', cls: 'wide', mono: true, placeholder: 'https://backend.example.com/orders/{id}' },
    { key: 'method', label: 'Method <span class="muted">(default: the endpoint\'s)</span>', options: ['', 'GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS'] },
    { key: 'body', label: 'Upstream body', options: [['', 'Auto (default)'], 'Parameters', 'BodyParameters', 'None'] },
    { key: 'timeoutSeconds', label: 'Timeout (s)', type: 'int', placeholder: '30' },
    { key: 'statusCode', label: 'Status code <span class="muted">(default: upstream)</span>', type: 'int' },
    { key: 'headers', label: 'Headers', type: 'headers', cls: 'wide', hint: HEADERS_HINT },
    { key: 'forwardHeaders', label: 'Forward request headers <span class="muted">(comma separated)</span>', type: 'list', cls: 'wide', placeholder: 'Authorization, Accept-Language' },
    { key: 'bodyTemplate', label: 'Body template <span class="muted">(JSON with {{name}}; overrides “Upstream body”)</span>', type: 'json', cls: 'wide' },
    { key: 'responseTemplate', label: 'Response template <span class="muted">(JSON; sees {{response…}} and {{status}})</span>', type: 'json', cls: 'wide' },
  ],
  webhook: [
    { key: 'url', label: 'Webhook URL', cls: 'wide', mono: true, placeholder: 'https://hooks.example.com/orders' },
    { key: 'method', label: 'Method', options: [['', 'POST (default)'], 'PUT', 'PATCH'] },
    { key: 'retries', label: 'Retries', type: 'int', placeholder: '3' },
    { key: 'retryDelayMilliseconds', label: 'First retry after (ms)', type: 'int', placeholder: '500' },
    { key: 'timeoutSeconds', label: 'Timeout per attempt (s)', type: 'int', placeholder: '10' },
    { key: 'statusCode', label: 'Status code on success', type: 'int', placeholder: '202' },
    { key: 'signingSecretConfigurationKey', label: 'Signing secret <span class="muted">(configuration key)</span>', mono: true, placeholder: 'Webhooks:Secret' },
    { key: 'signatureHeader', label: 'Signature header', placeholder: 'X-Webhook-Signature' },
    { key: 'background', label: 'Deliver in the background (answer right away)', type: 'bool' },
    { key: 'headers', label: 'Headers', type: 'headers', cls: 'wide', hint: HEADERS_HINT },
    { key: 'payload', label: 'Payload template <span class="muted">(JSON with {{name}}; default: all parameters)</span>', type: 'json', cls: 'wide' },
  ],
  response: [
    { key: 'statusCode', label: 'Status code', type: 'int', placeholder: '200' },
    { key: 'contentType', label: 'Content type', placeholder: 'application/json' },
    { key: 'body', label: 'Body template <span class="muted">(JSON; "{{qty}}" keeps the type)</span>', type: 'json', cls: 'wide' },
    { key: 'text', label: 'Text template <span class="muted">(instead of the body)</span>', type: 'text', cls: 'wide', placeholder: 'Hello, {{name}}!' },
    { key: 'headers', label: 'Response headers', type: 'headers', cls: 'wide', hint: 'One per line: <code>Name: value</code>, with <code>{param}</code> placeholders.' },
  ],
  'sql-query': [
    { key: 'query', label: 'Query <span class="muted">(one read-only statement; parameters as @name)</span>', type: 'text', cls: 'wide', mono: true, placeholder: 'SELECT id, name FROM customers WHERE country = @country' },
    { key: 'result', label: 'Result', options: [['', 'Rows (default)'], 'Row', 'Value'] },
    { key: 'connection', label: 'Connection <span class="muted">(default: the default one)</span>' },
    { key: 'maxRows', label: 'Max rows', type: 'int', placeholder: '100' },
    { key: 'timeoutSeconds', label: 'Timeout (s)', type: 'int', placeholder: '30' },
  ],
  // 'ef-crud' is added in the ef-crud section below.
};
const formOf = (processor) => PROCESSOR_FORMS[(processor || '').toLowerCase()];

// Configuration errors are plain messages; they go to the field they name ('body', TimeoutSeconds), else to the section.
function configErrors(form) {
  const byKey = {}, other = [];
  for (const message of draftErrors.processorConfig ?? []) {
    const f = form?.find(f => new RegExp(`\\b${f.key}\\b`, 'i').test(message));
    f ? (byKey[f.key] ??= []).push(message) : other.push(message);
  }
  return { byKey, other };
}

function renderProcessorSection(proc) {
  const d = draft;
  const form = formOf(proc?.name ?? d.processor);
  const asForm = form && editing.configView === 'form' && (d.processorConfig === null || (typeof d.processorConfig === 'object' && !Array.isArray(d.processorConfig)));
  const { byKey, other } = asForm ? configErrors(form) : { byKey: {}, other: draftErrors.processorConfig ?? [] };
  if (asForm) normalizeConfigKeys(form);
  const example = proc?.configurationExample ? `<a href="#" onclick="useExample();return false">use example</a>` : '';
  return `
    <section class="card" data-section="processorConfig">
      <h3>Processor<span class="spacer"></span>${form ? `<span class="tabs">${['form', 'json'].map(v => `<button class="small ${(asForm ? 'form' : 'json') === v ? 'active' : ''}" onclick="setConfigView('${v}')">${v === 'form' ? 'Form' : 'JSON'}</button>`).join('')}</span>` : ''}</h3>
      <div class="grid">
        ${field('Processor', select('processor', proc?.name ?? d.processor, processors.map(p => p.name)))}
        <div class="half" style="align-self:end">${proc?.description ? `<span class="muted">${esc(proc.description)}</span>` : ''}</div>
        ${asForm
          ? `${form.map(f => configField(f, byKey[f.key])).join('')}
             ${other.length ? `<p class="field-error wide" style="margin:0">${esc(other.join(' '))}</p>` : ''}
             <p class="hint wide" style="margin:0">Empty fields use the processor's defaults. ${example ? `Start from the ${example}.` : ''}</p>`
          : field(`Configuration (JSON) ${example}`, textarea('processorConfig', json(d.processorConfig), 'rows="6"'), 'wide')}
      </div>
    </section>`;
}

function configField(f, errors) {
  if (f.html) return f.html(errors);
  const value = (draft.processorConfig ?? {})[f.key];
  const path = `processorConfig.${f.key}`;
  const placeholder = f.placeholder ? ` placeholder="${esc(f.placeholder)}"` : '';
  const mono = f.mono ? ' class="mono"' : '';
  let html;
  switch (f.type) {
    case 'int': html = input(path, value, `type="number" step="1" data-number="int" data-optional="1"${placeholder}`); break;
    case 'bool': return `<label class="check ${f.cls || ''}"><input type="checkbox" data-path="${path}" data-optional="1" ${value ? 'checked' : ''}> ${f.label}</label>`;
    case 'json': html = textarea(path, json(value), `rows="4" data-optional="1"${placeholder}`); break;
    case 'text': html = `<textarea ${withClass(path, `rows="3" data-optional="1" spellcheck="false"${placeholder}${mono}`)}>${esc(value)}</textarea>`; break;
    case 'list': html = input(path, (value || []).join(', '), `data-list="1" data-optional="1"${placeholder}`); break;
    case 'headers': html = `<textarea ${withClass(path, `rows="2" data-headers="1" data-optional="1" spellcheck="false" class="mono" placeholder="X-Api-Key: {config:Backend:ApiKey}"`)}>${esc(Object.entries(value || {}).map(([k, v]) => `${k}: ${v}`).join('\n'))}</textarea>`; break;
    default: html = f.options
      ? select(path, value ?? '', typeof f.options === 'function' ? f.options() : f.options, `data-optional="1"${f.rerender ? ' data-rerender="1"' : ''}`)
      : input(path, value, `data-optional="1"${placeholder}${mono}`);
  }
  if (errors?.length) html = html.replace('class="', 'class="invalid ');
  return field(f.label, html + (f.hint ? `<span class="hint" style="margin:0">${f.hint}</span>` : ''), f.cls || '', errors);
}

// "Url" typed in the JSON editor is the form's "url" – the server reads configurations case-insensitively.
function normalizeConfigKeys(form) {
  const config = draft.processorConfig;
  if (!config) return;
  for (const f of form) {
    const key = Object.keys(config).find(k => k !== f.key && k.toLowerCase() === f.key.toLowerCase());
    if (key && config[f.key] === undefined) { config[f.key] = config[key]; delete config[key]; }
  }
}

function setConfigView(view) { editing.configView = view; renderEditor(); }

// ---------- caching & rate limits ----------
function renderCachingSection() {
  const c = draft.caching;
  const notGet = draft.method !== 'GET';
  return `
    <section class="card" data-section="caching">
      <h3>Response caching <label class="check" style="font-weight:400"><input type="checkbox" ${c ? 'checked' : ''} onchange="toggleCaching(this.checked)"> enabled</label></h3>
      ${sectionErrors('caching')}
      ${c ? `<div class="grid">
        ${field('Max age (s) <span class="muted">Cache-Control</span>', input('caching.maxAgeSeconds', c.maxAgeSeconds, 'type="number" min="0" data-number="int" data-optional="1" placeholder="60"'))}
        ${field('Visibility', select('caching.visibility', c.visibility ?? '', [['', 'Default'], 'Public', 'Private'], 'data-optional="1"'))}
        ${field('Output cache (s) <span class="muted">on the server</span>', input('caching.outputCacheSeconds', c.outputCacheSeconds, 'type="number" min="1" data-number="int" data-optional="1"'))}
        ${field('Output cache policy', input('caching.outputCachePolicy', c.outputCachePolicy, 'data-optional="1" placeholder="named policy"'))}
        ${field('Vary by query <span class="muted">(output cache; comma separated)</span>', input('caching.varyByQuery', (c.varyByQuery || []).join(', '), 'data-list="1" data-optional="1" placeholder="all query keys"'), 'half')}
        ${field('Vary by header <span class="muted">(comma separated)</span>', input('caching.varyByHeader', (c.varyByHeader || []).join(', '), 'data-list="1" data-optional="1" placeholder="Accept-Language"'), 'half')}
        ${checkbox('caching.eTag', c.eTag, 'ETag &amp; 304 Not Modified')}
        ${checkbox('caching.noStore', c.noStore, 'No store <span class="muted">(forbid caching)</span>', 'data-rerender="1"')}
        ${draftErrors['caching.eTag'] || draftErrors['caching.noStore'] ? `<p class="field-error wide" style="margin:0">${esc([...(draftErrors['caching.eTag'] ?? []), ...(draftErrors['caching.noStore'] ?? [])].join(' '))}</p>` : ''}
      </div>
      <p class="hint">${notGet ? '<b>Only GET endpoints can be cached</b> – other methods can only use “No store”. ' : ''}Output caching needs <code>AddOutputCache()</code> and <code>UseOutputCache()</code>; header parameters are always part of the key.</p>`
      : '<p class="muted">Not cached. Enable to send <code>Cache-Control</code>, ETags or cache responses on the server.</p>'}
    </section>`;
}

function renderRateLimitSection() {
  const r = draft.rateLimit;
  const algorithm = r?.algorithm || 'FixedWindow';
  return `
    <section class="card" data-section="rateLimit">
      <h3>Rate limit <label class="check" style="font-weight:400"><input type="checkbox" ${r ? 'checked' : ''} onchange="toggleRateLimit(this.checked)"> enabled</label></h3>
      ${sectionErrors('rateLimit')}
      ${r ? `<div class="grid">
        ${field('Algorithm', select('rateLimit.algorithm', algorithm, [['FixedWindow', 'Fixed window'], ['SlidingWindow', 'Sliding window'], ['TokenBucket', 'Token bucket'], ['Concurrency', 'Concurrency']], 'data-rerender="1"'))}
        ${field(algorithm === 'TokenBucket' ? 'Bucket size' : algorithm === 'Concurrency' ? 'Concurrent requests' : 'Requests per window', input('rateLimit.permitLimit', r.permitLimit, 'type="number" min="1" data-number="int" data-optional="1"'))}
        ${algorithm !== 'Concurrency' ? field(algorithm === 'TokenBucket' ? 'Refill every (s)' : 'Window (s)', input('rateLimit.windowSeconds', r.windowSeconds, 'type="number" min="1" data-number="int" data-optional="1" placeholder="60"')) : ''}
        ${algorithm === 'SlidingWindow' ? field('Segments per window', input('rateLimit.segmentsPerWindow', r.segmentsPerWindow, 'type="number" min="1" data-number="int" data-optional="1" placeholder="6"')) : ''}
        ${algorithm === 'TokenBucket' ? field('Tokens per refill', input('rateLimit.tokensPerPeriod', r.tokensPerPeriod, 'type="number" min="1" data-number="int" data-optional="1" placeholder="= bucket size"')) : ''}
        ${field('Queue <span class="muted">(waiting requests)</span>', input('rateLimit.queueLimit', r.queueLimit, 'type="number" min="0" data-number="int" data-optional="1" placeholder="0"'))}
        ${field('Budget per', select('rateLimit.partitionBy', r.partitionBy || 'IpAddress', [['IpAddress', 'Client IP address'], ['User', 'User'], ['Header', 'Header value (API key)'], ['Endpoint', 'Endpoint (all clients)']], 'data-rerender="1"'))}
        ${r.partitionBy === 'Header' ? field('Header', input('rateLimit.partitionHeader', r.partitionHeader, 'class="mono" placeholder="X-Api-Key"')) : ''}
        <label class="check"><input type="checkbox" ${r.quota ? 'checked' : ''} onchange="toggleQuota(this.checked)"> Quota</label>
        ${r.quota ? field('Quota requests', input('rateLimit.quota.limit', r.quota.limit, 'type="number" min="1" data-number="int" data-optional="1"')) : ''}
        ${r.quota ? field('per', select('rateLimit.quota.period', r.quota.period || 'Day', PERIODS)) : ''}
      </div>
      <p class="hint">Needs <code>AddRateLimiter()</code> and <code>UseRateLimiter()</code>. Rejected requests get <code>429</code> with <code>Retry-After</code>; counters live in each instance's memory.</p>`
      : '<p class="muted">No limit of its own. Enable to limit requests per client, user, API key or endpoint – no named policy needed.</p>'}
    </section>`;
}

function toggleCaching(on) { draft.caching = on ? (draft.method === 'GET' ? { maxAgeSeconds: 60 } : { noStore: true }) : null; renderEditor(); }
function toggleRateLimit(on) { draft.rateLimit = on ? { algorithm: 'FixedWindow', permitLimit: 10, windowSeconds: 60, partitionBy: 'IpAddress' } : null; renderEditor(); }
function toggleQuota(on) { if (on) draft.rateLimit.quota = { limit: 1000, period: 'Day' }; else delete draft.rateLimit.quota; renderEditor(); }

// Settings that don't apply to the chosen algorithm / partition are rejected by the server – drop them.
function tidyRateLimit() {
  const r = draft.rateLimit;
  if (!r) return;
  if (r.algorithm !== 'SlidingWindow') delete r.segmentsPerWindow;
  if (r.algorithm !== 'TokenBucket') delete r.tokensPerPeriod;
  if (r.algorithm === 'Concurrency') delete r.windowSeconds;
  if (r.partitionBy !== 'Header') delete r.partitionHeader;
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
  else if (el.dataset.headers) value = parseHeaders(el.value);
  else if (el.dataset.list) value = el.value.split(',').map(s => s.trim()).filter(Boolean);
  else value = el.value;

  // Optional settings (caching, rate limit, processor forms) are left out when empty, so the defaults apply.
  const empty = value === null || value === '' || value === false || (Array.isArray(value) && !value.length) ||
    (value && typeof value === 'object' && !Array.isArray(value) && !Object.keys(value).length);
  if (el.dataset.optional && empty) value = undefined;
  if (path.startsWith('processorConfig.') && (draft.processorConfig === null || typeof draft.processorConfig !== 'object')) draft.processorConfig = {};

  setPath(draft, path, value);

  if (path.endsWith('.allowedValues')) coerceAllowedValues(path);
  if (path === 'processor') {
    // Configurations are processor specific – start from the new processor's example.
    const proc = processors.find(p => p.name === value);
    draft.processorConfig = proc?.configurationExample ? structuredClone(proc.configurationExample) : null;
    editing.configView = 'form';
    renderEditor();
  }
  const vm = path.match(/^validators\[(\d+)\]\.name$/);
  if (vm) {
    const v = validators.find(x => x.name === value);
    draft.validators[+vm[1]].config = v?.configurationExample ? structuredClone(v.configurationExample) : null;
  }
  if (path.startsWith('rateLimit.')) tidyRateLimit();
  if (path === 'caching.noStore' && value) draft.caching = { noStore: true };
  if (el.dataset.rerender) {
    const m = path.match(/^parameters\[(\d+)\]\.source$/);
    if (m && value === 'Route') { draft.parameters[+m[1]].required = true; }
    renderEditor();
  }
}

// "Name: value" lines into a headers object.
function parseHeaders(text) {
  const headers = {};
  for (const line of text.split('\n')) {
    const i = line.indexOf(':');
    if (i > 0) headers[line.slice(0, i).trim()] = line.slice(i + 1).trim();
  }
  return headers;
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
  for (const part of parts.slice(0, -1)) target = target[part] ??= {};
  if (value === undefined) delete target[parts.at(-1)];
  else target[parts.at(-1)] = value;
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
  for (const k of ['name', 'description', 'group', 'tenant', 'authorizationPolicy', 'rateLimitingPolicy']) if (!d[k]) d[k] = null;
  if (d.tenant) d.tenant = d.tenant.trim();
  // A tenant's admin API assigns its own tenant.
  if (features.tenant) delete d.tenant;
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

// The example request of the unsaved definition (POST /snippets), on top of the form.
async function showDraftSnippets() {
  const box = document.getElementById('draft-snippets') ?? Object.assign(document.createElement('div'), { id: 'draft-snippets' });
  $('#drawer-body').prepend(box);
  $('#drawer-body').scrollTop = 0;
  const r = await api(`/snippets?${snippetQuery(draft.tenant)}`, { method: 'POST', body: cleanDraft() });
  if (!r.ok) {
    box.innerHTML = `<div class="errors">${r.status === 404 || r.status === 405 ? 'This server can\'t generate snippets for unsaved definitions – save the endpoint and use “Try”.' : esc(problemText(r.data))}</div>`;
    return;
  }
  const kinds = { curl: r.data.curl, httpie: r.data.httpIe, csharp: r.data.cSharp };
  box.innerHTML = `<section class="card"><h3>Example request <span class="muted">(unsaved definition)</span><span class="spacer"></span>
      <span class="tabs">${Object.keys(kinds).map(k => `<button class="small ${k === snippetKind ? 'active' : ''}" data-draft-snippet="${k}">${SNIPPET_LABELS[k]}</button>`).join('')}</span>
      <button class="small" onclick="copy(document.getElementById('draft-snippet').textContent)">⧉ Copy</button>
      <button class="small" onclick="document.getElementById('draft-snippets').remove()" aria-label="Close">✕</button></h3>
    <pre class="snippet" id="draft-snippet">${esc(kinds[snippetKind])}</pre></section>`;
  for (const b of box.querySelectorAll('[data-draft-snippet]')) b.onclick = () => {
    snippetKind = b.dataset.draftSnippet;
    for (const x of box.querySelectorAll('[data-draft-snippet]')) x.classList.toggle('active', x === b);
    $('#draft-snippet').textContent = kinds[snippetKind];
  };
}

// ---------- history & diffs ----------
async function openHistory(id) {
  const state = stateOf(id);
  const d = state.definition;
  openDrawer(`History of ${d.method} ${d.route}`);
  $('#drawer-foot').innerHTML = `${features.audit ? `<button onclick="openAudit('${id}')" style="margin-right:auto">Audit log</button>` : ''}<button onclick="closeDrawer()">Close</button>`;
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

const diffValue = (v) => v === null || v === undefined ? '' : esc(typeof v === 'string' ? v : JSON.stringify(v, null, 2));

function renderDiff(title, r, target) {
  $(target).innerHTML = !r.ok ? `<div class="errors">${esc(problemText(r.data))}</div>` : `
    <section class="card"><h3>${esc(title)}</h3>
      ${r.data.length ? `<table class="diff"><thead><tr><th>Path</th><th>Change</th><th>Before</th><th>After</th></tr></thead><tbody>
        ${r.data.map(c => `<tr class="d-${c.kind}"><td><code>${esc(c.path)}</code></td><td>${esc(c.kind)}</td>
          <td class="value from">${diffValue(c.from)}</td><td class="value to">${diffValue(c.to)}</td></tr>`).join('')}
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

// ---------- audit ----------
async function openAudit(id) {
  const d = stateOf(id)?.definition;
  openDrawer(`Audit of ${d ? `${d.method} ${d.route}` : id}`);
  $('#drawer-foot').innerHTML = `<button onclick="openAuditLog()" style="margin-right:auto">Whole audit log</button><button onclick="closeDrawer()">Close</button>`;
  $('#drawer-body').innerHTML = '<p class="muted">Loading…</p>';
  const r = await api(`/${id}/audit?limit=200`);
  $('#drawer-body').innerHTML = renderAuditEntries(r, false);
}

function openAuditLog() {
  openDrawer('Audit log');
  $('#drawer-foot').innerHTML = `<button onclick="closeDrawer()">Close</button>`;
  const options = [['', 'All endpoints'], ...endpoints.filter(e => e.status !== 'Draft').map(e => [e.definition.id, `${e.definition.method} ${e.definition.route}${e.definition.tenant ? ` (${e.definition.tenant})` : ''}`])];
  $('#drawer-body').innerHTML = `
    <section class="card"><h3>Filter</h3>
      <form class="grid" id="audit-filter" onsubmit="queryAudit();return false">
        <label class="field half">Endpoint<select name="endpointId">${options.map(([v, l]) => `<option value="${esc(v)}">${esc(l)}</option>`).join('')}</select></label>
        ${tenantsShown() ? `<label class="field">Tenant<input name="tenant" list="audit-tenants" class="mono" placeholder="any"><datalist id="audit-tenants">${tenants.map(t => `<option value="${esc(t)}">`).join('')}</datalist></label>` : ''}
        <label class="field">User<input name="user" placeholder="any"></label>
        <label class="field">From<input name="from" type="datetime-local"></label>
        <label class="field">To<input name="to" type="datetime-local"></label>
        <label class="field">Limit<input name="limit" type="number" min="1" max="1000" value="100"></label>
        <div style="align-self:end"><button class="primary" type="submit">Search</button></div>
      </form>
    </section>
    <div id="audit-result"></div>`;
  queryAudit();
}

async function queryAudit() {
  const form = new FormData($('#audit-filter'));
  const q = new URLSearchParams();
  for (const [k, v] of form) {
    if (!v) continue;
    q.set(k, k === 'from' || k === 'to' ? new Date(v).toISOString() : v);
  }
  $('#audit-result').innerHTML = '<p class="muted">Loading…</p>';
  const r = await api(`/audit?${q}`);
  $('#audit-result').innerHTML = renderAuditEntries(r, true);
}

function renderAuditEntries(r, showEndpoint) {
  if (!r.ok) return `<div class="errors">${r.status === 404 ? 'No queryable audit log is configured on the server.' : esc(problemText(r.data))}</div>`;
  if (!r.data.length) return '<div class="empty">No audit entries.</div>';
  return `<table class="audit">
    <thead><tr><th>When</th><th>Who</th><th>What</th>${showEndpoint ? '<th>Endpoint</th>' : ''}<th>Changes</th></tr></thead>
    <tbody>${r.data.map(e => `
      <tr>
        <td class="muted" title="${esc(e.timestamp)}">${esc(when(e.timestamp))}</td>
        <td>${esc(e.user) || '<span class="muted" title="Outside a request, or anonymous">—</span>'}</td>
        <td><span class="pill k-${esc(e.kind)}">${esc(e.kind)}</span> <span class="muted">r${e.revision}</span></td>
        ${showEndpoint ? `<td><span class="method m-${esc(e.method)}">${esc(e.method)}</span> <code>${esc(e.route)}</code>${e.tenant && tenantsShown() ? ` ${tenantPill(e.tenant)}` : ''}
          ${stateOf(e.endpointId) ? ` <a href="#" onclick="openAudit('${e.endpointId}');return false" title="Only this endpoint">⌕</a>` : ''}</td>` : ''}
        <td>${auditChanges(e)}</td>
      </tr>`).join('')}
    </tbody></table>`;
}

// before → after of every changed property; created and deleted endpoints show the whole definition instead.
function auditChanges(e) {
  const changes = e.changes || [];
  if (e.kind !== 'Updated') {
    const definition = e.kind === 'Deleted' ? e.previous : e.definition;
    return definition ? `<details><summary>${e.kind === 'Deleted' ? 'Deleted definition' : 'Definition'}</summary><pre class="response">${esc(json(definition))}</pre></details>`
      : `<span class="muted">${changes.length} properties</span>`;
  }
  if (!changes.length) return '<span class="muted">no visible change</span>';
  const kind = (c) => c.before === null || c.before === undefined ? 'Added' : c.after === null || c.after === undefined ? 'Removed' : 'Changed';
  return `<details ${changes.length <= 3 ? 'open' : ''}><summary>${changes.length} change${changes.length === 1 ? '' : 's'}: ${esc(changes.slice(0, 3).map(c => c.path).join(', '))}${changes.length > 3 ? '…' : ''}</summary>
    <table class="diff"><thead><tr><th>Path</th><th>Before</th><th>After</th></tr></thead><tbody>
    ${changes.map(c => `<tr class="d-${kind(c)}"><td><code>${esc(c.path)}</code></td><td class="value from">${diffValue(c.before)}</td><td class="value to">${diffValue(c.after)}</td></tr>`).join('')}
    </tbody></table></details>`;
}

// ---------- export & import ----------
function openExport(scope) {
  const shown = shownEndpoints().filter(s => s.status !== 'Draft');
  scope ??= selected.size ? 'selected' : 'all';
  openDrawer('Export endpoints');
  const scopes = [['all', `All endpoints${tenantFilter ? '' : ` (${endpoints.filter(e => e.status !== 'Draft').length})`}`],
    ...(shown.length !== endpoints.length ? [['shown', `Shown in the list (${shown.length})`]] : []),
    ...(selected.size ? [['selected', `Selected (${selected.size})`]] : [])];
  $('#drawer-body').innerHTML = `
    <section class="card"><h3>What</h3>
      <div class="grid">
        <label class="field half">Endpoints<select id="export-scope">${scopes.map(([v, l]) => `<option value="${v}" ${v === scope ? 'selected' : ''}>${esc(l)}</option>`).join('')}</select></label>
        <label class="field">Format<select id="export-format">${features.formats.map(f => `<option value="${esc(f)}">${esc(f.toUpperCase())}</option>`).join('')}</select></label>
      </div>
      <p class="hint">The stable export format (<code>dynamic-endpoints/v1</code>): sorted, without revisions and timestamps – friendly to diffs and Git. Unpublished drafts are not exported.${features.formats.includes('yaml') ? '' : ' YAML needs <code>AddYamlFormat()</code> on the server.'}</p>
    </section>
    <section class="card"><h3>Preview <span class="muted" id="export-size"></span></h3><pre class="snippet" id="export-preview" style="max-height:420px"></pre></section>`;
  $('#drawer-foot').innerHTML = `<button onclick="closeDrawer()">Close</button>
    <button onclick="copy(document.getElementById('export-preview').textContent)">⧉ Copy</button>
    <button class="primary" onclick="downloadExport()">⇩ Download</button>`;
  $('#export-scope').onchange = $('#export-format').onchange = previewExport;
  previewExport();
}

function exportIds() {
  const scope = $('#export-scope').value;
  return scope === 'selected' ? [...selected] : scope === 'shown' ? shownEndpoints().map(s => s.definition.id) : [];
}

async function fetchExport() {
  const q = new URLSearchParams();
  for (const id of exportIds()) q.append('id', id);
  q.set('format', $('#export-format').value);
  return api(`/export?${q}`);
}

async function previewExport() {
  const r = await fetchExport();
  $('#export-preview').textContent = r.ok ? r.text : problemText(r.data);
  $('#export-size').textContent = r.ok ? `${r.text.split('\n').length} lines` : '';
}

async function downloadExport() {
  const r = await fetchExport();
  if (!r.ok) { alert(problemText(r.data)); return; }
  const format = $('#export-format').value;
  const suffix = features.tenant || (tenantFilter && tenantFilter !== '*' ? tenantFilter : '');
  download(`dynamic-endpoints${suffix ? '-' + suffix : ''}.${format === 'yaml' ? 'yaml' : 'json'}`, r.text, format === 'yaml' ? 'application/yaml' : 'application/json');
  toast('Export downloaded');
}

let importPlan = null;  // the dry run the confirmation refers to

function openImport() {
  importPlan = null;
  openDrawer('Import endpoints');
  $('#drawer-body').innerHTML = `
    <section class="card"><h3>File</h3>
      <div class="grid">
        <label class="field half">Upload an export<input type="file" id="import-file" accept=".json,.yaml,.yml,application/json,application/yaml"></label>
        <label class="field half">Mode<select id="import-mode">${Object.entries(IMPORT_MODES).map(([v, l]) => `<option value="${v}" ${v === 'upsert' ? 'selected' : ''}>${esc(l)}</option>`).join('')}</select></label>
        <label class="field wide">…or paste it (${features.formats.map(f => f.toUpperCase()).join(' or ')})<textarea id="import-text" rows="10" spellcheck="false" placeholder='{ "format": "dynamic-endpoints/v1", "endpoints": [ … ] }'></textarea></label>
      </div>
      <p class="hint">Endpoints are matched by id, or by method and route. Nothing is written until you confirm the dry run below; when any endpoint is invalid, nothing is written at all.${features.tenant ? ` Only endpoints of tenant <b>${esc(features.tenant)}</b> are touched.` : ''}</p>
    </section>
    <div id="import-result"></div>`;
  $('#import-file').onchange = (e) => readFileInto(e.target, '#import-text', () => dryRunImport());
  $('#import-text').oninput = () => { delete $('#import-text').dataset.fileName; invalidateImport(); };
  $('#import-mode').onchange = () => { invalidateImport(); if ($('#import-text').value.trim()) dryRunImport(); };
  renderImportFoot();
}

function invalidateImport() { importPlan = null; renderImportFoot(); }

function renderImportFoot() {
  const changes = importPlan ? importPlan.created + importPlan.updated + importPlan.deleted : 0;
  $('#drawer-foot').innerHTML = `<button onclick="closeDrawer()">Close</button>
    <button ${importPlan ? '' : 'class="primary"'} onclick="dryRunImport()">Dry run</button>
    <button class="primary" onclick="confirmImport()" ${importPlan?.succeeded && changes ? '' : 'disabled title="Run a successful dry run with changes first"'}>Import ${changes ? `${changes} change${changes === 1 ? '' : 's'}` : ''}</button>`;
}

async function sendImport(dryRun) {
  const text = $('#import-text').value;
  if (!text.trim()) { toast('Choose or paste a file first'); return null; }
  const mode = $('#import-mode').value;
  return { mode, r: await api(`/import?mode=${mode}${dryRun ? '&dryRun=true' : ''}`, { method: 'POST', raw: text, contentType: contentTypeOf(text, $('#import-text').dataset.fileName) }) };
}

async function dryRunImport() {
  const sent = await sendImport(true);
  if (!sent) return;
  const { r, mode } = sent;
  importPlan = r.ok ? { ...r.data, mode } : null;
  $('#import-result').innerHTML = renderImportResult(r, true);
  renderImportFoot();
}

async function confirmImport() {
  if (!importPlan) return;
  const p = importPlan;
  const parts = [p.created && `create ${p.created}`, p.updated && `update ${p.updated}`, p.deleted && `DELETE ${p.deleted}`].filter(Boolean).join(', ');
  if (!confirm(`Import in ${p.mode} mode: ${parts} endpoint(s)?${p.deleted ? '\n\nDeleted endpoints lose their history.' : ''}`)) return;
  const sent = await sendImport(false);
  if (!sent) return;
  importPlan = null;
  $('#import-result').innerHTML = renderImportResult(sent.r, false);
  renderImportFoot();
  if (sent.r.ok) { toast('Imported'); await load(); }
}

const ACTION_ORDER = ['Invalid', 'Delete', 'Create', 'Update', 'Skip', 'Unchanged'];

function renderImportResult(r, dryRun) {
  if (r.status !== 200 && r.status !== 422) return `<div class="errors">${esc(problemText(r.data))}</div>`;
  const res = r.data;
  const items = [...res.items].sort((a, b) => ACTION_ORDER.indexOf(a.action) - ACTION_ORDER.indexOf(b.action));
  const counts = [['created', 'Create'], ['updated', 'Update'], ['deleted', 'Delete'], ['unchanged', 'Unchanged'], ['skipped', 'Skip'], ['invalid', 'Invalid']]
    .filter(([k]) => res[k]).map(([k, a]) => `<span class="pill a-${a}">${res[k]} ${k}</span>`).join(' ');
  const banner = !res.succeeded
    ? `<div class="errors"><b>${res.invalid} invalid endpoint${res.invalid === 1 ? '' : 's'} – nothing ${dryRun ? 'would be' : 'was'} written.</b> Fix the file and try again.</div>`
    : dryRun ? (res.hasChanges ? `<div class="notice"><span>Dry run – nothing was written yet. Review the changes, then click <b>Import</b>.</span></div>` : '<div class="success">✓ Nothing to change – the store already matches the file.</div>')
    : '<div class="success">✓ Imported.</div>';
  return `${banner}
    <section class="card"><h3>${dryRun ? 'What the import would do' : 'What the import did'} <span class="spacer"></span>${counts}</h3>
      <table><thead><tr><th>Action</th><th>Endpoint</th><th class="hide-sm">Name</th><th>Details</th></tr></thead><tbody>
      ${items.map(i => `<tr>
        <td><span class="pill a-${esc(i.action)}">${esc(i.action)}</span></td>
        <td><span class="method m-${esc(i.method)}">${esc(i.method)}</span> <code>${esc(i.route)}</code></td>
        <td class="hide-sm">${esc(i.name) || '<span class="muted">—</span>'}</td>
        <td>${Object.keys(i.errors || {}).length ? `<ul class="field-error" style="margin:0;padding-left:16px">${Object.entries(i.errors).map(([k, v]) => `<li><code>${esc(k)}</code>: ${esc(v.join(' '))}</li>`).join('')}</ul>`
          : i.changes?.length ? `<span class="muted">changes:</span> ${i.changes.map(c => `<code>${esc(c)}</code>`).join(', ')}` : ''}</td>
      </tr>`).join('')}
      </tbody></table>
    </section>`;
}

// ---------- OpenAPI import ----------
let openApiPlan = null;   // the dry run the confirmation refers to
let openApiTags = [];     // tags of the document (from the last dry run) for the tag → processor table
let openApiTagMap = {};   // tag → processor chosen in the table

const OPENAPI_MODES = {
  create: 'Create – only adds operations that weren\'t imported yet',
  upsert: 'Upsert – also updates endpoints imported from the document before',
  sync: 'Sync – like upsert, and deletes imported endpoints whose operation is gone',
};

const PROCESSOR_SOURCES = {
  OperationExtension: 'extension on the operation', PathExtension: 'extension on the path', Tag: 'tag mapping',
  DocumentExtension: 'extension on the document', Mock: 'mock', Option: 'processor option', Default: 'default processor', Kept: 'kept',
};

function openOpenApiImport() {
  openApiPlan = null; openApiTags = []; openApiTagMap = {};
  openDrawer('Import from OpenAPI');
  $('#drawer-body').innerHTML = `
    <section class="card"><h3>1 · Document</h3>
      <div class="grid">
        <label class="field half">Upload an OpenAPI 3.x document<input type="file" id="oa-file" accept=".json,.yaml,.yml"></label>
        <label class="field half">Mode<select id="oa-mode">${Object.entries(OPENAPI_MODES).map(([v, l]) => `<option value="${v}">${esc(l)}</option>`).join('')}</select></label>
        <label class="field wide">…or paste it (${features.formats.map(f => f.toUpperCase()).join(' or ')})<textarea id="oa-text" rows="8" spellcheck="false" placeholder='{ "openapi": "3.0.1", "paths": { … } }'></textarea></label>
      </div>
    </section>
    <section class="card"><h3>2 · Options</h3>
      <form class="grid" id="oa-options" onsubmit="return false">
        <label class="field">Processor<select name="processor"><option value="">Default</option>${processors.map(p => `<option>${esc(p.name)}</option>`).join('')}</select></label>
        <label class="field">Route prefix<input name="routePrefix" class="mono" placeholder="/imported"></label>
        <label class="field">Group<input name="group" placeholder="from the tags"></label>
        <label class="field">Only tags <span class="muted">(comma separated)</span><input name="tag" placeholder="all operations"></label>
        <label class="field">Document id <span class="muted">(re-imports match it)</span><input name="documentId" placeholder="info.title"></label>
        <label class="check"><input type="checkbox" name="mock"> Mock – answer with the documented examples</label>
        <label class="check"><input type="checkbox" name="enabled"> Enable right away</label>
        <label class="check"><input type="checkbox" name="skipInvalid"> Skip invalid operations</label>
      </form>
      <div id="oa-tags"></div>
      <p class="hint">Creates skeletons – routes, parameters with types and constraints, bodies, response schemas. Processors come from
        <code>x-dynamic-endpoints-processor</code> extensions, then the tag table, then the processor above; <b>Mock</b> uses the
        <code>response</code> processor instead (extensions still win). Endpoints that weren't imported from this document are never
        changed. New endpoints are disabled unless enabled here; updates keep their enabled state.${features.tenant ? ` Only endpoints of tenant <b>${esc(features.tenant)}</b> are touched.` : ''}</p>
    </section>
    <div id="oa-result"></div>`;
  $('#oa-file').onchange = (e) => readFileInto(e.target, '#oa-text', () => dryRunOpenApi());
  $('#oa-text').oninput = () => { delete $('#oa-text').dataset.fileName; invalidateOpenApi(); };
  $('#oa-mode').onchange = () => { invalidateOpenApi(); if ($('#oa-text').value.trim()) dryRunOpenApi(); };
  for (const el of document.querySelectorAll('#oa-options [name]')) el.addEventListener('change', () => { invalidateOpenApi(); renderOpenApiTags(); });
  renderOpenApiTags();
  renderOpenApiFoot();
}

function invalidateOpenApi() { openApiPlan = null; renderOpenApiFoot(); }

// Tag → processor table, filled from the document's tags once a dry run read them.
function renderOpenApiTags() {
  const box = $('#oa-tags');
  if (!box) return;
  if (!openApiTags.length) { box.innerHTML = ''; return; }
  const mock = !!new FormData($('#oa-options')).get('mock');
  box.innerHTML = `<h4 style="margin:12px 0 6px">Processor per tag <span class="muted" style="font-weight:normal">(an operation gets the mapping of its first mapped tag${mock ? ' – ignored in mock mode' : ''})</span></h4>
    <table><thead><tr><th>Tag</th><th>Processor</th></tr></thead><tbody>
    ${openApiTags.map(t => `<tr><td><code>${esc(t)}</code></td>
      <td><select data-tag="${esc(t)}" ${mock ? 'disabled' : ''} onchange="setOpenApiTag(this.dataset.tag, this.value)"><option value="">—</option>${processors.map(p => `<option ${openApiTagMap[t] === p.name ? 'selected' : ''}>${esc(p.name)}</option>`).join('')}</select></td></tr>`).join('')}
    </tbody></table>`;
}

function setOpenApiTag(tag, processor) {
  if (processor) openApiTagMap[tag] = processor; else delete openApiTagMap[tag];
  invalidateOpenApi();
}

function openApiChanges(p) { return p ? p.created + p.updated + p.deleted : 0; }

function renderOpenApiFoot() {
  const n = openApiChanges(openApiPlan);
  $('#drawer-foot').innerHTML = `<button onclick="closeDrawer()">Close</button>
    <button ${openApiPlan ? '' : 'class="primary"'} onclick="dryRunOpenApi()">3 · Dry run</button>
    <button class="primary" onclick="confirmOpenApi()" ${openApiPlan?.succeeded && n ? '' : 'disabled title="Run a successful dry run with changes first"'}>4 · Import ${n ? `${n} change${n === 1 ? '' : 's'}` : ''}</button>`;
}

function openApiQuery(dryRun) {
  const q = new URLSearchParams();
  const form = new FormData($('#oa-options'));
  for (const name of ['processor', 'routePrefix', 'group', 'documentId']) if (form.get(name)?.trim()) q.set(name, form.get(name).trim());
  for (const tag of (form.get('tag') || '').split(',').map(t => t.trim()).filter(Boolean)) q.append('tag', tag);
  for (const name of ['mock', 'enabled', 'skipInvalid']) if (form.get(name)) q.set(name, 'true');
  if (!form.get('mock')) for (const [tag, processor] of Object.entries(openApiTagMap)) q.append('processorByTag', `${tag}:${processor}`);
  q.set('mode', $('#oa-mode').value);
  if (dryRun) q.set('dryRun', 'true');
  return q;
}

async function sendOpenApi(dryRun) {
  const text = $('#oa-text').value;
  if (!text.trim()) { toast('Choose or paste a document first'); return null; }
  return api(`/import/openapi?${openApiQuery(dryRun)}`, { method: 'POST', raw: text, contentType: contentTypeOf(text, $('#oa-text').dataset.fileName) });
}

async function dryRunOpenApi() {
  const r = await sendOpenApi(true);
  if (!r) return;
  openApiPlan = r.ok ? { ...r.data, mode: $('#oa-mode').value } : null;
  if (r.data?.tags && r.data.tags.join('\n') !== openApiTags.join('\n')) { openApiTags = r.data.tags; renderOpenApiTags(); }
  $('#oa-result').innerHTML = renderOpenApiResult(r, true);
  renderOpenApiFoot();
}

async function confirmOpenApi() {
  if (!openApiPlan) return;
  const p = openApiPlan;
  const parts = [p.created && `create ${p.created}`, p.updated && `update ${p.updated}`, p.deleted && `DELETE ${p.deleted}`].filter(Boolean).join(', ');
  if (!confirm(`Import in ${p.mode} mode: ${parts} endpoint(s)?${p.deleted ? '\n\nDeleted endpoints lose their history.' : ''}`)) return;
  const r = await sendOpenApi(false);
  if (!r) return;
  openApiPlan = null;
  $('#oa-result').innerHTML = renderOpenApiResult(r, false);
  renderOpenApiFoot();
  if (r.ok) { toast(`Imported: ${r.data.created} created, ${r.data.updated} updated, ${r.data.deleted} deleted`); await load(); }
}

function renderOpenApiResult(r, dryRun) {
  if (r.status !== 200 && r.status !== 422) return `<div class="errors">${esc(problemText(r.data))}</div>`;
  const res = r.data;
  const operations = res.operations.map((o, i) => ({ o, i })).sort((a, b) => ACTION_ORDER.indexOf(a.o.action) - ACTION_ORDER.indexOf(b.o.action));
  const counts = [['created', 'Create'], ['updated', 'Update'], ['deleted', 'Delete'], ['unchanged', 'Unchanged'], ['skipped', 'Skip'], ['invalid', 'Invalid']]
    .filter(([k]) => res[k]).map(([k, a]) => `<span class="pill a-${a}">${res[k]} ${k}</span>`).join(' ');
  const banner = !res.succeeded
    ? `<div class="errors"><b>${res.invalid} operation${res.invalid === 1 ? '' : 's'} can't be imported – nothing ${dryRun ? 'would be' : 'was'} written.</b> Tick <b>Skip invalid operations</b> to import the rest.</div>`
    : dryRun ? (res.hasChanges ? '<div class="notice"><span>Dry run – nothing was written yet. Review the operations, then click <b>Import</b>.</span></div>' : '<div class="success">Nothing to change – the endpoints already match the document.</div>')
    : `<div class="success">✓ Imported: ${res.created} created, ${res.updated} updated, ${res.deleted} deleted.</div>`;
  return `${banner}
    ${res.warnings?.length ? `<div class="notice"><div><b>Warnings</b><ul style="margin:4px 0 0;padding-left:18px">${res.warnings.map(w => `<li>${esc(w)}</li>`).join('')}</ul></div></div>` : ''}
    <section class="card"><h3>Operations${res.document ? ` <span class="muted" style="font-weight:normal">of ${esc(res.document)}</span>` : ''} <span class="spacer"></span>${counts}</h3>
      <table><thead><tr><th>Action</th><th>Operation</th><th class="hide-sm">Processor</th><th>Details</th><th></th></tr></thead><tbody>
      ${operations.map(({ o, i }) => `<tr>
        <td><span class="pill a-${esc(o.action)}">${esc(o.action)}</span></td>
        <td><span class="method m-${esc(o.method)}">${esc(o.method)}</span> <code>${esc(o.path)}</code>${o.definition && o.action !== 'Delete' && o.definition.route !== o.path ? ` <span class="muted">→ <code>${esc(o.definition.route)}</code></span>` : ''}${o.operationId ? `<div class="muted" style="font-size:12px">${esc(o.operationId)}</div>` : ''}</td>
        <td class="hide-sm">${o.action === 'Delete' || o.action === 'Skip' ? '' : `${o.processor ? `<code>${esc(o.processor)}</code>` : '<span class="muted">none</span>'}<div class="muted" style="font-size:12px" title="${esc(o.processorReason)}">${esc(o.processorSource === 'Tag' || o.processorSource === 'Mock' ? o.processorReason : PROCESSOR_SOURCES[o.processorSource] ?? o.processorSource)}</div>`}</td>
        <td>${Object.keys(o.errors || {}).length ? `<ul class="field-error" style="margin:0;padding-left:16px">${Object.entries(o.errors).map(([k, v]) => `<li><code>${esc(k)}</code>: ${esc(v.join(' '))}</li>`).join('')}</ul>` : ''}
          ${o.reason ? `<div class="muted">${esc(o.reason)}</div>` : ''}
          ${o.changes?.length ? `<span class="muted">changes:</span> ${o.changes.map(c => `<code>${esc(c)}</code>`).join(', ')}` : ''}
          ${o.unmapped?.length ? `<ul class="muted" style="margin:0;padding-left:16px;font-size:12px">${o.unmapped.map(u => `<li>${esc(u)}</li>`).join('')}</ul>` : ''}</td>
        <td class="actions">${o.definition ? `<button class="small" onclick="toggleOpenApiDefinition(${i})">Definition</button>` : ''}</td>
      </tr>${o.definition ? `<tr id="oa-def-${i}" hidden><td colspan="5"><pre class="response">${esc(json(o.definition))}</pre></td></tr>` : ''}`).join('')}
      </tbody></table>
    </section>`;
}

function toggleOpenApiDefinition(i) { const row = document.getElementById(`oa-def-${i}`); row.hidden = !row.hidden; }

// ---------- ef-crud (DynamicEndpoints.EntityFrameworkCore) ----------
// The entities the developer exposed (GET /crud/entities): the form of the ef-crud processor and the "Scaffold CRUD" wizard.
const CRUD_OPERATIONS = ['list', 'get', 'create', 'update', 'patch', 'delete'];
const CRUD_METHODS = { list: 'GET', get: 'GET', create: 'POST', update: 'PUT', patch: 'PATCH', delete: 'DELETE' };
let crudEntities = [];
let crudPlan = null;    // the last successful scaffold dry run

async function loadCrudEntities() {
  const r = await api('/crud/entities');
  crudEntities = r.ok && Array.isArray(r.data) ? r.data : [];
}

const crudEntityOf = (name) => crudEntities.find(e => e.name.toLowerCase() === String(name || '').toLowerCase());
const crudDraftEntity = () => crudEntityOf((draft?.processorConfig ?? {}).entity);

PROCESSOR_FORMS['ef-crud'] = [
  { key: 'entity', label: 'Entity', rerender: true, options: () => [['', '— choose —'], ...crudEntities.map(e => e.name)] },
  { key: 'operation', label: 'Operation', rerender: true,
    options: () => [['', 'list (default)'], ...(crudDraftEntity()?.operations ?? CRUD_OPERATIONS).filter(o => o !== 'list').map(o => [o, `${o} (${CRUD_METHODS[o]})`])] },
  { key: 'key', label: 'Key route parameter <span class="muted">(get, update, patch, delete)</span>', mono: true, placeholder: 'the key field' },
  { key: 'pageSize', label: 'Page size <span class="muted">(list)</span>', type: 'int', placeholder: '50' },
  { key: 'maxPageSize', label: 'Max page size <span class="muted">(list)</span>', type: 'int', placeholder: '200' },
  { key: 'sort', label: 'Default order <span class="muted">(list; name,-price)</span>', mono: true },
  { key: 'sortParameter', label: 'Sort parameter <span class="muted">(list; a query parameter, e.g. sort)</span>', mono: true },
  { key: 'requireIfMatch', label: 'Require If-Match (428 without the ETag)', type: 'bool' },
  { key: 'filters', label: 'Filters <span class="muted">(list; JSON array)</span>', type: 'json', cls: 'wide', placeholder: '[{ "field": "price", "operator": "gte", "parameter": "minPrice" }]' },
  { key: '_fields', html: () => crudFieldsInfo() },
];

// What the chosen entity exposes – the fields admins can map parameters, filters and sorting to.
function crudFieldsInfo() {
  const entity = crudDraftEntity();
  if (!entity) {
    return `<p class="hint wide" style="margin:0">${crudEntities.length ? 'Choose an entity to see its fields.' : 'No entities are exposed to this admin API.'}</p>`;
  }
  const op = (draft.processorConfig ?? {}).operation || 'list';
  const writes = ['create', 'update', 'patch'].includes(op);
  return `<div class="wide"><table><thead><tr><th>Field</th><th>Type</th><th>Write</th><th>Filter</th><th>Sort</th></tr></thead><tbody>
    ${entity.fields.map(f => `<tr><td><code>${esc(f.name)}</code>${f.key ? ' <span class="pill">key</span>' : ''}${f.required ? ' <span class="muted">required</span>' : ''}</td>
      <td class="muted">${esc([].concat(f.schema.type).join(' | '))}${f.schema.format ? ` · ${esc(f.schema.format)}` : ''}${f.schema.maxLength ? ` · ≤ ${f.schema.maxLength}` : ''}${f.schema.enum ? ` · ${esc(f.schema.enum.filter(v => v !== null).join(', '))}` : ''}</td>
      <td>${!f.readOnly ? '✓' : f.creatable ? '<span class="muted">create</span>' : '<span class="muted">read-only</span>'}</td>
      <td class="muted">${f.filterable ? esc(f.operators.join(' ')) : ''}</td><td>${f.sortable ? '✓' : ''}</td></tr>`).join('')}
    </tbody></table>
    <p class="hint" style="margin:6px 0 0">${writes ? 'Body parameters named like writable fields are written; nothing else is.' : op === 'list'
      ? 'Rows come back as <code>{ items, page, pageSize, total }</code>; declare <code>page</code>/<code>pageSize</code> query parameters to let clients page.'
      : `The key comes from the route parameter <code>{${esc(entity.key || 'id')}}</code>.`}
      ${entity.tenantColumn ? ' Rows are filtered by tenant.' : ''}${entity.concurrencyToken ? ' Responses carry an <code>ETag</code>; updates and deletes check <code>If-Match</code>.' : ''}
      <a href="#" onclick="openCrudScaffold('${esc(entity.name)}');return false">Scaffold all endpoints of ${esc(entity.name)}</a> instead.</p></div>`;
}

function openCrudScaffold(entity) {
  crudPlan = null;
  openDrawer('Scaffold CRUD endpoints');
  const first = crudEntityOf(entity) ?? crudEntities[0];
  $('#drawer-body').innerHTML = `
    <section class="card"><h3>1 · Entity</h3>
      <form class="grid" id="crud-options" onsubmit="return false">
        <label class="field">Entity<select name="entity">${crudEntities.map(e => `<option ${e === first ? 'selected' : ''}>${esc(e.name)}</option>`).join('')}</select></label>
        <label class="field">Route prefix<input name="routePrefix" class="mono" placeholder="/${esc(first?.name ?? 'items')}"></label>
        <label class="field">Group<input name="group" placeholder="${esc(first?.name ?? '')}"></label>
        <div class="field wide">Operations<div id="crud-operations"></div></div>
        <label class="check"><input type="checkbox" name="enabled"> Enable right away</label>
      </form>
      <p class="hint">Generates one endpoint per operation from the entity's EF Core model: the key in the route, parameters with types,
        required fields, max lengths, precision and enum values, paging, sorting and filters. Only the fields the application exposed
        are used. Existing routes are skipped; new endpoints are disabled unless enabled here.${features.tenant ? ` They belong to tenant <b>${esc(features.tenant)}</b>.` : ''}</p>
    </section>
    <div id="crud-result"></div>`;
  const form = $('#crud-options');
  form.entity.onchange = () => { renderCrudOperations(); const e = crudEntityOf(form.entity.value); form.routePrefix.placeholder = `/${e.name}`; form.group.placeholder = e.name; invalidateCrud(); };
  form.addEventListener('change', invalidateCrud);
  renderCrudOperations();
  renderCrudFoot();
}

function renderCrudOperations() {
  const entity = crudEntityOf($('#crud-options').entity.value);
  $('#crud-operations').innerHTML = (entity?.operations ?? []).map(o =>
    `<label class="check" style="display:inline-flex;margin-right:12px"><input type="checkbox" name="operation" value="${o}" checked> ${o} <span class="muted">&nbsp;${CRUD_METHODS[o]}</span></label>`).join('');
}

function invalidateCrud() { crudPlan = null; renderCrudFoot(); }

function renderCrudFoot() {
  const n = crudPlan?.created ?? 0;
  $('#drawer-foot').innerHTML = `<button onclick="closeDrawer()">Close</button>
    <button ${crudPlan ? '' : 'class="primary"'} onclick="dryRunCrud()">2 · Dry run</button>
    <button class="primary" onclick="confirmCrud()" ${crudPlan?.succeeded && n ? '' : 'disabled title="Run a successful dry run with new endpoints first"'}>3 · Create ${n ? `${n} endpoint${n === 1 ? '' : 's'}` : ''}</button>`;
}

function crudQuery(dryRun) {
  const form = new FormData($('#crud-options'));
  const q = new URLSearchParams({ entity: form.get('entity') });
  for (const name of ['routePrefix', 'group']) if (form.get(name)?.trim()) q.set(name, form.get(name).trim());
  for (const op of form.getAll('operation')) q.append('operation', op);
  if (form.get('enabled')) q.set('enabled', 'true');
  if (dryRun) q.set('dryRun', 'true');
  return q;
}

async function dryRunCrud() {
  if (!new FormData($('#crud-options')).getAll('operation').length) { toast('Pick at least one operation'); return; }
  const r = await api(`/scaffold/crud?${crudQuery(true)}`, { method: 'POST' });
  crudPlan = r.ok ? r.data : null;
  $('#crud-result').innerHTML = renderCrudResult(r, true);
  renderCrudFoot();
}

async function confirmCrud() {
  if (!crudPlan || !confirm(`Create ${crudPlan.created} endpoint(s) for ${crudPlan.entity}?`)) return;
  const r = await api(`/scaffold/crud?${crudQuery(false)}`, { method: 'POST' });
  crudPlan = null;
  $('#crud-result').innerHTML = renderCrudResult(r, false);
  renderCrudFoot();
  if (r.ok) { toast(`Created ${r.data.created} endpoint(s)`); await load(); }
}

function renderCrudResult(r, dryRun) {
  if (r.status !== 200 && r.status !== 422) return `<div class="errors">${esc(problemText(r.data))}</div>`;
  const res = r.data;
  const counts = [['created', 'Create'], ['skipped', 'Skip'], ['invalid', 'Invalid']]
    .filter(([k]) => res[k]).map(([k, a]) => `<span class="pill a-${a}">${res[k]} ${k}</span>`).join(' ');
  const banner = !res.succeeded
    ? `<div class="errors"><b>${res.invalid} endpoint${res.invalid === 1 ? '' : 's'} can't be created – nothing ${dryRun ? 'would be' : 'was'} written.</b></div>`
    : dryRun ? (res.created ? '<div class="notice"><span>Dry run – nothing was written yet. Review the endpoints, then click <b>Create</b>.</span></div>' : '<div class="success">Nothing to create – all routes exist already.</div>')
    : `<div class="success">✓ Created ${res.created} endpoint${res.created === 1 ? '' : 's'}${res.skipped ? `, skipped ${res.skipped}` : ''}.</div>`;
  return `${banner}
    <section class="card"><h3>Endpoints <span class="muted" style="font-weight:normal">of ${esc(res.entity)}</span><span class="spacer"></span>${counts}</h3>
      <table><thead><tr><th>Action</th><th>Endpoint</th><th class="hide-sm">Operation</th><th>Details</th><th></th></tr></thead><tbody>
      ${res.operations.map((o, i) => `<tr>
        <td><span class="pill a-${esc(o.action)}">${esc(o.action)}</span></td>
        <td><span class="method m-${esc(o.method)}">${esc(o.method)}</span> <code>${esc(o.route)}</code></td>
        <td class="hide-sm"><code>${esc(o.operation)}</code> <span class="muted">${o.definition.parameters.length} parameter${o.definition.parameters.length === 1 ? '' : 's'}</span></td>
        <td>${Object.keys(o.errors || {}).length ? `<ul class="field-error" style="margin:0;padding-left:16px">${Object.entries(o.errors).map(([k, v]) => `<li><code>${esc(k)}</code>: ${esc(v.join(' '))}</li>`).join('')}</ul>` : o.action === 'Skip' ? '<span class="muted">the route exists</span>' : ''}</td>
        <td class="actions"><button class="small" onclick="toggleCrudDefinition(${i})">Definition</button></td>
      </tr><tr id="crud-def-${i}" hidden><td colspan="5"><pre class="response">${esc(json(o.definition))}</pre></td></tr>`).join('')}
      </tbody></table>
    </section>`;
}

function toggleCrudDefinition(i) { const row = document.getElementById(`crud-def-${i}`); row.hidden = !row.hidden; }

// ---------- example values (fallback for servers without the snippets API) ----------
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

// ---------- tenants in requests ----------
// The tenant a request of the "Try" console is sent as: the endpoint's own, the tenant of this admin API, or the one entered.
function requestTenant(d) {
  return d.tenant || features.tenant || ($('#test-tenant')?.value.trim() || null);
}
function tenantPrefix(tenant) {
  const t = features.tenancy;
  if (!t?.routePrefix) return '';
  return tenant ? t.routePrefix.replace(new RegExp(`\\{${t.routeParameter}(:[^}]*)?\\}`, 'i'), enc(tenant)) : t.routePrefix;
}
function snippetQuery(tenant) {
  const q = new URLSearchParams({ baseUrl: location.origin + PATH_BASE });
  if (tenant) q.set('tenant', tenant);
  return q;
}

// ---------- tester ----------
const SNIPPET_LABELS = { curl: 'curl', httpie: 'HTTPie', csharp: 'C# HttpClient' };

async function openTester(id) {
  const d = stateOf(id).definition;
  const nonBody = d.parameters.filter(p => p.source !== 'Body');
  const hasBody = !(d.method === 'GET' || d.method === 'DELETE' || d.parameters.some(p => p.source === 'Form'));
  // Shared endpoints are served to every tenant – with tenant routing, the console needs one to call them.
  const askTenant = features.tenancy && !d.tenant && !features.tenant && (features.tenancy.routePrefix || features.tenancy.header);
  tester = { id, server: null, snippets: null, seq: 0, timer: 0 };
  openDrawer(`Try ${d.method} ${d.route}`);
  $('#drawer-body').innerHTML = `
    <section class="card">
      <h3><span class="method m-${d.method}">${d.method}</span> <code>${esc(d.route)}</code>${d.tenant && features.tenancy ? ` ${tenantPill(d.tenant)}` : ''}<span class="spacer"></span>
        <button class="small" onclick="fillExample('${id}')" title="Generated from the parameters, their constraints and examples">↻ Example values</button></h3>
      ${d.description ? `<p class="muted">${esc(d.description)}</p>` : ''}
      ${askTenant ? `<div class="grid" style="margin-bottom:10px">${field(`Tenant <span class="muted">(${features.tenancy.routePrefix ? `route prefix ${esc(features.tenancy.routePrefix)}` : `header ${esc(features.tenancy.header)}`})</span>`,
        `<input id="test-tenant" class="mono" list="tenant-options" value="${esc(tenantFilter && tenantFilter !== '*' ? tenantFilter : features.tenancy.routePrefix ? tenants[0] ?? '' : '')}" placeholder="${features.tenancy.routePrefix ? 'required' : 'none'}"><datalist id="tenant-options">${tenants.map(t => `<option value="${esc(t)}">`).join('')}</datalist>`)}</div>` : ''}
      ${nonBody.length ? `<div class="grid">${nonBody.map(p => field(
        `${esc(p.sourceName || p.name)} <span class="muted">(${p.source.toLowerCase()}, ${p.type}${p.required || p.source === 'Route' ? ', required' : ''})</span>`,
        isFile(p)
          ? `<input type="file" data-test="${esc(p.sourceName || p.name)}" data-source="${p.source}" ${p.type === 'Array' ? 'multiple' : ''}>`
          : `<input data-test="${esc(p.sourceName || p.name)}" data-source="${p.source}" value="${esc(sampleText(p))}" class="mono"${p.pattern ? ` placeholder="${esc(p.pattern)}"` : ''}>`)).join('')}</div>` : ''}
      ${hasBody ? field('Body (JSON)', `<textarea id="test-body" rows="8" spellcheck="false">${esc(json(sampleBody(d)))}</textarea>`) : ''}
      <p class="hint">Array query parameters: separate values with commas. Clear a field to omit it.</p>
    </section>
    <section class="card"><h3>Response <span id="test-status"></span></h3><pre class="response" id="test-response">Click “Send”.</pre></section>
    <section class="card"><h3>Code <span class="muted" id="snippet-source"></span><span class="spacer"></span>
        <span class="tabs">${Object.keys(SNIPPET_LABELS).map(k => `<button class="small ${k === snippetKind ? 'active' : ''}" data-snippet="${k}" onclick="showSnippet('${id}', '${k}')">${SNIPPET_LABELS[k]}</button>`).join('')}</span>
        <button class="small" onclick="copy(document.getElementById('snippet').textContent)">⧉ Copy</button></h3>
      <pre class="snippet" id="snippet"></pre></section>`;
  const swagger = swaggerLink().url;
  $('#drawer-foot').innerHTML = `${swagger ? `<a class="btn" href="${esc(swagger)}" target="_blank" style="margin-right:auto">Open in Swagger UI ↗</a>` : '<span style="margin-right:auto"></span>'}
    <button onclick="copy(snippet('${id}', 'curl'))">Copy as curl</button>
    <button onclick="copy(snippet('${id}', 'httpie'))" class="hide-sm">Copy as HTTPie</button>
    <button onclick="copy(snippet('${id}', 'csharp'))" class="hide-sm">Copy as C#</button>
    <button onclick="closeDrawer()">Close</button><button class="primary" onclick="sendTest('${id}')">Send ▶</button>`;
  for (const el of document.querySelectorAll('#drawer-body [data-test], #test-body, #test-tenant')) el.addEventListener('input', () => scheduleSnippets(id));
  showSnippet(id);
  await fillExample(id);
}

// Example values come from the server (the same generator as GET /{id}/snippets); older servers: generated here.
async function fillExample(id) {
  const d = stateOf(id).definition;
  const t = tester;
  const r = await api(`/${id}/snippets?${snippetQuery(requestTenant(d))}`);
  if (tester !== t) return;
  if (tester.server === null) tester.server = r.ok;
  if (r.ok) applyServerExample(d, r.data.request);
  else {
    for (const el of document.querySelectorAll('[data-test]')) {
      const p = d.parameters.find(x => (x.sourceName || x.name) === el.dataset.test);
      if (p && el.type !== 'file') el.value = sampleText(p);
    }
    if ($('#test-body')) $('#test-body').value = json(sampleBody(d));
  }
  tester.snippets = r.ok ? r.data : null;
  showSnippet(id);
}

function applyServerExample(d, request) {
  const url = new URL(request.url);
  const route = routeValues(d, url.pathname.slice(encodeURI(PATH_BASE + tenantPrefix(requestTenant(d))).length) || '/');
  for (const el of document.querySelectorAll('[data-test]')) {
    if (el.type === 'file') continue;
    const name = el.dataset.test, same = (x) => x.name.toLowerCase() === name.toLowerCase();
    switch (el.dataset.source) {
      case 'Route': if (route && route[name.toLowerCase()] !== undefined) el.value = route[name.toLowerCase()]; break;
      case 'Query': el.value = request.query.filter(same).map(x => x.value).join(','); break;
      case 'Header': el.value = request.headers.find(same)?.value ?? ''; break;
      case 'Form': el.value = request.form.filter(f => same(f) && !f.fileName).map(f => f.value).join(','); break;
    }
  }
  if ($('#test-body')) $('#test-body').value = request.body === null || request.body === undefined ? '' : json(request.body);
}

// Route values of an example URL, by matching it against the route template.
function routeValues(d, path) {
  const names = [];
  const pattern = d.route.split(/(\{[^}]*\})/).map(part => {
    const m = part.match(/^\{(\*{0,2})([A-Za-z_][A-Za-z0-9_]*)/);
    if (!m) return part.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    names.push(m[2].toLowerCase());
    return m[1] ? '(.*)' : '([^/]*)';
  }).join('');
  const match = new RegExp(`^${pattern}/?$`, 'i').exec(path);
  return match ? Object.fromEntries(names.map((n, i) => [n, decodeURIComponent(match[i + 1])])) : null;
}

// The request as entered in the form – shared by "Send" and the fallback snippets.
function buildRequest(d) {
  let path = d.route;
  const query = new URLSearchParams();
  const headers = {};
  const fields = [], files = [];
  const isForm = d.parameters.some(p => p.source === 'Form');
  const tenant = requestTenant(d);
  if (tenant && features.tenancy?.header) headers[features.tenancy.header] = tenant;
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
  const url = PATH_BASE + tenantPrefix(tenant) + path + (query.size ? `?${query}` : '');
  return { method: d.method, url, absoluteUrl: location.origin + url, headers, body, isForm, fields, files };
}

// The definition with the entered values as examples – so the server's snippets show exactly what "Send" sends.
function definitionAsEntered(d) {
  const copyOf = structuredClone(d);
  const typed = (v, type) => (type === 'Integer' || type === 'Number') && v.trim() !== '' && !isNaN(Number(v)) ? Number(v)
    : type === 'Boolean' && /^(true|false)$/i.test(v.trim()) ? v.trim().toLowerCase() === 'true'
    : type === 'Object' ? (() => { try { return JSON.parse(v); } catch { return v; } })()
    : v;
  copyOf.parameters = d.parameters.flatMap(p => {
    if (p.source === 'Body') return [p];
    const el = [...document.querySelectorAll('[data-test]')].find(x => x.dataset.test === (p.sourceName || p.name));
    if (!el || el.type === 'file') return [p];
    if (el.value === '') return p.source === 'Route' ? [p] : [];
    const example = p.type === 'Array' ? el.value.split(',').map(v => typed(v.trim(), p.itemType || 'String')) : typed(el.value, p.type);
    return [{ ...p, example, default: null }];
  });
  const bodyEl = $('#test-body');
  if (bodyEl) {
    const body = bodyEl.value.trim();
    if (!body) { copyOf.parameters = copyOf.parameters.filter(p => p.source !== 'Body'); copyOf.requestExample = null; }
    else {
      const parsed = JSON.parse(body);  // throws for invalid JSON – the caller keeps the last snippets
      copyOf.requestExample = parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed : null;
    }
  } else copyOf.requestExample = null;
  return copyOf;
}

function scheduleSnippets(id) {
  if (!tester?.server) { showSnippet(id); return; }
  clearTimeout(tester.timer);
  tester.timer = setTimeout(() => refreshSnippets(id), 250);
}

async function refreshSnippets(id) {
  const t = tester;
  const d = stateOf(id).definition;
  let definition;
  try { definition = definitionAsEntered(d); $('#test-body')?.classList.remove('invalid'); }
  catch { $('#test-body')?.classList.add('invalid'); return; }
  const seq = ++t.seq;
  const r = await api(`/snippets?${snippetQuery(requestTenant(d))}`, { method: 'POST', body: definition });
  if (tester !== t || seq !== t.seq) return;  // closed, or a newer edit is on its way
  if (!r.ok) { t.server = false; t.snippets = null; }  // e.g. a server without POST /snippets – generate here
  else t.snippets = r.data;
  showSnippet(id);
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
  const limit = ['x-ratelimit-remaining', 'retry-after', 'cache-control', 'etag', 'age'].map(h => response.headers.get(h) ? `${h}: ${response.headers.get(h)}` : '').filter(Boolean).join(' · ');
  $('#test-status').innerHTML = `<span class="status ${response.ok ? 's-Active' : 's-Invalid'}">${response.status}</span> <span class="muted">${ms} ms · ${esc(req.method)} ${esc(req.url)}${limit ? ` · ${esc(limit)}` : ''}</span>`;
  $('#test-response').textContent = pretty || '(empty body)';
}

function showSnippet(id, kind) {
  if (kind) snippetKind = kind;
  for (const b of document.querySelectorAll('[data-snippet]')) b.classList.toggle('active', b.dataset.snippet === snippetKind);
  const el = document.getElementById('snippet');
  if (el) el.textContent = snippet(id, snippetKind);
  const source = document.getElementById('snippet-source');
  if (source) source.textContent = tester?.snippets ? '' : tester?.server === false ? '(generated in the browser)' : '';
}

const sh = (v) => `'${String(v).replace(/'/g, `'\\''`)}'`;
const compact = (body) => { try { return JSON.stringify(JSON.parse(body)); } catch { return body; } };
const fileName = (f) => f.file?.name ?? `path/to/${f.name}`;

// The server's snippets when it has them; the browser's own generator otherwise.
function snippet(id, kind) {
  if (tester?.id === id && tester.snippets) return { curl: tester.snippets.curl, httpie: tester.snippets.httpIe, csharp: tester.snippets.cSharp }[kind];
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
detect().then(load);
