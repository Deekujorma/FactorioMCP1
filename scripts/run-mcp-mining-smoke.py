"""Bounded, evidence-producing MCP smoke test for the isolated Factorio world.

It uses only FactorioMCP's normal player-navigation and tick-based mining tools.
It neither teleports the player nor creates/inserts resources.
"""
from __future__ import annotations

import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time
from typing import Any

ROOT = Path(r"C:\FactorioMCP1")
LOCAL = ROOT / ".local"
EVIDENCE = LOCAL / "evidence"
STAMP = time.strftime("%Y%m%dT%H%M%SZ", time.gmtime())
REPORT_PATH = EVIDENCE / f"mcp-mining-smoke-{STAMP}.json"


class McpClient:
    def __init__(self) -> None:
        password = (LOCAL / "factorio" / "rcon-password.txt").read_text(encoding="ascii").strip()
        env = os.environ | {
            "FACTORIO_RCON_HOST": "127.0.0.1",
            "FACTORIO_RCON_PORT": "27015",
            "FACTORIO_RCON_PASSWORD": password,
            "FACTORIO_GOALS_FILE": str(LOCAL / "data" / "goals.json"),
            "FACTORIO_BUILDINGS_FILE": str(LOCAL / "data" / "buildings.json"),
            "FACTORIO_SCRIPT_OUTPUT_DIR": str(LOCAL / "factorio" / "script-output"),
        }
        self.process = subprocess.Popen(
            ["dotnet", str(ROOT / "FactorioMCP" / "bin" / "Release" / "net9.0" / "FactorioMCP.dll")],
            cwd=ROOT, env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, bufsize=1,
        )
        self.messages: queue.Queue[dict[str, Any]] = queue.Queue()
        self.stderr: list[str] = []
        threading.Thread(target=self._read_stdout, daemon=True).start()
        threading.Thread(target=self._read_stderr, daemon=True).start()
        self.request_id = 0

    def _read_stdout(self) -> None:
        assert self.process.stdout
        for line in self.process.stdout:
            if line.strip():
                self.messages.put(json.loads(line))

    def _read_stderr(self) -> None:
        assert self.process.stderr
        self.stderr.extend(self.process.stderr.readlines())

    def send(self, message: dict[str, Any]) -> None:
        assert self.process.stdin
        self.process.stdin.write(json.dumps(message) + "\n")
        self.process.stdin.flush()

    def request(self, method: str, params: dict[str, Any] | None = None, timeout: float = 90) -> dict[str, Any]:
        self.request_id += 1
        request_id = self.request_id
        self.send({"jsonrpc": "2.0", "id": request_id, "method": method, "params": params or {}})
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            try:
                result = self.messages.get(timeout=0.5)
            except queue.Empty:
                continue
            if result.get("id") == request_id:
                if "error" in result:
                    raise RuntimeError(f"{method}: {result['error']}")
                return result["result"]
        raise TimeoutError(f"Timed out waiting for {method}")

    def start(self) -> list[dict[str, Any]]:
        self.request("initialize", {
            "protocolVersion": "2025-03-26", "capabilities": {},
            "clientInfo": {"name": "factorio-mcp-mining-smoke", "version": "1"},
        })
        self.send({"jsonrpc": "2.0", "method": "notifications/initialized", "params": {}})
        return self.request("tools/list", timeout=30)["tools"]

    def tool(self, name: str, arguments: dict[str, Any] | None = None, timeout: float = 90) -> tuple[Any, list[dict[str, Any]]]:
        result = self.request("tools/call", {"name": name, "arguments": arguments or {}}, timeout)
        content = result.get("content", [])
        text = "\n".join(c.get("text", "") for c in content if c.get("type") == "text")
        try:
            return json.loads(text), content
        except json.JSONDecodeError:
            return text, content

    def close(self) -> None:
        self.process.terminate()
        try:
            self.process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait(timeout=10)


