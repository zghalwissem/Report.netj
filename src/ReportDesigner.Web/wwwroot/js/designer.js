/* Report Designer — designer web de modèles de rapports rendus en PDF par ReportDesigner.Pdf.
   Aucune dépendance : le modèle (JSON) est édité côté navigateur et rendu en PDF par l'API .NET. */
'use strict';

// ---------------------------------------------------------------------------
// Constantes
// ---------------------------------------------------------------------------

/** Formats en points — doit rester aligné sur PageSizes.cs. */
const PAGE_SIZES = { A3: [841.89, 1190.55], A4: [595.28, 841.89], A5: [419.53, 595.28], Letter: [612, 792], Legal: [612, 1008] };

const BAND_TYPES = [
  { type: 'title', label: 'Titre', desc: 'une fois, au début' },
  { type: 'pageHeader', label: 'En-tête de page', desc: 'chaque page' },
  { type: 'columnHeader', label: 'En-tête de colonnes', desc: 'chaque page' },
  { type: 'groupHeader', label: 'En-tête de groupe', desc: 'début de groupe' },
  { type: 'detail', label: 'Détail', desc: 'chaque ligne' },
  { type: 'groupFooter', label: 'Pied de groupe', desc: 'fin de groupe' },
  { type: 'summary', label: 'Résumé', desc: 'une fois, à la fin' },
  { type: 'pageFooter', label: 'Pied de page', desc: 'chaque page' },
];
const BAND_ORDER = BAND_TYPES.map(b => b.type);
const bandInfo = type => BAND_TYPES.find(b => b.type === type);
const isGroupBand = type => type === 'groupHeader' || type === 'groupFooter';

/** Identifiant d'une bande : son type, ou « type:niveau » pour les bandes de groupe. */
const bandKey = band => isGroupBand(band.type) ? `${band.type}:${band.group || 0}` : band.type;
const keyType = key => String(key).split(':')[0];
const keyGroup = key => Number(String(key).split(':')[1] || 0);

/** Ordre d'impression : en-têtes de groupe du plus externe au plus interne, pieds dans l'ordre inverse. */
function compareBands(a, b) {
  const byType = BAND_ORDER.indexOf(a.type) - BAND_ORDER.indexOf(b.type);
  if (byType !== 0) return byType;
  return a.type === 'groupFooter' ? (b.group || 0) - (a.group || 0) : (a.group || 0) - (b.group || 0);
}

/** Libellé d'une bande, avec le niveau et l'expression pour les groupes. */
function bandLabel(band) {
  const info = bandInfo(band.type);
  if (!isGroupBand(band.type)) return info.label;
  const expression = S.template.groups?.[band.group || 0]?.expression;
  return `${info.label} ${(band.group || 0) + 1}${expression ? ` · ${expression}` : ''}`;
}

// Complété au démarrage par les polices enregistrées sur le serveur (GET /api/fonts).
const FONTS = ['Lato', 'Arial', 'Helvetica', 'Times New Roman', 'Courier New', 'DejaVu Sans', 'DejaVu Serif', 'Inter'];
const CULTURES = ['fr-FR', 'fr-TN', 'fr-CA', 'en-US', 'en-GB', 'de-DE', 'es-ES', 'ar-TN'];
const VARIABLES = ['$PAGE', '$PAGES', '$DATE', '$NOW', '$ROW', '$GROUP_ROW', '$GROUP', 'COUNT()'];
const NAME_PATTERN = /^[\p{L}\p{N}_-]{1,80}$/u;
const ELEMENT_LABELS = { text: 'Texte', image: 'Image', line: 'Ligne', rectangle: 'Rectangle' };

// ---------------------------------------------------------------------------
// État
// ---------------------------------------------------------------------------

const S = {
  template: null,
  savedName: null,      // nom sous lequel le modèle est enregistré sur le serveur
  selection: new Set(), // ids d'éléments
  band: 'detail',       // bande courante
  zoom: 1.25,
  snap: true,
  grid: 5,
  undo: [],
  redo: [],
  lastUndoKey: null,
  lastUndoTime: 0,
  dirty: false,
  clipboard: null,
  fields: { rows: [], parameters: [] },
  previewUrl: null,
};

// ---------------------------------------------------------------------------
// Utilitaires
// ---------------------------------------------------------------------------

const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];
const clone = obj => JSON.parse(JSON.stringify(obj));
const uid = () => Math.random().toString(36).slice(2, 10);
const round = v => Math.round(v * 100) / 100;
const clamp = (v, min, max) => Math.min(Math.max(v, min), max);

