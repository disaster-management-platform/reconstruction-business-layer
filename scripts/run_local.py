#!/usr/bin/env python3
"""Run the three processes; demonstration identity/data require an explicit flag."""
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
env = {**os.environ, 'ASPNETCORE_ENVIRONMENT': 'Development',
       'Messaging__Enabled': str(args.messaging).lower(),
       'Reconstruction__DemoExternalData': str(args.demo).lower(), 'Security__LocalDemo': str(args.demo).lower()}
if args.demo:
    env.update(Security__LocalDemo='true', Reconstruction__DemoExternalData='true')
processes = []
try:
    for project, port in [('DisasterLocation', 5156), ('Reconstruction.Api', 5160), ('DisasterLocation.Gateway', 5080)]:
        folder = root / project
        process_env = env
        if project == 'DisasterLocation.Gateway':
            folder = Path(os.environ.get('GATEWAY_PROJECT_PATH', root.parent / 'disaster-api-gateway' / project))
            process_env = {**env, 'ASPNETCORE_ENVIRONMENT': 'Reconstruction'}
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
