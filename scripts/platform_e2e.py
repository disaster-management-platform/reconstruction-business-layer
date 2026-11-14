#!/usr/bin/env python3
"""Real HTTP integration with isolated DB, fake external modules and real attachments."""
from datetime import datetime, timedelta, timezone
from decimal import Decimal
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
from pathlib import Path
import sqlite3
import tempfile
from threading import Thread
from urllib.request import Request, urlopen
import uuid

from smoke_test import free_port, request, start, stop, wait_ready


class ExternalModules(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path.startswith('/api/evaluaciones?disasterId='):
            disaster = int(self.path.split('=')[-1])
            evaluations = []
            for i, (zone, asset, level) in enumerate([
                ('Norte', 'Vivienda', 'Minor'), ('Norte', 'Vivienda', 'Destroyed'),
                ('Sur', 'Puente', 'Destroyed'), ('Este', 'Escuela', 'Minor')]):
                evaluations.append({'evaluationId': str(uuid.UUID(int=i + 1)), 'disasterId': disaster,
                    'evaluatedAt': datetime.now(timezone.utc).replace(microsecond=0).isoformat(),
                    'evaluator': 'Evaluador', 'organization': 'Organización', 'location': zone,
                    'assetType': asset, 'damageLevel': level, 'description': 'Daño validado',
                    'evidence': ['https://assessments.test/evidence'], 'status': 'Validated',
                    'assetId': f'asset-{i}', 'revision': 1})
            # Snapshots must be stable across repeated reads of the same revision.
            for evaluation in evaluations: evaluation['evaluatedAt'] = '2026-11-13T12:00:00Z'
            body = {'schemaVersion': 1, 'disasterId': disaster, 'evaluations': evaluations}
        elif self.path.startswith('/api/donaciones/asignaciones/reconstruccion/'):
            body = {'schemaVersion': 1, 'disasterId': int(self.path.split('/')[-1]), 'availableAmount': 1000.01,
                'source': 'Donaciones asignadas', 'sourceId': 'allocation-1', 'currency': 'COP',
                'asOf': datetime.now(timezone.utc).isoformat()}
        else:
            self.send_error(404); return
        raw = json.dumps(body).encode()
        self.send_response(200); self.send_header('Content-Type', 'application/json'); self.end_headers(); self.wfile.write(raw)
    def log_message(self, *_): pass


def main():
    processes = []
    modules = ThreadingHTTPServer(('127.0.0.1', 0), ExternalModules)
    worker = Thread(target=modules.serve_forever, daemon=True); worker.start()
    try:
        with tempfile.TemporaryDirectory(prefix='platform-integrated-') as folder:
            database = Path(folder) / 'test.db'
            a, r, g = free_port(), free_port(), free_port()
            api, reconstruction, gateway = (f'http://127.0.0.1:{port}' for port in (a, r, g))
            external = f'http://127.0.0.1:{modules.server_port}/'
            common = {'ConnectionStrings__Disasters': f'Data Source={database}', 'Security__LocalDemo': 'true',
                'ASPNETCORE_ENVIRONMENT': 'Development', 'Messaging__Enabled': 'false'}
            with open(Path(folder) / 'process.log', 'w+') as log:
                try:
                    processes.append(start('DisasterLocation', a, common, log))
                    processes.append(start('Reconstruction.Api', r, {**common, 'Reconstruction__DemoExternalData': 'false',
                        'ExternalApis__Disasters': api + '/', 'ExternalApis__DamageAssessments__BaseUrl': external,
                        'ExternalApis__Funding__BaseUrl': external, 'Attachments__Directory': str(Path(folder) / 'attachments')}, log))
                    processes.append(start('DisasterLocation.Gateway', g, {
                        'ReverseProxy__Clusters__disasters-api__Destinations__local__Address': api + '/',
                        'ReverseProxy__Clusters__reconstruction-api__Destinations__local__Address': reconstruction + '/'}, log))
                    wait_ready(api, '/api/desastres', processes[0]); wait_ready(reconstruction, '/api/reconstruccion/health', processes[1])
                    wait_ready(gateway, '/', processes[2])
                    def call(path, method='GET', body=None, expected=200):
                        status, _, data = request(gateway, path, method, body)
                        assert status == expected, (status, expected, data)
                        return data
                    assert call('/api/reconstruccion/ready')['ready']
                    disaster = call('/api/desastres', 'POST', {'nombre': 'Integración', 'ubicacion': 'Bogotá', 'severidad': 4,
                        'descripcion': 'Prueba temporal', 'latitud': 4.7, 'longitud': -74.0, 'radio': 500}, 201)
                    assert disaster['radio'] == 500
                    call('/api/reconstruccion/propuestas/generar', 'POST', {'disasterId': disaster['id']}, 409)
                    for phase in ['Response', 'Recovery']:
                        disaster = call(f"/api/desastres/{disaster['id']}/fase", 'PUT', {'phase': phase, 'expectedVersion': disaster['version']})
                    proposals = call('/api/reconstruccion/propuestas/generar', 'POST', {'disasterId': disaster['id']})
                    assert len(proposals) == 3 and sum(Decimal(str(p['proposedBudget'])) for p in proposals) == Decimal('1000.01')
                    mixed = next(p for p in proposals if p['assessmentCount'] == 2)
                    assert mixed['averageSeverity'] == 2.5
                    project = call('/api/reconstruccion/proyectos', 'POST', {'proposalId': mixed['id'], 'expectedVersion': mixed['version']})
                    path = f"/api/reconstruccion/proyectos/{project['id']}"
                    funding = call(f"/api/reconstruccion/financiacion?disasterId={disaster['id']}")
                    now = datetime.now(timezone.utc)
                    definition = {'name': 'Actividad original', 'responsible': 'Equipo', 'startsAt': now.isoformat(),
                        'dueAt': (now + timedelta(days=1)).isoformat(), 'budget': 80}
                    formalization = {'finalBudget': 100, 'fundingSource': funding['source'], 'fundingSourceId': funding['sourceId'],
                        'responsible': 'Responsable', 'contractor': 'Contratista', 'activities': [definition], 'expectedVersion': project['version']}
                    call(path + '/formalizacion', 'PUT', {**formalization, 'fundingSourceId': 'invented'}, 422)
                    project = call(path + '/formalizacion', 'PUT', formalization)
                    old_activity = project['activities'][0]['id']
                    project = call(path + '/formalizacion', 'PUT', {**formalization, 'activities': [{**definition, 'name': 'Actividad definitiva'}], 'expectedVersion': project['version']})
                    audit = call(path + '/auditoria')
                    assert audit[-1]['before']['activities'][0]['id'] == old_activity
                    assert audit[-1]['actor'] == 'local-demonstration'
                    project = call(path + '/confirmar-formalizacion', 'POST', {'expectedVersion': project['version']})
                    assert project['status'] == 'Monitoring' and not project['signedContractReference']
                    activity = project['activities'][0]['id']
                    project = call(path + '/presupuesto', 'PUT', {'budget': 120, 'reason': 'Nueva asignación',
                        'activityBudgets': {activity: 110}, 'expectedVersion': project['version']})
                    def upload(category):
                        boundary = 'attachment-' + uuid.uuid4().hex
                        raw = (f'--{boundary}\r\nContent-Disposition: form-data; name="category"\r\n\r\n{category}\r\n'
                            f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="soporte.pdf"\r\nContent-Type: application/pdf\r\n\r\n%PDF-1.4\nTest\r\n--{boundary}--\r\n').encode()
                        with urlopen(Request(gateway + path + '/adjuntos', data=raw, headers={'Content-Type': 'multipart/form-data; boundary=' + boundary})) as response:
                            return json.load(response)
                    evidence, document = upload('evidence'), upload('document')
                    report_path = path + f'/actividades/{activity}/reportes'
                    payload = {'status': 'InProgress', 'actualCost': 30, 'evidence': ['invented'],
                        'documents': [f"attachment:{document['id']}"], 'expectedVersion': project['version'], 'requestId': str(uuid.uuid4())}
                    call(report_path, 'POST', payload, 422)
                    payload['evidence'] = [f"attachment:{evidence['id']}"]
                    project = call(report_path, 'POST', payload)
                    assert call(report_path, 'POST', payload)['version'] == project['version']
                    assert call(report_path, 'POST', {**payload, 'expectedVersion': project['version']})['version'] == project['version']
                    call(report_path, 'POST', {**payload, 'actualCost': 31}, 409)
                    call(path + '/cerrar', 'POST', {'expectedVersion': project['version']}, 422)
                    project = call(report_path, 'POST', {**payload, 'status': 'Completed', 'actualCost': 50,
                        'expectedVersion': project['version'], 'requestId': str(uuid.uuid4())})
                    project = call(path + '/cerrar', 'POST', {'expectedVersion': project['version']})
                    final = call(path + '/reporte-final')
                    assert final['totalExecuted'] == 80 and final['totalReports'] == 2 and final['remainingBudget'] == 40
                    assert call(f"/api/reconstruccion/financiacion?disasterId={disaster['id']}")['committedAmount'] == 80
                    download = request(gateway, path + f"/adjuntos/{evidence['id']}")
                    assert download[0] == 200 and download[2].startswith(b'%PDF-')
                    with sqlite3.connect(database) as db:
                        assert db.execute('PRAGMA foreign_key_check').fetchall() == []
                        assert db.execute('SELECT COUNT(*) FROM ReconstructionReports').fetchone()[0] == 2
                    stop(processes[1])
                    processes[1] = start('Reconstruction.Api', r, {**common, 'Reconstruction__DemoExternalData': 'false',
                        'ExternalApis__Disasters': api + '/', 'ExternalApis__DamageAssessments__BaseUrl': external,
                        'ExternalApis__Funding__BaseUrl': external, 'Attachments__Directory': str(Path(folder) / 'attachments')}, log)
                    wait_ready(reconstruction, '/api/reconstruccion/health', processes[1])
                    assert call(path + '/reporte-final') == final
                    print('OK: contratos HTTP, fases, reparto exacto, financiación, auditoría completa, presupuesto, adjuntos, idempotencia, cierre y reinicio.')
                except BaseException:
                    log.flush(); log.seek(0); print(log.read()[-12000:]); raise
                finally:
                    for process in reversed(processes): stop(process)
    finally:
        modules.shutdown(); modules.server_close(); worker.join()


if __name__ == '__main__': main()