function h(tag, attrs = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs || {})) {
    if (value === undefined || value === null || value === false) continue;
    if (key === 'class') node.className = value;
    else if (key === 'style' && typeof value === 'object') Object.assign(node.style, value);
    else if (key.startsWith('on') && typeof value === 'function') node.addEventListener(key.slice(2), value);
    else if (key === 'dataset') Object.assign(node.dataset, value);
    else if (key in node && key !== 'list') node[key] = value;
    else node.setAttribute(key, value === true ? '' : value);
  }
  for (const child of children.flat()) {
    if (child === null || child === undefined || child === false) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

function escapeHtml(text) {
  return String(text).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

function snap(v) {
  return S.snap ? Math.round(v / S.grid) * S.grid : round(v);
}

let toastTimer;
function toast(message, isError = false) {
  const node = $('#toast');
  node.textContent = message;
  node.classList.toggle('error', isError);
  node.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => (node.hidden = true), isError ? 7000 : 2500);
}

async function api(method, url, body) {
  const response = await fetch(url, {
    method,
    headers: body !== undefined ? { 'Content-Type': 'application/json' } : {},
    body: body !== undefined ? JSON.stringify(body) : undefined,
  });
  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`;
    try {
      const problem = await response.json();
      message = [problem.title, problem.detail].filter(Boolean).join('\n') || message;
    } catch { /* corps non JSON */ }
    throw new Error(message);
  }
  return response;
}

function downloadBlob(blob, filename) {
  const url = URL.createObjectURL(blob);
  const link = h('a', { href: url, download: filename });
  document.body.append(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

function pickFile(accept) {
  return new Promise(resolve => {
    const input = $('#fileInput');
    input.accept = accept;
    input.value = '';
    input.onchange = () => resolve(input.files[0] || null);
    input.click();
  });
}

const readFile = (file, asDataUrl) => new Promise((resolve, reject) => {
  const reader = new FileReader();
  reader.onload = () => resolve(reader.result);
  reader.onerror = () => reject(reader.error);
  asDataUrl ? reader.readAsDataURL(file) : reader.readAsText(file);
});

// ---------------------------------------------------------------------------
// Modèle
// ---------------------------------------------------------------------------

function defaultTemplate() {
  return normalize({
    name: 'nouveau-rapport',
    title: 'Nouveau rapport',
    page: {},
    bands: [
      { type: 'title', height: 50, elements: [
        { type: 'text', x: 0, y: 5, width: 400, height: 30, text: '{$P.titre}', fontSize: 20, bold: true },
      ] },
      { type: 'columnHeader', height: 22, background: '#EEF2F7', elements: [
        { type: 'text', x: 0, y: 0, width: 200, height: 22, text: 'Nom', bold: true, vAlign: 'middle', padding: 4 },
        { type: 'text', x: 200, y: 0, width: 150, height: 22, text: 'Ville', bold: true, vAlign: 'middle', padding: 4 },
        { type: 'text', x: 405, y: 0, width: 130, height: 22, text: 'Montant', bold: true, vAlign: 'middle', hAlign: 'right', padding: 4 },
      ] },
      { type: 'detail', height: 20, elements: [
        { type: 'text', x: 0, y: 0, width: 200, height: 20, text: '{nom}', vAlign: 'middle', padding: 4 },
        { type: 'text', x: 200, y: 0, width: 150, height: 20, text: '{ville}', vAlign: 'middle', padding: 4 },
        { type: 'text', x: 405, y: 0, width: 130, height: 20, text: '{montant:N2}', vAlign: 'middle', hAlign: 'right', padding: 4 },
      ] },
      { type: 'summary', height: 30, elements: [
        { type: 'line', x: 335, y: 2, width: 200, height: 4, direction: 'horizontal', borderWidth: 1 },
        { type: 'text', x: 335, y: 8, width: 200, height: 18, text: 'Total : {SUM(montant):N2}', bold: true, hAlign: 'right', padding: 4 },
      ] },
      { type: 'pageFooter', height: 20, elements: [
        { type: 'text', x: 335, y: 2, width: 200, height: 16, text: 'Page {$PAGE} / {$PAGES}', fontSize: 8, hAlign: 'right', color: '#777777' },
      ] },
    ],
    sampleData: {
      parameters: { titre: 'Liste des clients' },
      rows: [
        { nom: 'Alice Martin', ville: 'Tunis', montant: 1250.5 },
        { nom: 'Bruno Petit', ville: 'Sfax', montant: 980 },
        { nom: 'Chloé Durand', ville: 'Sousse', montant: 2310.75 },
      ],
    },
  });
}

/** Complète les valeurs par défaut et trie les bandes, pour accepter des JSON partiels. */
function normalize(template) {
  const t = template || {};
  t.name = t.name || 'nouveau-rapport';
  t.page = Object.assign({ size: 'A4', orientation: 'portrait', fontFamily: 'Lato', fontSize: 10, culture: 'fr-FR' }, t.page);
  t.page.margins = Object.assign({ top: 30, right: 30, bottom: 30, left: 30 }, t.page.margins);
  t.bands = (t.bands || [])
    .filter(b => BAND_ORDER.includes(b.type))
    .sort(compareBands);
  // Ancien format : un seul champ « groupBy ».
  t.groups = Array.isArray(t.groups) ? t.groups : [];
  if (!t.groups.length && t.groupBy) t.groups.push({ expression: t.groupBy, startOnNewPage: false });
  t.groupBy = undefined;
  for (const band of t.bands) if (isGroupBand(band.type)) band.group = band.group || 0;
  for (const band of t.bands) {
    band.height = Number(band.height ?? 30);
    band.elements = (band.elements || []).map(normalizeElement);
  }
  return t;
}

function normalizeElement(e) {
  const defaults = { id: uid(), type: 'text', x: 0, y: 0, width: 100, height: 20 };
  if ((e.type || 'text') === 'text') Object.assign(defaults, { hAlign: 'left', vAlign: 'top', overflow: 'clip', padding: 2 });
  if (e.type === 'line') Object.assign(defaults, { direction: 'horizontal', borderWidth: 1 });
  if (e.type === 'image') defaults.imageFit = 'area';
  return Object.assign(defaults, e);
}

function pageSize() {
  const p = S.template.page;
  let [w, hgt] = PAGE_SIZES[p.size] || [Number(p.width) || PAGE_SIZES.A4[0], Number(p.height) || PAGE_SIZES.A4[1]];
  if (p.orientation === 'landscape') [w, hgt] = [Math.max(w, hgt), Math.min(w, hgt)];
  else [w, hgt] = [Math.min(w, hgt), Math.max(w, hgt)];
  return [w, hgt];
}

function contentWidth() {
  const m = S.template.page.margins;
  return round(pageSize()[0] - m.left - m.right);
}

const getBand = key => S.template.bands.find(b => bandKey(b) === key);

function findElement(id) {
  for (const band of S.template.bands) {
    const element = band.elements.find(e => e.id === id);
    if (element) return { element, band };
  }
  return null;
}

const selectedElements = () => [...S.selection].map(findElement).filter(Boolean).map(r => r.element);

function ensureBand(key) {
  let band = getBand(key);
  const type = keyType(key);
  if (!band) {
    band = { type, height: type === 'detail' ? 20 : 30, elements: [] };
    if (isGroupBand(type)) band.group = keyGroup(key);
    S.template.bands.push(band);
    S.template.bands.sort(compareBands);
  }
  return band;
}

// ---------------------------------------------------------------------------
// Historique (annuler / rétablir)
// ---------------------------------------------------------------------------

/** Mémorise l'état avant modification. Les saisies successives d'un même champ sont regroupées. */
function pushUndo(key = null) {
  const now = Date.now();
  if (key && key === S.lastUndoKey && now - S.lastUndoTime < 1500) { S.lastUndoTime = now; return; }
  S.undo.push(JSON.stringify(S.template));
  if (S.undo.length > 150) S.undo.shift();
  S.redo = [];
  S.lastUndoKey = key;
  S.lastUndoTime = now;
}

function markDirty() {
  S.dirty = true;
  updateTitle();
}

/** Modification structurelle : historique + rendu complet. */
function mutate(fn, key) {
  pushUndo(key);
  fn();
  markDirty();
  render();
}

/** Modification depuis le panneau de propriétés : ne redessine pas le panneau (garde le focus). */
function update(fn, key) {
  pushUndo(key);
  fn();
  markDirty();
  renderCanvas();
}

function undo() {
  if (!S.undo.length) return;
  S.redo.push(JSON.stringify(S.template));
  restore(S.undo.pop());
}

function redo() {
  if (!S.redo.length) return;
  S.undo.push(JSON.stringify(S.template));
  restore(S.redo.pop());
}

function restore(snapshot) {
  S.template = JSON.parse(snapshot);
  S.selection = new Set([...S.selection].filter(id => findElement(id)));
  S.lastUndoKey = null;
  markDirty();
  refreshData();
  render();
}

// ---------------------------------------------------------------------------
// Rendu du canevas
// ---------------------------------------------------------------------------

function render() {
  renderCanvas();
  renderBandsList();
  renderProps();
  updateTitle();
}

function updateTitle() {
  const node = $('#templateName');
  node.textContent = S.savedName ? `${S.savedName}.json` : '(non enregistré)';
  node.classList.toggle('dirty', S.dirty);
  $('[data-action=undo]').disabled = !S.undo.length;
  $('[data-action=redo]').disabled = !S.redo.length;
  document.title = `${S.dirty ? '• ' : ''}${S.template.title || S.template.name} — Report Designer`;
}

function renderCanvas() {
  const z = S.zoom;
  const width = contentWidth();
  const page = $('#page');
  page.style.width = `${width * z}px`;
  page.style.fontFamily = cssFont(S.template.page.fontFamily);
  page.style.background = S.template.page.background || '#fff';
  page.replaceChildren();

  renderRuler(width);

  if (!S.template.bands.length)
    page.append(h('div', { class: 'band-empty-hint', style: { position: 'static', padding: '40px' } }, 'Aucune bande : activez-en dans le panneau de gauche.'));

  for (const band of S.template.bands) {
    const info = bandInfo(band.type);
    const key = bandKey(band);
    const body = h('div', { class: 'band-body', dataset: { band: key } });
    body.style.height = `${band.height * z}px`;
    body.style.backgroundColor = band.background || '';
    body.style.backgroundSize = `${S.grid * z * 2}px ${S.grid * z * 2}px`;
    if (!band.elements.length && band.height * z > 14)
      body.append(h('div', { class: 'band-empty-hint' }, 'Glissez des éléments ici'));
    for (const element of band.elements) body.append(renderElement(element));

    page.append(h('div', { class: `band${S.band === key ? ' selected' : ''}`, dataset: { band: key } },
      h('div', { class: 'band-label', dataset: { bandLabel: key }, title: `${bandLabel(band)} — ${info.desc}${band.printWhen ? `\nImprimée si : ${band.printWhen}` : ''}` },
        h('b', {}, bandLabel(band)), `${round(band.height)} pt${band.printWhen ? ' · ?' : ''}`),
      body,
      h('div', { class: 'band-resize', dataset: { bandResize: key }, title: 'Glisser pour changer la hauteur de la bande' })));
  }

  $('#alignBar').hidden = S.selection.size < 2;
}

function renderRuler(width) {
  const ruler = $('#ruler');
  ruler.style.width = `${width * S.zoom}px`;
  ruler.replaceChildren();
  for (let x = 0; x <= width; x += 10) {
    const major = x % 50 === 0;
    const tick = h('div', { class: `tick${major ? ' major' : ''}`, style: { left: `${x * S.zoom}px` } });
    if (major && x % 100 === 0) tick.append(h('span', {}, x));
    ruler.append(tick);
  }
}

/** Couleur affichable dans le canevas (une couleur calculée par expression n'est connue qu'au rendu). */
function cssColor(value, fallback = '') {
  if (!value) return fallback;
  if (value.startsWith('{')) {
    const match = value.match(/'(#[0-9a-f]{6})'/i); // première couleur littérale de l'expression
    return match ? match[1] : fallback;
  }
  return value;
}

function cssFont(name) {
  return name ? `"${name}", "Lato", system-ui, sans-serif` : '';
}

function renderElement(e) {
  const z = S.zoom;
  const selected = S.selection.has(e.id);
  const node = h('div', { class: `el ${e.type}${selected ? ' selected' : ''}${e.printWhen ? ' conditional' : ''}${e.stretch ? ' stretch' : ''}`, dataset: { id: e.id } });
  Object.assign(node.style, {
    left: `${e.x * z}px`, top: `${e.y * z}px`, width: `${e.width * z}px`, height: `${e.height * z}px`,
  });

  if (e.type !== 'line') {
    if (e.background) node.style.background = cssColor(e.background);
    if (e.borderWidth > 0) node.style.boxShadow = `inset 0 0 0 ${Math.max(1, e.borderWidth * z)}px ${cssColor(e.borderColor, '#000')}`;
    if (e.cornerRadius > 0) node.style.borderRadius = `${e.cornerRadius * z}px`;
  }

  switch (e.type) {
    case 'text': {
      const fontSize = (e.fontSize || S.template.page.fontSize || 10) * z;
      Object.assign(node.style, {
        fontFamily: cssFont(e.fontFamily) || 'inherit',
        fontSize: `${fontSize}px`,
        fontWeight: e.bold ? '700' : '400',
        fontStyle: e.italic ? 'italic' : 'normal',
        textDecoration: e.underline ? 'underline' : 'none',
        color: cssColor(e.color, '#000'),
        padding: `${(e.padding || 0) * z + (e.borderWidth || 0) * z}px`,
        textAlign: e.hAlign === 'justify' ? 'justify' : (e.hAlign || 'left'),
        alignItems: { middle: 'center', bottom: 'flex-end' }[e.vAlign] || 'flex-start',
      });
      const content = h('div', { class: 'content' });
      content.innerHTML = highlightExpressions(e.text ?? '');
      node.append(content);
      node.title = e.text || '';
      break;
    }
    case 'image':
      node.append(e.image && !e.image.startsWith('{')
        ? h('img', { src: e.image, alt: '', style: { objectFit: { stretch: 'fill', width: 'contain' }[e.imageFit] || 'contain' } })
        : h('span', { class: 'placeholder' }, e.image ? `🖼 ${e.image}` : '🖼 Image'));
      break;
    case 'line': {
      const thickness = Math.max(1, (e.borderWidth || 1) * z);
      const vertical = e.direction === 'vertical';
      node.append(h('div', { style: {
        position: 'absolute', background: cssColor(e.borderColor || e.color, '#000'),
        ...(vertical
          ? { left: `calc(50% - ${thickness / 2}px)`, top: 0, bottom: 0, width: `${thickness}px` }
          : { top: `calc(50% - ${thickness / 2}px)`, left: 0, right: 0, height: `${thickness}px` }),
      } }));
      break;
    }
  }

  if (e.printWhen) node.title = `${node.title ? node.title + '\n' : ''}Affiché si : ${e.printWhen}`;

  if (selected && S.selection.size === 1)
    for (const dir of ['nw', 'n', 'ne', 'e', 'se', 's', 'sw', 'w']) node.append(h('div', { class: 'handle', dataset: { h: dir } }));
  return node;
}

function highlightExpressions(text) {
  return escapeHtml(text)
    .replace(/\{\{/g, '\u0001').replace(/\}\}/g, '\u0002')
    .replace(/\{[^{}]*\}/g, m => `<span class="expr">${m}</span>`)
    .replace(/\u0001/g, '{').replace(/\u0002/g, '}');
}

// ---------------------------------------------------------------------------
// Interactions souris sur le canevas
// ---------------------------------------------------------------------------

let drag = null;

function initCanvasInteractions() {
  const page = $('#page');
  page.addEventListener('pointerdown', onPointerDown);
  window.addEventListener('pointermove', onPointerMove);
  window.addEventListener('pointerup', onPointerUp);

  // Clic hors de la page : sélection du rapport
  $('#canvasWrap').addEventListener('pointerdown', e => {
    if (e.target === e.currentTarget || e.target.id === 'ruler') {
      S.selection.clear();
      render();
    }
  });

  // Glisser-déposer depuis la boîte à outils / la liste des champs
  page.addEventListener('dragover', e => {
    const body = e.target.closest('.band-body');
    if (!body || !e.dataTransfer.types.includes('application/x-report-designer')) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = 'copy';
    $$('.band.drop-target').forEach(n => n.classList.remove('drop-target'));
    body.parentElement.classList.add('drop-target');
  });
  page.addEventListener('dragleave', e => {
    if (!e.relatedTarget || !page.contains(e.relatedTarget)) $$('.band.drop-target').forEach(n => n.classList.remove('drop-target'));
  });
  page.addEventListener('drop', e => {
    const body = e.target.closest('.band-body');
    $$('.band.drop-target').forEach(n => n.classList.remove('drop-target'));
    if (!body) return;
    e.preventDefault();
    const payload = JSON.parse(e.dataTransfer.getData('application/x-report-designer') || '{}');
    const rect = body.getBoundingClientRect();
    addElement(payload, body.dataset.band, (e.clientX - rect.left) / S.zoom, (e.clientY - rect.top) / S.zoom);
  });
}

function onPointerDown(e) {
  if (e.button !== 0) return;
  const handle = e.target.closest('.handle');
  const resizeBar = e.target.closest('[data-band-resize]');
  const elNode = e.target.closest('.el');
  const label = e.target.closest('[data-band-label]');
  const body = e.target.closest('.band-body');

  if (resizeBar) {
    const band = getBand(resizeBar.dataset.bandResize);
    S.band = bandKey(band);
    drag = { kind: 'band', band, startY: e.clientY, orig: band.height, node: resizeBar };
    resizeBar.classList.add('active');
    e.preventDefault();
    return;
  }

  if (handle) {
    const id = handle.parentElement.dataset.id;
    const { element } = findElement(id);
    drag = { kind: 'resize', dir: handle.dataset.h, element, startX: e.clientX, startY: e.clientY, orig: { ...element }, started: false };
    e.preventDefault();
    return;
  }

  if (elNode) {
    const id = elNode.dataset.id;
    const { band } = findElement(id);
    S.band = bandKey(band);
    if (e.shiftKey || e.ctrlKey || e.metaKey) {
      S.selection.has(id) ? S.selection.delete(id) : S.selection.add(id);
    } else if (!S.selection.has(id)) {
      S.selection = new Set([id]);
    }
    render();
    if (!S.selection.has(id)) return;
    const rect = $(`.el[data-id="${id}"]`).getBoundingClientRect();
    drag = {
      kind: 'move', id, startX: e.clientX, startY: e.clientY, started: false,
      grabY: (e.clientY - rect.top) / S.zoom,
      orig: selectedElements().map(el => ({ el, x: el.x, y: el.y })),
    };
    e.preventDefault();
    return;
  }

  if (label) {
    S.band = label.dataset.bandLabel;
    S.selection.clear();
    render();
    return;
  }

  if (body) {
    S.band = body.dataset.band;
    if (!e.shiftKey) S.selection.clear();
    render();
    drag = { kind: 'marquee', startX: e.clientX, startY: e.clientY, additive: e.shiftKey, base: new Set(S.selection), node: null };
    e.preventDefault();
  }
}

function onPointerMove(e) {
  if (!drag) return;
  const z = S.zoom;
  const dx = (e.clientX - drag.startX) / z;
  const dy = (e.clientY - drag.startY) / z;

  switch (drag.kind) {
    case 'move': {
      if (!drag.started) {
        if (Math.abs(e.clientX - drag.startX) + Math.abs(e.clientY - drag.startY) < 3) return;
        drag.started = true;
        pushUndo();
      }
      for (const o of drag.orig) {
        o.el.x = Math.max(0, snap(o.x + dx));
        o.el.y = Math.max(0, snap(o.y + dy));
        const node = $(`.el[data-id="${o.el.id}"]`);
        if (node) { node.style.left = `${o.el.x * z}px`; node.style.top = `${o.el.y * z}px`; }
      }
      const first = drag.orig[0].el;
      showDragInfo(e, `x ${first.x} · y ${first.y}`);
      highlightBandUnder(e);
      break;
    }
    case 'resize': {
      if (!drag.started) { drag.started = true; pushUndo(); }
      const { orig, dir, element } = drag;
      let { x, y, width, height } = orig;
      if (dir.includes('e')) width = snap(orig.x + orig.width + dx) - x;
      if (dir.includes('s')) height = snap(orig.y + orig.height + dy) - y;
      if (dir.includes('w')) { x = Math.min(snap(orig.x + dx), orig.x + orig.width - 1); width = orig.x + orig.width - x; }
      if (dir.includes('n')) { y = Math.min(snap(orig.y + dy), orig.y + orig.height - 1); height = orig.y + orig.height - y; }
      Object.assign(element, { x: Math.max(0, round(x)), y: Math.max(0, round(y)), width: Math.max(1, round(width)), height: Math.max(1, round(height)) });
      const node = $(`.el[data-id="${element.id}"]`);
      Object.assign(node.style, { left: `${element.x * z}px`, top: `${element.y * z}px`, width: `${element.width * z}px`, height: `${element.height * z}px` });
      showDragInfo(e, `${element.width} × ${element.height}`);
      break;
    }
    case 'band': {
      if (!drag.started) { drag.started = true; pushUndo(); }
      drag.band.height = Math.max(0, snap(drag.orig + dy));
      const body = $(`.band-body[data-band="${bandKey(drag.band)}"]`);
      body.style.height = `${drag.band.height * z}px`;
      showDragInfo(e, `hauteur ${drag.band.height} pt`);
      break;
    }
    case 'marquee': {
      const x1 = Math.min(e.clientX, drag.startX), y1 = Math.min(e.clientY, drag.startY);
      const x2 = Math.max(e.clientX, drag.startX), y2 = Math.max(e.clientY, drag.startY);
      if (!drag.node) {
        if (x2 - x1 + y2 - y1 < 4) return;
        drag.node = h('div', { class: 'marquee' });
        document.body.append(drag.node);
      }
      Object.assign(drag.node.style, { left: `${x1}px`, top: `${y1}px`, width: `${x2 - x1}px`, height: `${y2 - y1}px` });
      const selection = new Set(drag.additive ? drag.base : []);
      for (const node of $$('#page .el')) {
        const r = node.getBoundingClientRect();
        if (r.right >= x1 && r.left <= x2 && r.bottom >= y1 && r.top <= y2) selection.add(node.dataset.id);
      }
      S.selection = selection;
      for (const node of $$('#page .el')) node.classList.toggle('selected', selection.has(node.dataset.id));
      break;
    }
  }
}

function onPointerUp(e) {
  if (!drag) return;
  const current = drag;
  drag = null;
  hideDragInfo();
  $$('.band.drop-target').forEach(n => n.classList.remove('drop-target'));

  if (current.kind === 'move' && current.started) {
    // Déplacement d'un élément unique vers une autre bande
    const target = bandUnder(e);
    if (current.orig.length === 1 && target) {
      const { element, band } = findElement(current.id);
      if (target !== band) {
        band.elements.splice(band.elements.indexOf(element), 1);
        const rect = $(`.band-body[data-band="${bandKey(target)}"]`).getBoundingClientRect();
        element.y = Math.max(0, snap((e.clientY - rect.top) / S.zoom - current.grabY));
        target.elements.push(element);
        S.band = bandKey(target);
      }
    }
    markDirty();
  } else if (current.kind === 'resize' || current.kind === 'band') {
    if (current.started) markDirty();
    current.node?.classList.remove('active');
  } else if (current.kind === 'marquee') {
    current.node?.remove();
  }
  render();
}

function bandUnder(e) {
  const node = document.elementsFromPoint(e.clientX, e.clientY).find(n => n.classList?.contains('band-body'));
  return node ? getBand(node.dataset.band) : null;
}

function highlightBandUnder(e) {
  if (drag.orig.length !== 1) return;
  const target = bandUnder(e);
  const source = findElement(drag.id).band;
  $$('.band').forEach(n => n.classList.toggle('drop-target', !!target && target !== source && n.dataset.band === bandKey(target)));
}

let dragInfo;
function showDragInfo(e, text) {
  dragInfo ??= document.body.appendChild(h('div', { class: 'drag-info' }));
  dragInfo.hidden = false;
  dragInfo.textContent = text;
  dragInfo.style.left = `${e.clientX + 14}px`;
  dragInfo.style.top = `${e.clientY + 14}px`;
}
function hideDragInfo() { if (dragInfo) dragInfo.hidden = true; }

// ---------------------------------------------------------------------------
// Création d'éléments
// ---------------------------------------------------------------------------

function fieldInfo(name) {
  return S.fields.rows.find(f => f.name === name) || S.fields.parameters.find(f => f.name === name);
}

function labelFor(path) {
  const last = path.replace(/^\$P\./, '').split('.').pop();
  const words = last.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/[_-]+/g, ' ');
  return words.charAt(0).toUpperCase() + words.slice(1);
}

/** Construit un élément à partir d'un outil (ou d'un champ) déposé dans une bande. */
function buildElement({ tool, field }, bandType) {
  if (field) {
    const info = fieldInfo(field);
    const numeric = info?.kind === 'nombre';
    const isParam = field.startsWith('$P.');
    let text = `{${field}${numeric ? ':N2' : ''}${info?.kind === 'date' ? ':dd/MM/yyyy' : ''}}`;
    if (bandType === 'columnHeader') text = labelFor(field);
    else if (numeric && !isParam && ['summary', 'groupFooter', 'title'].includes(bandType)) text = `{SUM(${field}):N2}`;
    return {
      type: 'text', width: numeric ? 90 : 140, height: 18, text,
      hAlign: numeric ? 'right' : 'left', bold: bandType === 'columnHeader', vAlign: 'middle',
    };
  }
  switch (tool) {
    case 'text': return { type: 'text', width: 140, height: 18, text: 'Texte' };
    case 'field': {
      const first = S.fields.rows[0]?.name || 'champ';
      return buildElement({ field: first }, bandType);
    }
    case 'image': return { type: 'image', width: 80, height: 60, imageFit: 'area' };
    case 'rectangle': return { type: 'rectangle', width: 140, height: 40, borderWidth: 1, borderColor: '#000000' };
    case 'hline': return { type: 'line', direction: 'horizontal', width: 200, height: 4, borderWidth: 1, borderColor: '#000000' };
    case 'vline': return { type: 'line', direction: 'vertical', width: 4, height: 40, borderWidth: 1, borderColor: '#000000' };
    case 'pageNumber': return { type: 'text', width: 120, height: 16, text: 'Page {$PAGE} / {$PAGES}', hAlign: 'right' };
    case 'date': return { type: 'text', width: 100, height: 16, text: '{$DATE:dd/MM/yyyy}' };
    default: return null;
  }
}

function addElement(payload, bandType, x, y) {
  const spec = buildElement(payload, keyType(bandType));
  if (!spec) return;
  mutate(() => {
    const band = ensureBand(bandType);
    const element = normalizeElement(spec);
    const width = contentWidth();
    element.width = Math.min(element.width, width);
    element.x = clamp(snap(x ?? 10), 0, Math.max(0, width - element.width));
    element.y = Math.max(0, snap(y ?? 0));
    if (element.y + element.height > band.height) {
      if (y === undefined) element.y = Math.max(0, snap(band.height - element.height));
      band.height = Math.max(band.height, element.y + element.height);
    }
    band.elements.push(element);
    S.selection = new Set([element.id]);
    S.band = bandType;
  });
}

// ---------------------------------------------------------------------------
// Commandes d'édition
// ---------------------------------------------------------------------------

function deleteSelection() {
  if (!S.selection.size) return;
  mutate(() => {
    for (const band of S.template.bands) band.elements = band.elements.filter(e => !S.selection.has(e.id));
    S.selection.clear();
  });
}

function nudge(dx, dy) {
  if (!S.selection.size) return;
  mutate(() => {
    for (const e of selectedElements()) {
      e.x = Math.max(0, round(e.x + dx));
      e.y = Math.max(0, round(e.y + dy));
    }
  }, 'nudge');
}

function copySelection() {
  if (!S.selection.size) return false;
  S.clipboard = [...S.selection].map(findElement).filter(Boolean).map(({ element, band }) => ({ element: clone(element), band: bandKey(band) }));
  return true;
}

function paste() {
  if (!S.clipboard?.length) return;
  mutate(() => {
    const ids = [];
    for (const item of S.clipboard) {
      const band = ensureBand(S.clipboard.length === 1 ? S.band : item.band);
      const element = { ...clone(item.element), id: uid() };
      if (bandKey(band) === item.band) { element.x += 10; element.y += 10; }
      band.height = Math.max(band.height, element.y + element.height);
      band.elements.push(element);
      ids.push(element.id);
      item.element = clone(element); // collages successifs en cascade
    }
    S.selection = new Set(ids);
  });
}

function duplicate() {
  if (copySelection()) paste();
}

function selectAllInBand() {
  const band = getBand(S.band);
  if (!band) return;
  S.selection = new Set(band.elements.map(e => e.id));
  render();
}

function reorder(toFront) {
  mutate(() => {
    for (const { element, band } of [...S.selection].map(findElement).filter(Boolean)) {
      band.elements.splice(band.elements.indexOf(element), 1);
      toFront ? band.elements.push(element) : band.elements.unshift(element);
    }
  });
}

function align(mode) {
  const els = selectedElements();
  if (els.length < 2) return;
  const left = Math.min(...els.map(e => e.x));
  const right = Math.max(...els.map(e => e.x + e.width));
  const top = Math.min(...els.map(e => e.y));
  const bottom = Math.max(...els.map(e => e.y + e.height));
  const ref = els[0];
  mutate(() => {
    for (const e of els) {
      switch (mode) {
        case 'left': e.x = left; break;
        case 'right': e.x = round(right - e.width); break;
        case 'hcenter': e.x = round((left + right) / 2 - e.width / 2); break;
        case 'top': e.y = top; break;
        case 'bottom': e.y = round(bottom - e.height); break;
        case 'vmiddle': e.y = round((top + bottom) / 2 - e.height / 2); break;
        case 'sameWidth': e.width = ref.width; break;
        case 'sameHeight': e.height = ref.height; break;
      }
    }
  });
}

function toggleBand(type, enabled) {
  const band = getBand(type);
  if (enabled && !band) {
    mutate(() => { ensureBand(type); S.band = type; });
  } else if (!enabled && band) {
    if (band.elements.length && !confirm(`Supprimer la bande « ${bandLabel(band)} » et ses ${band.elements.length} élément(s) ?`)) {
      renderBandsList();
      return;
    }
    mutate(() => {
      S.template.bands = S.template.bands.filter(b => b !== band);
      for (const e of band.elements) S.selection.delete(e.id);
    });
  }
}

// ---------------------------------------------------------------------------
// Panneau des bandes et des champs
// ---------------------------------------------------------------------------

function renderBandsList() {
  const items = [];
  const levels = S.template.groups.length;
  for (const info of BAND_TYPES) {
    if (!isGroupBand(info.type)) {
      items.push({ key: info.type, label: info.label, desc: info.desc });
      continue;
    }
    const range = [...Array(levels).keys()];
    if (info.type === 'groupFooter') range.reverse();
    for (const level of range)
      items.push({ key: `${info.type}:${level}`, label: bandLabel({ type: info.type, group: level }), desc: info.desc, indent: level });
  }
  const nodes = items.map(item => h('li', { style: { paddingLeft: `${(item.indent || 0) * 10}px` } },
    h('label', { title: item.desc },
      h('input', { type: 'checkbox', checked: !!getBand(item.key), onchange: e => toggleBand(item.key, e.target.checked) }),
      item.label)));
  if (!levels) nodes.push(h('li', { class: 'hint' }, 'Bandes de groupe : ajoutez un niveau dans « Groupes » (propriétés du rapport).'));
  $('#bandsList').replaceChildren(...nodes);
}

// ---------------------------------------------------------------------------
// Niveaux de groupe
// ---------------------------------------------------------------------------

function addGroupLevel() {
  mutate(() => {
    S.template.groups.push({ expression: S.fields.rows[0]?.name || '', startOnNewPage: false });
    const level = S.template.groups.length - 1;
    ensureBand(`groupHeader:${level}`).height = 22;
    S.band = `groupHeader:${level}`;
    S.selection.clear();
  });
}

function removeGroupLevel(level) {
  const bands = S.template.bands.filter(b => isGroupBand(b.type) && (b.group || 0) === level);
  const count = bands.reduce((n, b) => n + b.elements.length, 0);
  if (count && !confirm(`Supprimer le niveau ${level + 1} et ses bandes (${count} élément(s)) ?`)) return;
  mutate(() => {
    S.template.groups.splice(level, 1);
    S.template.bands = S.template.bands.filter(b => !bands.includes(b));
    for (const b of S.template.bands) if (isGroupBand(b.type) && b.group > level) b.group--;
    for (const b of bands) for (const e of b.elements) S.selection.delete(e.id);
    S.template.bands.sort(compareBands);
    if (!getBand(S.band)) S.band = 'detail';
  });
}

/** Échange deux niveaux de groupe (et leurs bandes). */
function moveGroupLevel(level, delta) {
  const other = level + delta;
  if (other < 0 || other >= S.template.groups.length) return;
  mutate(() => {
    const groups = S.template.groups;
    [groups[level], groups[other]] = [groups[other], groups[level]];
    for (const b of S.template.bands) {
      if (!isGroupBand(b.type)) continue;
      if (b.group === level) b.group = other;
      else if (b.group === other) b.group = level;
    }
    S.template.bands.sort(compareBands);
    if (isGroupBand(keyType(S.band))) S.band = keyGroup(S.band) === level ? `${keyType(S.band)}:${other}` : S.band;
  });
}

function groupsEditor() {
  const t = S.template;
  const rows = t.groups.map((group, level) => h('div', { class: 'group-row' },
    h('span', { class: 'group-level' }, level + 1),
    textInput(group.expression, v => update(() => { group.expression = v; renderBandsList(); }, `group-${level}`),
      { list: 'groupFields', placeholder: 'champ ou expression, ex. YEAR(date)' }),
    h('label', { class: 'inline', title: 'Commencer chaque groupe sur une nouvelle page' },
      h('input', { type: 'checkbox', checked: !!group.startOnNewPage, onchange: e => update(() => (group.startOnNewPage = e.target.checked)) }), '⤓ page'),
    h('button', { type: 'button', title: 'Monter', disabled: level === 0, onclick: () => moveGroupLevel(level, -1) }, '↑'),
    h('button', { type: 'button', title: 'Descendre', disabled: level === t.groups.length - 1, onclick: () => moveGroupLevel(level, 1) }, '↓'),
    h('button', { type: 'button', class: 'danger', title: 'Supprimer ce niveau', onclick: () => removeGroupLevel(level) }, '✕')));
  return h('fieldset', {}, h('legend', {}, 'Groupes'),
    ...rows,
    h('div', { class: 'props-actions' }, h('button', { type: 'button', onclick: addGroupLevel }, '+ Ajouter un niveau')),
    h('p', { class: 'hint' }, t.groups.length
      ? 'Niveau 1 = le plus externe. Dans les bandes de groupe : {$GROUP}, {$GROUP1}, {SUM(montant)} (sur le groupe), {SUM(montant, \'report\')}.'
      : 'Aucun regroupement.'),
    datalist('groupFields', S.fields.rows.map(f => f.name)));
}

/** Déduit la liste des champs (lignes et paramètres) à partir des données d'exemple. */
function extractFields(data) {
  let rows = [], parameters = {};
  if (Array.isArray(data)) rows = data;
  else if (data && typeof data === 'object') {
    if ('rows' in data || 'parameters' in data) { rows = Array.isArray(data.rows) ? data.rows : []; parameters = data.parameters || {}; }
    else parameters = data;
  }
  const collect = (objects, prefix) => {
    const found = new Map();
    const visit = (value, path, depth) => {
      if (value && typeof value === 'object' && !Array.isArray(value) && depth < 4) {
        for (const [k, v] of Object.entries(value)) visit(v, path ? `${path}.${k}` : k, depth + 1);
      } else if (path && !found.has(path)) {
        found.set(path, kindOf(value));
      } else if (path && found.get(path) === 'vide') {
        found.set(path, kindOf(value));
      }
    };
    for (const obj of objects.slice(0, 100)) visit(obj, '', 0);
    return [...found].map(([name, kind]) => ({ name: prefix + name, kind }));
  };
  return { rows: collect(rows.filter(r => r && typeof r === 'object'), ''), parameters: collect([parameters], '$P.') };
}

function kindOf(value) {
  if (value === null || value === undefined) return 'vide';
  if (typeof value === 'number') return 'nombre';
  if (typeof value === 'boolean') return 'booléen';
  if (Array.isArray(value)) return 'liste';
  if (typeof value === 'string' && /^\d{4}-\d{2}-\d{2}/.test(value)) return 'date';
  return 'texte';
}

function renderFields() {
  const filter = $('#fieldFilter').value.trim().toLowerCase();
  const item = (name, kind) => h('li', {
    draggable: true, title: 'Glisser dans une bande, ou cliquer pour insérer',
    ondragstart: e => e.dataTransfer.setData('application/x-report-designer', JSON.stringify({ field: name })),
    onclick: () => insertField(name),
  }, h('span', {}, name), h('span', { class: 'type' }, kind));
  const section = (title, list) => {
    const visible = list.filter(f => !filter || f.name.toLowerCase().includes(filter));
    return visible.length ? [h('li', { class: 'group-title' }, title), ...visible.map(f => item(f.name, f.kind))] : [];
  };
  const nodes = [
    ...section('Lignes', S.fields.rows),
    ...section('Paramètres', S.fields.parameters),
  ];
  $('#fields').replaceChildren(...(nodes.length ? nodes : [h('li', { class: 'empty' }, 'Aucun champ : saisissez des données dans l\'onglet « Données ».')]));
}

/** Clic sur un champ : insère dans le texte sélectionné, sinon crée un élément dans la bande courante. */
function insertField(name) {
  const els = selectedElements();
  if (els.length === 1 && els[0].type === 'text') {
    const textarea = $('#propText');
    const token = `{${name}}`;
    if (textarea) {
      const start = textarea.selectionStart ?? textarea.value.length;
      const end = textarea.selectionEnd ?? start;
      textarea.setRangeText(token, start, end, 'end');
      textarea.dispatchEvent(new Event('input'));
      textarea.focus();
    } else {
      update(() => (els[0].text = (els[0].text || '') + token));
    }
    return;
  }
  addElement({ field: name }, S.band || 'detail');
}

// ---------------------------------------------------------------------------
// Panneau des propriétés
// ---------------------------------------------------------------------------

function prop(label, input, cls = '') {
  return h('label', { class: `prop ${cls}` }, label, input);
}

/** Champ numérique ; value === '' => null quand allowEmpty. */
function numberInput(value, onChange, { step = 1, min, allowEmpty = false, placeholder } = {}) {
  return h('input', {
    type: 'number', value: value ?? '', step, min, placeholder,
    oninput: e => {
      const raw = e.target.value;
      if (raw === '') { if (allowEmpty) onChange(null); return; }
      const n = Number(raw);
      if (!Number.isNaN(n)) onChange(n);
    },
  });
}

function textInput(value, onChange, attrs = {}) {
  return h('input', { type: 'text', value: value ?? '', ...attrs, oninput: e => onChange(e.target.value) });
}

function selectInput(value, options, onChange) {
  return h('select', { onchange: e => onChange(e.target.value) },
    ...options.map(([v, label]) => h('option', { value: v, selected: v === (value ?? '') }, label)));
}

/** Couleur #RRGGBB, ou expression calculée (« {IF(montant < 0, '#DC2626', '#000000')} »). */
function colorInput(value, onChange, { allowEmpty = true } = {}) {
  const valid = v => /^#[0-9a-f]{6}$/i.test(v || '');
  const isExpression = v => /^\{.*\}$/s.test(v || '');
  const text = h('input', { type: 'text', value: value || '', placeholder: allowEmpty ? 'aucune' : '#000000',
    title: 'Couleur #RRGGBB, ou expression : {IF(montant < 0, \'#DC2626\', \'#000000\')}' });
  const picker = h('input', { type: 'color', value: valid(value) ? value : '#000000' });
  picker.addEventListener('input', () => { text.value = picker.value.toUpperCase(); onChange(text.value); });
  text.addEventListener('input', () => {
    const v = text.value.trim();
    if (v === '' && allowEmpty) onChange(null);
    else if (valid(v)) { picker.value = v; onChange(v.toUpperCase()); }
    else if (isExpression(v)) onChange(v);
  });
  const clear = allowEmpty && h('button', { type: 'button', title: 'Aucune couleur', onclick: () => { text.value = ''; onChange(null); } }, '✕');
  return h('div', { class: 'color-input' }, picker, text, clear);
}

function toggleButton(label, active, onToggle, title, style = {}) {
  const button = h('button', { type: 'button', class: active ? 'on' : '', title, style }, label);
  button.addEventListener('click', () => { button.classList.toggle('on'); onToggle(button.classList.contains('on')); });
  return button;
}

function datalist(id, values) {
  return h('datalist', { id }, ...values.map(v => h('option', { value: v })));
}

function renderProps() {
  const container = $('#props');
  const els = selectedElements();
  container.replaceChildren(...(els.length ? elementProps(els) : [...bandProps(), ...reportProps()]).filter(Boolean));
}

function elementProps(els) {
  const e = els[0];
  const multi = els.length > 1;
  const set = (key, mergeKey) => value => update(() => els.forEach(el => (el[key] = value)), `${mergeKey || key}:${e.id}`);
  const sameType = els.every(x => x.type === e.type);
  const nodes = [];

  nodes.push(h('div', { class: 'props-title' },
    h('h3', {}, multi ? `${els.length} éléments` : ELEMENT_LABELS[e.type]),
    h('span', { class: 'badge' }, multi ? 'modification groupée' : `bande : ${bandLabel(findElement(e.id).band)}`)));

  nodes.push(h('fieldset', {}, h('legend', {}, 'Position et taille (pt)'),
    h('div', { class: 'grid4' },
      prop('X', numberInput(e.x, set('x'), { step: 0.5, min: 0 })),
      prop('Y', numberInput(e.y, set('y'), { step: 0.5, min: 0 })),
      prop('Largeur', numberInput(e.width, set('width'), { step: 0.5, min: 1 })),
      prop('Hauteur', numberInput(e.height, set('height'), { step: 0.5, min: 1 })))));

  if (sameType && e.type === 'text') {
    if (!multi) {
      const textarea = h('textarea', { id: 'propText', value: e.text ?? '', spellcheck: false, oninput: ev => set('text')(ev.target.value) });
      const chips = h('div', { class: 'insert-fields' },
        ...[...S.fields.rows.slice(0, 12).map(f => f.name), ...S.fields.parameters.slice(0, 6).map(f => f.name), ...VARIABLES]
          .map(name => h('button', { type: 'button', title: `Insérer {${name}}`, onclick: () => insertField(name) }, name)));
      nodes.push(h('fieldset', {}, h('legend', {}, 'Texte et expressions'), prop('', textarea, 'full'), chips));
    }

    nodes.push(h('fieldset', {}, h('legend', {}, 'Police'),
      h('div', { class: 'grid2' },
        prop('Famille', textInput(e.fontFamily, v => set('fontFamily')(v || null), { list: 'fontList', placeholder: S.template.page.fontFamily })),
        prop('Taille', numberInput(e.fontSize, set('fontSize'), { step: 0.5, min: 1, allowEmpty: true, placeholder: String(S.template.page.fontSize) })),
        prop('Style', h('div', { class: 'toggles' },
          toggleButton('G', e.bold, set('bold'), 'Gras', { fontWeight: 700 }),
          toggleButton('I', e.italic, set('italic'), 'Italique', { fontStyle: 'italic' }),
          toggleButton('S', e.underline, set('underline'), 'Souligné', { textDecoration: 'underline' }))),
        prop('Couleur', colorInput(e.color, set('color'))),
        datalist('fontList', FONTS))));

    nodes.push(h('fieldset', {}, h('legend', {}, 'Alignement'),
      h('div', { class: 'grid2' },
        prop('Horizontal', selectInput(e.hAlign, [['left', 'Gauche'], ['center', 'Centre'], ['right', 'Droite'], ['justify', 'Justifié']], set('hAlign'))),
        prop('Vertical', selectInput(e.vAlign, [['top', 'Haut'], ['middle', 'Milieu'], ['bottom', 'Bas']], set('vAlign'))),
        prop('Texte trop long', selectInput(e.overflow, [['clip', 'Tronquer (…)'], ['shrink', 'Réduire pour tenir']], set('overflow')), 'full'))));
  }

  if (sameType && e.type === 'image') {
    nodes.push(h('fieldset', {}, h('legend', {}, 'Image'),
      h('div', { class: 'grid2' },
        prop('Ajustement', selectInput(e.imageFit, [['area', 'Contenir'], ['width', 'Largeur'], ['stretch', 'Étirer']], set('imageFit')), 'full')),
      h('div', { class: 'props-actions' },
        h('button', { type: 'button', onclick: async () => {
          const file = await pickFile('image/png,image/jpeg,image/webp,image/gif,image/bmp');
          if (!file) return;
          if (file.size > 4 * 1024 * 1024) { toast('Image trop lourde (max. 4 Mo).', true); return; }
          const dataUrl = await readFile(file, true);
          mutate(() => els.forEach(el => (el.image = dataUrl)));
        } }, 'Choisir une image…'),
        e.image && h('button', { type: 'button', class: 'danger', onclick: () => mutate(() => els.forEach(el => delete el.image)) }, 'Retirer')),
      prop('Ou image provenant des données (expression)', textInput(e.image?.startsWith('{') ? e.image : '', v => {
        const value = v.trim();
        if (value === '' || /^\{.*\}$/s.test(value)) set('image')(value || null);
      }, { placeholder: '{photo}', list: 'imageFields' }), 'full'),
      datalist('imageFields', S.fields.rows.map(f => `{${f.name}}`)),
      h('p', { class: 'hint' }, 'Le champ doit contenir une data URL (data:image/png;base64,…) ou du base64.')));
  }

  if (sameType && e.type === 'line') {
    nodes.push(h('fieldset', {}, h('legend', {}, 'Ligne'),
      h('div', { class: 'grid2' },
        prop('Direction', selectInput(e.direction, [['horizontal', 'Horizontale'], ['vertical', 'Verticale']], set('direction'))),
        prop('Épaisseur', numberInput(e.borderWidth, set('borderWidth'), { step: 0.25, min: 0.25 })),
        prop('Couleur', colorInput(e.borderColor, set('borderColor'), { allowEmpty: false }), 'full'))));
  }

  if (sameType && (e.type === 'text' || e.type === 'rectangle' || e.type === 'image')) {
    nodes.push(h('fieldset', {}, h('legend', {}, 'Cadre et fond'),
      h('div', { class: 'grid2' },
        prop('Fond', colorInput(e.background, set('background')), 'full'),
        prop('Bordure (pt)', numberInput(e.borderWidth, set('borderWidth'), { step: 0.25, min: 0 })),
        prop('Arrondi (pt)', numberInput(e.cornerRadius, set('cornerRadius'), { step: 1, min: 0 })),
        prop('Couleur de bordure', colorInput(e.borderColor, set('borderColor')), 'full'),
        e.type === 'text' && prop('Marge intérieure (pt)', numberInput(e.padding, set('padding'), { step: 0.5, min: 0 })))));
  }

  if (sameType && e.type !== 'line' || (sameType && e.type === 'line' && e.direction === 'vertical')) {
    const help = e.type === 'text'
      ? 'Le texte grandit pour être affiché en entier : la bande s\'agrandit et les éléments situés dessous descendent.'
      : 'S\'étire avec la bande quand un texte extensible la fait grandir (cadres, séparateurs verticaux).';
    nodes.push(h('fieldset', {}, h('legend', {}, 'Extensible'),
      h('label', { class: 'inline', style: { marginLeft: 0 } },
        h('input', { type: 'checkbox', checked: !!e.stretch, onchange: ev => update(() => els.forEach(el => (el.stretch = ev.target.checked)), `stretch:${e.id}`) }),
        e.type === 'text' ? 'Agrandir pour afficher tout le texte' : 'S\'étirer avec la bande'),
      h('p', { class: 'hint' }, help)));
  }

  nodes.push(h('fieldset', {}, h('legend', {}, 'Affichage conditionnel'),
    prop('Afficher si', textInput(e.printWhen, v => set('printWhen')(v.trim() || null), { placeholder: 'toujours (ex. remise > 0)' }), 'full')));

  nodes.push(h('div', { class: 'props-actions' },
    h('button', { type: 'button', onclick: duplicate, title: 'Ctrl+D' }, 'Dupliquer'),
    h('button', { type: 'button', onclick: () => reorder(true) }, 'Premier plan'),
    h('button', { type: 'button', onclick: () => reorder(false) }, 'Arrière-plan'),
    h('button', { type: 'button', class: 'danger', onclick: deleteSelection, title: 'Suppr' }, 'Supprimer')));
  return nodes;
}

function bandProps() {
  const band = getBand(S.band);
  if (!band) return [];
  const info = bandInfo(band.type);
  const set = key => value => update(() => (band[key] = value), `band-${key}:${bandKey(band)}`);
  const groupWarning = isGroupBand(band.type) && !S.template.groups[band.group || 0]?.expression
    && h('p', { class: 'hint', style: { color: 'var(--danger)' } }, 'Renseignez l\'expression de ce niveau dans « Groupes » (propriétés du rapport) pour activer cette bande.');
  return [
    h('div', { class: 'props-title' }, h('h3', {}, `Bande : ${bandLabel(band)}`), h('span', { class: 'badge' }, info.desc)),
    h('div', { class: 'grid2' },
      prop('Hauteur (pt)', numberInput(band.height, set('height'), { step: 1, min: 0 })),
      prop('Imprimer si', textInput(band.printWhen, v => set('printWhen')(v.trim() || null),
        { placeholder: band.type === 'pageHeader' ? 'toujours (ex. $PAGE > 1)' : 'toujours (ex. montant > 0)' }), 'full'),
      prop('Éléments', h('input', { type: 'text', value: band.elements.length, disabled: true })),
      prop('Fond', colorInput(band.background, set('background')), 'full'),
      band.type === 'detail' && prop('Fond une ligne sur deux', colorInput(band.alternateBackground, set('alternateBackground')), 'full')),
    groupWarning,
    h('div', { class: 'props-actions' },
      h('button', { type: 'button', onclick: selectAllInBand }, 'Tout sélectionner'),
      h('button', { type: 'button', class: 'danger', onclick: () => toggleBand(bandKey(band), false) }, 'Supprimer la bande')),
  ];
}

function reportProps() {
  const t = S.template;
  const p = t.page;
  const setPage = key => value => { update(() => (p[key] = value), `page-${key}`); };
  const setMargin = key => value => update(() => (p.margins[key] = value ?? 0), `margin-${key}`);
  return [
    h('fieldset', {}, h('legend', {}, 'Rapport'),
      h('div', { class: 'grid2' },
        prop('Titre', textInput(t.title, v => update(() => { t.title = v || null; updateTitle(); }, 'title')), 'full'))),
    groupsEditor(),
    h('fieldset', {}, h('legend', {}, 'Page'),
      h('div', { class: 'grid2' },
        prop('Format', selectInput(p.size, [...Object.keys(PAGE_SIZES).map(k => [k, k]), ['Custom', 'Personnalisé']],
          v => mutate(() => { p.size = v; if (v === 'Custom') { p.width ??= 595.28; p.height ??= 841.89; } }))),
        prop('Orientation', selectInput(p.orientation, [['portrait', 'Portrait'], ['landscape', 'Paysage']], v => mutate(() => (p.orientation = v)))),
        p.size === 'Custom' && prop('Largeur (pt)', numberInput(p.width, setPage('width'), { min: 50 })),
        p.size === 'Custom' && prop('Hauteur (pt)', numberInput(p.height, setPage('height'), { min: 50 })),
        prop('Fond de page', colorInput(p.background, setPage('background')), 'full')),
      h('div', { class: 'grid4', style: { marginTop: '6px' } },
        prop('Haut', numberInput(p.margins.top, setMargin('top'), { min: 0 })),
        prop('Droite', numberInput(p.margins.right, setMargin('right'), { min: 0 })),
        prop('Bas', numberInput(p.margins.bottom, setMargin('bottom'), { min: 0 })),
        prop('Gauche', numberInput(p.margins.left, setMargin('left'), { min: 0 }))),
      h('p', { class: 'hint' }, `Zone utile : ${contentWidth()} pt de large (page ${pageSize().map(round).join(' × ')} pt).`)),
    h('fieldset', {}, h('legend', {}, 'Texte par défaut'),
      h('div', { class: 'grid2' },
        prop('Police', textInput(p.fontFamily, setPage('fontFamily'), { list: 'fontList2' })),
        prop('Taille', numberInput(p.fontSize, setPage('fontSize'), { step: 0.5, min: 1 })),
        prop('Culture (formats)', textInput(p.culture, setPage('culture'), { list: 'cultureList', placeholder: 'invariante' }), 'full'),
        datalist('fontList2', FONTS), datalist('cultureList', CULTURES))),
  ];
}

// ---------------------------------------------------------------------------
// Données d'exemple
// ---------------------------------------------------------------------------

function refreshData() {
  $('#dataEditor').value = S.template.sampleData === undefined || S.template.sampleData === null
    ? '' : JSON.stringify(S.template.sampleData, null, 2);
  setDataStatus('');
  S.fields = extractFields(S.template.sampleData);
  renderFields();
}

function setDataStatus(message, kind = '') {
  const node = $('#dataStatus');
  node.textContent = message;
  node.className = `status ${kind}`;
}

/** Applique le JSON saisi. Retourne false si invalide. */
function applyData({ silent = false } = {}) {
  const raw = $('#dataEditor').value.trim();
  let data = null;
  if (raw) {
    try { data = JSON.parse(raw); } catch (err) { setDataStatus(`JSON invalide : ${err.message}`, 'error'); return false; }
  }
  if (JSON.stringify(data) !== JSON.stringify(S.template.sampleData ?? null)) {
    pushUndo();
    S.template.sampleData = data;
    markDirty();
  }
  S.fields = extractFields(data);
  renderFields();
  renderProps();
  const rowCount = Array.isArray(data) ? data.length : Array.isArray(data?.rows) ? data.rows.length : 0;
  if (!silent) setDataStatus(`✓ ${rowCount} ligne(s), ${S.fields.rows.length} champ(s), ${S.fields.parameters.length} paramètre(s).`, 'ok');
  return true;
}

// ---------------------------------------------------------------------------
// Aperçu, ouverture, enregistrement
// ---------------------------------------------------------------------------

async function preview() {
  if (!applyData({ silent: true })) { switchTab('data'); toast('Corrigez les données JSON avant l\'aperçu.', true); return; }
  const button = $('[data-action=preview]');
  button.disabled = true;
  button.textContent = 'Génération…';
  try {
    const response = await api('POST', '/api/render', { template: S.template, data: null });
    const blob = await response.blob();
    if (S.previewUrl) URL.revokeObjectURL(S.previewUrl);
    S.previewUrl = URL.createObjectURL(blob);
    $('#previewFrame').src = S.previewUrl;
    $('#previewOpen').href = S.previewUrl;
    $('#previewDownload').href = S.previewUrl;
    $('#previewDownload').download = `${S.savedName || S.template.name || 'rapport'}.pdf`;
    $('#previewModal').hidden = false;
  } catch (err) {
    toast(`Erreur de génération :\n${err.message}`, true);
  } finally {
    button.disabled = false;
    button.textContent = '▶ Aperçu PDF';
  }
}

function setTemplate(template, savedName) {
  S.template = normalize(template);
  S.savedName = savedName;
  S.selection.clear();
  S.band = getBand('detail') ? 'detail' : (S.template.bands[0] ? bandKey(S.template.bands[0]) : 'detail');
  S.undo = [];
  S.redo = [];
  S.dirty = false;
  if (savedName) {
    try { localStorage.setItem('reportDesigner.last', savedName); } catch { /* stockage indisponible */ }
    history.replaceState(null, '', `#${encodeURIComponent(savedName)}`);
  } else {
    history.replaceState(null, '', location.pathname);
  }
  refreshData();
  render();
}

