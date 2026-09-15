"""Real Azure Functions host gate. Requires .NET 10 and Azure Functions Core Tools v4.

Run from the repository root: python3 -m unittest tools/test_functions_host.py -v
Uses disposable copies, loopback ports and no Azure account or storage service.
"""
import json
import os
from pathlib import Path
import shutil
import signal
import socket
import subprocess
import tempfile
import time
import unittest
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
SAMPLE = ROOT / "samples" / "FunctionsApi"
TOOL = ROOT / "Rivet.Tool" / "bin" / "Debug" / "net9.0" / "Rivet.Tool.dll"


def run(args, cwd, env=None, expected=0):
    result = subprocess.run(args, cwd=cwd, env=env, capture_output=True, text=True, timeout=180)
    if result.returncode != expected:
        raise AssertionError(f"{args}: exit {result.returncode}\n{result.stdout}\n{result.stderr}")
    return result


class FunctionsHostTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        for executable in ("dotnet", "func"):
            if not shutil.which(executable):
                raise RuntimeError(f"Install {executable} before running the Functions host gate.")
        run(["dotnet", "build", str(ROOT / "Rivet.Tool" / "Rivet.Tool.csproj")], ROOT)

    def test_default_empty_and_custom_prefixes_on_real_host_and_checker(self):
        for prefix in ("api", "", "public/v1"):
            with self.subTest(prefix=prefix), tempfile.TemporaryDirectory(prefix="rivet-functions-") as tmp:
                project = Path(tmp) / "app"
                shutil.copytree(SAMPLE, project, ignore=shutil.ignore_patterns("bin", "obj", "local.settings.json"))
                csproj = project / "FunctionsApi.csproj"
                csproj.write_text(csproj.read_text().replace("../../Rivet.Attributes/Rivet.Attributes.csproj", str(ROOT / "Rivet.Attributes" / "Rivet.Attributes.csproj")))
                contract = project / "Contracts" / "Images" / "ImagesContract.cs"
                public_prefix = "/" + prefix if prefix else ""
                contract.write_text(contract.read_text().replace('Prefix = "/api"', f'Prefix = "{public_prefix}"'))
                (project / "host.json").write_text(json.dumps({"version": "2.0", "extensions": {"http": {"routePrefix": prefix}}}))
                env = dict(os.environ)
                env.pop("AzureFunctionsJobHost__extensions__http__routePrefix", None)
                env["FUNCTIONS_WORKER_RUNTIME"] = "dotnet-isolated"
                run(["dotnet", "build", str(csproj)], project, env)
                cli = ["dotnet", str(TOOL), "--project", str(csproj)]
                run([*cli, "--check"], project, env)
                wrong = dict(env, AzureFunctionsJobHost__extensions__http__routePrefix="wrong")
                mismatch = run([*cli, "--check"], project, wrong, expected=1)
                self.assertIn("RouteMismatch", mismatch.stderr)
                generated = Path(tmp) / "generated"
                run([*cli, "--output", str(generated)], project, env)
                spec = json.loads((generated / "openapi.json").read_text())
                representations = spec["paths"][public_prefix + "/images/{format}"]["get"]["responses"]["200"]["content"]
                self.assertEqual({"image/png", "image/jpeg"}, set(representations))
                output = project / "bin" / "Debug" / "net10.0"
                with socket.socket() as sock:
                    sock.bind(("127.0.0.1", 0))
                    port = sock.getsockname()[1]
                with (Path(tmp) / "host.log").open("w+") as log:
                    host = subprocess.Popen(["func", "start", "--no-build", "--script-root", str(output), "--port", str(port)], cwd=project, env=env, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
                    try:
                        base = f"http://127.0.0.1:{port}{public_prefix}/images/"
                        deadline = time.monotonic() + 60
                        while True:
                            try:
                                with urllib.request.urlopen(base + "png", timeout=2) as response:
                                    if response.status == 200:
                                        break
                            except (urllib.error.URLError, TimeoutError):
                                pass
                            if host.poll() is not None or time.monotonic() >= deadline:
                                log.flush()
                                log.seek(0)
                                self.fail("Functions host failed to start:\n" + log.read())
                            time.sleep(0.25)
                        for extension, media, data in (("png", "image/png", bytes([137, 80, 78, 71])), ("jpeg", "image/jpeg", bytes([255, 216, 255, 217]))):
                            with urllib.request.urlopen(base + extension, timeout=5) as response:
                                self.assertEqual(media, response.headers.get_content_type())
                                self.assertEqual(data, response.read())
                        with self.assertRaises(urllib.error.HTTPError) as missing:
                            urllib.request.urlopen(base + "gif", timeout=5)
                        self.assertEqual(404, missing.exception.code)
                        self.assertEqual("not_found", json.loads(missing.exception.read())["code"])
                        with self.assertRaises(urllib.error.HTTPError) as wrong_method:
                            urllib.request.urlopen(urllib.request.Request(base + "png", data=b"", method="POST"), timeout=5)
                        self.assertIn(wrong_method.exception.code, (404, 405))
                    finally:
                        if host.poll() is None:
                            os.killpg(host.pid, signal.SIGTERM)
                            try:
                                host.wait(timeout=10)
                            except subprocess.TimeoutExpired:
                                os.killpg(host.pid, signal.SIGKILL)
                                host.wait(timeout=10)
