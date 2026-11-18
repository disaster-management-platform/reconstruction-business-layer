"""Prueba real Gateway -> API -> BLL -> DAL -> PostgreSQL, con base temporal.

Ejecutar desde la raíz:
dotnet build && dotnet build ../disaster-api-gateway/DisasterLocation.Gateway.sln
python3 scripts/smoke_test.py
No modifica los datos de la aplicación ni usa sus puertos de desarrollo.
"""
import json
import os
from pathlib import Path
import socket
import psycopg2
import psycopg2.sql
from contextlib import closing, contextmanager
import subprocess
import tempfile
import time
import uuid
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

ROOT = Path(__file__).resolve().parents[1]
BACKEND = Path(os.environ.get("BACKEND_PROJECT_PATH", ROOT.parent / "disaster-backend-offline/DisasterLocation"))


@contextmanager
def test_database():
    settings = psycopg2.extensions.parse_dsn(os.environ["TEST_POSTGRES_DSN"])
    if not settings.get("host", "").startswith("/"):
        raise ValueError("Las pruebas requieren un socket PostgreSQL local, nunca Supabase.")
    name = "platform_test_" + uuid.uuid4().hex
    with closing(psycopg2.connect(**settings)) as admin:
        admin.autocommit = True
        with admin.cursor() as cursor:
            cursor.execute(psycopg2.sql.SQL("CREATE DATABASE {}").format(psycopg2.sql.Identifier(name)))
        try:
            settings["dbname"] = name
            settings["options"] = "-csearch_path=disasters,reconstruction,public"
            yield psycopg2.extensions.make_dsn(**settings)
        finally:
            with admin.cursor() as cursor:
                cursor.execute(psycopg2.sql.SQL("DROP DATABASE {} WITH (FORCE)").format(psycopg2.sql.Identifier(name)))


def postgres_connection_string(database):
    settings = psycopg2.extensions.parse_dsn(database)
    def escape(value):
        return '"' + value.replace('"', '""') + '"'
    return ";".join(key + "=" + escape(settings[source]) for key, source in
        [("Host", "host"), ("Port", "port"), ("Database", "dbname"), ("Username", "user"), ("Password", "password")]
        if source in settings)


def migrate_database(database, log):
    project = BACKEND
    subprocess.run(["dotnet", str(project / "bin/Debug/net10.0/DisasterLocation.dll"), "--Database:Migrate=true"],
        cwd=project, env={**os.environ, "ConnectionStrings__Disasters": postgres_connection_string(database)},
        stdout=log, stderr=log, check=True, timeout=60)


def free_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def request(base, path="/api/desastres", method="GET", data=None, headers=None):
    if method == "PUT" and path.endswith("/fase") and isinstance(data, dict):
        data = {"reason": "Transición autorizada del escenario de integración", "confirmNoCriticalCommitments": True, **data}
    body = None if data is None else json.dumps(data).encode()
    request_headers = {"Content-Type": "application/json"} if body else {}
    request_headers.update(headers or {})
    req = Request(base + path, data=body, method=method, headers=request_headers)
    try:
        response = urlopen(req, timeout=5)
    except HTTPError as error:
        response = error
    with response:
        raw = response.read()
        content = json.loads(raw) if raw and "json" in response.headers.get("Content-Type", "") else raw
        return response.status, response.headers, content


def wait_ready(base, path, process):
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise AssertionError("El proceso terminó antes de iniciar. Revisa los logs.")
        try:
            if request(base, path)[0] == 200:
                return
        except (URLError, TimeoutError):
            pass
        time.sleep(.15)
    raise AssertionError("La aplicación no inició en 30 segundos.")


def start(project, port, env, log):
    folder = BACKEND if project == "DisasterLocation" else ROOT / project
    process_env = {**os.environ, "ASPNETCORE_ENVIRONMENT": "Development", "Security__LocalDemo": "true", **env}
    if project == "DisasterLocation.Gateway":
        folder = Path(os.environ.get("GATEWAY_PROJECT_PATH", ROOT.parent / "disaster-api-gateway" / project))
    dll = folder / "bin/Debug/net10.0" / f"{project}.dll"
    return subprocess.Popen(
        ["dotnet", str(dll), "--urls", f"http://127.0.0.1:{port}"],
        cwd=folder, env=process_env, stdout=log, stderr=log)


def stop(process):
    if process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()