async function loadTemplate(name) {
  const response = await api('GET', `/api/templates/${encodeURIComponent(name)}`);
  setTemplate(await response.json(), name);
}

function confirmDiscard() {
  return !S.dirty || confirm('Les modifications non enregistrées seront perdues. Continuer ?');
}

async function save(asNew = false) {
  if (!applyData({ silent: true })) { switchTab('data'); toast('Données JSON invalides : enregistrement annulé.', true); return; }
  let name = S.savedName;
  if (asNew || !name) {
    name = prompt('Nom du modèle (lettres, chiffres, « - » et « _ ») :', S.savedName || S.template.name || 'mon-rapport');
    if (name === null) return;
    name = name.trim();
    if (!NAME_PATTERN.test(name)) { toast('Nom invalide : lettres, chiffres, « - » et « _ » uniquement.', true); return; }
    if (name !== S.savedName) {
      const existing = await (await api('GET', '/api/templates')).json();
      if (existing.some(t => t.name === name) && !confirm(`Le modèle « ${name} » existe déjà. Le remplacer ?`)) return;
    }
  }
  try {
    S.template.name = name;
    await api('PUT', `/api/templates/${encodeURIComponent(name)}`, S.template);
    S.savedName = name;
    S.dirty = false;
    try { localStorage.setItem('reportDesigner.last', name); } catch { /* ignoré */ }
    history.replaceState(null, '', `#${encodeURIComponent(name)}`);
    updateTitle();
    toast(`Modèle « ${name} » enregistré.`);
  } catch (err) {
    toast(`Enregistrement impossible :\n${err.message}`, true);
  }
}

