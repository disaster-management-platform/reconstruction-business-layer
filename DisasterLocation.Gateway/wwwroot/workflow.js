(() => {
  const panel = document.createElement('section');
  panel.id = 'workflow'; panel.className = 'panel source-panel'; panel.hidden = true;
  const heading = document.createElement('h2'); heading.textContent = 'Pendientes y avisos de reconstrucción';
  heading.id = 'workflow-title'; panel.setAttribute('aria-labelledby', heading.id);
  const list = document.createElement('div'); list.id = 'workflow-items';
  const state = document.createElement('p'); state.className = 'status'; state.setAttribute('role', 'status');
  panel.append(heading, list, state);
  const main = document.querySelector('main'); main.insertBefore(panel, main.children[1] || null);
  const money = amount => new Intl.NumberFormat('es-CO', { style: 'currency', currency: 'COP' }).format(amount || 0);
  let context = null, revision = 0, pending = false, lastSnapshot = '';
  function text(tag, value) { const node = document.createElement(tag); node.textContent = value; return node; }
  function render(items) {
    state.textContent = items.length ? '' : 'No hay pendientes ni avisos para esta consulta.';
    state.dataset.error = 'false';
    const snapshot = JSON.stringify(items);
    if (snapshot === lastSnapshot) return;
    lastSnapshot = snapshot;
    list.replaceChildren();
    for (const item of items) {
      const data = item.data;
      const project = encodeURIComponent(item.projectId || '');
      let title, description, destination, action;
      switch (item.kind) {
        case 'ProposalReview':
          title = 'Propuesta disponible para revisión';
          description = `${data.assetType} · ${data.zone} · ${money(data.proposedBudget)}`;
          destination = `/reconstruction.html?disasterId=${item.disasterId}#proposal-${encodeURIComponent(item.proposalId)}`;
          action = 'Revisar propuesta'; break;
        case 'FormalizationRequest':
          title = 'Solicitud pendiente de formalización';
          description = 'Define el acuerdo, presupuesto, responsables y cronograma de este proyecto.';
          destination = `/formalization.html?projectId=${project}`; action = 'Formalizar proyecto'; break;
        case 'ProjectReviewRequested':
          title = 'Revisión de proyecto requerida';
          description = data.reason === 'DisasterHasNoDamage'
            ? 'El desastre ya no presenta daño. Se conserva el alcance original del proyecto para revisión.'
            : 'Las evaluaciones del proyecto cambiaron. Revisa las diferencias con su alcance original.';
          destination = `/formalization.html?projectId=${project}`; action = 'Consultar proyecto'; break;
        case 'MonitoringPrepared':
          title = 'Información de seguimiento preparada';
          description = `${data.activities.length} actividades · Presupuesto: ${money(data.finalBudget)}`;
          destination = `/monitoring.html?projectId=${project}`; action = 'Abrir seguimiento'; break;
        case 'ClosureAvailable':
          title = 'Cierre disponible para revisión';
          description = `${data.resolvedActivities} de ${data.totalActivities} actividades resueltas con reporte final completo.`;
          destination = `/monitoring.html?projectId=${project}#finalization-section`; action = 'Revisar y finalizar proyecto'; break;
        case 'ProjectClosed':
          title = 'Proyecto finalizado';
          description = `${data.totalActivities} actividades · ${data.totalReports} reportes · Ejecutado: ${money(data.totalExecuted)}`;
          destination = `/final-report.html?projectId=${project}`; action = 'Consultar reporte final'; break;
        default: continue;
      }
      const entry = document.createElement('article'); entry.className = 'proposal';
      const link = text('a', action); link.className = 'location-button'; link.href = destination;
      entry.append(text('h3', title), text('p', description));
      if (item.kind === 'ProjectReviewRequested') {
        const details = document.createElement('details'); details.append(text('summary', 'Comparar evaluaciones'));
        for (const [label, evaluations] of [['Originales', data.originalEvaluations], ['Actuales', data.currentEvaluations]]) {
          details.append(text('h4', label));
          const evaluationsList = document.createElement('ul');
          for (const evaluation of evaluations) evaluationsList.append(text('li',
            `${evaluation.evaluationId} · ${evaluation.location} · ${evaluation.assetType} · ${evaluation.damageLevel} · ${evaluation.status} · ${money(evaluation.budget)}`));
          if (!evaluations.length) evaluationsList.append(text('li', 'No hay evaluaciones actuales asociadas.'));
          details.append(evaluationsList);
        }
        entry.append(details);
      }
      if (item.kind === 'MonitoringPrepared') {
        const details = document.createElement('details'); details.append(text('summary', 'Cronograma preparado'));
        const schedule = document.createElement('ul');
        for (const activity of data.activities) schedule.append(text('li', `${activity.name} · ${activity.responsible} · ${new Date(activity.dueAt).toLocaleDateString('es-CO')} · ${money(activity.budget)}`));
        details.append(schedule); entry.append(details);
      }
      entry.append(link); list.append(entry);
    }
  }
  async function refresh() {
    if (!context || pending || document.visibilityState === 'hidden') return;
    const current = revision; pending = true;
    try {
      const query = new URLSearchParams(context);
      const response = await platformFetch('/api/reconstruccion/workflow?' + query);
      if (!response.ok) throw new Error('No se pudieron actualizar los pendientes y avisos.');
      const items = await response.json();
      if (current === revision) render(items);
    } catch (error) {
      if (current === revision) { state.textContent = error.message; state.dataset.error = 'true'; }
    } finally {
      pending = false;
      if (current !== revision) refresh();
    }
  }
  window.addEventListener('reconstruction-context', event => {
    revision++; context = event.detail; panel.hidden = !context; lastSnapshot = '';
    list.replaceChildren(); state.textContent = context ? 'Consultando pendientes y avisos…' : '';
    refresh();
  });
  // Los consumidores son asíncronos; actualizar sin volver a emitir comandos.
  let timer = window.setInterval(refresh, 5000);
  window.addEventListener('pagehide', () => window.clearInterval(timer));
  window.addEventListener('pageshow', () => { window.clearInterval(timer); timer = window.setInterval(refresh, 5000); refresh(); });
})();
