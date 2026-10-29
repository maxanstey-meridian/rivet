"""Real Azure Functions host gate. Requires .NET 10 and Azure Functions Core Tools v4.

Run from the repository root: python3 -m unittest tools/test_functions_host.py -v
Uses disposable copies, loopback ports and no Azure account or storage service.
"""
import contextlib
import json
import os
from pathlib import Path
import re
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

# Functions that echo what the worker bound for each parameter, so the binding-data rules
# RIV4005 and RIV4006 encode are pinned on the real host. They implement no contract.
PROBES = """
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace FunctionsApi;

public enum ProbeSize { Small, Medium, Large }

public sealed class Probes
{
    [Function("ProbeNames")]
    public IActionResult Names(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "probe/names/{kind}")] HttpRequest _,
        string? kind, string? other, string? term)
        => new OkObjectResult(new { kind, other, term });

    [Function("ProbeCase")]
    public IActionResult Case(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "probe/case/{Kind}")] HttpRequest _,
        string? kind)
        => new OkObjectResult(new { kind });

    [Function("ProbeEnumRoute")]
    public IActionResult EnumRoute(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "probe/enum/{size}")] HttpRequest _,
        ProbeSize size)
        => new OkObjectResult(new { size = size.ToString() });

    [Function("ProbeEnumQuery")]
    public IActionResult EnumQuery(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "probe/enum")] HttpRequest _,
        ProbeSize? size)
        => new OkObjectResult(new { size = size?.ToString() });

    [Function("ProbeValueType")]
    public IActionResult ValueType(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "probe/count")] HttpRequest _,
        int count)
        => new OkObjectResult(new { count });
}
"""


def run(args, cwd, env=None, expected=0):
    result = subprocess.run(args, cwd=cwd, env=env, capture_output=True, text=True, timeout=180)
    if result.returncode != expected:
        raise AssertionError(f"{args}: exit {result.returncode}\n{result.stdout}\n{result.stderr}")
    return result


def copy_sample(tmp, prefix):
    """A disposable copy of the sample, its contract and host.json set to the prefix."""
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
    return project, csproj, public_prefix, env


