#!/usr/bin/env python3
"""Run Reconstruction and Gateway; Disaster Management is configured over HTTP."""
import argparse
import os
from pathlib import Path
import subprocess
import time

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('--demo', action='store_true')
parser.add_argument('--messaging', action='store_true')
args = parser.parse_args()
connection = os.environ.get('RECONSTRUCTION_CONNECTION_STRING')
if not connection:
    raise SystemExit('Configure RECONSTRUCTION_CONNECTION_STRING con las credenciales de Reconstrucción.')
disasters_url = os.environ.get('ExternalApis__Disasters', 'http://localhost:5156/')
env = {key: value for key, value in os.environ.items()
       if 'connectionstring' not in key.replace('_', '').replace(':', '').lower()
       and not key.upper().startswith('PG')
       and 'database__' not in key.replace(':', '__').lower()}
env.update(ASPNETCORE_ENVIRONMENT='Development', Messaging__Enabled=str(args.messaging).lower(),
           Reconstruction__DemoExternalData=str(args.demo).lower(), Security__LocalDemo=str(args.demo).lower())
processes = []
try:
    for project, port in [('Reconstruction.Api', 5160), ('DisasterLocation.Gateway', 5080)]:
        folder = root / project
        process_env = {**env}
        if project == 'DisasterLocation.Gateway':
            folder = Path(os.environ.get('GATEWAY_PROJECT_PATH', root.parent / 'disaster-api-gateway' / project))
            for cluster, address in [('disasters-api', disasters_url), ('reconstruction-api', 'http://localhost:5160/')]:
                prefix = f'ReverseProxy__Clusters__{cluster}__Destinations__'
                if not any(key.replace(':', '__').lower().startswith(prefix.lower()) for key in env):
                    process_env[prefix + 'local__Address'] = address
        else:
            process_env['ConnectionStrings__Disasters'] = connection
            process_env['ExternalApis__Disasters'] = disasters_url
        dll = folder / 'bin/Debug/net10.0' / (project + '.dll')
        if not dll.exists(): raise SystemExit('Compile la solución y el Gateway compartido antes de iniciar.')
        processes.append(subprocess.Popen(['dotnet', str(dll), '--urls', f'http://localhost:{port}'], cwd=folder, env=process_env))
    print('Interfaz: http://localhost:5080. Ctrl+C detiene los procesos.', flush=True)
    while all(process.poll() is None for process in processes): time.sleep(0.5)
except KeyboardInterrupt:
    pass
finally:
    for process in reversed(processes):
        if process.poll() is None:
            process.terminate()
            try: process.wait(timeout=5)
            except subprocess.TimeoutExpired: process.kill(); process.wait()
