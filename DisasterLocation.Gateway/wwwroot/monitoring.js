const picker = document.querySelector('#project');
const refresh = document.querySelector('#refresh');
const form = document.querySelector('#report-form');
const fields = document.querySelector('#report-fields');
const activityPicker = document.querySelector('#activity');
const reportState = document.querySelector('#report-state');
const statuses = { Pending: 'Pendiente', InProgress: 'En ejecución', Completed: 'Terminado' };
const projectStates = { Draft: 'En formalización', Formalized: 'Formalizado', Monitoring: 'En monitoreo', Closed: 'Cerrado' };
const money = value => '$ ' + new Intl.NumberFormat('es-CO', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(value);
let current = null;
let busy = false;
let requiresRefresh = false;
let canFinalize = false;

function node(tag, text, className) {
  const element = document.createElement(tag); element.textContent = text;
  if (className) element.className = className;
  return element;
}
function message(selector, text, error = false) {
  const element = document.querySelector(selector); element.textContent = text; element.dataset.error = String(error);
}
async function api(path, body) {
  const response = await platformFetch('/api/reconstruccion' + path, body === undefined ? undefined : {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body)
  });
  if (!response.ok) {
    const problem = await response.json().catch(() => null);
    const error = new Error(problem?.detail || `No se pudo completar la operación (HTTP ${response.status}).`);
    error.status = response.status;
    throw error;
  }
  return response.json();
}
function controls() {
  picker.disabled = busy || picker.options.length <= 1;
  refresh.disabled = busy || !picker.value;
  fields.disabled = busy || requiresRefresh || !current || current.status !== 'Monitoring' || !current.activities.some(a => !a.isResolved);
  for (const button of document.querySelectorAll('.choose-report')) button.disabled = busy || requiresRefresh;
  document.querySelector('#budget-revision-form').querySelector('button').disabled = busy || requiresRefresh;
  document.querySelector('#finalize').disabled = busy || requiresRefresh || !canFinalize;
}
function selectActivity(id) {
  activityPicker.value = id;
  const activity = current?.activities.find(a => a.id === id);
  const context = document.querySelector('#activity-context');
  for (const option of reportState.options) option.disabled = false;
  if (!activity) { context.textContent = ''; document.querySelector('#actual-cost').removeAttribute('max'); return; }
  context.textContent = `${activity.responsible} · Costo acumulado: ${money(activity.actualCost)} · Presupuesto restante: ${money(activity.remainingBudget)}`;
  document.querySelector('#actual-cost').max = String(activity.remainingBudget);
  for (const option of reportState.options)
    option.disabled = activity.status === 'Completed' ? option.value !== 'Completed'
      : activity.status === 'InProgress' && option.value === 'Pending';
  reportState.value = activity.status;
}
function reportReferences(container, title, references) {
  container.append(node('strong', title));
  const list = document.createElement('ul');
  for (const [index, reference] of references.entries()) list.append(attachmentReference(current.id, reference, `${title} ${index + 1}`));
  if (!references.length) list.append(node('li', 'Sin referencias registradas.'));
  container.append(list);
}
function renderActivities() {
  const list = document.querySelector('#activities'); list.replaceChildren();
  for (const activity of current.activities) {
    const article = node('article', '', 'tracked-activity'); article.dataset.activityId = activity.id;
    const heading = node('div', '', 'tracked-heading');
    heading.append(node('h3', activity.name), node('span', statuses[activity.status], `activity-status ${activity.status}`));
    const facts = node('dl', '', 'activity-facts');
    for (const [label, value] of [['Responsable', activity.responsible], ['Inicio', new Date(activity.startsAt).toLocaleDateString('es-CO')],
      ['Finalización prevista', new Date(activity.dueAt).toLocaleDateString('es-CO')], ['Presupuesto', money(activity.budget)],
      ['Costo acumulado', money(activity.actualCost)], ['Presupuesto restante', money(activity.remainingBudget)]]) {
      const fact = document.createElement('div'); fact.append(node('dt', label), node('dd', value)); facts.append(fact);
    }
    article.append(heading, facts);
    if (activity.isResolved) article.append(node('p', 'Actividad resuelta con reporte final, evidencias y documentación.', 'saved'));
    else if (activity.status === 'Completed') article.append(node('p', 'Marcada como terminada en el cronograma; falta registrar su reporte final.', 'muted'));
    if (current.status === 'Monitoring' && !activity.isResolved) {
      const button = node('button', 'Registrar reporte', 'secondary choose-report'); button.type = 'button';
      button.addEventListener('click', () => {
        selectActivity(activity.id);
        document.querySelector('#report-section').scrollIntoView?.({ behavior: 'smooth', block: 'start' });
        activityPicker.focus();
      }); article.append(button);
    }
    const history = document.createElement('details'); history.className = 'report-history';
    history.append(node('summary', `Historial de reportes (${activity.reports.length})`));
    if (!activity.reports.length) history.append(node('p', 'Esta actividad todavía no tiene reportes.', 'muted'));
    for (const [index, report] of activity.reports.entries()) {
      const entry = node('div', '', 'report-entry');
      entry.append(node('h4', `Reporte ${index + 1} · ${statuses[report.status]}`),
        node('p', `${new Date(report.registeredAt).toLocaleString('es-CO')} · Registrado por ${report.actor}`),
        node('p', `Costo de este reporte: ${money(report.actualCost)}`));
      reportReferences(entry, 'Evidencias', report.evidence);
      reportReferences(entry, 'Documentación', report.documents);
      history.append(entry);
    }
    article.append(history); list.append(article);
  }
  if (!current.activities.length) list.append(node('p', 'Este proyecto no tiene actividades en su cronograma.', 'empty'));
}
function render(data, selectedId = '') {
  requiresRefresh = false;
  current = data.project;
  window.dispatchEvent(new CustomEvent('reconstruction-context', { detail: { disasterId: current.disasterId, projectId: current.id } }));
  document.querySelector('#monitoring').hidden = false;
  const summary = data.summary;
  canFinalize = summary.canFinalize;
  document.querySelector('#project-state').textContent = `${projectStates[current.status]} · Proyecto ${current.id}`;
  document.querySelector('#phase-notice').textContent = current.status === 'Monitoring' ? '' : current.status === 'Closed'
    ? 'Proyecto cerrado. Puedes consultar su trazabilidad.' : 'Completa y confirma la formalización para habilitar los reportes.';
  document.querySelector('#resolved-count').textContent = `${summary.resolvedActivities} / ${summary.totalActivities}`;
  document.querySelector('#project-budget').textContent = current.isBudgetLocked ? money(current.finalBudget) : 'Sin bloquear';
  document.querySelector('#total-cost').textContent = money(summary.totalCost);
  document.querySelector('#remaining-budget').textContent = money(summary.remainingBudget);
  document.querySelector('#progress').value = summary.progressPercentage;
  document.querySelector('#progress-label').textContent = `Avance respaldado por reportes: ${summary.progressPercentage}%`;
  document.querySelector('#status-counts').textContent = `Pendientes: ${summary.pendingActivities} · En ejecución: ${summary.inProgressActivities} · Terminadas: ${summary.completedActivities} · Reportes: ${summary.reportsCount}`;
  renderActivities();
  document.querySelector('#budget-revision-section').hidden = current.status !== 'Monitoring';
  document.querySelector('#revised-budget').value = current.finalBudget;
  const allocation = document.querySelector('#revised-activities'); allocation.replaceChildren();
  for (const activity of current.activities) {
    const label = node('label', activity.name); const input = document.createElement('input');
    input.type = 'number'; input.min = String(activity.actualCost); input.step = '0.01'; input.required = true;
    input.value = activity.budget; input.dataset.activityId = activity.id; label.append(input); allocation.append(label);
  }
  activityPicker.replaceChildren(new Option('Selecciona una actividad', ''));
  for (const activity of current.activities.filter(a => !a.isResolved)) activityPicker.add(new Option(activity.name, activity.id));
  form.reset();
  selectActivity(current.activities.some(a => a.id === selectedId && !a.isResolved) ? selectedId : '');
  document.querySelector('#report-section').hidden = current.status !== 'Monitoring';
  if (current.status === 'Monitoring' && current.activities.length && current.activities.every(a => a.isResolved))
    message('#report-status', 'Todas las actividades tienen su reporte final. Puedes finalizar el proyecto.');
  document.querySelector('#finalization-section').hidden = !['Monitoring', 'Closed'].includes(current.status);
  document.querySelector('#finalize').hidden = current.status === 'Closed';
  const reportLink = document.querySelector('#final-report-link');
  reportLink.hidden = current.status !== 'Closed';
  reportLink.href = `/final-report.html?projectId=${encodeURIComponent(current.id)}`;
  document.querySelector('#finalization-hint').textContent = current.status === 'Closed'
    ? `Proyecto finalizado${current.closedAt ? ' el ' + new Date(current.closedAt).toLocaleString('es-CO') : ''}. Su reporte final consolida todos los reportes registrados en el cronograma.`
    : canFinalize ? 'Todas las actividades están terminadas y tienen reporte final con evidencias y documentación. Puedes finalizar el proyecto.'
    : `Para finalizar, cada actividad debe estar terminada y tener reporte final con evidencias y documentación. Resueltas: ${summary.resolvedActivities} de ${summary.totalActivities}.`;
  controls();
}
async function load(id) {
  if (busy) return;
  window.dispatchEvent(new CustomEvent('reconstruction-context', { detail: null }));
  current = null; document.querySelector('#monitoring').hidden = true;
  canFinalize = false;
  document.querySelector('#project-state').textContent = ''; document.querySelector('#phase-notice').textContent = '';
  message('#report-status', '');
  message('#finalization-status', '');
  if (!id) { controls(); return; }
  busy = true; controls(); message('#load-status', 'Cargando cronograma y reportes…');
  try {
    render(await api(`/proyectos/${id}/monitoreo`));
    history.replaceState(null, '', `/monitoring.html?projectId=${encodeURIComponent(id)}`);
    message('#load-status', '');
  } catch (error) { message('#load-status', error.message, true); }
  finally { busy = false; controls(); }
}
picker.addEventListener('change', () => load(picker.value));
refresh.addEventListener('click', () => load(picker.value));
activityPicker.addEventListener('change', () => selectActivity(activityPicker.value));
form.addEventListener('submit', async event => {
  event.preventDefault();
  if (busy || !current || fields.disabled || !form.reportValidity()) return;
  const references = name => form.elements[name].value.split(/\r?\n/).map(value => value.trim()).filter(Boolean);
  const evidence = references('evidence'), documents = references('documents');
  if (!evidence.length || !documents.length) { message('#report-status', 'Incluye al menos una evidencia y un documento.', true); return; }
  const activityId = activityPicker.value;
  const request = { status: reportState.value, actualCost: Number(form.elements.actualCost.value), evidence, documents, expectedVersion: current.version };
  const storageKey = `report-request:${current.id}:${activityId}`;
  const content = JSON.stringify({ ...request, expectedVersion: undefined });
  const previous = JSON.parse(sessionStorage.getItem(storageKey) || 'null');
  request.requestId = previous?.content === content ? previous.requestId : crypto.randomUUID();
  sessionStorage.setItem(storageKey, JSON.stringify({ content, requestId: request.requestId }));
  busy = true; controls(); message('#report-status', 'Guardando reporte…');
  let recorded = false;
  try {
    await api(`/proyectos/${current.id}/actividades/${activityId}/reportes`, request);
    recorded = true;
    sessionStorage.removeItem(storageKey);
    const data = await api(`/proyectos/${current.id}/monitoreo`);
    render(data, activityId);
    message('#report-status', data.project.activities.every(a => a.isResolved)
      ? 'Reporte guardado. Todas las actividades están resueltas; puedes finalizar el proyecto.' : 'Reporte guardado. El avance y los costos del proyecto están actualizados.');
  } catch (error) {
    requiresRefresh = recorded || error.status === 409;
    message('#report-status', recorded ? 'El reporte fue guardado, pero no se pudo actualizar la vista. Usa Actualizar para consultar el avance.'
      : error.status === 409 ? 'El proyecto cambió mientras registrabas el reporte. Usa Actualizar para revisar los cambios antes de continuar.'
      : error.message, true);
  }
  finally { busy = false; controls(); }
});
document.querySelector('#finalize').addEventListener('click', async () => {
  if (busy || requiresRefresh || !current || !canFinalize) return;
  busy = true; controls(); message('#finalization-status', 'Finalizando proyecto y consolidando el reporte…');
  let finalized = false;
  try {
    await api(`/proyectos/${current.id}/cerrar`, { expectedVersion: current.version });
    finalized = true;
    render(await api(`/proyectos/${current.id}/monitoreo`));
    message('#report-status', '');
    message('#finalization-status', 'Proyecto finalizado. El reporte final está disponible para consulta e impresión.');
  } catch (error) {
    requiresRefresh = finalized || error.status === 409;
    message('#finalization-status', finalized ? 'El proyecto fue finalizado. Usa Actualizar para consultar su reporte final.'
      : error.status === 409 ? 'El proyecto cambió antes de finalizar. Usa Actualizar para revisar el cronograma.' : error.message, true);
  } finally { busy = false; controls(); }
});
async function initialize() {
  busy = true; controls();
  try {
    const projects = await api('/proyectos');
    picker.replaceChildren(new Option('Selecciona un proyecto', ''));
    for (const project of projects) picker.add(new Option(`${project.contractor || 'Proyecto'} · ${project.id.slice(0, 8)} · ${projectStates[project.status]}`, project.id));
    const id = new URLSearchParams(location.search).get('projectId');
    if (id) picker.value = id;
    if (!projects.length) message('#load-status', 'No hay proyectos. Crea y formaliza uno desde Reconstrucción.');
    busy = false;
    if (id) await load(id);
  } catch (error) { message('#load-status', error.message, true); }
  finally { busy = false; controls(); }
}
initialize();

