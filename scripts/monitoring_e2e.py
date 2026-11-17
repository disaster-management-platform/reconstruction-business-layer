#!/usr/bin/env python3
"""Verify activity reports and progress over HTTP with a temporary shared database.

Run after dotnet build. No application data or development ports are used.
"""
from datetime import datetime, timedelta, timezone
from pathlib import Path
import psycopg2
from contextlib import closing
from smoke_test import test_database, postgres_connection_string, migrate_database
import tempfile
import uuid

from smoke_test import free_port, request, start, stop, wait_ready


def main():
    processes = []
    with tempfile.TemporaryDirectory(prefix="monitoring-e2e-") as folder, test_database() as database:
        a, r, g = free_port(), free_port(), free_port()
        api_url, reconstruction_url, gateway_url = (f"http://127.0.0.1:{p}" for p in (a, r, g))
        common = {"ConnectionStrings__Disasters": postgres_connection_string(database)}
        reconstruction_env = {**common, "ASPNETCORE_ENVIRONMENT": "Development",
                              "Reconstruction__DemoExternalData": "true", "Messaging__Enabled": "false",
                              "ExternalApis__Disasters": api_url + "/"}
        with open(Path(folder) / "processes.log", "w+") as log:
            migrate_database(database, log)
            try:
                disasters = start("DisasterLocation", a, common, log); processes.append(disasters)
                reconstruction = start("Reconstruction.Api", r, reconstruction_env, log); processes.append(reconstruction)
                gateway = start("DisasterLocation.Gateway", g, {
                    "ReverseProxy__Clusters__disasters-api__Destinations__local__Address": api_url + "/",
                    "ReverseProxy__Clusters__reconstruction-api__Destinations__local__Address": reconstruction_url + "/",
                }, log); processes.append(gateway)
                wait_ready(api_url, "/api/desastres", disasters)
                wait_ready(reconstruction_url, "/api/reconstruccion/health", reconstruction)
                wait_ready(gateway_url, "/", gateway)

                def call(path, method="GET", body=None, expected=200):
                    if body is not None and path.endswith('/reportes') and method == 'POST': body = {**body, 'requestId': str(uuid.uuid4())}
                    status, _, data = request(gateway_url, path, method, body, {"X-Actor": "supervisor-e2e"})
                    assert status == expected, (status, expected, data)
                    return data

                disaster = call("/api/desastres", "POST", {"nombre": "Ensayo monitoreo", "ubicacion": "Bogotá",
                    "severidad": 4, "descripcion": "Datos temporales", "latitud": 4.71, "longitud": -74.07}, 201)
                for phase in ['Response', 'Recovery']:
                    disaster = call(f"/api/desastres/{disaster['id']}/fase", 'PUT', {'phase': phase, 'expectedVersion': disaster['version']})
                proposals = call("/api/reconstruccion/propuestas/generar", "POST", {"disasterId": disaster["id"]})
                project = call("/api/reconstruccion/proyectos", "POST", {"proposalId": proposals[0]["id"], "expectedVersion": proposals[0]["version"]})
                path = f"/api/reconstruccion/proyectos/{project['id']}"
                now = datetime.now(timezone.utc)
                project = call(path + "/formalizacion", "PUT", {"finalBudget": 100, "fundingSource": "DEMO: financiación simulada, no asignada", "fundingSourceId": "donations",
                    "responsible": "Responsable", "contractor": "Entidad", "expectedVersion": project["version"],
                    "activities": [{"name": name, "responsible": "Equipo", "startsAt": now.isoformat(),
                        "dueAt": (now + timedelta(days=7)).isoformat(), "budget": budget}
                        for name, budget in [("Reparar", 60), ("Inspeccionar", 40)]]})
                assert all(activity["status"] == "Pending" for activity in project["activities"])
                project = call(path + "/contrato-firmado", "POST", {"contractReference": "contrato://e2e",
                    "signedAt": now.isoformat(), "expectedVersion": project["version"]})
                monitoring = call(path + "/monitoreo")
                assert monitoring["summary"]["progressPercentage"] == 0
                activity = next(a for a in project["activities"] if a["name"] == "Reparar")
                report_path = path + f"/actividades/{activity['id']}/reportes"
                payload = {"status": "InProgress", "actualCost": 20, "evidence": ["foto://avance"],
                           "documents": ["acta://avance"], "expectedVersion": project["version"]}
                for field in payload:
                    incomplete = {key: value for key, value in payload.items() if key != field}
                    call(report_path, "POST", incomplete, 400)
                for change in [{"evidence": []}, {"documents": []}, {"actualCost": -1}, {"actualCost": 61}, {"status": 99}]:
                    call(report_path, "POST", {**payload, **change}, 422)
                call(path + f"/actividades/{uuid.uuid4()}/reportes", "POST", payload, 422)

                project = call(report_path, "POST", {**payload, "status": "Pending", "actualCost": 0})
                pending_version = project["version"]
                project = call(report_path, "POST", {**payload, "expectedVersion": project["version"]})
                call(report_path, "POST", {**payload, "expectedVersion": pending_version}, 409)
                call(report_path, "POST", {**payload, "status": "Pending", "expectedVersion": project["version"]}, 422)
                call(report_path, "POST", {**payload, "actualCost": 41, "expectedVersion": project["version"]}, 422)
                project = call(report_path, "POST", {**payload, "status": "Completed", "actualCost": 40, "expectedVersion": project["version"]})
                monitoring = call(path + "/monitoreo")
                assert monitoring["summary"]["progressPercentage"] == 50
                assert monitoring["summary"]["totalCost"] == 60
                other = next(a for a in project["activities"] if a["name"] == "Inspeccionar")
                project = call(path + f"/actividades/{other['id']}/reportes", "POST", {**payload,
                    "status": "Completed", "actualCost": 35, "expectedVersion": project["version"]})
                call(report_path, "POST", {**payload, "status": "Completed", "actualCost": 0, "expectedVersion": project["version"]}, 422)

                stop(reconstruction)
                reconstruction = start("Reconstruction.Api", r, reconstruction_env, log); processes.append(reconstruction)
                wait_ready(reconstruction_url, "/api/reconstruccion/health", reconstruction)
                monitoring = call(path + "/monitoreo")
                summary = monitoring["summary"]
                assert (summary["progressPercentage"], summary["reportsCount"], summary["totalCost"], summary["remainingBudget"]) == (100, 4, 95, 5)
                assert monitoring["project"]["status"] == "Monitoring"
                assert monitoring["project"]["finalBudget"] == 100 and monitoring["project"]["isBudgetLocked"]
                reports = next(a for a in monitoring["project"]["activities"] if a["id"] == activity["id"])["reports"]
                assert [r["actualCost"] for r in reports] == [0, 20, 40]
                assert all(r["evidence"] == ["foto://avance"] and r["documents"] == ["acta://avance"] and r["actor"] == "local-demonstration" for r in reports)
                with closing(psycopg2.connect(database)) as connection, connection.cursor() as db:
                    db.execute('SELECT COUNT(*) FROM "ReconstructionReports"')
                    assert db.fetchone()[0] == 4
                    db.execute('SELECT COUNT(*) FROM "ReconstructionAudit"')
                    assert db.fetchone()[0] == 8
                    db.execute('SELECT COUNT(*) FROM "OutboxMessages"')
                    assert db.fetchone()[0] == 8
                    db.execute('SELECT (SELECT COUNT(*) FROM reconstruction."ReconstructionReports" r LEFT JOIN reconstruction."ReconstructionActivities" a ON r."ActivityId"=a."Id" WHERE a."Id" IS NULL) + (SELECT COUNT(*) FROM reconstruction."ReconstructionActivities" a LEFT JOIN reconstruction."ReconstructionProjects" p ON a."ProjectId"=p."Id" WHERE p."Id" IS NULL)')
                    assert db.fetchone()[0] == 0
                for asset in ["/monitoring.html", "/monitoring.js", "/monitoring.css"]:
                    call(asset)
                print("OK: reportes completos, estados, costos acumulados, trazabilidad, concurrencia HTTP y persistencia tras reinicio.")
            except BaseException:
                log.flush(); log.seek(0); print(log.read()[-15000:]); raise
            finally:
                for process in reversed(processes):
                    stop(process)


if __name__ == "__main__":
    main()