async function showOpenDialog() {
  let list;
  try { list = await (await api('GET', '/api/templates')).json(); } catch (err) { toast(err.message, true); return; }
  const items = list.map(t => h('li', {},
    h('div', { class: 'info', onclick: async () => {
      if (!confirmDiscard()) return;
      $('#openModal').hidden = true;
      try { await loadTemplate(t.name); } catch (err) { toast(err.message, true); }
    } },
      h('b', {}, t.title || t.name),
      h('small', {}, `${t.name}.json — modifié le ${new Date(t.modifiedUtc).toLocaleString('fr-FR')}`)),
    h('a', { class: 'button', href: `/api/reports/${encodeURIComponent(t.name)}`, target: '_blank', title: 'PDF avec les données d\'exemple' }, 'PDF'),
    h('button', { class: 'danger', title: 'Supprimer', onclick: async () => {
      if (!confirm(`Supprimer définitivement le modèle « ${t.name} » ?`)) return;
      try {
        await api('DELETE', `/api/templates/${encodeURIComponent(t.name)}`);
        if (S.savedName === t.name) { S.savedName = null; S.dirty = true; updateTitle(); }
        showOpenDialog();
      } catch (err) { toast(err.message, true); }
    } }, 'Supprimer')));
  $('#templateList').replaceChildren(...(items.length ? items : [h('li', {}, 'Aucun modèle enregistré.')]));
  $('#openModal').hidden = false;
}

