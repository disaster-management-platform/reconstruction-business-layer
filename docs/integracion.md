# Contratos de integración v1

## Evaluación de daños

`ExternalApis__DamageAssessments__BaseUrl`; token de servicio opcional en `ExternalApis__DamageAssessments__BearerToken`.

`GET /api/evaluaciones?disasterId=42`

```json
{"schemaVersion":1,"disasterId":42,"evaluations":[{"evaluationId":"11111111-1111-1111-1111-111111111111","disasterId":42,"evaluatedAt":"2026-11-13T12:00:00Z","evaluator":"Evaluador","organization":"Organismo","location":"Zona Norte","assetType":"Puente","damageLevel":"Destroyed","description":"Daño estructural","evidence":["https://evaluaciones/evidencia/1"],"status":"Validated","assetId":"puente-1","revision":1}]}
```

`assetId` es estable y único dentro del desastre; `revision` aumenta con cada modificación. La fuente entrega una vista completa y estable de las evaluaciones actuales del desastre; una respuesta vacía significa que no quedan evaluaciones elegibles, no un fallo temporal. Los fallos deben responder con error HTTP. Estados: Draft, Validated, Rejected. El presupuesto legado de evaluación es informativo y no interviene en el reparto de donaciones.

## Donaciones

`ExternalApis__Funding__BaseUrl`; token de servicio opcional `ExternalApis__Funding__BearerToken`.

`GET /api/donaciones/asignaciones/reconstruccion/42`

```json
{"schemaVersion":1,"disasterId":42,"availableAmount":150000000,"source":"Donaciones para reconstrucción","sourceId":"allocation-42","currency":"COP","asOf":"2026-11-14T15:00:00Z"}
```

`availableAmount` es el total monetario asignado EXCLUSIVAMENTE a reconstrucción, antes de sus compromisos locales; no es el saldo global de donaciones. El módulo financiero debe impedir que otro consumidor gaste esta misma asignación, conservar sourceId y coordinar cualquier reducción por debajo de lo comprometido. Reconstrucción no puede garantizar exclusión entre módulos sobre un saldo global consultado por HTTP. Si el subsistema futuro no puede ofrecer asignación exclusiva, será necesario implementar un protocolo de reserva/confirmación distribuido; no se simula esa garantía aquí.

Se exige COP y una observación de hasta cinco minutos de antigüedad. Los presupuestos son decimal con dos posiciones. La fuente seleccionada se valida por sourceId y nombre. `GET /api/reconstruccion/financiacion?disasterId=42` expone asignación, compromisos y saldo.

## Desastres y Android

Se conservan GET/POST de `/api/desastres`, Idempotency-Key y semántica 201/200/409. Radio es opcional y se conserva. GET añade phase y version; los clientes antiguos ignoran campos nuevos.

`PUT /api/desastres/42/fase` recibe `phase`, `expectedVersion` y, al omitir etapas, `reason`; exige permiso de escritura y acceso al desastre. Cerrar requiere además `confirmNoCriticalCommitments=true`. La transición se audita atómicamente. Recovery y Reconstruction habilitan nuevas propuestas y proyectos; Closed conserva la consulta y finalización de proyectos históricos. Una respuesta sin fase válida no habilita reconstrucción.

## Comandos de reconstrucción

Todos requieren JWT o demostración explícita. POST `/propuestas/generar` recibe disasterId. POST `/proyectos` recibe proposalId y expectedVersion. PUT `/proyectos/{id}/formalizacion` incluye finalBudget, fundingSource, fundingSourceId, responsible, contractor, activities y expectedVersion. Actividades: name, responsible, startsAt, dueAt y budget.

POST `/proyectos/{id}/confirmar-formalizacion` recibe expectedVersion. PUT `/proyectos/{id}/presupuesto` recibe budget, reason, expectedVersion y activityBudgets, un objeto cuyos nombres son UUID de actividad y valores son montos. El contrato firmado puede registrarse por el endpoint histórico opcional antes de iniciar monitoreo.

POST `/proyectos/{id}/adjuntos`: multipart con file y category (evidence/document). Devuelve UUID y hash. Descarga autorizada: GET `/proyectos/{id}/adjuntos/{attachmentId}`. El reporte usa referencias `attachment:UUID`.

POST `/proyectos/{id}/actividades/{activityId}/reportes`: requestId UUID, status, actualCost, evidence[], documents[], expectedVersion. Los reintentos conservan requestId, costo y referencias. POST `/proyectos/{id}/cerrar`: expectedVersion. Consultas: proyecto, monitoreo, auditoria, linea-tiempo y reporte-final.

400: contrato incompleto; 401: identidad inválida; 403: permisos insuficientes; 404: recurso ausente/no visible; 409: versión o clave en conflicto; 422: regla de negocio; 503: dependencia no disponible.

## Mensajería

Topic exchange reconstruction.events. Integraciones recibe reconstruction.#; cada subsistema debe tener su propia cola y deduplicar por EventEnvelope.Id. Los eventos incluyen Type, DisasterId, ProjectId, Actor, OccurredAt, SchemaVersion y Data tipado. No se deben extraer reglas de Detail.

Cambios relevantes: reconstruction.proposals.generated, reconstruction.proposals.selected, reconstruction.formalization.saved, reconstruction.project.formalized, reconstruction.project.budget-revised, reconstruction.activity.progress, reconstruction.activity.completed, reconstruction.project.closed.

Outbox y auditoría se guardan con el agregado. El publicador espera confirmación; cada consumidor confirma Inbox y proyección antes del ACK. La entrega es al menos una vez. Las colas de error tienen reintentos limitados. El consumidor financiero futuro puede usar los eventos para seguimiento; no debe efectuar un segundo débito sobre la asignación exclusiva por cada reentrega.
