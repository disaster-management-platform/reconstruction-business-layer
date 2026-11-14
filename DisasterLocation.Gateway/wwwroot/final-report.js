let reportProjectId;
const money = value => '$ ' + new Intl.NumberFormat('es-CO', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(value);
const states = { Pending: 'Pendiente', InProgress: 'En ejecución', Completed: 'Terminado' };
const date = value => value ? new Date(value).toLocaleString('es-CO') : 'Sin fecha registrada';
function node(tag, text, className) {
  const element = document.createElement(tag); element.textContent = text;
  if (className) element.className = className;
  return element;
}
function references(container, title, values) {
  container.append(node('strong', title));
  const list = document.createElement('ul');
  for (const [index, value] of values.entries()) list.append(attachmentReference(reportProjectId, value, `${title} ${index + 1}`));
  if (!values.length) list.append(node('li', 'Sin referencias registradas.'));
  container.append(list);
}
function render(report) {
  reportProjectId = report.projectId;
  const data = document.querySelector('#project-data');
  for (const [label, value] of [['Proyecto', report.projectId], ['Desastre', report.disasterId], ['Estado', 'Finalizado'],
    ['Creación', date(report.createdAt)], ['Finalización', date(report.closedAt)], ['Responsable', report.responsible],
    ['Contratista', report.contractor], ['Fuente de financiación', report.fundingSource],
    ['Contrato firmado', report.signedContractReference || 'Sin referencia registrada'], ['Fecha de firma', date(report.contractSignedAt)]])
    data.append(node('dt', label), node('dd', String(value)));
  document.querySelector('#final-budget').textContent = money(report.finalBudget);
  document.querySelector('#total-executed').textContent = money(report.totalExecuted);
  document.querySelector('#remaining-budget').textContent = money(report.remainingBudget);
  document.querySelector('#total-reports').textContent = String(report.totalReports);
  document.querySelector('#activity-summary').textContent = `${report.totalActivities} ${report.totalActivities === 1 ? "actividad" : "actividades"} del cronograma en estado terminado.`;
  if (report.activitiesWithoutReports) {
    document.querySelector('#missing-reports').hidden = false;
    document.querySelector('#missing-reports').textContent = `${report.activitiesWithoutReports} ${report.activitiesWithoutReports === 1 ? "actividad no tiene reportes registrados" : "actividades no tienen reportes registrados"}. Este consolidado refleja la información disponible en el cronograma.`;
  }
  const list = document.querySelector('#activities');
  for (const activity of report.activities) {
    const article = node('article', '', 'tracked-activity'); article.dataset.activityId = activity.id;
    article.append(node('h3', activity.name));
    const facts = node('dl', '', 'activity-facts');
    for (const [label, value] of [['Responsable', activity.responsible], ['Estado', states[activity.status] || String(activity.status)],
      ['Inicio previsto', date(activity.startsAt)], ['Fin previsto', date(activity.dueAt)], ['Presupuesto', money(activity.budget)],
      ['Costo ejecutado', money(activity.actualCost)], ['Presupuesto restante', money(activity.remainingBudget)]]) {
      const fact = document.createElement('div'); fact.append(node('dt', label), node('dd', value)); facts.append(fact);
    }
    article.append(facts);
    if (!activity.reports.length) article.append(node('p', 'No se registraron reportes para esta actividad.', 'muted'));
    for (const [index, report] of activity.reports.entries()) {
      const entry = node('div', '', 'report-entry');
      entry.append(node('h4', `Reporte ${index + 1} · ${states[report.status] || String(report.status)}`),
        node('p', `Fecha: ${date(report.registeredAt)} · Usuario: ${report.actor}`), node('p', `Costo del reporte: ${money(report.actualCost)}`));
      references(entry, 'Evidencias', report.evidence); references(entry, 'Documentación', report.documents);
      article.append(entry);
    }
    list.append(article);
  }
  document.querySelector('#final-report').hidden = false;
  document.querySelector('#print-report').disabled = false;
}
document.querySelector('#print-report').addEventListener('click', () => window.print());
async function initialize() {
  const id = new URLSearchParams(location.search).get('projectId');
  const status = document.querySelector('#report-status');
  if (!id) { status.textContent = 'Abre el reporte desde un proyecto finalizado en Monitoreo.'; return; }
  document.querySelector('#back').href = `/monitoring.html?projectId=${encodeURIComponent(id)}`;
  status.textContent = 'Cargando reporte final…';
  try {
    const response = await platformFetch(`/api/reconstruccion/proyectos/${encodeURIComponent(id)}/reporte-final`);
    const report = await response.json().catch(() => null);
    if (!response.ok) throw new Error(report?.detail || `No se pudo cargar el reporte (HTTP ${response.status}).`);
    render(report); status.textContent = '';
  } catch (error) { status.textContent = error.message; status.dataset.error = 'true'; }
}
initialize();
