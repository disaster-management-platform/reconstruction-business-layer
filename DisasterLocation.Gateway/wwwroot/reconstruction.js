const sourceForm = document.querySelector('#source-form');
const source = document.querySelector('#source');
const consult = document.querySelector('#consult');
const results = document.querySelector('#results');
const sort = document.querySelector('#sort');
const newProject = document.querySelector('#new-project');
const levels = { Minor: [1, 'Daño leve', 'Afectaciones menores que requieren intervención.'],
  Moderate: [2, 'Daño moderado', 'Afectación parcial del funcionamiento del activo.'],
  Severe: [3, 'Daño grave', 'Daños importantes que comprometen el funcionamiento.'],
  Destroyed: [4, 'Destrucción total', 'El activo requiere reconstrucción.'] };
const money = value => '$ ' + new Intl.NumberFormat('es-CO', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(value);
const projectStates = { Draft: 'Borrador', Formalized: 'Formalizado', Monitoring: 'En monitoreo', Closed: 'Cerrado' };
let disasters = [];
let projects = [];
let proposals = [];
let busy = false;
let sourceRevision = 0;
let evaluationsMode = null;
function setBusy(value) {
  busy = value;
  source.disabled = value || source.options.length === 1;
  consult.disabled = source.disabled;
  newProject.disabled = value;
}
function element(tag, text, className) {
  const node = document.createElement(tag);
  node.textContent = text;
  if (className) node.className = className;
  return node;
}
function status(text, error = false) {
  const node = document.querySelector('#source-status');
  node.textContent = text;
  node.dataset.error = String(error);
}
async function api(path, body) {
  const response = await platformFetch(path, body === undefined ? undefined : {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body)
  });
  if (!response.ok) {
    const problem = await response.json().catch(() => null);
    throw new Error(problem?.detail || `No se pudo completar la consulta (HTTP ${response.status}).`);
  }
  if (path.endsWith('/propuestas/generar')) evaluationsMode = response.headers.get('X-Evaluations-Mode');
  return response.json();
}
function invalidate() {
  window.dispatchEvent(new CustomEvent('reconstruction-context', { detail: null }));
  sourceRevision++;
  proposals = [];
  projects = [];
  results.hidden = true;
  document.querySelector('#existing-projects').hidden = true;
  document.querySelector('#demo-notice').hidden = true;
}
function projectUrl(project) {
  const page = ['Draft', 'Formalized'].includes(project.status) ? 'formalization' : 'monitoring';
  return `/${page}.html?projectId=${encodeURIComponent(project.id)}`;
}
function renderProjects() {
  const list = document.querySelector('#projects');
  list.replaceChildren();
  if (!projects.length) list.append(element('p', 'Este desastre aún no tiene proyectos.', 'empty'));
  for (const project of projects) {
    const row = element('p', '');
    const link = element('a', `Abrir proyecto ${project.id.slice(0, 8)} · ${projectStates[project.status] || project.status}`, 'location-button');
    link.href = projectUrl(project);
    row.append(link);
    list.append(row);
  }
  document.querySelector('#existing-projects').hidden = false;
}
function evaluationDetail(evaluation) {
  const block = element('div', '', 'evaluation');
  block.append(element('p', evaluation.description));
  const metadata = document.createElement('dl');
  for (const [label, value] of [['Evaluación', evaluation.evaluationId], ['Fecha', new Date(evaluation.evaluatedAt).toLocaleString('es-CO')],
    ['Evaluador', evaluation.evaluator], ['Organización', evaluation.organization], ['Ubicación', evaluation.location],
    ['Estado', evaluation.status === 'Validated' ? 'Validada' : evaluation.status], ['Presupuesto', money(evaluation.budget)]]) {
    metadata.append(element('dt', label), element('dd', value));
  }
  block.append(metadata, element('p', 'Evidencias:'));
  const evidence = document.createElement('ul');
  for (const reference of evaluation.evidence) evidence.append(element('li', reference));
  if (!evaluation.evidence.length) evidence.append(element('li', 'Sin referencias de evidencia.'));
  block.append(evidence);
  return block;
}
function render() {
  const list = document.querySelector('#proposals');
  list.replaceChildren();
  const ordered = [...proposals].sort((a, b) => (levels[a.damage][0] - levels[b.damage][0]) * (sort.value === 'asc' ? 1 : -1)
    || (a.proposedBudget - b.proposedBudget) * (sort.value === 'asc' ? 1 : -1)
    || a.zone.localeCompare(b.zone) || a.assetType.localeCompare(b.assetType));
  if (!ordered.length) list.append(element('p', 'No hay daños evaluados que requieran reconstrucción.', 'empty'));
  for (const proposal of ordered) {
    const article = element('article', '', 'proposal');
    article.id = `proposal-${proposal.id}`;
    const top = element('div', '', 'proposal-top');
    const content = document.createElement('div');
    content.append(element('h3', `${proposal.assetType} · ${proposal.zone}`, 'proposal-name'),
      element('p', `${proposal.assessmentCount} evaluaciones · Gravedad promedio: ${proposal.averageSeverity.toFixed(2)}/4 · ${money(proposal.proposedBudget)}`, 'proposal-meta'));
    const project = projects.find(p => p.proposalId === proposal.id);
    if (proposal.status === 'Proposed') {
      const link = element('a', 'Crear proyecto y formalizar', 'location-button');
      link.href = `/formalization.html?proposalId=${encodeURIComponent(proposal.id)}&expectedVersion=${proposal.version}`;
      content.append(link);
    } else if (proposal.status === 'Selected') {
      content.append(element('p', 'Propuesta con proyecto.', 'saved'));
      if (project) {
        const link = element('a', 'Abrir proyecto', 'location-button');
        link.href = projectUrl(project);
        content.append(link);
      }
    }
    const level = levels[proposal.damage];
    const badge = element('span', level[1], `damage damage-${level[0]}`);
    badge.title = level[2];
    top.append(content, badge);
    article.append(top, element('p', level[2], 'muted'));
    const details = document.createElement('details');
    details.append(element('summary', 'Ver evaluaciones y evidencias'));
    for (const evaluation of proposal.evaluations) details.append(evaluationDetail(evaluation));
    if (!proposal.evaluations.length) details.append(element('p', 'Propuesta histórica sin detalle de evaluaciones.'));
    article.append(details);
    list.append(article);
  }
}
source.addEventListener('change', () => { invalidate(); renderPhase(disasters.find(d => d.id === Number(source.value))); });
sourceForm.addEventListener('submit', async event => {
  event.preventDefault();
  if (!sourceForm.reportValidity() || busy) return;
  invalidate();
  const revision = sourceRevision;
  const disasterId = Number(source.value);
  const disaster = disasters.find(d => d.id === disasterId);
  renderPhase(disaster);
  setBusy(true);
  status('Consultando proyectos y evaluaciones del desastre…');
  try {
    const loaded = await Promise.allSettled([
      api(`/api/reconstruccion/proyectos?disasterId=${disasterId}`),
      api('/api/reconstruccion/propuestas/generar', { disasterId })
    ]);
    if (revision !== sourceRevision) return;
    window.dispatchEvent(new CustomEvent('reconstruction-context', { detail: { disasterId } }));
    if (loaded[0].status === 'fulfilled') { projects = loaded[0].value; renderProjects(); }
    if (loaded[1].status === 'fulfilled') {
      proposals = loaded[1].value;
      document.querySelector('#context').textContent = disaster?.nombre || `Desastre ${disasterId}`;
      results.hidden = false;
      document.querySelector('#demo-notice').hidden = evaluationsMode !== 'demo';
      sort.value = 'desc';
      render();
    }
    const errors = loaded.filter(result => result.status === 'rejected');
    status(errors.length ? errors.map(result => result.reason.message).join(' ')
      : `${projects.length} proyectos · ${proposals.filter(p => p.status === 'Proposed').length} propuestas disponibles.`, errors.length > 0);
  } finally { setBusy(false); }
});
newProject.addEventListener('click', () => { if (!busy) sourceForm.requestSubmit(); });
sort.addEventListener('change', render);

