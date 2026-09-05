#!/usr/bin/env python3
"""Real Minecraft/systemd/cgroup soak. Uses disposable state, worlds and credentials.
Requires root and one stopped, EULA-accepted Vanilla server as a JAR/Java source.
Never starts or modifies the original server. Evidence and test data are retained.
"""
import argparse
import datetime as dt
import http.cookiejar
import json
import os
from pathlib import Path
import pwd
import shutil
import signal
import socket
import sqlite3
import struct
import subprocess
import time
import urllib.request
import uuid


def run(*args):
    return subprocess.run(args, check=True, capture_output=True, text=True).stdout.strip()


def wait(check, seconds=180):
    deadline = time.monotonic() + seconds
    last = None
    while time.monotonic() < deadline:
        try:
            value = check()
            if value:
                return value
        except (OSError, ValueError, RuntimeError) as error:
            last = error
        time.sleep(0.05)
    raise RuntimeError(f"Timed out: {last}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--hours', type=float, default=24)
    parser.add_argument('--interval', type=int, default=300)
    parser.add_argument('--installation', default='/opt/mcpanel')
    parser.add_argument('--source-data', default='/var/lib/mcpanel')
    parser.add_argument('--evidence', required=True)
    args = parser.parse_args()
    def terminate(_signal, _frame):
        raise KeyboardInterrupt('Soak was stopped')
    signal.signal(signal.SIGTERM, terminate)
    if os.geteuid() != 0 or args.hours <= 0 or args.interval < 10:
        parser.error('Requires root, positive hours, and an interval of at least ten seconds.')
    root = Path(args.evidence).resolve()
    root.mkdir(mode=0o700, parents=True, exist_ok=False)
    account = pwd.getpwnam('mcpanel')
    data, config = root / 'data', root / 'config'
    for directory in (root, data, config):
        directory.mkdir(exist_ok=True)
        os.chown(directory, account.pw_uid, account.pw_gid)
    # Panel restarts must keep testing the captured build if the main installation
    # is updated during the run.
    release = root / 'installation'
    shutil.copytree(Path(args.installation).resolve(), release, symlinks=True)
    binary = str(release / 'McPanel.Api')
    prefix = 'mcpanel-soak-' + uuid.uuid4().hex[:10]
    runtime_unit, panel_unit = prefix + '-runtime', prefix + '-panel'
    units = []

    def event(kind, **fields):
        with (root / 'events.jsonl').open('a') as output:
            output.write(json.dumps(dict(time=dt.datetime.now(dt.timezone.utc).isoformat(), event=kind, **fields)) + '\n')

    def port():
        with socket.socket() as candidate:
            candidate.bind(('0.0.0.0', 0))
            return candidate.getsockname()[1]

    panel_port, game_port = port(), port()
    base = f'http://127.0.0.1:{panel_port}'
    opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))

    def api(path, body=None):
        headers = {} if body is None else {'X-XSRF-TOKEN': api('/api/v1/auth/antiforgery')['token'], 'Content-Type': 'application/json'}
        request = urllib.request.Request(base + path, data=None if body is None else json.dumps(body).encode(), headers=headers)
        with opener.open(request, timeout=180) as response:
            content = response.read()
            return json.loads(content) if content else None

    def control(operation, payload=None):
        message = json.dumps(dict(version=1, requestId=str(uuid.uuid4()), operation=operation, payload=payload)).encode()
        with socket.socket(socket.AF_UNIX) as connection:
            connection.settimeout(180)
            connection.connect(str(data / 'runtime/control.sock'))
            connection.sendall(struct.pack('>I', len(message)) + message)
            def exact(size):
                result = b''
                while len(result) < size:
                    chunk = connection.recv(size - len(result))
                    if not chunk:
                        raise RuntimeError('Runtime connection closed')
                    result += chunk
                return result
            reply = json.loads(exact(struct.unpack('>I', exact(4))[0]))
            if not reply['success']:
                raise RuntimeError(reply['error'])
            return reply['payload']

    environment = [f'MCPANEL_DATA_DIR={data}', f'MCPANEL_CONFIG_DIR={config}', 'ASPNETCORE_ENVIRONMENT=Production',
                   f'ASPNETCORE_URLS=http://0.0.0.0:{panel_port}', 'Panel__ConsoleLinesPerServer=1000',
                   'Panel__BackupRetentionCount=3', 'Panel__BackupRetentionBytes=2147483648', 'Panel__BackupLeaseSeconds=10']
    setup_token = uuid.uuid4().hex + uuid.uuid4().hex
    token_file = config / 'setup-token'
    token_file.write_text(setup_token)
    token_file.chmod(0o600)
    os.chown(token_file, account.pw_uid, account.pw_gid)

    def start_unit(name, runtime):
        command = ['systemd-run', '--unit', name, '--property=Type=exec', '--property=Delegate=yes',
                   '--property=KillMode=mixed', '--property=TimeoutStopSec=90', '--property=NoNewPrivileges=yes',
                   '--uid=mcpanel', '--gid=mcpanel', '--working-directory=' + str(data)]
        command += ['--setenv=' + value for value in environment]
        command += [binary] + (['--mcpanel-runtime-host'] if runtime else [])
        run(*command)
        if name not in units:
            units.append(name)

    def restart_panel():
        try:
            run('systemctl', 'start', panel_unit)
        except subprocess.CalledProcessError:
            start_unit(panel_unit, False)
        wait(lambda: api('/health/ready')['status'] == 'ready')

    def job_done(job):
        def finished():
            current = api('/api/v1/jobs/' + job['id'])
            if current['state'] in ('Failed', 'Canceled', 'Interrupted'):
                raise AssertionError(json.dumps(current))
            return current if current['state'] == 'Completed' else None
        return wait(finished, 300)

    result = dict(startedAt=dt.datetime.now(dt.timezone.utc).isoformat(), requestedHours=args.hours,
                  panelPort=panel_port, runtimeUnit=runtime_unit, panelUnit=panel_unit, cycles=0, status='running', binary=binary)
    (root / 'result.json').write_text(json.dumps(result, indent=2))
    try:
        start_unit(runtime_unit, True)
        start_unit(panel_unit, False)
        wait(lambda: api('/health/ready')['status'] == 'ready')
        api('/api/v1/auth/setup', dict(token=setup_token, username='soak-admin', password=uuid.uuid4().hex + uuid.uuid4().hex))
        with sqlite3.connect('file:' + str(Path(args.source_data) / 'state.db') + '?mode=ro', uri=True) as source:
            source.row_factory = sqlite3.Row
            original = source.execute("SELECT * FROM Servers WHERE Kind='Vanilla' AND State='Stopped' AND EulaAcceptedAt>0 LIMIT 1").fetchone()
            if original is None:
                raise RuntimeError('A stopped, EULA-accepted Vanilla server is required as a source.')
            original = dict(original)
            java = dict(source.execute('SELECT * FROM JavaRuntimes WHERE Id=?', (original['JavaRuntimeId'],)).fetchone())
        source_instance = Path(args.source_data) / 'instances' / original['Id'].replace('-', '').lower()
        if original['LaunchTarget'] != 'server.jar':
            raise RuntimeError('The source must use the standard Vanilla server.jar launch target.')
        server_id = str(uuid.uuid4()).upper()
        instance = data / 'instances' / server_id.replace('-', '').lower()
        instance.mkdir()
        shutil.copy2(source_instance / 'server.jar', instance / 'server.jar')
        shutil.copy2(source_instance / 'eula.txt', instance / 'eula.txt')
        (instance / 'server.properties').write_text(f'server-port={game_port}\nserver-ip=127.0.0.1\nonline-mode=true\nview-distance=3\nsimulation-distance=3\nlevel-name=soak-world\nmax-players=1\n')
        with (instance / 'copy-fault-probe.dat').open('wb') as probe:
            probe.truncate(512 * 1024 * 1024)
        for file in [instance, *instance.iterdir()]:
            os.chown(file, account.pw_uid, account.pw_gid)
        original.update(Id=server_id, Name='Disposable production soak', Port=game_port, State='Stopped', ProcessId=None,
                        StartOnBoot=0, CrashRecovery=1, CrashAttempts=0, StartedAt=None, MemoryMb=1024, InitialMemoryMb=512,
                        MemoryLimitMb=1536, RecoveryRequired=0, RecoveryReason=None, PublicHost=None, PublicPort=None)
        with sqlite3.connect(data / 'state.db') as database:
            columns = {row[1] for row in database.execute('PRAGMA table_info(Servers)')}
            original = {key: value for key, value in original.items() if key in columns}
            for table, row in [('JavaRuntimes', java), ('Servers', original)]:
                database.execute('INSERT OR REPLACE INTO ' + table + ' (' + ','.join(row) + ') VALUES (' + ','.join('?' for _ in row) + ')', list(row.values()))
        endpoint = '/api/v1/servers/' + server_id
        job_done(api(endpoint + '/actions/start', {}))
        if not control('snapshot')[0]['memoryEnforced']:
            raise AssertionError('The runtime did not enforce the real cgroup memory limit')
        event('started', runtime=control('capabilities'), memoryEnforced=True)
        deadline = time.monotonic() + args.hours * 3600
        while time.monotonic() < deadline:
            job_done(api(endpoint + '/backups', {}))
            event('backup-completed')
            previous_backups = {x['id'] for x in api(endpoint + '/backups')}
            before_kill_pid = control('snapshot')[0]['processId']
            operation = api(endpoint + '/backups', {})
            lease_file = data / 'runtime/leases' / (server_id.replace('-', '').lower() + '.json')
            wait(lease_file.exists, 30)
            run('systemctl', 'kill', '--kill-whom=main', '--signal=KILL', panel_unit)
            wait(lambda: not lease_file.exists(), 30)
            after_kill = control('snapshot')[0]
            if after_kill['state'] != 1 or after_kill['processId'] != before_kill_pid:
                raise AssertionError('Minecraft did not survive panel death')
            restart_panel()
            outcome = api('/api/v1/jobs/' + operation['id'])
            if outcome['state'] != 'Interrupted':
                raise AssertionError('Interrupted snapshot was incorrectly accepted: ' + json.dumps(outcome))
            if {x['id'] for x in api(endpoint + '/backups')} != previous_backups:
                raise AssertionError('An incomplete backup was published')
            event('panel-kill-recovered', interruptedJob=operation['id'])
            run('systemctl', 'stop', panel_unit)
            prior_pid = control('snapshot')[0]['processId']
            os.kill(prior_pid, signal.SIGKILL)
            def recovered():
                state = control('snapshot')[0]
                return state['state'] == 1 and state['processId'] != prior_pid
            wait(recovered, 180)
            restart_panel()
            event('runtime-crash-recovered')
            job_done(api(endpoint + '/actions/stop', {}))
            job_done(api(endpoint + '/actions/start', {}))
            with sqlite3.connect(data / 'console.db') as logs:
                retained = logs.execute('SELECT count(*) FROM Lines').fetchone()[0]
            if retained > 2500:
                raise AssertionError('Console retention exceeded the configured bound plus one-minute allowance')
            event('retention-checked', retainedLines=retained)
            result['cycles'] += 1
            (root / 'result.json').write_text(json.dumps(result, indent=2))
            time.sleep(min(args.interval, max(0, deadline - time.monotonic())))
        result['status'] = 'passed'
    except BaseException as error:
        result['status'] = 'failed'
        result['error'] = str(error)
        event('failure', error=str(error))
        raise
    finally:
        result['finishedAt'] = dt.datetime.now(dt.timezone.utc).isoformat()
        (root / 'result.json').write_text(json.dumps(result, indent=2))
        for unit in reversed(units):
            subprocess.run(['systemctl', 'stop', unit], capture_output=True)
            with (root / (unit + '.journal')).open('w') as output:
                subprocess.run(['journalctl', '-u', unit, '--no-pager'], stdout=output, stderr=subprocess.STDOUT)
        print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