@contextlib.contextmanager
def functions_host(test, project, env, tmp, public_prefix):
    """Starts Core Tools on the built copy and yields its base URL once it answers."""
    output = project / "bin" / "Debug" / "net10.0"
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        port = sock.getsockname()[1]
    with (Path(tmp) / "host.log").open("w+") as log:
        host = subprocess.Popen(["func", "start", "--no-build", "--script-root", str(output), "--port", str(port)], cwd=project, env=env, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
        try:
            base = f"http://127.0.0.1:{port}{public_prefix}/"
            deadline = time.monotonic() + 60
            while True:
                try:
                    with urllib.request.urlopen(base + "images/png", timeout=2) as response:
                        if response.status == 200:
                            break
                except (urllib.error.URLError, TimeoutError):
                    pass
                if host.poll() is not None or time.monotonic() >= deadline:
                    log.flush()
                    log.seek(0)
                    test.fail("Functions host failed to start:\n" + log.read())
                time.sleep(0.25)
            yield base
        finally:
            if host.poll() is None:
                os.killpg(host.pid, signal.SIGTERM)
                try:
                    host.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    os.killpg(host.pid, signal.SIGKILL)
                    host.wait(timeout=10)


def get(url, data=None, headers=None, method=None):
    """The status and the decoded JSON (or None) of one request, errors included."""
    request = urllib.request.Request(url, data=data, headers=headers or {}, method=method)
    try:
        with urllib.request.urlopen(request, timeout=5) as response:
            return response.status, json.loads(response.read() or b"null")
    except urllib.error.HTTPError as error:
        with error:
            body = error.read()
        try:
            return error.code, json.loads(body or b"null")
        except ValueError:
            return error.code, None


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
                project, csproj, public_prefix, env = copy_sample(tmp, prefix)
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
                with functions_host(self, project, env, tmp, public_prefix) as base:
                    for extension, media, data in (("png", "image/png", bytes([137, 80, 78, 71])), ("jpeg", "image/jpeg", bytes([255, 216, 255, 217]))):
                        with urllib.request.urlopen(base + "images/" + extension, timeout=5) as response:
                            self.assertEqual(media, response.headers.get_content_type())
                            self.assertEqual(data, response.read())
                    with self.assertRaises(urllib.error.HTTPError) as missing:
                        urllib.request.urlopen(base + "images/gif", timeout=5)
                    with missing.exception:
                        self.assertEqual(404, missing.exception.code)
                        self.assertEqual("not_found", json.loads(missing.exception.read())["code"])
                    self.assertIn(get(base + "images/png", b"", method="POST")[0], (404, 405))
                    # The enum route value (size) and query value (fit) bind as the integers
                    # the spec advertises.
                    self.assertEqual((200, {"format": "png", "size": 1, "fit": 1, "width": 128}), get(base + "images/png/thumbnails/1?fit=1"))

    def test_worker_binding_data_on_real_host_and_checker(self):
        with tempfile.TemporaryDirectory(prefix="rivet-functions-") as tmp:
            project, csproj, public_prefix, env = copy_sample(tmp, "api")
            (project / "Probes.cs").write_text(PROBES)
            run(["dotnet", "build", str(csproj)], project, env)
            cli = ["dotnet", str(TOOL), "--project", str(csproj)]
            run([*cli, "--check"], project, env)

            # RIV4006: a parameter named after no route token is filled from the query, which
            # the contract doesn't declare, so the worker always hands it null.
            thumbnail = project / "ThumbnailFunction.cs"
            original = thumbnail.read_text()
            thumbnail.write_text(re.sub(r"\bsize\b", "thumbnailSize", original))
            misspelt = run([*cli, "--check"], project, env, expected=1)
            self.assertIn("RIV4006", misspelt.stderr)
            self.assertIn("query parameter 'thumbnailSize'", misspelt.stderr)
            thumbnail.write_text(original)

            with functions_host(self, project, env, tmp, public_prefix) as base:
                names = base + "probe/names/route"
                json_body = {"Content-Type": "application/json"}
                # A parameter with no binding attribute is filled by its name: a route value
                # first, then the query, then a JSON body's top-level property (whatever the
                # content type), each ignoring case.
                self.assertEqual((200, {"kind": "route", "other": "query", "term": "query"}), get(names + "?other=query&TERM=query"))
                self.assertEqual((200, {"kind": "route", "other": None, "term": None}), get(names + "?kind=query"))
                self.assertEqual((200, {"kind": "route", "other": "body", "term": "query"}), get(names + "?term=query", b'{"Other":"body","term":"body"}', json_body, "POST"))
                self.assertEqual((200, {"kind": "route", "other": None, "term": "body"}), get(names, b'{"term":"body"}', {"Content-Type": "text/plain"}, "POST"))
                self.assertEqual((200, {"kind": "Route"}), get(base + "probe/case/Route"))
                # Headers, cookies, and form fields never reach a parameter, and a name nothing
                # carries is null.
                self.assertEqual((200, {"kind": "route", "other": None, "term": None}), get(names, headers={"other": "header", "term": "header"}))
                self.assertEqual((200, {"kind": "route", "other": None, "term": None}), get(names, headers={"Cookie": "other=cookie; term=cookie"}))
                self.assertEqual((200, {"kind": "route", "other": None, "term": None}), get(names, b"other=form&term=form", {"Content-Type": "application/x-www-form-urlencoded"}, "POST"))
                # A value type nothing carries fails the invocation.
                self.assertEqual(500, get(base + "probe/count")[0])
                self.assertEqual((200, {"count": 5}), get(base + "probe/count?count=5"))
                # RIV4005: the worker parses an enum's route or query value as JSON, so an
                # integer binds and a member name, quoted or not, fails the invocation.
                self.assertEqual((200, {"size": "Medium"}), get(base + "probe/enum/1"))
                self.assertEqual((200, {"size": "Large"}), get(base + "probe/enum?size=2"))
                for refused in ("probe/enum/Large", "probe/enum/%22Large%22", "probe/enum?size=Large", "probe/enum?size=%22Large%22"):
                    self.assertEqual(500, get(base + refused)[0], refused)
                # The sample's own enum inputs follow the same rules.
                self.assertEqual(500, get(base + "images/png/thumbnails/Large?fit=1")[0])
                self.assertEqual(500, get(base + "images/png/thumbnails/1?fit=Contain")[0])
                status, error = get(base + "images/png/thumbnails/7?fit=1")
                self.assertEqual((404, "not_found"), (status, error["code"]))
