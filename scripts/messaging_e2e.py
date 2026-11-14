#!/usr/bin/env python3
"""Real RabbitMQ/SQLite integration test. Requires the local compose broker.

Creates and removes its own temporary vhost, processes and DB; preserves default
queues, messages and application data. Run after dotnet build.
"""
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import sqlite3
import tempfile
import time
from urllib.parse import quote
import uuid

from rabbitmq_setup import RabbitManagement
from smoke_test import free_port, request, start, stop, wait_ready


def wait_for(test, message):
    deadline = time.monotonic() + 40
    while time.monotonic() < deadline:
        result = test()
        if result: return result
        time.sleep(.15)
    raise AssertionError(message)


def main():
    broker = RabbitManagement()
    vhost = "reconstruction-test-" + uuid.uuid4().hex
    vh = quote(vhost, safe="")
    broker.call("/vhosts/" + vh, "PUT", {})
    processes = []
    try:
        broker.call(f"/permissions/{vh}/guest", "PUT", {"configure": ".*", "write": ".*", "read": ".*"})
        with tempfile.TemporaryDirectory(prefix="messaging-e2e-") as folder:
            database = Path(folder) / "disasters.db"
            a, r = free_port(), free_port()
            api_url, reconstruction_url = f"http://127.0.0.1:{a}", f"http://127.0.0.1:{r}"
            env = {"ConnectionStrings__Disasters": f"Data Source={database}", "ASPNETCORE_ENVIRONMENT": "Development",
                   "Reconstruction__DemoExternalData": "true", "ExternalApis__Disasters": api_url + "/",
                   "Messaging__VirtualHost": vhost, "Messaging__RetryDelayMilliseconds": "100"}
            # Enabled is intentionally read from the real Development configuration.
            with open(Path(folder) / "processes.log", "w+") as log:
                try:
                    disasters = start("DisasterLocation", a, env, log); processes.append(disasters)
                    api = start("Reconstruction.Api", r, env, log); processes.append(api)
                    wait_ready(api_url, "/api/desastres", disasters)
                    wait_ready(reconstruction_url, "/api/reconstruccion/health", api)

                    def sql(query, args=()):
                        with sqlite3.connect(database) as db: return db.execute(query, args).fetchall()

                    def call(path, method="GET", body=None, base=reconstruction_url, expected=200):
                        if body is not None and path.endswith('/reportes') and method == 'POST':
                            body = {**body, 'requestId': str(uuid.uuid4())}
                        status, _, data = request(base, path, method, body)
                        assert status == expected, (status, expected, data)
                        return data

                    def publish(event_type, payload, message_id=None):
                        result = broker.call(f"/exchanges/{vh}/reconstruction.events/publish", "POST", {
                            "routing_key": event_type, "properties": {"delivery_mode": 2, "type": event_type,
                                "message_id": message_id or str(uuid.uuid4())}, "payload": payload, "payload_encoding": "string"})
                        assert result["routed"]

                    def inbox(event_id, count):
                        return sql("SELECT COUNT(*) FROM InboxMessages WHERE Id=?", (event_id,))[0][0] == count

                    def take_dead_letter(queue):
                        # Management queue counters are sampled; poll the actual
                        # delivery instead of trusting potentially stale counts.
                        messages = wait_for(lambda: broker.call(f"/queues/{vh}/{queue}.dlq/get", "POST", {
                            "count": 1, "ackmode": "ack_requeue_false", "encoding": "auto"}), "Falta mensaje en DLQ")
                        return messages[0]

                    def replay(event_type):
                        event_id, payload = sql("SELECT Id, PayloadJson FROM OutboxMessages WHERE EventType=?", (event_type,))[0]
                        publish(event_type, payload, event_id); publish(event_type, payload, event_id)
                        return event_id

                    disaster = call("/api/desastres", "POST", {"nombre": "Ensayo MOM", "ubicacion": "Bogotá", "severidad": 4,
                        "descripcion": "Datos temporales", "latitud": 4.71, "longitud": -74.07}, base=api_url, expected=201)
                    for phase in ['Response', 'Recovery']:
                        disaster = call(f"/api/desastres/{disaster['id']}/fase", 'PUT', {'phase': phase, 'expectedVersion': disaster['version']}, base=api_url)
                    proposals = call("/api/reconstruccion/propuestas/generar", "POST", {"disasterId": disaster["id"]})
                    generated = sql("SELECT Id FROM OutboxMessages WHERE EventType='reconstruction.proposals.generated'")[0][0]
                    wait_for(lambda: inbox(generated, 2), "Formalización y Timeline no recibieron el evento")
                    reviews = call(f"/api/reconstruccion/workflow?disasterId={disaster['id']}")
                    assert len(reviews) == 4 and all(item["kind"] == "ProposalReview" for item in reviews)
                    generated_data = json.loads(sql("SELECT PayloadJson FROM OutboxMessages WHERE Id=?", (generated,))[0][0])["Data"]
                    assert len(generated_data["proposals"]) == 4
                    assert sql("SELECT COUNT(*) FROM ReconstructionProjects")[0][0] == 0
                    replay("reconstruction.proposals.generated")
                    time.sleep(.5)
                    assert inbox(generated, 2)

                    project = call("/api/reconstruccion/proyectos", "POST", {"proposalId": proposals[0]["id"], "expectedVersion": proposals[0]["version"]})
                    path = f"/api/reconstruccion/proyectos/{project['id']}"
                    workflow_path = f"/api/reconstruccion/workflow?projectId={project['id']}"
                    selected = sql("SELECT Id FROM OutboxMessages WHERE EventType='reconstruction.proposals.selected'")[0][0]
                    wait_for(lambda: inbox(selected, 2), "No se preparó la solicitud de formalización")
                    pending_formalization = call(workflow_path)
                    assert len(pending_formalization) == 1 and pending_formalization[0]["kind"] == "FormalizationRequest"
                    assert pending_formalization[0]["data"]["proposalId"] == proposals[0]["id"]
                    now = datetime.now(timezone.utc)
                    project = call(path + "/formalizacion", "PUT", {"finalBudget": 100, "fundingSource": "DEMO: financiación simulada, no asignada", "fundingSourceId": "donations",
                        "responsible": "Responsable", "contractor": "Entidad", "expectedVersion": project["version"],
                        "activities": [{"name": "Reparar", "responsible": "Equipo", "startsAt": now.isoformat(),
                                        "dueAt": (now + timedelta(days=5)).isoformat(), "budget": 100, "status": "Pending"}]})
                    assert project["status"] == "Draft"
                    project = call(path + "/contrato-firmado", "POST", {"contractReference": "contrato://prueba",
                        "signedAt": now.isoformat(), "expectedVersion": project["version"]})
                    formalized = sql("SELECT Id FROM OutboxMessages WHERE EventType='reconstruction.project.formalized'")[0][0]
                    wait_for(lambda: inbox(formalized, 2), "Monitoreo no recibió la formalización")
                    prepared = call(workflow_path)
                    assert len(prepared) == 1 and prepared[0]["kind"] == "MonitoringPrepared"
                    assert prepared[0]["data"]["activities"][0]["activityId"] == project["activities"][0]["id"]
                    assert call(path)["version"] == project["version"] and sql("SELECT COUNT(*) FROM ReconstructionReports")[0][0] == 0
                    replay("reconstruction.project.formalized")
                    activity = project["activities"][0]
                    project = call(path + f"/actividades/{activity['id']}/reportes", "POST", {"status": "Completed", "actualCost": 90,
                        "evidence": ["foto://1"], "documents": ["acta://1"], "expectedVersion": project["version"]})
                    completed = sql("SELECT Id FROM OutboxMessages WHERE EventType='reconstruction.activity.completed'")[0][0]
                    wait_for(lambda: inbox(completed, 2), "Cierre no recibió ActivityCompleted")
                    available_closure = call(workflow_path)
                    assert any(item["kind"] == "ClosureAvailable" and item["data"]["resolvedActivities"] == 1 for item in available_closure)
                    completed_data = json.loads(sql("SELECT PayloadJson FROM OutboxMessages WHERE Id=?", (completed,))[0][0])["Data"]
                    assert completed_data["activityId"] == activity["id"] and completed_data["actualCost"] == 90
                    assert completed_data["reportId"] == project["activities"][0]["reports"][0]["id"]
                    assert call(path)["status"] == "Monitoring" and not sql("SELECT Id FROM OutboxMessages WHERE EventType='reconstruction.project.closed'")
                    replay("reconstruction.activity.completed")
                    project = call(path + "/cerrar", "POST", {"expectedVersion": project["version"]})
                    wait_for(lambda: sql("SELECT COUNT(*) FROM OutboxMessages WHERE SentAt IS NULL")[0][0] == 0
                             and sql("SELECT COUNT(*) FROM InboxMessages")[0][0] == 11, "Publicación/consumo incompleto")
                    closed_notice = call(workflow_path)
                    assert len(closed_notice) == 1 and closed_notice[0]["kind"] == "ProjectClosed"
                    assert sql("SELECT COUNT(*) FROM ReconstructionTimeline")[0][0] == 6
                    integration = broker.call(f"/queues/{vh}/reconstruction.integrations/get", "POST", {"count": 100, "ackmode": "ack_requeue_false", "encoding": "auto"})
                    integration_event = next(json.loads(item["payload"]) for item in integration if json.loads(item["payload"])["Type"] == "reconstruction.project.closed")
                    assert integration_event["Type"] == "reconstruction.project.closed"
                    assert integration_event["Data"]["projectId"] == project["id"] and integration_event["Data"]["totalExecuted"] == 90

                    # Real Outbox redelivery, with the same EventId, must not duplicate effects.
                    with sqlite3.connect(database) as db: db.execute("UPDATE OutboxMessages SET SentAt=NULL WHERE Id=?", (generated,))
                    wait_for(lambda: sql("SELECT Attempts, SentAt FROM OutboxMessages WHERE Id=?", (generated,))[0][0] >= 2
                             and sql("SELECT SentAt FROM OutboxMessages WHERE Id=?", (generated,))[0][0] is not None, "Outbox no reintentó")
                    assert inbox(generated, 2)
                    assert len(call(workflow_path)) == 1  # Reentregar generación no reabre tareas resueltas.

                    # Rebuild the read model after losing only its projection table contents.
                    # Inbox and published business events must remain unchanged.
                    stop(api)
                    with sqlite3.connect(database) as db: db.execute("DELETE FROM ReconstructionWorkflow")
                    api = start("Reconstruction.Api", r, env, log); processes.append(api)
                    wait_ready(reconstruction_url, "/api/reconstruccion/health", api)
                    rebuilt = call(workflow_path)
                    assert len(rebuilt) == 1 and rebuilt[0]["kind"] == "ProjectClosed"
                    assert sql("SELECT COUNT(*) FROM InboxMessages")[0][0] == 11
                    assert sql("SELECT COUNT(*) FROM OutboxMessages")[0][0] == 6

                    # Simulate an unreachable broker for this API only; the shared broker stays running.
                    stop(api)
                    api = start("Reconstruction.Api", r, {**env, "Messaging__Port": str(free_port())}, log); processes.append(api)
                    wait_ready(reconstruction_url, "/api/reconstruccion/health", api)
                    other = call("/api/desastres", "POST", {"nombre": "MOM temporalmente inaccesible", "ubicacion": "Bogotá", "severidad": 3,
                        "descripcion": "Prueba Outbox", "latitud": 4.71, "longitud": -74.07}, base=api_url, expected=201)
                    for phase in ['Response', 'Recovery']:
                        other = call(f"/api/desastres/{other['id']}/fase", 'PUT', {'phase': phase, 'expectedVersion': other['version']}, base=api_url)
                    call("/api/reconstruccion/propuestas/generar", "POST", {"disasterId": other["id"]})
                    pending = sql("SELECT Id FROM OutboxMessages WHERE SentAt IS NULL")[0][0]
                    wait_for(lambda: sql("SELECT Attempts FROM OutboxMessages WHERE Id=?", (pending,))[0][0] > 0, "No se registró el fallo de publicación")
                    assert sql("SELECT SentAt FROM OutboxMessages WHERE Id=?", (pending,))[0][0] is None
                    stop(api)
                    api = start("Reconstruction.Api", r, env, log); processes.append(api)
                    wait_ready(reconstruction_url, "/api/reconstruccion/health", api)
                    wait_for(lambda: inbox(pending, 2), "No se recuperó el evento pendiente")
                    assert sql("SELECT COUNT(*) FROM ReconstructionProjects")[0][0] == 1

                    # Poison payload goes to each interested consumer's own DLQ after 3 attempts.
                    publish("reconstruction.project.formalized", "{invalid-json}")
                    for queue in ["reconstruction.monitoring", "reconstruction.timeline"]:
                        failed = take_dead_letter(queue)
                        assert failed["payload"] == "{invalid-json}"
                        assert failed["properties"]["headers"]["x-processing-attempts"] == 3
                        assert failed["properties"]["headers"]["x-consumer-name"] == queue
                    assert sql("SELECT COUNT(*) FROM InboxMessages")[0][0] == 13
                    assert sql("SELECT COUNT(*) FROM ReconstructionTimeline")[0][0] == 7
                    # One consumer can fail while Timeline successfully processes the same EventId.
                    missing_id = str(uuid.uuid4()).upper()
                    missing = {"Id": missing_id, "Type": "reconstruction.project.formalized", "DisasterId": disaster["id"],
                               "ProjectId": str(uuid.uuid4()), "Actor": "test", "Detail": "Proyecto inexistente",
                               "OccurredAt": datetime.now(timezone.utc).isoformat()}
                    publish(missing["Type"], json.dumps(missing), missing_id)
                    wait_for(lambda: inbox(missing_id, 1), "Timeline no procesó el evento independientemente")
                    failed = take_dead_letter("reconstruction.monitoring")
                    assert json.loads(failed["payload"])["Id"] == missing_id
                    assert sql("SELECT ConsumerName FROM InboxMessages WHERE Id=?", (missing_id,)) == [("reconstruction.timeline",)]
                    assert call(path)["status"] == "Closed"
                    print("OK: RabbitMQ real, eventos tipados, 5 efectos visibles vía API, Inbox/Outbox, duplicados, recuperación, DLQ y decisiones del usuario preservadas.")
                except BaseException:
                    log.flush(); log.seek(0); print(log.read()[-20000:]); raise
                finally:
                    for process in reversed(processes): stop(process)
    finally:
        broker.call("/vhosts/" + vh, "DELETE")


if __name__ == "__main__": main()
