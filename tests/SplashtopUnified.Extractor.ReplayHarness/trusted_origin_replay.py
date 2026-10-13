#!/usr/bin/env python3
"""Trusted-origin CDP replay for the linked production extractor and fixtures."""
from __future__ import annotations
import base64
import json
import os
from pathlib import Path
import secrets
import socket
import struct
import subprocess
import sys
import time
import urllib.parse
import urllib.request

ORIGIN = "https://my.splashtop.com"

class WebSocket:
    def __init__(self, url: str):
        parsed = urllib.parse.urlparse(url)
        self.sock = socket.create_connection((parsed.hostname, parsed.port or 80), timeout=10)
        self.sock.settimeout(0.2)
        key = base64.b64encode(secrets.token_bytes(16)).decode()
        ws_path = parsed.path + (f"?{parsed.query}" if parsed.query else "")
        request = (f"GET {ws_path} HTTP/1.1\r\nHost: {parsed.hostname}:{parsed.port}\r\n"
                   f"Upgrade: websocket\r\nConnection: Upgrade\r\nOrigin: http://localhost\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n")
        self.sock.sendall(request.encode("ascii"))
        headers = bytearray()
        while not headers.endswith(b"\r\n\r\n"):
            byte = self.sock.recv(1)
            if not byte:
                raise RuntimeError("CDP websocket closed during handshake")
            headers.extend(byte)
            if len(headers) > 16384:
                raise RuntimeError("oversized CDP websocket handshake")
        if b" 101 " not in headers:
            detail = self.sock.recv(4096)
            raise RuntimeError(f"CDP websocket handshake refused: {headers[:200]!r} {detail!r}")
        self.next_id = 0
        self.events: list[dict] = []

    def _read(self, count: int) -> bytes:
        data = bytearray()
        while len(data) < count:
            if time.monotonic() >= self.receive_deadline:
                raise TimeoutError("CDP response deadline exceeded")
            try:
                chunk = self.sock.recv(count - len(data))
            except socket.timeout:
                continue
            if not chunk:
                raise RuntimeError("CDP websocket closed")
            data.extend(chunk)
        return bytes(data)

    def _send_frame(self, opcode: int, payload: bytes):
        length = len(payload)
        head = bytearray([0x80 | opcode])
        if length < 126:
            head.append(0x80 | length)
        elif length <= 0xFFFF:
            head.append(0x80 | 126)
            head.extend(struct.pack("!H", length))
        else:
            head.append(0x80 | 127)
            head.extend(struct.pack("!Q", length))
        mask = secrets.token_bytes(4)
        head.extend(mask)
        masked = bytes(value ^ mask[index & 3] for index, value in enumerate(payload))
        self.sock.sendall(head + masked)

    def send(self, method: str, params: dict | None = None) -> int:
        self.next_id += 1
        command_id = self.next_id
        body = json.dumps({"id": command_id, "method": method, "params": params or {}}).encode()
        self._send_frame(1, body)
        return command_id

    def receive(self, deadline: float) -> dict:
        self.receive_deadline = deadline
        while True:
            first, second = self._read(2)
            opcode = first & 0x0F
            length = second & 0x7F
            if length == 126:
                length = struct.unpack("!H", self._read(2))[0]
            elif length == 127:
                length = struct.unpack("!Q", self._read(8))[0]
            masked = bool(second & 0x80)
            mask = self._read(4) if masked else b""
            payload = self._read(length)
            if masked:
                payload = bytes(value ^ mask[index & 3] for index, value in enumerate(payload))
            if opcode == 9:
                self._send_frame(10, payload)
                continue
            if opcode == 8:
                raise RuntimeError("CDP websocket received close")
            if opcode == 1:
                return json.loads(payload)

    def call(self, method: str, params: dict | None = None, timeout: float = 15.0) -> dict:
        command_id = self.send(method, params)
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            message = self.receive(deadline)
            if message.get("method"):
                self.events.append(message)
                continue
            if message.get("id") == command_id:
                if "error" in message:
                    raise RuntimeError(f"CDP {method}: {message['error']}")
                return message.get("result", {})
        raise TimeoutError(f"CDP command timed out: {method}")