async function initialize() {
  try {
    disasters = await api('/api/desastres');
    source.replaceChildren(new Option('Selecciona un desastre', ''));
    for (const disaster of disasters) source.add(new Option(`${disaster.nombre} · ${disaster.ubicacion}`, String(disaster.id)));
    setBusy(false);
    if (!disasters.length) status('No hay desastres registrados. Registra uno en la sección Registro y mapa.');
    const disasterId = new URLSearchParams(location.search).get('disasterId');
    if (disasterId && disasters.some(d => String(d.id) === disasterId)) {
      source.value = disasterId;
      sourceForm.requestSubmit();
    }
  } catch (error) { status(error.message, true); }
}
initialize();

const phasePanel = document.createElement('div');
const phaseText = document.createElement('p');
const phaseButton = document.createElement('button'); phaseButton.type = 'button';
phasePanel.append(phaseText, phaseButton); sourceForm.after(phasePanel); phasePanel.hidden = true;
function renderPhase(disaster) {
  phasePanel.hidden = !disaster;
  if (!disaster) return;
  const labels = { Registered: 'Registrado', Response: 'En respuesta', Recovery: 'En recuperación', Closed: 'Cerrado' };
  phaseText.textContent = `Fase del desastre: ${labels[disaster.phase] || 'Sin información'}`;
  phaseButton.hidden = !['Registered', 'Response'].includes(disaster.phase);
  phaseButton.textContent = disaster.phase === 'Registered' ? 'Iniciar fase de respuesta' : 'Finalizar respuesta y habilitar reconstrucción';
}
phaseButton.addEventListener('click', async () => {
  const disaster = disasters.find(d => d.id === Number(source.value));
  if (!disaster) return;
  phaseButton.disabled = true;
  try {
    const response = await platformFetch(`/api/desastres/${disaster.id}/fase`, { method: 'PUT',
      headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ phase: disaster.phase === 'Registered' ? 'Response' : 'Recovery', expectedVersion: disaster.version }) });
    if (!response.ok) throw new Error('No se pudo cambiar la fase. Actualiza la página y revisa tus permisos.');
    const updated = await response.json(); Object.assign(disaster, updated); renderPhase(disaster);
    status('Fase actualizada. Puedes volver a consultar las propuestas.');
  } catch (error) { status(error.message, true); }
  finally { phaseButton.disabled = false; }
});