for (const [inputId, field, category] of [['evidence-files', 'evidence', 'evidence'], ['document-files', 'documents', 'document']]) {
  document.querySelector(`#${inputId}`).addEventListener('change', async event => {
    if (!current || busy) return;
    busy = true; controls();
    try {
      const references = [];
      for (const file of event.target.files) {
        const body = new FormData(); body.append('file', file); body.append('category', category);
        const response = await platformFetch(`/api/reconstruccion/proyectos/${current.id}/adjuntos`, { method: 'POST', body });
        const result = await response.json();
        if (!response.ok) throw new Error(result.detail || 'No se pudo adjuntar el archivo.');
        references.push(`attachment:${result.id}`);
      }
      form.elements[field].value = references.join('\n');
      message('#report-status', 'Archivos adjuntados al proyecto.');
    } catch (error) { message('#report-status', error.message, true); }
    finally { busy = false; controls(); }
  });
}

document.querySelector('#budget-revision-form').addEventListener('submit', async event => {
  event.preventDefault(); if (busy || !current || requiresRefresh) return;
  const request = { budget: Number(document.querySelector('#revised-budget').value), reason: document.querySelector('#revision-reason').value.trim(),
    expectedVersion: current.version, activityBudgets: Object.fromEntries([...document.querySelectorAll('[data-activity-id] input, input[data-activity-id]')]
      .filter(input => input.dataset.activityId).map(input => [input.dataset.activityId, Number(input.value)])) };
  busy = true; controls();
  try {
    const response = await platformFetch(`/api/reconstruccion/proyectos/${current.id}/presupuesto`, { method: 'PUT',
      headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(request) });
    const result = await response.json(); if (!response.ok) throw new Error(result.detail || 'No se pudo actualizar el presupuesto.');
    render(await api(`/proyectos/${current.id}/monitoreo`)); document.querySelector('#revision-reason').value = '';
    message('#revision-status', 'Revisión guardada y auditada.');
  } catch (error) { message('#revision-status', error.message, true); }
  finally { busy = false; controls(); }
});