def main() -> int:
    if len(sys.argv) != 3:
        print("Usage: trusted_origin_replay.py <generated-fixtures-dir> <chromium-path>", file=sys.stderr)
        return 2
    fixture_dir = Path(sys.argv[1]).resolve()
    chromium = str(Path(sys.argv[2]).resolve())
    fixtures = json.loads((fixture_dir / "manifest.json").read_text())
    html_by_name = {name: (fixture_dir / f"{name}.html").read_text() for name in fixtures}
    runtime_exceptions: list[str] = []
    delivered_routes: list[str] = []
    navigations = 0
    ws: WebSocket | None = None

    def event_handler(message: dict):
        assert ws is not None
        method = message.get("method")
        params = message.get("params", {})
        if method == "Runtime.exceptionThrown":
            details = params.get("exceptionDetails", {})
            runtime_exceptions.append(details.get("text") or details.get("exception", {}).get("description", "unknown runtime exception"))
        elif method == "Fetch.requestPaused":
            request_id = params["requestId"]
            url = params.get("request", {}).get("url", "")
            path = urllib.parse.urlparse(url).path.rsplit("/", 1)[-1]
            name = path[:-5] if path.endswith(".html") else ""
            if params.get("resourceType") == "Document" and name in html_by_name and url == f"{ORIGIN}/{name}.html":
                body = base64.b64encode(html_by_name[name].encode()).decode()
                delivered_routes.append(name)
                ws.send("Fetch.fulfillRequest", {"requestId": request_id, "responseCode": 200,
                    "responseHeaders": [{"name": "Content-Type", "value": "text/html; charset=utf-8"},
                                        {"name": "Cache-Control", "value": "no-store"}], "body": body})
            else:
                ws.send("Fetch.failRequest", {"requestId": request_id, "errorReason": "BlockedByClient"})

    def pump_until_response(command_id: int, timeout: float = 60.0) -> dict:
        assert ws is not None
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            message = ws.receive(deadline)
            if message.get("method"):
                ws.events.append(message)
                event_handler(message)
            elif message.get("id") == command_id:
                if "error" in message:
                    raise RuntimeError(f"CDP command failed: {message['error']}")
                return message.get("result", {})
        raise TimeoutError(f"CDP command {command_id} timed out")

    def call(method: str, params: dict | None = None, timeout: float = 15.0) -> dict:
        assert ws is not None
        return pump_until_response(ws.send(method, params), timeout)

    def evaluate(expression: str, await_promise: bool = False, timeout: float = 12.0) -> object:
        result = call("Runtime.evaluate", {"expression": expression, "returnByValue": True,
                      "awaitPromise": await_promise, "userGesture": False}, timeout)
        if result.get("exceptionDetails"):
            details = result["exceptionDetails"]
            raise RuntimeError(details.get("text", "Runtime.evaluate exception"))
        remote = result.get("result", {})
        return remote.get("value")

    # Private, unique profile and denied DNS prevent fallback network access; the only
    # document bytes enter through Fetch.fulfillRequest from this process.
    root = Path(os.environ.get("TMPDIR", str(fixture_dir / ".browser-profiles")))
    root.mkdir(parents=True, exist_ok=True)
    profile = root / f"chromium-trusted-replay-{os.getpid()}-{secrets.token_hex(5)}"
    profile.mkdir()
    log_path = profile / "chromium.log"
    args = [chromium, "--headless=new", "--no-sandbox", "--disable-gpu", "--disable-extensions",
            "--disable-background-networking", "--disable-component-update", "--disable-sync",
            "--no-first-run", "--no-default-browser-check", "--no-proxy-server",
            "--host-resolver-rules=MAP * ~NOTFOUND", "--remote-debugging-port=0", "--remote-allow-origins=*",
            f"--user-data-dir={profile}", "about:blank"]
    process = None
    try:
        with log_path.open("wb") as log:
            process = subprocess.Popen(args, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
        active_port = profile / "DevToolsActivePort"
        deadline = time.monotonic() + 15
        while not active_port.exists() and time.monotonic() < deadline:
            if process.poll() is not None:
                raise RuntimeError(f"Chromium exited early ({process.returncode}); see {log_path}")
            time.sleep(0.05)
        if not active_port.exists():
            raise TimeoutError(f"Chromium CDP startup timed out; see {log_path}")
        port = int(active_port.read_text().splitlines()[0])
        request = urllib.request.Request(f"http://127.0.0.1:{port}/json/new?about:blank", data=b"", method="PUT")
        target = json.loads(urllib.request.urlopen(request, timeout=5).read())
        ws = WebSocket(target["webSocketDebuggerUrl"])
        call("Page.enable")
        call("Runtime.enable")
        call("Fetch.enable", {"patterns": [{"urlPattern": "*", "requestStage": "Request"}]})
        preload = """(()=>{window.__replayPosts=[];window.__replayErrors=[];window.addEventListener('error',e=>window.__replayErrors.push(String(e.message||'error')));window.addEventListener('unhandledrejection',e=>window.__replayErrors.push(String(e.reason||'rejection')));window.chrome=window.chrome||{};window.chrome.webview={postMessage:m=>window.__replayPosts.push(String(m))};})()"""
        call("Page.addScriptToEvaluateOnNewDocument", {"source": preload})
        report = []
        for name in fixtures:
            # All network requests, including the top-level HTTPS page, must be fulfilled
            # from this exact generated fixture or explicitly rejected.
            event_mark = len(delivered_routes)
            runtime_exceptions.clear()
            navigations += 1
            call("Page.navigate", {"url": f"{ORIGIN}/{name}.html"}, timeout=20)
            ready_deadline = time.monotonic() + 10
            while time.monotonic() < ready_deadline:
                if evaluate("document.readyState") == "complete":
                    break
                time.sleep(0.05)
            else:
                raise TimeoutError(f"document readiness timed out for {name}")
            # Initial hydration is checked independently from document readiness.
            hydration_deadline = time.monotonic() + 2
            while time.monotonic() < hydration_deadline:
                count = evaluate("document.querySelectorAll('table tbody > tr').length")
                if isinstance(count, int) and count > 0:
                    break
                if name in {"unsupported", "login"}:
                    break
                time.sleep(0.025)
            # Diagnostics script returns a JSON string from production; row data stays on this synthetic page.
            raw_diagnostics = evaluate((fixture_dir / "diagnostics.js").read_text())
            diagnostics = json.loads(raw_diagnostics) if isinstance(raw_diagnostics, str) else {}
            script = (fixture_dir / "extract.js").read_text()
            call("Runtime.evaluate", {"expression": script, "awaitPromise": True, "returnByValue": True}, timeout=75)
            page_state = evaluate("JSON.stringify({posts:window.__replayPosts||[],errors:window.__replayErrors||[],scrollEvents:window.__reconcileScrolls||0})")
            if not isinstance(page_state, str):
                raise RuntimeError(f"invalid recorder state for {name}")
            state = json.loads(page_state)
            parsed_posts = []
            for raw in state["posts"]:
                try:
                    parsed_posts.append(json.loads(raw))
                except json.JSONDecodeError:
                    pass
            finals = [p for p in parsed_posts if p.get("source") == "splashtop-console-inventory"]
            progress = [p.get("rowsRead") for p in parsed_posts if p.get("source") == "splashtop-inventory-progress"]
            final = finals[-1] if finals else None
            report.append({"fixture": name, "deliveredLocally": len(delivered_routes) > event_mark,
                           "diagnostics": diagnostics, "final": final, "progressPosts": len(progress),
                           "maxProgressRows": max(progress) if progress else 0,
                           "scrollEvents": state["scrollEvents"],
                           "pageErrors": state["errors"], "runtimeExceptions": list(runtime_exceptions)})
        # Connect locating pass. Production runs the same walk (extract) first and then
        # locates and clicks through the production read/click steps; the host's page and
        # window traversal is proven by the Windows smoke, so this replay pins the
        # single-step contract on identities the initial document renders.
        connect_report = []
        connect_manifest_path = fixture_dir / "connect-manifest.json"
        if connect_manifest_path.exists():
            instrumentation = ("window.__replayClicks=0;"
                               "document.addEventListener('click',function(e){"
                               "var n=e.target;if(n&&n.closest&&n.closest('button,a,[role=button]'))window.__replayClicks++;},true);")
            for target in json.loads(connect_manifest_path.read_text()):
                runtime_exceptions.clear()
                navigations += 1
                call("Page.navigate", {"url": target["url"]}, timeout=20)
                ready_deadline = time.monotonic() + 10
                while time.monotonic() < ready_deadline:
                    if evaluate("document.readyState") == "complete":
                        break
                    time.sleep(0.05)
                hydration_deadline = time.monotonic() + 2
                while time.monotonic() < hydration_deadline:
                    count = evaluate("document.querySelectorAll('table tbody > tr').length")
                    if isinstance(count, int) and count > 0:
                        break
                    time.sleep(0.025)
                call("Runtime.evaluate", {"expression": (fixture_dir / "extract.js").read_text(),
                                          "awaitPromise": True, "returnByValue": True}, timeout=75)
                evaluate(instrumentation)
                read_raw = evaluate((fixture_dir / target["readScript"]).read_text())
                read_state = json.loads(read_raw) if isinstance(read_raw, str) else {}
                token = read_state.get("documentToken")
                click_script = (fixture_dir / target["clickScript"]).read_text().replace(
                    '"documentToken":-1', f'"documentToken":{token if isinstance(token, (int, float)) else 0}')
                click_error = None
                try:
                    click_raw = evaluate(click_script)
                except RuntimeError as error:
                    click_raw = None
                    click_error = str(error)
                click_state = json.loads(click_raw) if isinstance(click_raw, str) else {}
                clicks = evaluate("window.__replayClicks||0")
                connect_report.append({"target": target["target"], "expected": target["expected"],
                                       "readStatus": read_state.get("status"),
                                       "targetCount": read_state.get("targetCount"),
                                       "targetControlCounts": read_state.get("targetControlCounts"),
                                       "documentToken": token,
                                       "status": click_state.get("status"),
                                       "clickError": click_error,
                                       "clicks": clicks if isinstance(clicks, int) else -1,
                                       "pageErrors": evaluate("JSON.stringify(window.__replayErrors||[])"),
                                       "runtimeExceptions": list(runtime_exceptions)})
            (fixture_dir / "connect-results.json").write_text(json.dumps(connect_report, indent=2) + "\n")

        if len(delivered_routes) != navigations:
            raise AssertionError(f"only {len(delivered_routes)}/{navigations} navigated documents were locally fulfilled")
        (fixture_dir / "replay-results.json").write_text(json.dumps(report, indent=2) + "\n")

        expected = {
            "basic": ("incomplete", 5, "single", None),
            "paged": ("complete", 9, "paged", 9),
            "paged-duplicates": ("complete", 4, "paged", None),
            "virtual-identical": ("incomplete", 5, "scrolled", None),
            "virtual-18": ("complete", 18, "scrolled", 18),
            "virtual-240": ("complete", 240, "scrolled", 240),
            "no-total-6014": ("complete", 136, "scrolled", None),
            "no-total-25508": ("complete", 579, "scrolled", None),
            "no-total-distant-duplicate": ("incomplete", 135, "scrolled", None),
            "short-viewport": ("complete", 24, "scrolled", 24),
            "slow-repaint": ("incomplete", None, "scrolled", 24),
            "nested-changing-status": ("complete", 48, "scrolled", 48),
            "reconciled-scrollable": ("complete", 5, "single", 5),
            "reordered": ("incomplete", 1, "single", None),
            "nested-decorative": ("incomplete", 2, "single", None),
            "ambiguous-headers": ("unavailable", 0, "none", None),
            "chooser": ("incomplete", 1, "single", None),
            "unsupported": ("unavailable", 0, "none", None),
            "login": ("unavailable", 0, "none", None),
        }
        failures = []
        for result in report:
            name, final = result["fixture"], result["final"]
            if final is None:
                failures.append(f"{name}: missing correlated final payload")
                continue
            wanted_outcome, wanted_count, wanted_mode, wanted_total = expected[name]
            actual = (final.get("outcome"), final.get("rowCount"), final.get("mode"), final.get("reportedTotal"))
            if wanted_count is None:
                matches = actual[0] == wanted_outcome and actual[1] < 24 and actual[2:] == (wanted_mode, wanted_total)
            else:
                matches = actual == (wanted_outcome, wanted_count, wanted_mode, wanted_total)
            if not result["deliveredLocally"]:
                matches = False
                failures.append(f"{name}: document was not fulfilled locally")
            if result["pageErrors"] or result["runtimeExceptions"]:
                matches = False
                failures.append(f"{name}: browser errors: {result['pageErrors']!r} {result['runtimeExceptions']!r}")
            if name == "login" and final.get("pageKind") != "login":
                matches = False
            if name == "ambiguous-headers" and (final.get("pageKind") != "unknown" or final.get("rows")):
                matches = False
            if name == "reconciled-scrollable" and result["scrollEvents"] != 0:
                matches = False
            if name == "virtual-identical" and not final.get("ambiguousDuplicates"):
                matches = False
            if name == "no-total-distant-duplicate" and not final.get("ambiguousDuplicates"):
                matches = False
            if name == "no-total-6014" and not any(row.get("name") == "Fixture Node 136" for row in final.get("rows", [])):
                matches = False
            if name == "no-total-25508" and not any(row.get("name") == "Fixture Node 579" for row in final.get("rows", [])):
                matches = False
            if name == "virtual-18" and not any(row.get("name") == "Fixture Node 18" for row in final.get("rows", [])):
                matches = False
            if name == "virtual-240" and (not any(row.get("name") == "Fixture Node 240" for row in final.get("rows", [])) or final.get("walkMillis", 999999) >= 12000):
                matches = False
            if name == "short-viewport" and not any(row.get("name") == "Fixture Node 24" for row in final.get("rows", [])):
                matches = False
            if name == "paged-duplicates":
                rows = final.get("rows", [])
                exact_copies = sum(row.get("name") == "Exact duplicate" for row in rows)
                repeated_notes = {row.get("notes") for row in rows if row.get("name") == "Repeated device"}
                if exact_copies != 2 or repeated_notes != {"notes A", "notes B"}:
                    matches = False
            if name == "basic" and sum(row.get("name") == "Fixture VM" for row in final.get("rows", [])) != 2:
                matches = False
            if name == "nested-changing-status":
                rows = final.get("rows", [])
                if len({row.get("name") for row in rows}) != 48 or not any(row.get("name") == "Fixture Node 48" for row in rows):
                    matches = False
            if name == "reordered":
                rows = final.get("rows", [])
                if len(rows) != 1 or (rows[0].get("name"), rows[0].get("deviceName"), rows[0].get("group"), rows[0].get("notes")) != (
                        "Fixture Computer", "fixture-device", "Fixture Group", ""):
                    matches = False
            if name == "nested-decorative" and [row.get("name") for row in final.get("rows", [])] != ["First fixture", "Nested target"]:
                matches = False
            if name == "no-total-6014" and (result["diagnostics"].get("tableSchemas", [{}])[0].get("renderedRows") != 16 or
                    result["diagnostics"].get("tableSchemas", [{}])[0].get("scrollAncestors", [{}])[0].get("clientHeight") != 646 or
                    result["diagnostics"].get("tableSchemas", [{}])[0].get("scrollAncestors", [{}])[0].get("scrollHeight") != 6014):
                matches = False
                failures.append("no-total-6014: generated fixture geometry drifted from 16 rows / 646 / 6014")
            if matches:
                print(f"PASS {name}: outcome={actual[0]} rows={actual[1]} mode={actual[2]} total={actual[3]} walkMs={final.get('walkMillis')}", flush=True)
            else:
                failures.append(f"{name}: expected {(wanted_outcome, wanted_count, wanted_mode, wanted_total)!r}, got {actual!r}")
                print(f"FAIL {name}: expected {(wanted_outcome, wanted_count, wanted_mode, wanted_total)!r}, got {actual!r}; pageErrors={result['pageErrors']!r}", flush=True)
        if failures:
            raise AssertionError("behavioral replay failures: " + "; ".join(failures))
        print(f"PASS all {len(report)} production fixture replays; results={fixture_dir / 'replay-results.json'}")
        return 0
    finally:
        if ws is not None:
            try:
                ws.sock.close()
            except Exception:
                pass
        if process is not None and process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
        # Retain Chromium log and evidence directory for inspection; it contains only local synthetic data.
        print(f"CHROMIUM_PROFILE={profile}", file=sys.stderr)

if __name__ == "__main__":
    raise SystemExit(main())
