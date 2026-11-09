# Local isolated FactorioMCP setup

This checkout uses an isolated Factorio write-data directory at `C:\FactorioMCP1\.local\factorio`. It does not modify the normal Factorio saves, configuration, mods, or installation.

## Start and stop

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\create-test-world.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-test-world.ps1
python .\scripts\run-mcp-mining-smoke.py
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\stop-test-world.ps1
```

The world uses seed `424242`, disabled enemy evolution/expansion, localhost RCON on port `27015`, and a generated password in `.local\factorio\rcon-password.txt`. That directory is ignored by Git.

## Pi MCP

`.pi/mcp.json` defines a project-local deferred stdio server named `factorio_mcp`. Its password environment variable is populated by a local PowerShell read command; no password is stored in the committed MCP configuration.

From this directory, verify the server after starting Factorio:

```powershell
pi mcp list
```

## Manual model choice

Pi has not been switched from its configured OpenAI/Codex model. For a later, deliberate Ollama session, start Pi explicitly with:

```powershell
pi --model ollama/qwen3.8:27b
```

Return explicitly to the configured Codex model with:

```powershell
pi --model openai/gpt-6-luna
```

These commands select a model for the launched session only; they do not edit Pi's global default. Do not run the Ollama session concurrently with a GPU workload unless a safe handoff has been verified.

## Evidence

The smoke script writes JSON evidence under `.local\evidence`. It proves normal pathfinding movement to iron, reach-range validation, tick-based mining, and a positive inventory delta. Desktop capture is deliberately disabled because unrelated windows can expose private information; Factorio's built-in screenshot output is also not used because it does not honor this isolated write-data location.