async function importTemplate() {
  const file = await pickFile('.json,application/json');
  if (!file || !confirmDiscard()) return;
  try {
    const template = JSON.parse(await readFile(file, false));
    if (!template || !Array.isArray(template.bands)) throw new Error('Ce fichier ne contient pas de modèle (propriété « bands » manquante).');
    setTemplate(template, null);
    S.dirty = true;
    updateTitle();
    toast('Modèle importé. Pensez à l\'enregistrer.');
  } catch (err) {
    toast(`Import impossible : ${err.message}`, true);
  }
}

function exportTemplate() {
  applyData({ silent: true });
  const blob = new Blob([JSON.stringify(S.template, null, 2)], { type: 'application/json' });
  downloadBlob(blob, `${S.savedName || S.template.name || 'rapport'}.json`);
}

// ---------------------------------------------------------------------------
// Initialisation
// ---------------------------------------------------------------------------

function switchTab(name) {
  $$('.tab').forEach(t => t.classList.toggle('active', t.dataset.tab === name));
  $$('.tab-body').forEach(b => (b.hidden = b.dataset.tabBody !== name));
}

const ACTIONS = {
  new: () => { if (confirmDiscard()) setTemplate(defaultTemplate(), null); },
  open: showOpenDialog,
  save: () => save(false),
  saveAs: () => save(true),
  import: importTemplate,
  export: exportTemplate,
  undo,
  redo,
  preview,
  applyData: () => applyData(),
  formatData: () => {
    try { $('#dataEditor').value = JSON.stringify(JSON.parse($('#dataEditor').value), null, 2); setDataStatus(''); }
    catch (err) { setDataStatus(`JSON invalide : ${err.message}`, 'error'); }
  },
  loadDataFile: async () => {
    const file = await pickFile('.json,application/json');
    if (!file) return;
    $('#dataEditor').value = await readFile(file, false);
    applyData();
  },
};

