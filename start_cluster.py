import subprocess
import os
import os.path as path
import signal

HOST_COUNT = 2

wd = path.join(os.getcwd(), 'PingPong.Server')

host_processes = []

for i in range(1, HOST_COUNT + 1):
    proc = subprocess.Popen('dotnet run -- Host{0}.config.json'.format(i), shell=True, cwd=wd)
    host_processes.append(proc)

signal.sigwait(set([signal.SIGTERM, signal.SIGINT]))

for proc in host_processes:
    proc.terminate()
    proc.wait()