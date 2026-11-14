const picker = document.querySelector('#project');
const form = document.querySelector('#formalization-form');
const contractForm = document.querySelector('#contract-form');
const agreementFields = document.querySelector('#agreement-fields');
const contractFields = document.querySelector('#contract-fields');
const rows = document.querySelector('#activities');
const money = value => '$ ' + new Intl.NumberFormat('es-CO', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(value);
const states = { Draft: 'En formalización', Formalized: 'Formalizado', Monitoring: 'En monitoreo', Closed: 'Cerrado' };
let current = null;
let busy = false;
let dirty = false;

function node(tag, text, className) {
  const element = document.createElement(tag);
  element.textContent = text;
  if (className) element.className = className;
  return element;
}
function message(selector, text, error = false) {
  const element = document.querySelector(selector);
  element.textContent = text;
  element.dataset.error = String(error);
}
async function api(path, method = 'GET', body) {
  const response = await platformFetch('/api/reconstruccion' + path, { method,
    ...(body === undefined ? {} : { headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }) });
  if (!response.ok) {
    const problem = await response.json().catch(() => null);
    throw new Error(problem?.detail || `No se pudo completar la operación (HTTP ${response.status}).`);
  }
  return response.json();
}
function updateControls() {
  document.querySelector('#confirm-formalization').disabled = busy || !current || !current.formalizationSavedAt || current.isBudgetLocked || dirty;
  picker.disabled = busy || picker.options.length <= 1;
  agreementFields.disabled = busy || !current || current.isBudgetLocked;
  const ready = current && (current.formalizationSavedAt || current.status === 'Formalized') && !current.budgetLockedAt;
  contractFields.disabled = busy || !ready || dirty || !['Draft', 'Formalized'].includes(current?.status);
  const hint = !current ? '' : current.budgetLockedAt || ['Monitoring', 'Closed'].includes(current.status)
    ? 'El proyecto está en monitoreo. Los cambios de presupuesto requieren un motivo y quedan auditados.'
    : dirty ? 'Guarda los cambios del acuerdo antes de registrar el contrato firmado.'
    : ready ? `Presupuesto guardado: ${money(current.finalBudget)}. Al registrar el contrato firmado se bloqueará este monto y el proyecto pasará automáticamente a monitoreo.`
    : 'Guarda los datos y confirma la formalización. El contrato firmado es opcional.';
  document.querySelector('#contract-hint').textContent = hint;
}
function markDirty() { dirty = true; updateControls(); totals(); }
function totals() {
  const total = [...rows.querySelectorAll('[data-field="budget"]')].reduce((sum, input) => sum + (Number(input.value) || 0), 0);
  const budget = Number(form.elements.finalBudget.value) || 0;
  message('#activity-totals', `Actividades: ${money(total)} · Presupuesto del proyecto: ${money(budget)}${total > budget ? ' · La suma de las actividades supera el presupuesto.' : ''}`, total > budget);
}
function inputField(container, label, field, type, value, options = {}) {
  const group = document.createElement('div');
  if (field === 'name') group.className = 'activity-task';
  const title = node('label', label);
  const input = document.createElement('input');
  input.type = type; input.value = value || ''; input.required = true; input.dataset.field = field;
  Object.assign(input, options);
  title.append(input);
  group.append(title); container.append(group);
  return input;
}
function addActivity(activity = {}) {
  const row = node('div', '', 'activity-row');
  const heading = node('div', '', 'activity-heading');
  const remove = node('button', 'Quitar actividad', 'remove-activity'); remove.type = 'button';
  remove.addEventListener('click', () => { row.remove(); renumber(); markDirty(); });
  heading.append(node('h4', '', 'activity-number'), remove);
  const fields = node('div', '', 'activity-fields');
  inputField(fields, 'Tarea', 'name', 'text', activity.name, { maxLength: 250 });
  inputField(fields, 'Responsable', 'responsible', 'text', activity.responsible, { maxLength: 200 });
  inputField(fields, 'Fecha de inicio', 'startsAt', 'date', activity.startsAt?.slice(0, 10));
  inputField(fields, 'Fecha de finalización', 'dueAt', 'date', activity.dueAt?.slice(0, 10));
  inputField(fields, 'Presupuesto', 'budget', 'number', activity.budget === undefined ? '' : String(activity.budget), { min: '0', step: '0.01' });
  row.append(heading, fields); rows.append(row); renumber();
}
function renumber() {
  for (const [index, row] of [...rows.children].entries()) row.querySelector('.activity-number').textContent = `Actividad ${index + 1}`;
}
function render(project) {
  window.dispatchEvent(new CustomEvent('reconstruction-context', { detail: { disasterId: project.disasterId, projectId: project.id } }));
  current = project; dirty = false;
  document.querySelector('#formalization').hidden = false;
  for (const field of ['finalBudget', 'responsible', 'contractor']) form.elements[field].value = project[field] || '';
  if (project.fundingSource) form.elements.fundingSource.value = project.fundingSource;
  rows.replaceChildren();
  for (const activity of project.activities) addActivity(activity);
  if (!project.activities.length && !project.isBudgetLocked) addActivity();
  document.querySelector('#project-state').textContent = `${states[project.status] || project.status} · Proyecto ${project.id}`;
  const newProject = document.querySelector('#new-project');
  newProject.href = `/reconstruction.html?disasterId=${encodeURIComponent(project.disasterId)}`;
  newProject.hidden = false;
  contractForm.reset();
  const locked = document.querySelector('#locked-summary'); locked.hidden = !project.isBudgetLocked;
  locked.replaceChildren();
  if (project.isBudgetLocked) {
    locked.append(node('strong', `Presupuesto bloqueado: ${money(project.finalBudget)}`));
    if (project.signedContractReference) {
      locked.append(node('p', `Contrato firmado: ${project.signedContractReference}`),
        node('p', `Fecha de firma: ${new Date(project.contractSignedAt).toLocaleString('es-CO')}`),
        node('p', `Registro: ${project.contractRecordedBy}`));
    } else locked.append(node('p', 'Formalización confirmada sin contrato adjunto.'));
  }
  if (['Monitoring', 'Closed'].includes(project.status)) {
    const link = node('a', 'Abrir monitoreo y reportes del proyecto', 'location-button');
    link.href = `/monitoring.html?projectId=${encodeURIComponent(project.id)}`;
    locked.append(link);
  }
  updateControls(); totals();
}
async function loadFunding(project) {
  const data = await api(`/financiacion?disasterId=${project.disasterId}`);
  form.elements.fundingSource.dataset.sourceId = data.sourceId;
  form.elements.fundingSource.replaceChildren(new Option(data.source, data.source));
  document.querySelector('#funding-availability').textContent = `Donaciones disponibles: ${money(data.availableAmount)} ${data.currency}`;
}
async function loadProject(id) {
  if (busy) return;
  current = null; document.querySelector('#formalization').hidden = true;
  window.dispatchEvent(new CustomEvent('reconstruction-context', { detail: null }));
  document.querySelector('#new-project').hidden = true;
  if (!id) return;
  busy = true; updateControls();
  message('#load-status', 'Cargando proyecto…'); message('#save-status', ''); message('#contract-status', '');
  try { const project = await api(`/proyectos/${id}`); await loadFunding(project); render(project); message('#load-status', ''); }
  catch (error) { message('#load-status', error.message, true); }
  finally { busy = false; updateControls(); }
}
picker.addEventListener('change', () => loadProject(picker.value));
form.addEventListener('input', markDirty);
form.addEventListener('change', markDirty);
document.querySelector('#add-activity').addEventListener('click', () => { addActivity(); markDirty(); });
form.addEventListener('submit', async event => {
  event.preventDefault();
  if (busy || !current || current.isBudgetLocked || !form.reportValidity()) return;
  if (!rows.children.length) { message('#save-status', 'Agrega al menos una actividad al cronograma.', true); return; }
  const activities = [...rows.children].map(row => {
    const get = field => row.querySelector(`[data-field="${field}"]`).value;
    return { name: get('name').trim(), responsible: get('responsible').trim(), budget: Number(get('budget')),
      startsAt: new Date(get('startsAt') + 'T00:00:00').toISOString(), dueAt: new Date(get('dueAt') + 'T00:00:00').toISOString() };
  });
  const request = { finalBudget: Number(form.elements.finalBudget.value), fundingSource: form.elements.fundingSource.value.trim(),
    fundingSourceId: form.elements.fundingSource.dataset.sourceId, responsible: form.elements.responsible.value.trim(), contractor: form.elements.contractor.value.trim(), activities, expectedVersion: current.version };
  busy = true; updateControls(); message('#save-status', 'Guardando formalización…'); message('#contract-status', '');
  try { render(await api(`/proyectos/${current.id}/formalizacion`, 'PUT', request)); message('#save-status', 'Formalización guardada. Puedes confirmar la formalización para iniciar el monitoreo.'); }
  catch (error) { message('#save-status', error.message, true); }
  finally { busy = false; updateControls(); }
});
contractForm.addEventListener('submit', async event => {
  event.preventDefault();
  if (busy || !current || contractFields.disabled || !contractForm.reportValidity()) return;
  const request = { contractReference: contractForm.elements.contractReference.value.trim(),
    signedAt: new Date(contractForm.elements.signedAt.value).toISOString(), expectedVersion: current.version };
  busy = true; updateControls(); message('#contract-status', 'Registrando contrato firmado…');
  try {
    render(await api(`/proyectos/${current.id}/contrato-firmado`, 'POST', request));
    message('#contract-status', 'Contrato registrado. El presupuesto quedó bloqueado y el proyecto pasó a monitoreo.');
  } catch (error) { message('#contract-status', error.message, true); }
  finally { busy = false; updateControls(); }
});
async function initialize() {
  busy = true; updateControls();
  try {
    const params = new URLSearchParams(location.search);
    let id = params.get('projectId');
    if (params.has('proposalId')) {
      const expectedVersion = Number(params.get('expectedVersion'));
      if (!Number.isSafeInteger(expectedVersion) || expectedVersion < 1) throw new Error('Vuelve a consultar las propuestas antes de crear el proyecto.');
      const project = await api('/proyectos', 'POST', { proposalId: params.get('proposalId'), expectedVersion }); id = project.id;
      history.replaceState(null, '', `/formalization.html?projectId=${encodeURIComponent(id)}`);
    }
    const projects = await api('/proyectos');
    picker.replaceChildren(new Option('Selecciona un proyecto', ''));
    for (const project of projects) picker.add(new Option(`${project.contractor || 'Proyecto'} · ${project.id.slice(0, 8)} · ${states[project.status]}`, project.id));
    if (id) picker.value = id;
    if (!projects.length) message('#load-status', 'No hay proyectos. Selecciona una reconstrucción en Propuestas para crear uno.');
    busy = false;
    if (id) await loadProject(id);
  } catch (error) { message('#load-status', error.message, true); }
  finally { busy = false; updateControls(); }
}
initialize();

document.querySelector('#confirm-formalization').addEventListener('click', async () => {
  if (busy || !current || dirty || current.isBudgetLocked) return;
  busy = true; updateControls();
  try { render(await api(`/proyectos/${current.id}/confirmar-formalizacion`, 'POST', { expectedVersion: current.version }));
    message('#contract-status', 'Formalización confirmada. Puedes registrar avances en Monitoreo.');
  } catch (error) { message('#contract-status', error.message, true); }
  finally { busy = false; updateControls(); }
});