function initUi() {
  document.addEventListener('click', e => {
    const action = e.target.closest('[data-action]')?.dataset.action;
    if (action && ACTIONS[action]) ACTIONS[action]();
    if (e.target.closest('[data-close]')) e.target.closest('.modal').hidden = true;
  });
  $$('.modal').forEach(m => m.addEventListener('pointerdown', e => { if (e.target === m) m.hidden = true; }));

  $$('.tab').forEach(t => t.addEventListener('click', () => switchTab(t.dataset.tab)));
  $$('[data-align]').forEach(b => b.addEventListener('click', () => align(b.dataset.align)));

  $('#zoom').addEventListener('change', e => { S.zoom = Number(e.target.value); try { localStorage.setItem('reportDesigner.zoom', S.zoom); } catch { /* */ } renderCanvas(); });
  $('#snap').addEventListener('change', e => (S.snap = e.target.checked));
  $('#fieldFilter').addEventListener('input', renderFields);
  $('#dataEditor').addEventListener('keydown', e => {
    if (e.key === 'Tab') { e.preventDefault(); e.target.setRangeText('  ', e.target.selectionStart, e.target.selectionEnd, 'end'); }
  });

  for (const button of $$('#toolbox [data-tool]')) {
    button.addEventListener('dragstart', e => e.dataTransfer.setData('application/x-report-designer', JSON.stringify({ tool: button.dataset.tool })));
    button.addEventListener('click', () => addElement({ tool: button.dataset.tool }, S.band || 'detail'));
  }

  document.addEventListener('keydown', onKeyDown);
  window.addEventListener('beforeunload', e => { if (S.dirty) { e.preventDefault(); e.returnValue = ''; } });
}