def iron_count(inventory: Any) -> int:
    if isinstance(inventory, dict):
        if isinstance(inventory.get("items"), list):
            for item in inventory["items"]:
                if item.get("name") == "iron-ore":
                    return int(item.get("count", 0))
        return int(inventory.get("iron-ore", 0))
    return 0


def main() -> int:
    EVIDENCE.mkdir(parents=True, exist_ok=True)
    (EVIDENCE / "screenshots").mkdir(parents=True, exist_ok=True)
    client = McpClient()
    report: dict[str, Any] = {"started_utc": STAMP, "constraints": {"teleport": False, "item_insertion": False, "ore_spawning": False}}
    try:
        tools = client.start()
        report["mcp_tool_count"] = len(tools)
        required = {"get_player_position", "get_inventory", "scan_resources", "move_to_entity", "find_nearest", "mine_resource"}
        report["required_tools_present"] = required <= {tool["name"] for tool in tools}
        report["before_position"], _ = client.tool("get_player_position")
        report["before_inventory"], _ = client.tool("get_inventory")
        report["iron_scan"], _ = client.tool("scan_resources", {"radius": 200})
        report["move_to_iron"], _ = client.tool("move_to_entity", {"entityType": "iron-ore", "radius": 200, "tolerance": 2.0, "timeoutSeconds": 60}, timeout=90)
        report["mining_attempts"] = []
        mined_total = 0
        for _ in range(3):
            if mined_total >= 10:
                break
            target, _ = client.tool("find_nearest", {"entityType": "iron-ore", "radius": 10})
            if not target.get("success"):
                break
            distance, _ = client.tool("check_distance", {"x": target["x"], "y": target["y"]})
            mining, _ = client.tool("mine_resource", {"x": target["x"], "y": target["y"], "count": 10 - mined_total, "pollIntervalSeconds": 0.25, "timeoutSeconds": 60}, timeout=90)
            report["mining_attempts"].append({"target": target, "distance": distance, "result": mining})
            mined = max(0, int(mining.get("mined", 0)))
            mined_total += mined
            if mined == 0:
                break
        report["nearest_iron"] = report["mining_attempts"][0]["target"] if report["mining_attempts"] else None
        report["distance_before_mining"] = report["mining_attempts"][0]["distance"] if report["mining_attempts"] else None
        report["mining"] = {"success": mined_total >= 10, "mined": mined_total, "requested": 10, "attempts": len(report["mining_attempts"])}
        report["after_position"], _ = client.tool("get_player_position")
        report["after_inventory"], _ = client.tool("get_inventory")
        # Do not capture the desktop: unrelated windows can expose private information.
        # Factorio's built-in `game.take_screenshot` writes outside this isolated write-data tree,
        # so visual capture is intentionally left to an explicitly supervised future action.
        report["screenshot"] = None
        report["screenshot_disposition"] = "not captured: isolation safeguard"
        before = iron_count(report["before_inventory"])
        after = iron_count(report["after_inventory"])
        report["iron_ore_before"] = before
        report["iron_ore_after"] = after
        report["iron_ore_added"] = after - before
        report["success"] = (
            report["required_tools_present"]
            and report["mining"].get("success") is True
            and report["mining"].get("mined", 0) >= 10
            and report["iron_ore_added"] >= 10
        )
    except Exception as exc:
        report["success"] = False
        report["error"] = f"{type(exc).__name__}: {exc}"
    finally:
        client.close()
        report["mcp_stderr_tail"] = client.stderr[-20:]
        report["finished_utc"] = time.strftime("%Y%m%dT%H%M%SZ", time.gmtime())
        REPORT_PATH.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"success": report["success"], "report": str(REPORT_PATH), "screenshot": report.get("screenshot"), "iron_ore_added": report.get("iron_ore_added")}, indent=2))
    return 0 if report["success"] else 1


if __name__ == "__main__":
    sys.exit(main())
