"""Opt-in stdio MCP screenshot smoke test; use only a disposable graphical world.

Build Debug first. Configure FACTORIO_RCON_* and FACTORIO_SCREENSHOT_* in the
parent environment. Does not create/load/modify saves or inject game items.
"""
import base64
import json
import os
from pathlib import Path
import queue
import subprocess
import threading
import time

root = Path(__file__).resolve().parents[1]
messages = queue.Queue()
process = subprocess.Popen(
    ["dotnet", str(root / "FactorioMCP/bin/Debug/net9.0/FactorioMCP.dll")],
    cwd=root, env=os.environ.copy(), stdin=subprocess.PIPE, stdout=subprocess.PIPE,
    stderr=subprocess.DEVNULL, text=True, bufsize=1,
)


def read_stdout():
    for line in process.stdout:
        if line.strip():
            messages.put(json.loads(line))


def request(number, method, params):
    process.stdin.write(json.dumps({"jsonrpc": "2.0", "id": number, "method": method, "params": params}) + "\n")
    process.stdin.flush()
    deadline = time.monotonic() + 90
    while time.monotonic() < deadline:
        try:
            message = messages.get(timeout=0.5)
        except queue.Empty:
            if process.poll() is not None:
                raise RuntimeError("MCP process exited")
            continue
        if message.get("id") == number:
            if "error" in message:
                raise RuntimeError(message["error"])
            return message["result"]
    raise TimeoutError(method)


try:
    threading.Thread(target=read_stdout, daemon=True).start()
    request(1, "initialize", {"protocolVersion": "2025-03-26", "capabilities": {},
                              "clientInfo": {"name": "screenshot-smoke", "version": "1"}})
    process.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
    process.stdin.flush()
    for capture in range(2):
        result = request(capture + 2, "tools/call", {"name": "take_screenshot", "arguments": {}})
        images = [block for block in result.get("content", []) if block["type"] == "image"]
        texts = [block["text"] for block in result.get("content", []) if block["type"] == "text"]
        assert len(images) == 1, texts
        assert images[0]["mimeType"] == "image/jpeg"
        assert base64.b64decode(images[0]["data"], validate=True).startswith(b"\xff\xd8")
        legend = next(text for text in texts if text.startswith("## Map Legend\n\n"))
        parsed = json.loads(legend.removeprefix("## Map Legend\n\n"))
        assert "entities" in parsed and "entity_count" in parsed
        print(json.dumps({"capture": capture + 1, "image": "JPEG", "entities": parsed["entity_count"]}))
finally:
    process.terminate()
    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait()