function onKeyDown(e) {
  const ctrl = e.ctrlKey || e.metaKey;
  const key = e.key.toLowerCase();
  const typing = /^(INPUT|TEXTAREA|SELECT)$/.test(document.activeElement?.tagName);

  if (ctrl && key === 's') { e.preventDefault(); save(false); return; }
  if (ctrl && key === 'enter') { e.preventDefault(); preview(); return; }
  if (e.key === 'Escape') {
    const modal = $$('.modal').find(m => !m.hidden);
    if (modal) { modal.hidden = true; return; }
  }
  if (typing) return;

  if (ctrl && key === 'z' && !e.shiftKey) { e.preventDefault(); undo(); }
  else if (ctrl && (key === 'y' || (key === 'z' && e.shiftKey))) { e.preventDefault(); redo(); }
  else if (ctrl && key === 'c') { copySelection(); }
  else if (ctrl && key === 'v') { e.preventDefault(); paste(); }
  else if (ctrl && key === 'd') { e.preventDefault(); duplicate(); }
  else if (ctrl && key === 'a') { e.preventDefault(); selectAllInBand(); }
  else if (e.key === 'Delete' || e.key === 'Backspace') { e.preventDefault(); deleteSelection(); }
  else if (e.key === 'Escape') { S.selection.clear(); render(); }
  else if (e.key.startsWith('Arrow') && S.selection.size) {
    e.preventDefault();
    const step = e.shiftKey ? 10 : 1;
    nudge({ ArrowLeft: -step, ArrowRight: step }[e.key] || 0, { ArrowUp: -step, ArrowDown: step }[e.key] || 0);
  }
}

async function start() {
  initUi();
  initCanvasInteractions();
  try {
    const zoom = localStorage.getItem('reportDesigner.zoom');
    if (zoom) { S.zoom = Number(zoom); $('#zoom').value = zoom; }
  } catch { /* stockage indisponible */ }

  try {
    for (const family of await (await api('GET', '/api/fonts')).json())
      if (!FONTS.some(f => f.toLowerCase() === family.toLowerCase())) FONTS.push(family);
  } catch { /* liste par défaut */ }

  let available = [];
  try { available = (await (await api('GET', '/api/templates')).json()).map(t => t.name); } catch { /* API indisponible */ }
  let last = null;
  try { last = localStorage.getItem('reportDesigner.last'); } catch { /* */ }
  const wanted = [decodeURIComponent(location.hash.slice(1)), last, available[0]].find(n => n && available.includes(n));

  if (wanted) {
    try { await loadTemplate(wanted); return; } catch (err) { toast(err.message, true); }
  }
  setTemplate(defaultTemplate(), null);
}

start();
