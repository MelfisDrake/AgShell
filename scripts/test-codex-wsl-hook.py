#!/usr/bin/env python3
import json
import os
import shutil
import subprocess
import sys
import time
import uuid
from pathlib import Path

if len(sys.argv) != 2:
    raise SystemExit("Usage: python3 scripts/test-codex-wsl-hook.py /mnt/c/path/to/agshellctl.exe")
executable = Path(sys.argv[1]).resolve()
if not executable.is_file():
    raise SystemExit("agshellctl.exe not found")
failed = False

marker='agshell-hook-probe-'+uuid.uuid4().hex[:12]
base=Path('/mnt/c/Temp')/marker
base.mkdir(parents=True)
winbase='C:\\Temp\\'+marker
pipe=marker+'-pipe'
ps=base/'server.ps1'
ps.write_text(r'''$ErrorActionPreference = 'Stop'
$root = '__ROOT__'
[IO.File]::WriteAllText("$root\server.pid", [string]$PID)
while ($true) {
    $pipe = [IO.Pipes.NamedPipeServerStream]::new('__PIPE__', [IO.Pipes.PipeDirection]::InOut, 1, [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::Asynchronous)
    try {
        [IO.File]::WriteAllText("$root\ready", 'ok')
        $pipe.WaitForConnection()
        $reader = [IO.StreamReader]::new($pipe, [Text.UTF8Encoding]::new($false), $false, 1024, $true)
        $writer = [IO.StreamWriter]::new($pipe, [Text.UTF8Encoding]::new($false), 1024, $true)
        $writer.AutoFlush = $true
        $line = $reader.ReadLine()
        if ($line) {
            $request = $line | ConvertFrom-Json
            [IO.File]::AppendAllText("$root\requests.log", [string]$request.cmd + "`n")
            $writer.WriteLine('{"ok":true,"result":{"text":""}}')
        }
        $writer.Dispose()
        $reader.Dispose()
    } catch [IO.IOException] {
    } finally { $pipe.Dispose() }
}
'''.replace('__ROOT__',winbase).replace('__PIPE__',pipe))
server=subprocess.Popen(['powershell.exe','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',winbase+'\\server.ps1'],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)

def stop_watchers(session):
    script="Get-CimInstance Win32_Process -Filter \"Name = 'agshellctl.exe'\" | Where-Object { $_.CommandLine -like '*"+session+"*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }"
    subprocess.run(['powershell.exe','-NoProfile','-Command',script],capture_output=True,timeout=10)

try:
    deadline=time.monotonic()+8
    while not (base/'ready').exists() and time.monotonic()<deadline:time.sleep(.1)
    assert (base/'ready').exists(),'Probe pipe server did not start'
    variants=[('tested',str(executable))]
    for label,source in variants:
        folder=base/label;folder.mkdir();exe=folder/'agshellctl.exe';shutil.copy2(source,exe)
        session=marker+'-'+label
        env=dict(os.environ)
        env.update(AGSHELL_ENABLED='1',AGSHELL_SESSION_ID=session,AGSHELL_PANE='left',AGSHELL_PANE_ID='probe',AGSHELL_PIPE=pipe)
        env.pop('AGSHELL_STATUS_WRAPPER',None)
        env['WSLENV']='AGSHELL_ENABLED:AGSHELL_SESSION_ID:AGSHELL_PANE:AGSHELL_PANE_ID:AGSHELL_PIPE'
        for action in ('session-start','pre-tool-use','user-prompt-submit','permission-request','stop'):
            start=time.monotonic()
            p=subprocess.Popen([str(exe),'hook','codex',action],env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
            try:
                out, err = p.communicate(input=b'{}\n',timeout=4)
                if out or err: failed = True
                if action in ('user-prompt-submit', 'permission-request'):
                    watch_deadline = time.monotonic() + 2
                    while time.monotonic() < watch_deadline:
                        if (base/'requests.log').exists() and (base/'requests.log').read_text().splitlines().count('session.text') >= (1 if action == 'user-prompt-submit' else 2): break
                        time.sleep(.05)
                print(label,action,'exit',p.returncode,'seconds',round(time.monotonic()-start,2),flush=True)
                if p.returncode != 0: failed = True
            except subprocess.TimeoutExpired:
                failed = True
                print(label,action,'HANGS after 4 seconds',flush=True)
                # The hook process can have exited on Windows while WSL is still waiting for its watcher.
                script="Get-CimInstance Win32_Process -Filter \"Name = 'agshellctl.exe'\" | Select-Object ProcessId,ParentProcessId,CommandLine | ConvertTo-Json -Compress"
                r=subprocess.run(['powershell.exe','-NoProfile','-Command',script],capture_output=True,text=True,timeout=10)
                items=json.loads(r.stdout) if r.stdout.strip() else []
                if isinstance(items,dict):items=[items]
                own=[x for x in items if session in (x.get('CommandLine') or '')]
                print('Own watcher count:',len(own),flush=True)
                alive={x['ProcessId'] for x in items}
                print('Hook parent process already exited:',bool(own) and all(x['ParentProcessId'] not in alive for x in own),flush=True)
                stopped=time.monotonic();stop_watchers(session)
                try:
                    p.communicate(timeout=4)
                    print('After stopping only watcher: hook returned',p.returncode,'in',round(time.monotonic()-stopped,2),'seconds',flush=True)
                except subprocess.TimeoutExpired:
                    p.kill();p.communicate();print('Probe launcher required cleanup',flush=True)
            finally:stop_watchers(session)
    if (base/'requests.log').exists():
        lines=(base/'requests.log').read_text().splitlines()
        print('Fake server requests:',{name:lines.count(name) for name in set(lines)},flush=True)
        if lines.count('session.status') != 4 or lines.count('session.text') < 2: failed = True
    else:
        failed = True
finally:
    if (base/'server.pid').exists():
        pid=(base/'server.pid').read_text().strip()
        subprocess.run(['powershell.exe','-NoProfile','-Command','Stop-Process -Id '+pid+' -Force -ErrorAction SilentlyContinue'],capture_output=True,timeout=10)
    try:server.wait(timeout=5)
    except subprocess.TimeoutExpired:server.kill();server.wait()
    script="@(Get-CimInstance Win32_Process | Where-Object { $_.ProcessId -ne $PID -and $_.CommandLine -like '*"+marker+"*' -and $_.Name -in @('agshellctl.exe','powershell.exe') }).Count"
    r=subprocess.run(['powershell.exe','-NoProfile','-Command',script],capture_output=True,text=True,timeout=10)
    print('Probe process cleanup check:',r.stdout.strip(),flush=True)
    if r.stdout.strip() != '0': failed = True
    shutil.rmtree(base)

raise SystemExit(1 if failed else 0)
