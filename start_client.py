import subprocess
import os
import os.path as path
import signal

wd = path.join(os.getcwd(), 'PingPong.Client')

proc = subprocess.Popen('dotnet run --configuration Release', shell=True, cwd=wd)

signal.sigwait(set([signal.SIGTERM, signal.SIGINT]))

proc.send_signal(signal.SIGINT)
proc.wait()