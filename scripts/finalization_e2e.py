#!/usr/bin/env python3
"""Verify finalization and the complete final report using temporary processes/data."""
from datetime import datetime, timedelta, timezone
from pathlib import Path
import sqlite3
import tempfile
import uuid

from smoke_test import free_port, request, start, stop, wait_ready


def main():
    processes = []
    with tempfile.TemporaryDirectory(prefix="finalization-e2e-") as folder:
        database = Path(folder) / "disasters.db"
        a, r, g = free_port(), free_port(), free_port()
        api_url, reconstruction_url, gateway_url = (f"http://127.0.0.1:{p}" for p in (a, r, g))
        common = {"ConnectionStrings__Disasters": f"Data Source={database}"}
        reconstruction_env = {**common, "Reconstruction__DemoExternalData": "true",
                              "Messaging__Enabled": "false", "ExternalApis__Disasters": api_url + "/"}
        with open(Path(folder) / "processes.log", "w+") as log:
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
                    status, _, data = request(gateway_url, path, method, body, {"X-Actor": "responsable-e2e"})
                    assert status == expected, (status, expected, data)
                    return data

                disaster = call("/api/desastres", "POST", {"nombre": "Ensayo finalización", "ubicacion": "Bogotá",
                    "severidad": 4, "descripcion": "Datos temporales", "latitud": 4.71, "longitud": -74.07}, 201)
                for phase in ['Response', 'Recovery']:
                    disaster = call(f"/api/desastres/{disaster['id']}/fase", 'PUT', {'phase': phase, 'expectedVersion': disaster['version']})
                proposals = call("/api/reconstruccion/propuestas/generar", "POST", {"disasterId": disaster["id"]})
                now = datetime.now(timezone.utc)

                def prepare(proposal, completed=False):
                    refreshed = call('/api/reconstruccion/propuestas/generar', 'POST', {'disasterId': disaster['id']})
                    proposal = next(p for p in refreshed if p['id'] == proposal['id'])
                    project = call("/api/reconstruccion/proyectos", "POST", {"proposalId": proposal["id"], "expectedVersion": proposal["version"]})
                    path = f"/api/reconstruccion/proyectos/{project['id']}"
                    activities = [{"name": name, "responsible": "Equipo", "startsAt": now.isoformat(),
                        "dueAt": (now + timedelta(days=7)).isoformat(), "budget": budget}
                        for name, budget in [("Reparar", 60), ("Inspeccionar", 40)]]
                    project = call(path + "/formalizacion", "PUT", {"finalBudget": 100, "fundingSource": "DEMO: financiación simulada, no asignada", "fundingSourceId": "donations",
                        "responsible": "Responsable", "contractor": "Entidad", "expectedVersion": project["version"], "activities": activities})
                    project = call(path + "/contrato-firmado", "POST", {"contractReference": "contrato://e2e",
                        "signedAt": now.isoformat(), "expectedVersion": project["version"]})
                    if completed:
                        # Restore legacy rows for the historical no-report scenario.
                        # New activities can only be created Pending through the API.
                        with sqlite3.connect(database) as db:
                            db.execute("UPDATE ReconstructionActivities SET Status=2 WHERE ProjectId=?", (project["id"].upper(),))
                    return path, project

                path, project = prepare(proposals[0])
                call(path + "/reporte-final", expected=409)
                call(path + "/cerrar", "POST", {"expectedVersion": project["version"]}, 422)
                assert not call(path + "/monitoreo")["summary"]["canFinalize"]
                activity = next(a for a in project["activities"] if a["name"] == "Reparar")
                report_path = path + f"/actividades/{activity['id']}/reportes"
                for index, (state, cost) in enumerate([("Pending", 0), ("InProgress", 10), ("InProgress", 15), ("Completed", 30)]):
                    project = call(report_path, "POST", {"status": state, "actualCost": cost,
                        "evidence": [f"foto://{index}"], "documents": [f"acta://{index}"], "expectedVersion": project["version"]})
                assert not call(path + "/monitoreo")["summary"]["canFinalize"]
                other = next(a for a in project["activities"] if a["name"] == "Inspeccionar")
                stale_version = project["version"]
                project = call(path + f"/actividades/{other['id']}/reportes", "POST", {"status": "Completed", "actualCost": 35,
                    "evidence": ["foto://inspección"], "documents": ["acta://inspección"], "expectedVersion": project["version"]})
                assert call(path + "/monitoreo")["summary"]["canFinalize"]
                call(path + "/cerrar", "POST", {"expectedVersion": stale_version}, 409)
                project = call(path + "/cerrar", "POST", {"expectedVersion": project["version"]})
                assert project["status"] == "Closed" and project["closedAt"] and project["isBudgetLocked"]
                final = call(path + "/reporte-final")
                assert (final["totalActivities"], final["totalReports"], final["totalExecuted"], final["remainingBudget"]) == (2, 5, 90, 10)
                assert final["finalBudget"] == 100
                consolidated = next(a for a in final["activities"] if a["name"] == "Reparar")["reports"]
                assert [r["actualCost"] for r in consolidated] == [0, 10, 15, 30]
                assert [r["status"] for r in consolidated] == ["Pending", "InProgress", "InProgress", "Completed"]
                assert all(r["actor"] == "local-demonstration" and r["evidence"] == [f"foto://{i}"] and r["documents"] == [f"acta://{i}"] for i, r in enumerate(consolidated))
                call(report_path, "POST", {"status": "Completed", "actualCost": 0, "evidence": ["otra"], "documents": ["otra"], "expectedVersion": project["version"]}, 422)
                assert call(path + "/cerrar", "POST", {"expectedVersion": project["version"]})["closedAt"] == project["closedAt"]
                assert final == call(path + "/reporte-final")
                stop(reconstruction)
                reconstruction = start("Reconstruction.Api", r, reconstruction_env, log); processes.append(reconstruction)
                wait_ready(reconstruction_url, "/api/reconstruccion/health", reconstruction)
                assert final == call(path + "/reporte-final")

                # Legacy Completed states without final reports must not allow closure.
                other_path, other_project = prepare(proposals[1], completed=True)
                assert not call(other_path + "/monitoreo")["summary"]["canFinalize"]
                call(other_path + "/cerrar", "POST", {"expectedVersion": other_project["version"]}, 422)
                call(other_path + "/reporte-final", expected=409)
                unchanged = call(other_path)
                assert unchanged["status"] == "Monitoring" and unchanged["closedAt"] is None
                assert unchanged["version"] == other_project["version"]
                with sqlite3.connect(database) as db:
                    assert db.execute("SELECT COUNT(*) FROM ReconstructionReports").fetchone()[0] == 5
                    assert db.execute("SELECT COUNT(*) FROM ReconstructionAudit").fetchone()[0] == 14
                    assert db.execute("SELECT COUNT(*) FROM OutboxMessages").fetchone()[0] == 14
                    assert db.execute("SELECT COUNT(*) FROM ReconstructionAudit WHERE Action='reconstruction.project.closed'").fetchone()[0] == 1
                for asset in ["/final-report.html", "/final-report.js", "/final-report.css"]:
                    call(asset)
                print("OK: finalización por estados, reporte completo, totales, reintentos y conservación tras reinicio.")
            except BaseException:
                log.flush(); log.seek(0); print(log.read()[-15000:]); raise
            finally:
                for process in reversed(processes): stop(process)


if __name__ == "__main__":
    main()
