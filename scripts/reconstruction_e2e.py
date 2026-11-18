#!/usr/bin/env python3
"""Smoke test independiente de dependencias Python. Requiere ambos backends y Gateway."""
import json
import os
import uuid
import urllib.request
from datetime import datetime, timedelta, timezone

BASE = os.environ.get("PLATFORM_BASE_URL", "http://localhost:5080")

def call(method, path, body=None, idempotency_key=None):
    if method == "PUT" and path.endswith("/fase") and isinstance(body, dict):
        body = {"reason": "Transición autorizada del escenario de reconstrucción", "confirmNoCriticalCommitments": True, **body}
    if body is not None and path.endswith('/reportes') and method == 'POST': body = {**body, 'requestId': str(uuid.uuid4())}
    headers = {"Content-Type":"application/json", "X-Actor":"e2e-test"}
    if idempotency_key: headers["Idempotency-Key"] = idempotency_key
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(BASE + path, method=method, headers=headers, data=data)
    with urllib.request.urlopen(req, timeout=10) as response:
        raw = response.read()
        return json.loads(raw) if raw else None

name = "Ensayo E2E " + str(uuid.uuid4())[:8]
disaster = call("POST", "/api/desastres", {
    "nombre":name, "ubicacion":"Bogotá", "severidad":4,
    "descripcion":"Evaluación controlada", "latitud":4.711, "longitud":-74.0721,
}, str(uuid.uuid4()))
for phase in ['Response', 'Recovery']:
    disaster = call('PUT', f"/api/desastres/{disaster['id']}/fase", {'phase': phase, 'expectedVersion': disaster['version']})
print("Desastre:", disaster["id"])

proposals = call("POST", "/api/reconstruccion/propuestas/generar", {"disasterId":disaster["id"]})
assert len(proposals) > 0
proposal = proposals[0]
print("Propuesta:", proposal["id"], proposal["priority"])

project = call("POST", "/api/reconstruccion/proyectos", {"proposalId":proposal["id"], "expectedVersion":proposal["version"]})
assert project["status"] == "Draft"
print("Proyecto:", project["id"])

now = datetime.now(timezone.utc)
project = call("PUT", f"/api/reconstruccion/proyectos/{project['id']}/formalizacion", {
    "finalBudget":1000000, "fundingSource":"DEMO: financiación simulada, no asignada", "fundingSourceId": "donations",
    "responsible":"Autoridad", "contractor":"Entidad contratista",
    "expectedVersion":project["version"],
    "activities":[{
        "name":"Evaluar y reparar", "responsible":"Equipo técnico",
        "startsAt":now.isoformat(), "dueAt":(now + timedelta(days=30)).isoformat(),
        "budget": 1000000,
        "status": "Completed"  # Forged creation input must not set the activity state.
    }]
})
assert project["status"] == "Draft"
assert project["activities"][0]["status"] == "Pending"
assert not project["isBudgetLocked"]
project = call("POST", f"/api/reconstruccion/proyectos/{project['id']}/contrato-firmado", {
    "contractReference":"contrato://e2e-firmado", "signedAt":now.isoformat(),
    "expectedVersion":project["version"]
})
assert project["status"] == "Monitoring" and project["isBudgetLocked"]
activity = project["activities"][0]
project = call("POST", f"/api/reconstruccion/proyectos/{project['id']}/actividades/{activity['id']}/reportes", {
    "status":"Completed", "actualCost":900000,
    "evidence":["foto://evidencia1"], "documents":["acta://cierre1"],
    "expectedVersion":project["version"]
})
project = call("POST", f"/api/reconstruccion/proyectos/{project['id']}/cerrar", {
    "expectedVersion":project["version"]
})
assert project["status"] == "Closed"
summary = call("GET", f"/api/reconstruccion/proyectos/{project['id']}/reporte-final")
assert summary["totalExecuted"] == 900000
print("Cerrado y comprobado:", summary["projectId"])
print("OK: flujo E2E completado")