def main():
    processes = []
    with tempfile.TemporaryDirectory(prefix="disasters-test-") as folder, test_database() as database:
        api_port, gateway_port = free_port(), free_port()
        api_url = f"http://127.0.0.1:{api_port}"
        gateway_url = f"http://127.0.0.1:{gateway_port}"
        api_env = {"ConnectionStrings__Disasters": postgres_connection_string(database)}
        with open(Path(folder) / "processes.log", "w+") as log:
            try:
                migrate_database(database, log)
                backend = start("DisasterLocation", api_port, api_env, log)
                processes.append(backend)
                wait_ready(api_url, "/api/desastres", backend)
                gateway = start("DisasterLocation.Gateway", gateway_port, {
                    "ReverseProxy__Clusters__disasters-api__Destinations__local__Address": api_url + "/",
                    "Cors__AllowedOrigins__0": "http://localhost:3000"
                }, log)
                processes.append(gateway)
                wait_ready(gateway_url, "/", gateway)
                assert request(gateway_url)[2] == []
                for asset in ["/", "/app.js", "/styles.css", "/lib/leaflet/leaflet.js", "/lib/leaflet/leaflet.css"]:
                    status, _, body = request(gateway_url, asset)
                    assert status == 200 and body, asset

                payload = {
                    "nombre": "  Inundación Bogotá  ", "ubicacion": "Bogotá, Colombia",
                    "severidad": 4, "descripcion": "Desbordamiento <script>alert(1)</script>",
                    "latitud": 4.711, "longitud": -74.0721
                }
                status, headers, saved = request(gateway_url, method="POST", data=payload,
                    headers={"User-Agent": "WebClient/1.0"})
                assert status == 201 and saved["nombre"] == "Inundación Bogotá"
                assert saved["id"] > 0 and saved["descripcion"] == payload["descripcion"]
                location = headers["Location"]
                assert location == f'/api/desastres/{saved["id"]}'
                assert request(gateway_url, location)[2] == saved
                assert request(api_url)[2] == request(gateway_url)[2] == [saved]
                mobile = {**payload, "nombre": "Reporte desde móvil", "severidad": 1,
                          "latitud": 0, "longitud": 0}
                assert request(gateway_url, method="POST", data=mobile,
                    headers={"User-Agent": "MobileClient/1.0"})[0] == 201
                assert len(request(gateway_url)[2]) == 2

                invalid_cases = [
                    ({"severidad": 0}, "severidad"), ({"severidad": 6}, "severidad"),
                    ({"severidad": None}, "severidad"), ({"nombre": "  "}, "nombre"),
                    ({"ubicacion": None}, "ubicacion"), ({"descripcion": ""}, "descripcion"),
                    ({"nombre": "a" * 121}, "nombre"), ({"ubicacion": "a" * 201}, "ubicacion"),
                    ({"descripcion": "a" * 2001}, "descripcion"),
                    ({"latitud": 91}, "latitud"), ({"latitud": -91}, "latitud"),
                    ({"longitud": 181}, "longitud"), ({"longitud": -181}, "longitud"),
                    ({"latitud": None}, "latitud"), ({"longitud": None}, "longitud")
                ]
                for change, field in invalid_cases:
                    status, headers, problem = request(gateway_url, method="POST", data={**payload, **change})
                    assert status == 400 and field in problem["errors"], (change, problem)
                    assert "application/problem+json" in headers["Content-Type"]
                for invalid in [{}, {**payload, "severidad": 1.5}, {**payload, "latitud": "abc"}]:
                    assert request(gateway_url, method="POST", data=invalid)[0] == 400
                assert request(gateway_url, "/api/desastres/999999")[0] == 404
                assert request(gateway_url, method="DELETE")[0] == 405
                assert len(request(gateway_url)[2]) == 2, "Los datos inválidos no se deben guardar"

                # El gateway conserva la respuesta de la API, incluso los errores.
                bad = {**payload, "severidad": 8}
                gateway_problem = request(gateway_url, method="POST", data=bad)[2]
                api_problem = request(api_url, method="POST", data=bad)[2]
                # Problem Details incluye un traceId distinto en cada solicitud.
                for field in ("status", "title", "type", "errors"):
                    assert gateway_problem[field] == api_problem[field]
                status, headers, _ = request(gateway_url, method="OPTIONS", headers={
                    "Origin": "http://localhost:3000", "Access-Control-Request-Method": "POST",
                    "Access-Control-Request-Headers": "content-type"
                })
                assert status == 204 and headers["Access-Control-Allow-Origin"] == "http://localhost:3000"

                stop(backend)
                assert request(gateway_url)[0] == 502, "El gateway debe indicar que la API no está disponible"
                backend = start("DisasterLocation", api_port, api_env, log)
                processes.append(backend)
                wait_ready(api_url, "/api/desastres", backend)
                assert len(request(gateway_url)[2]) == 2, "Los datos deben persistir tras reiniciar"
                with closing(psycopg2.connect(database)) as connection, connection.cursor() as cursor:
                    cursor.execute('SELECT COUNT(*) FROM "Desastres"')
                    assert cursor.fetchone()[0] == 2
                    try:
                        cursor.execute('UPDATE "Desastres" SET "Severidad" = 6')
                    except psycopg2.IntegrityError:
                        connection.rollback()
                    else:
                        raise AssertionError("Falta la restricción de severidad en PostgreSQL")
                print("OK: registro web/móvil, consulta, validación, gateway, CORS, DAL y persistencia tras reinicio.")
            except Exception:
                log.flush()
                log.seek(0)
                print(log.read()[-10000:])
                raise
            finally:
                for process in reversed(processes):
                    stop(process)


if __name__ == "__main__":
    main()
