import json, os, queue, subprocess, threading, time, datetime
from pathlib import Path

root = Path(r'C:/FactorioMCP1')
local = root / '.local'
env = os.environ.copy()
env.update({
    'FACTORIO_RCON_HOST': '127.0.0.1',
    'FACTORIO_RCON_PORT': '27015',
    'FACTORIO_RCON_PASSWORD': (local / 'factorio/rcon-password.txt').read_text(encoding='ascii').strip(),
    'FACTORIO_GOALS_FILE': str(local / 'data/goals.json'),
    'FACTORIO_BUILDINGS_FILE': str(local / 'data/buildings.json'),
    'FACTORIO_SCRIPT_OUTPUT_DIR': str(local / 'factorio/script-output'),
})
p = subprocess.Popen(
    ['dotnet', str(root / 'FactorioMCP/bin/Release/net9.0/FactorioMCP.dll')],
    cwd=str(root), env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
    stderr=subprocess.DEVNULL, text=True, bufsize=1)
q = queue.Queue()


def readout():
    for line in p.stdout:
        if line.strip():
            q.put(json.loads(line))


threading.Thread(target=readout, daemon=True).start()
_counter = {'id': 0}


def next_id():
    _counter['id'] += 1
    return _counter['id']


def req(method, params, timeout=180):
    i = next_id()
    p.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': i, 'method': method, 'params': params}) + '\n')
    p.stdin.flush()
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            r = q.get(timeout=.5)
        except queue.Empty:
            continue
        if r.get('id') == i:
            if 'error' in r:
                raise RuntimeError(r['error'])
            return r['result']
    raise TimeoutError(method)


def call(name, args, timeout=180):
    res = req('tools/call', {'name': name, 'arguments': args}, timeout)
    return [x.get('text') for x in res.get('content', []) if x.get('type') == 'text']


log = []
t0 = int(time.time())


def step(label, name, args, timeout=180):
    rec = {
        'tool': name,
        'args': args,
        'elapsed_s': None,
        'ts': datetime.datetime.now(datetime.timezone.utc).isoformat(),
    }
    t_start = time.time()
    try:
        rec['result'] = call(name, args, timeout)
    except Exception as ex:  # keep going, record failure
        rec['result'] = [{'exception': str(ex)}]
    rec['elapsed_s'] = round(time.time() - t_start, 2)
    log.append(rec)
    print(json.dumps({'step': label, 'result': rec['result'], 'elapsed_s': rec['elapsed_s']}), flush=True)


try:
    req('initialize', {'protocolVersion': '2025-03-26', 'capabilities': {}, 'clientInfo': {'name': 'smelt-run', 'version': '1'}})
    step('player_position', 'get_player_position', {})
    step('inventory_before', 'get_inventory', {})
    step('furnace_fuel_slot', 'get_entity_inventory', {'x': 9, 'y': -32, 'inventoryType': 'fuel'})
    step('insert_ore', 'insert_items', {'x': 9, 'y': -32, 'itemName': 'iron-ore', 'count': 1, 'inventoryType': 'furnace_source'})
    step('wait_for_plate', 'wait_for_entity_inventory',
         {'x': 9, 'y': -32, 'itemName': 'iron-plate', 'targetCount': 1, 'inventoryType': 'furnace_result',
          'pollIntervalSeconds': 2.0, 'timeoutSeconds': 120.0}, timeout=200)
    step('output_slot_before_collect', 'get_entity_inventory', {'x': 9, 'y': -32, 'inventoryType': 'furnace_result'})
    step('collect_plates', 'remove_items', {'x': 9, 'y': -32, 'itemName': 'iron-plate', 'count': 10, 'inventoryType': 'furnace_result'})
    step('inventory_after', 'get_inventory', {})
finally:
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ')
    ev = {'task': 'produce_and_collect_one_iron_plate', 'started_unix': t0, 'steps': log}
    outp = local / 'evidence' / f'iron-plate-smelt-{stamp}.json'
    outp.write_text(json.dumps(ev, indent=2), encoding='utf-8')
    print('EVIDENCE:', outp, flush=True)
    p.terminate()
    try:
        p.wait(timeout=10)
    except subprocess.TimeoutExpired:
        p.kill()
