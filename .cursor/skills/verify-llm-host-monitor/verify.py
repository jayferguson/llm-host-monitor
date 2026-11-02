#!/usr/bin/env python3
"""Agent-facing verification CLI for llm-host-monitor (data-driven).

The app is a Windows-only WinForms UI, so this skill proves what can be proven on
Linux without touching real hosts: it launches stub "hosts" (llama-server
/v1/models on :8080 and the GPU exporter /metrics on :9835) on spare loopback
addresses (127.77.0.N), then runs the app's real polling/parsing code
(MetricsClient.cs, MachineStore.cs, Models.cs compiled by link into a headless
.NET harness) and host/check.sh against them, and compares the results with the
expectations in checks/scenarios/*.toml. Hosts and payloads are data in
checks/app.toml and checks/fixtures/*.toml; edit data, not this file.
"""
from __future__ import annotations

import argparse
import fnmatch
import glob
import http.server
import json
import os
import re
import shutil
import signal
import socket
import subprocess
import sys
import threading
import time
import tomllib
from pathlib import Path
from typing import Any

SKILL = Path(__file__).resolve().parent
REPO = SKILL.parents[2]
CHECKS = SKILL / "checks"
SCENARIOS = CHECKS / "scenarios"
FIXTURES = CHECKS / "fixtures"
FEATURES = SKILL / "features"
EVIDENCE = SKILL / "evidence"
SCRATCH = SKILL / "scratch"
STATE = SCRATCH / "instance.state"
CFG = tomllib.loads((CHECKS / "app.toml").read_text())

# Step vocabulary: do -> (required keys, optional keys). The gate rejects anything else.
STEP_KEYS: dict[str, tuple[set[str], set[str]]] = {
    "probe": ({"hosts"}, {"expect", "expect_gpus"}),
    "parse": ({"fixture"}, {"expect_gpus", "expect_count"}),
    "check_sh": ({"host"}, {"expect_exit", "stdout_contains", "stdout_not_contains"}),
    "store": (set(), {"config_toml", "raw", "expect"}),
    "expect_http": ({"host", "port", "path"}, {"status", "contains"}),
}
COMMON_STEP_KEYS = {"do", "why", "timeout_s", "name"}
EXPECT_STEPS = {"probe", "parse", "check_sh", "store", "expect_http"}


def now() -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%S%z")


def out(obj: Any, as_json: bool) -> None:
    if as_json:
        print(json.dumps(obj, indent=2, sort_keys=True, default=str))
    else:
        for k, v in (obj.items() if isinstance(obj, dict) else [("result", obj)]):
            print(f"{k}: {v if not isinstance(v, (dict, list)) else json.dumps(v, default=str)}")


# ------------------------------------------------------------------ data
def fixture(name: str) -> dict[str, Any]:
    p = FIXTURES / f"{name}.toml"
    if not p.exists():
        raise SystemExit(f"unknown fixture {name!r} (checks/fixtures/{name}.toml)")
    return tomllib.loads(p.read_text())


def hosts_cfg() -> dict[str, dict[str, Any]]:
    return {h["name"]: h for h in CFG["hosts"]}


def scenario_files() -> dict[str, Path]:
    return {p.stem: p for p in sorted(SCENARIOS.glob("*.toml"))}


def load_scenario(fid: str) -> dict[str, Any]:
    f = scenario_files().get(fid)
    if not f:
        raise SystemExit(f"unknown feature {fid!r}; mapped: {', '.join(scenario_files())}")
    return tomllib.loads(f.read_text())


def validate_scenarios() -> list[str]:
    problems: list[str] = []
    files = scenario_files()
    hosts = hosts_cfg()
    if not files:
        problems.append("no scenarios under checks/scenarios/")
    for fid, p in files.items():
        try:
            sc = tomllib.loads(p.read_text())
        except tomllib.TOMLDecodeError as e:
            problems.append(f"{p.name}: TOML error {e}")
            continue
        for k in ("title", "feature_doc", "steps"):
            if k not in sc:
                problems.append(f"{p.name}: missing {k}")
        if sc.get("feature_doc") and not (FEATURES / sc["feature_doc"]).exists():
            problems.append(f"{p.name}: feature_doc {sc['feature_doc']} not found in features/")
        n_expect = 0
        for i, s in enumerate(sc.get("steps", [])):
            do = s.get("do")
            if do not in STEP_KEYS:
                problems.append(f"{p.name} step {i}: unknown do={do!r}")
                continue
            req, opt = STEP_KEYS[do]
            if req - set(s):
                problems.append(f"{p.name} step {i} ({do}): missing {sorted(req - set(s))}")
            extra = set(s) - req - opt - COMMON_STEP_KEYS
            if extra:
                problems.append(f"{p.name} step {i} ({do}): unknown keys {sorted(extra)}")
            refs = s.get("hosts", []) + ([s["host"]] if "host" in s else [])
            for h in refs:
                if h not in hosts:
                    problems.append(f"{p.name} step {i}: host {h!r} not in app.toml [[hosts]]")
            if "fixture" in s and not (FIXTURES / f"{s['fixture']}.toml").exists():
                problems.append(f"{p.name} step {i}: fixture {s['fixture']!r} missing")
            if do in EXPECT_STEPS and (set(s) & {"expect", "expect_gpus", "expect_count", "expect_exit",
                                                  "stdout_contains", "status", "contains"}):
                n_expect += 1
        if n_expect == 0:
            problems.append(f"{p.name}: no step asserts anything")
    for h in CFG["hosts"]:
        if h.get("fixture") and not (FIXTURES / f"{h['fixture']}.toml").exists():
            problems.append(f"app.toml host {h['name']}: fixture {h['fixture']!r} missing")
    docs = {p.name for p in FEATURES.glob("*.md")} - {"README.md"}
    used = set()
    for p in files.values():
        try:
            used.add(tomllib.loads(p.read_text()).get("feature_doc"))
        except tomllib.TOMLDecodeError:
            pass
    for d in sorted(docs - used):
        problems.append(f"features/{d} has no scenario in checks/scenarios/ (map lists a feature nothing drives)")
    readme = (FEATURES / "README.md").read_text() if (FEATURES / "README.md").exists() else ""
    for d in sorted(docs):
        if f"./{d}" not in readme:
            problems.append(f"features/README.md does not link ./{d}")
    for d in sorted(docs):
        h2 = re.findall(r"^## (.+)$", (FEATURES / d).read_text(), re.M)
        want = ["Sub-features", "How to get to it (user POV)", "Driving it with verify.py", "Gotchas"]
        if h2 != want:
            problems.append(f"features/{d}: H2s {h2} != {want}")
    return problems


# ------------------------------------------------------------------ stub hosts
def _handler(fx: dict[str, Any]):
    routes = {
        CFG["ports"]["llm"]: {"/v1/models": (fx.get("models_status", 200), fx.get("models_json", ""), "application/json")},
        CFG["ports"]["metrics"]: {"/metrics": (fx.get("metrics_status", 200), fx.get("metrics", ""), "text/plain; version=0.0.4")},
    }

    class H(http.server.BaseHTTPRequestHandler):
        def do_GET(self):  # noqa: N802
            port = self.server.server_address[1]
            st, body, ctype = routes.get(port, {}).get(self.path.split("?")[0], (404, "not found", "text/plain"))
            data = body.encode()
            self.send_response(st)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def log_message(self, fmt, *a):
            sys.stderr.write(f"{now()} {self.client_address[0]} -> {self.server.server_address} {fmt % a}\n")

    return H


def serve_stubs(plan: list[dict[str, Any]]) -> None:
    """Internal: run every stub host in this process until SIGTERM."""
    servers = []
    for h in plan:
        fx = fixture(h["fixture"])
        for port in (CFG["ports"]["llm"], CFG["ports"]["metrics"]):
            s = http.server.ThreadingHTTPServer((h["addr"], port), _handler(fx))
            servers.append(s)
            threading.Thread(target=s.serve_forever, daemon=True).start()
    print("stubs ready", flush=True)
    signal.signal(signal.SIGTERM, lambda *_: sys.exit(0))
    while True:
        time.sleep(3600)


def addr_free(addr: str) -> bool:
    for port in (CFG["ports"]["llm"], CFG["ports"]["metrics"]):
        with socket.socket() as s:
            try:
                s.bind((addr, port))
            except OSError:
                return False
    return True


def pick_addrs(n: int) -> list[str]:
    pool = [f"{CFG['launch']['addr_prefix']}{i}" for i in range(CFG["launch"]["addr_first"], CFG["launch"]["addr_last"] + 1)]
    got = [a for a in pool if addr_free(a)][:n]
    if len(got) < n:
        raise SystemExit(f"need {n} free loopback addresses with ports 8080/9835 free; found {len(got)}")
    return got


def read_state() -> dict[str, Any] | None:
    if not STATE.exists():
        return None
    st = json.loads(STATE.read_text())
    return st if pid_is_ours(st) else None


def pid_is_ours(st: dict[str, Any]) -> bool:
    try:
        cmdline = Path(f"/proc/{st['pid']}/cmdline").read_bytes().replace(b"\0", b" ").decode()
    except (OSError, KeyError):
        return False
    return st.get("marker", "\0") in cmdline


def cmd_launch(a) -> dict[str, Any]:
    st = read_state()
    if st:
        return {"ok": True, "reused": True, **st}
    hosts = CFG["hosts"]
    addrs = pick_addrs(len(hosts))
    plan = [{"name": h["name"], "addr": ad, "fixture": h.get("fixture")} for h, ad in zip(hosts, addrs)]
    serving = [p for p in plan if p["fixture"]]
    cmd = [sys.executable, str(Path(__file__)), "_serve", json.dumps(serving)]
    res = {"plan": plan, "cmd": " ".join(cmd[:3]) + " <plan>", "note": "fixture=none hosts get an address with nothing listening"}
    if a.plan:
        return {"ok": True, "dry_plan": True, **res}
    SCRATCH.mkdir(parents=True, exist_ok=True)
    log = open(SCRATCH / "stubs.log", "w")
    p = subprocess.Popen(cmd, stdout=log, stderr=log, start_new_session=True, cwd=REPO)
    deadline = time.time() + 10
    while time.time() < deadline:
        if "stubs ready" in (SCRATCH / "stubs.log").read_text():
            break
        if p.poll() is not None:
            return {"ok": False, "error": "stub process exited", "log": (SCRATCH / "stubs.log").read_text()[-2000:]}
        time.sleep(0.1)
    else:
        os.killpg(p.pid, signal.SIGTERM)
        return {"ok": False, "error": "stubs not ready in 10s"}
    st = {"pid": p.pid, "marker": "verify.py _serve", "hosts": {x["name"]: x for x in plan}, "started": now()}
    STATE.write_text(json.dumps(st, indent=2))
    return {"ok": True, **st}


def cmd_cleanup(a) -> dict[str, Any]:
    msg = "nothing running"
    if STATE.exists():
        st = json.loads(STATE.read_text())
        if pid_is_ours(st):
            try:
                os.killpg(st["pid"], signal.SIGTERM)
                for _ in range(50):
                    if not pid_is_ours(st):
                        break
                    time.sleep(0.1)
                msg = f"killed stub pid {st['pid']}"
            except ProcessLookupError:
                msg = "already gone"
        else:
            msg = "state file stale (pid not ours); not killing anything"
        STATE.unlink()
    return {"ok": True, "instance": msg, "evidence_kept": True,
            "evidence_present": EVIDENCE.exists() and any(EVIDENCE.iterdir())}


# ------------------------------------------------------------------ tools
def dotnet() -> str | None:
    for c in [os.environ.get("VERIFY_DOTNET", ""), *CFG["tools"]["dotnet_candidates"]]:
        c = os.path.expanduser(c)
        if c and (Path(c).exists() or shutil.which(c)):
            return shutil.which(c) or c
    return None


def dotnet_env() -> dict[str, str]:
    env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1", DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1")
    d = dotnet()
    if d:
        env["PATH"] = str(Path(d).parent) + os.pathsep + env.get("PATH", "")
        env.setdefault("DOTNET_ROOT", str(Path(d).parent))
    return env


def build_harness(repo: Path = REPO) -> tuple[bool, str, Path]:
    hdir = SCRATCH / "harness"
    hdir.mkdir(parents=True, exist_ok=True)
    links = "\n".join(f'    <Compile Include="{(repo / f).as_posix()}" Link="app/{f}" />' for f in CFG["harness"]["link_files"])
    (hdir / "Harness.csproj").write_text((SKILL / "harness" / "Harness.csproj.in").read_text().replace("{LINKS}", links))
    shutil.copy(SKILL / "harness" / "Program.cs", hdir / "Program.cs")
    d = dotnet()
    if not d:
        return False, "dotnet not found (set VERIFY_DOTNET or install the .NET 9 SDK)", hdir
    r = subprocess.run([d, "build", str(hdir / "Harness.csproj"), "-c", "Release", "-o", str(hdir / "out"), "-nologo", "-v", "q"],
                       capture_output=True, text=True, env=dotnet_env(), timeout=300)
    return r.returncode == 0, (r.stdout + r.stderr)[-3000:], hdir / "out" / "verify-harness.dll"


def harness(dll: Path, args: list[str], env_extra: dict[str, str] | None = None, timeout: int = 60) -> tuple[int, str, str]:
    env = dotnet_env()
    env.update(env_extra or {})
    r = subprocess.run([dotnet(), str(dll), *args], capture_output=True, text=True, env=env, timeout=timeout)
    return r.returncode, r.stdout, r.stderr


# ------------------------------------------------------------------ doctor
def cmd_doctor(a) -> dict[str, Any]:
    st = read_state()
    checks = []
    tools = {"dotnet": dotnet(), "bash": shutil.which("bash"), "curl": shutil.which("curl")}
    for k, v in tools.items():
        checks.append({"check": f"tool:{k}", "ok": bool(v), "detail": v})
    if not st:
        return {"ok": False, "owned": False, "checks": checks, "error": "no instance: run `verify.py launch` first"}
    for name, h in st["hosts"].items():
        if not h["fixture"]:
            ok = addr_free(h["addr"])
            checks.append({"check": f"{name}: nothing listening on {h['addr']}", "ok": ok})
            continue
        fx = fixture(h["fixture"])
        for port, path, key in ((CFG["ports"]["llm"], "/v1/models", "models_json"), (CFG["ports"]["metrics"], "/metrics", "metrics")):
            code, body = http_get(f"http://{h['addr']}:{port}{path}")
            ok = code == fx.get(key.replace("_json", "") + "_status", 200) and body == fx.get(key, "")
            checks.append({"check": f"{name}: {path} serves fixture {h['fixture']}", "ok": ok, "status": code})
    return {"ok": all(c["ok"] for c in checks), "owned": True, "hosts": st["hosts"], "checks": checks}


def http_get(url: str, timeout: float = 3) -> tuple[int, str]:
    import urllib.error
    import urllib.request
    if not re.match(r"^http://127\.", url):
        raise SystemExit(f"refusing non-loopback URL {url} (stubs only; see `probe-live` for read-only real hosts)")
    try:
        with urllib.request.urlopen(url, timeout=timeout) as r:
            return r.status, r.read().decode(errors="replace")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode(errors="replace")
    except OSError as e:
        return 0, str(e)


# ------------------------------------------------------------------ drive
def match(expect: dict[str, Any], got: dict[str, Any]) -> list[str]:
    """Compare expected fields with harness output. Supports *_contains, *_absent, gpu_count."""
    bad = []
    for k, v in expect.items():
        if k == "why":
            continue
        if k == "gpu_count":
            n = len(got.get("gpus") or [])
            if n != v:
                bad.append(f"gpu_count={n}, expected {v}")
        elif k.endswith("_contains"):
            f = k[: -len("_contains")]
            for piece in (v if isinstance(v, list) else [v]):
                if piece not in str(got.get(f) or ""):
                    bad.append(f"{f}={got.get(f)!r} lacks {piece!r}")
        elif k.endswith("_absent"):
            f = k[: -len("_absent")]
            if got.get(f) not in (None, "") and v:
                bad.append(f"{f}={got.get(f)!r}, expected absent")
        else:
            gv = got.get(k)
            if isinstance(v, float) and isinstance(gv, (int, float)):
                if abs(gv - v) > 1e-6:
                    bad.append(f"{k}={gv!r}, expected {v!r}")
            elif gv != v:
                bad.append(f"{k}={gv!r}, expected {v!r}")
    return bad


def toml_to_store_json(cfg: dict[str, Any]) -> str:
    def camel(k: str) -> str:
        p = k.split("_")
        return p[0] + "".join(x.title() for x in p[1:])

    def conv(o):
        if isinstance(o, dict):
            return {camel(k): conv(v) for k, v in o.items()}
        if isinstance(o, list):
            return [conv(x) for x in o]
        return o
    return json.dumps(conv(cfg), indent=2)


def run_step(s: dict[str, Any], st: dict[str, Any], dll: Path, outdir: Path, i: int) -> dict[str, Any]:
    do = s["do"]
    rec: dict[str, Any] = {"i": i, "do": do, "why": s.get("why")}
    hosts = st["hosts"]
    timeout = int(s.get("timeout_s", 60))
    if do == "probe":
        machines = [{"id": h, "name": h, "llmBaseUrl": f"http://{hosts[h]['addr']}:{CFG['ports']['llm']}",
                     "metricsUrl": f"http://{hosts[h]['addr']}:{CFG['ports']['metrics']}/metrics"} for h in s["hosts"]]
        mf = outdir / f"step{i}-machines.json"
        mf.write_text(json.dumps(machines, indent=2))
        rc, so, se = harness(dll, ["probe", str(mf)], timeout=timeout)
        rec["rc"] = rc
        if rc != 0:
            rec.update(ok=False, detail=f"harness rc={rc}: {se[-800:]}")
            return rec
        res = json.loads(so)
        (outdir / f"step{i}-probe.json").write_text(json.dumps(res, indent=2))
        bad = []
        for h, got in zip(s["hosts"], res):
            bad += [f"{h}: {b}" for b in match(s.get("expect", {}).get(h, {}), got)]
            for j, eg in enumerate(s.get("expect_gpus", {}).get(h, [])):
                g = (got.get("gpus") or [None] * (j + 1))[j] if j < len(got.get("gpus") or []) else None
                if g is None:
                    bad.append(f"{h}: gpu[{j}] missing")
                else:
                    bad += [f"{h}: gpu[{j}] {b}" for b in match(eg, g)]
        rec.update(ok=not bad, detail="; ".join(bad) or f"{len(res)} host(s) as expected", observed=res)
    elif do == "parse":
        fx = fixture(s["fixture"])
        tf = outdir / f"step{i}-metrics.txt"
        tf.write_text(fx.get("metrics", ""))
        rc, so, se = harness(dll, ["parse", str(tf)], timeout=timeout)
        if rc != 0:
            rec.update(ok=False, detail=f"harness rc={rc}: {se[-800:]}")
            return rec
        gpus = json.loads(so)
        bad = []
        if "expect_count" in s and len(gpus) != s["expect_count"]:
            bad.append(f"gpu count {len(gpus)}, expected {s['expect_count']}")
        for j, eg in enumerate(s.get("expect_gpus", [])):
            bad += ([f"gpu[{j}] missing"] if j >= len(gpus) else [f"gpu[{j}] {b}" for b in match(eg, gpus[j])])
        rec.update(ok=not bad, detail="; ".join(bad) or f"{len(gpus)} GPU(s) as expected", observed=gpus)
    elif do == "check_sh":
        h = hosts[s["host"]]
        r = subprocess.run(["bash", str(REPO / CFG["check_sh"]["path"]), h["addr"]], capture_output=True, text=True, timeout=timeout)
        (outdir / f"step{i}-check_sh.txt").write_text(f"$ host/check.sh {h['addr']}\n{r.stdout}{r.stderr}\n(exit {r.returncode})\n")
        bad = []
        if "expect_exit" in s and r.returncode != s["expect_exit"]:
            bad.append(f"exit {r.returncode}, expected {s['expect_exit']}")
        for piece in s.get("stdout_contains", []):
            if piece not in r.stdout:
                bad.append(f"stdout lacks {piece!r}")
        for piece in s.get("stdout_not_contains", []):
            if piece in r.stdout:
                bad.append(f"stdout has {piece!r}")
        rec.update(ok=not bad, detail="; ".join(bad) or f"exit {r.returncode} as expected", stdout=r.stdout[-1500:])
    elif do == "store":
        home = SCRATCH / f"store-{outdir.name}-{i}"
        shutil.rmtree(home, ignore_errors=True)
        cfgdir = home / "LlmHostMonitor"
        cfgdir.mkdir(parents=True)
        if "raw" in s:
            (cfgdir / "machines.json").write_text(s["raw"])
        elif "config_toml" in s:
            (cfgdir / "machines.json").write_text(toml_to_store_json(tomllib.loads(s["config_toml"])))
        rc, so, se = harness(dll, ["store"], {"XDG_CONFIG_HOME": str(home), "HOME": str(home)}, timeout=timeout)
        if rc != 0:
            rec.update(ok=False, detail=f"harness rc={rc}: {se[-800:]}")
            return rec
        res = json.loads(so)
        (outdir / f"step{i}-store.json").write_text(json.dumps(res, indent=2))
        cfg = res["config"]
        flat = {**cfg, "machine_count": len(cfg.get("machines") or []),
                "first_machine_id": (cfg.get("machines") or [{}])[0].get("id"),
                "config_path_in_sandbox": res["config_path"].startswith(str(home))}
        bad = match(s.get("expect", {}), flat)
        rec.update(ok=not bad, detail="; ".join(bad) or "store loaded as expected", observed=flat)
        shutil.rmtree(home, ignore_errors=True)
    elif do == "expect_http":
        h = hosts[s["host"]]
        code, body = http_get(f"http://{h['addr']}:{s['port']}{s['path']}")
        bad = []
        if code != s.get("status", 200):
            bad.append(f"status {code}, expected {s.get('status', 200)}")
        if "contains" in s and s["contains"] not in body:
            bad.append(f"body lacks {s['contains']!r}")
        rec.update(ok=not bad, detail="; ".join(bad) or f"status {code}")
    return rec


def drive(fids: list[str], st: dict[str, Any], plan_only: bool = False) -> list[dict[str, Any]]:
    results = []
    if plan_only:
        return [{"feature": f, "steps": load_scenario(f)["steps"]} for f in fids]
    ok, log, dll = build_harness()
    if not ok:
        return [{"feature": f, "ok": False, "error": "harness build failed", "build_log": log} for f in fids]
    for fid in fids:
        try:
            sc = load_scenario(fid)
        except tomllib.TOMLDecodeError as e:
            results.append({"feature": fid, "ok": False, "failed_step": None, "failed_detail": f"scenario TOML error: {e}"})
            continue
        outdir = EVIDENCE / fid
        shutil.rmtree(outdir, ignore_errors=True)
        outdir.mkdir(parents=True)
        steps, failed = [], None
        for i, s in enumerate(sc["steps"]):
            try:
                rec = run_step(s, st, dll, outdir, i)
            except Exception as e:  # a crash is a failure, never a pass
                rec = {"i": i, "do": s.get("do"), "ok": False, "detail": f"{type(e).__name__}: {e}"}
            steps.append(rec)
            if not rec["ok"]:
                failed = rec
                break
        res = {"feature": fid, "title": sc["title"], "ok": failed is None, "started": now(), "steps": steps,
               "failed_step": failed["i"] if failed else None, "failed_detail": failed["detail"] if failed else None}
        (outdir / "result.json").write_text(json.dumps(res, indent=2, default=str))
        results.append({k: res[k] for k in ("feature", "ok", "failed_step", "failed_detail")} | {"evidence": str(outdir)})
    return results


def cmd_drive(a) -> dict[str, Any]:
    fids = list(scenario_files()) if a.feature == "all" else [a.feature]
    if a.plan:
        return {"ok": True, "plan": drive(fids, {}, plan_only=True)}
    st = read_state()
    if not st:
        return {"ok": False, "error": "no owned instance: run `verify.py launch` (drive only talks to stubs this skill started)"}
    doc = cmd_doctor(a)
    if not doc["ok"]:
        return {"ok": False, "error": "doctor failed; not driving", "doctor": doc}
    res = drive(fids, st)
    return {"ok": all(r["ok"] for r in res), "dry_run": True, "results": res}


def cmd_compose(a) -> dict[str, Any]:
    la = cmd_launch(argparse.Namespace(plan=False))
    if not la["ok"]:
        return {"ok": False, "stage": "launch", "launch": la}
    try:
        dr = cmd_drive(a)
    finally:
        cl = cmd_cleanup(a)
    return {"ok": dr["ok"], "drive": dr, "cleanup": cl}


# ------------------------------------------------------------------ live (opt-in, read-only)
def cmd_probe_live(a) -> dict[str, Any]:
    """Read-only GETs against a real host the caller names. Never part of drive or gate."""
    if not a.yes_read_only:
        return {"ok": False, "error": "pass --yes-read-only to confirm: two GETs (/v1/models, /metrics) per host, nothing else"}
    r = subprocess.run(["bash", str(REPO / CFG["check_sh"]["path"]), a.host], capture_output=True, text=True, timeout=30)
    res = {"ok": r.returncode == 0, "host": a.host, "check_sh_exit": r.returncode, "check_sh": r.stdout}
    ok, log, dll = build_harness()
    if ok:
        SCRATCH.mkdir(exist_ok=True)
        mf = SCRATCH / "live-machines.json"
        mf.write_text(json.dumps([{"id": "live", "name": a.host, "llmBaseUrl": f"http://{a.host}:8080",
                                   "metricsUrl": f"http://{a.host}:9835/metrics"}]))
        rc, so, se = harness(dll, ["probe", str(mf)])
        res["probe"] = json.loads(so) if rc == 0 else se[-800:]
        mf.unlink()
    return res


# ------------------------------------------------------------------ gate
def git(*a: str, repo: Path = REPO) -> str:
    return subprocess.run(["git", *a], cwd=repo, capture_output=True, text=True, check=True).stdout


def run_part(name: str, cmd: list[str], timeout: int, must_match: list[str] | None = None, env=None) -> dict[str, Any]:
    t = time.time()
    try:
        r = subprocess.run(cmd, cwd=REPO, capture_output=True, text=True, timeout=timeout, env=env)
        txt = r.stdout + r.stderr
        ok = r.returncode == 0 and all(re.search(m, txt, re.M) for m in (must_match or []))
        return {"name": name, "ok": ok, "rc": r.returncode, "secs": round(time.time() - t, 1), "tail": txt[-1500:],
                **({"must_match": must_match} if must_match else {})}
    except FileNotFoundError as e:
        return {"name": name, "ok": False, "error": f"tool missing: {e}"}
    except subprocess.TimeoutExpired:
        return {"name": name, "ok": False, "error": f"timeout after {timeout}s"}


def expand(cmd: list[str]) -> list[str]:
    return [c.replace("{dotnet}", dotnet() or "dotnet").replace("{python}", sys.executable) for c in cmd]


def cmd_gate(a) -> dict[str, Any]:
    g = CFG["gate"]
    before = git("status", "--porcelain")
    checks: list[dict[str, Any]] = []
    warnings: list[str] = []
    # 1 tests: scenario journeys against fresh stubs (always torn down)
    parts = []
    for t in g.get("tests", []):
        parts.append(run_part(t["name"], expand(t["cmd"]), t.get("timeout_s", 300), t.get("must_match"), dotnet_env()))
    if read_state():
        warnings.append("an instance was already running; gate used its own fresh stubs after stopping it")
        cmd_cleanup(a)
    la = cmd_launch(argparse.Namespace(plan=False))
    if not la["ok"]:
        parts.append({"name": "journeys", "ok": False, "error": "stub launch failed", "launch": la})
    else:
        try:
            dr = cmd_drive(argparse.Namespace(feature="all", plan=False))
            parts.append({"name": "journeys", "ok": dr["ok"], "features": dr.get("results"), "error": dr.get("error")})
        finally:
            parts[-1]["cleanup"] = cmd_cleanup(a)["instance"]
    checks.append({"check": "tests", "ok": all(p["ok"] for p in parts), "parts": parts})
    # 2 typecheck
    parts = []
    for t in g.get("typecheck", []):
        parts.append(run_part(t["name"], expand(t["cmd"]), t.get("timeout_s", 300), t.get("must_match"), dotnet_env()))
    errs = []
    for pat in g.get("py_globs", []):
        for f in glob.glob(str(REPO / pat)):
            try:
                compile(Path(f).read_text(), f, "exec")
            except SyntaxError as e:
                errs.append(f"{f}: {e}")
    parts.append({"name": "py_syntax", "ok": not errs, "errors": errs})
    probs = validate_scenarios()
    parts.append({"name": "scenario_schema", "ok": not probs, "problems": probs})
    checks.append({"check": "typecheck", "ok": all(p["ok"] for p in parts), "parts": parts})
    # 3 lint
    parts = [run_part(t["name"], expand(t["cmd"]), t.get("timeout_s", 300), t.get("must_match"), dotnet_env()) for t in g.get("lint", [])]
    checks.append({"check": "lint", "ok": all(p["ok"] for p in parts), "parts": parts} if parts else {"check": "lint", "ok": True, "na": g.get("lint_na")})
    # 4 private scan
    if g.get("private_scan"):
        parts = [run_part(t["name"], expand(t["cmd"]), t.get("timeout_s", 60), t.get("must_match")) for t in g["private_scan"]]
        checks.append({"check": "private_scan", "ok": all(p["ok"] for p in parts), "parts": parts})
    # 5 scope
    try:
        base = a.diff_base or git("merge-base", "HEAD", g["main_ref"]).strip()
        changed = set(git("diff", "--name-only", base).split()) | set(git("ls-files", "--others", "--exclude-standard").split())
        skill_rel = SKILL.relative_to(REPO).as_posix() + "/"
        allow = list(g.get("scope_allow", [])) + list(a.allow_path or [])
        oos = sorted(f for f in changed if not f.startswith(skill_rel) and not any(fnmatch.fnmatch(f, p) for p in allow))
        tracked_bad = [f for f in git("ls-files").split() if f.startswith(skill_rel + "evidence/") or f.startswith(skill_rel + "scratch/")]
        after = git("status", "--porcelain")
        side = sorted(set(after.splitlines()) - set(before.splitlines()))
        checks.append({"check": "scope_check", "ok": not oos and not tracked_bad and not side, "base": base[:12],
                       "changed": len(changed), "out_of_scope": oos, "tracked_evidence_or_scratch": tracked_bad,
                       "gate_side_effects": side})
    except subprocess.CalledProcessError as e:
        checks.append({"check": "scope_check", "ok": False, "error": f"git failed (no merge-base with {g['main_ref']}?): {e.stderr}"})
    failed = next((c for c in checks if not c["ok"]), None)
    sugg = None
    if failed:
        sugg = g.get("suggestions", {}).get(failed["check"], "")
        if failed["check"] == "tests":
            for p in failed["parts"]:
                for f in (p.get("features") or []):
                    if not f["ok"]:
                        sugg += f" First failing journey: {f['feature']} step {f['failed_step']} ({f['failed_detail']}); see evidence/{f['feature']}/result.json."
                        break
                else:
                    if not p["ok"]:
                        sugg += f" First failing part: {p['name']} ({p.get('error') or 'see tail'})."
                        continue
                break
    res = {"verdict": "pass" if not failed else "fail", "failed_check": failed["check"] if failed else None,
           "suggestion": sugg, "checks": checks, "warnings": warnings, "finished": now()}
    (EVIDENCE / "gate").mkdir(parents=True, exist_ok=True)
    (EVIDENCE / "gate" / "result.json").write_text(json.dumps(res, indent=2, default=str))
    return res


def cmd_features(a) -> dict[str, Any]:
    feats = []
    for fid, p in scenario_files().items():
        sc = tomllib.loads(p.read_text())
        feats.append({"feature": fid, "title": sc.get("title"), "doc": f"features/{sc.get('feature_doc')}", "steps": len(sc.get("steps", []))})
    probs = validate_scenarios()
    return {"ok": not probs, "features": feats, "problems": probs}


EPILOG = """typical loop (from the repo root):
  verify.py launch                 start stub hosts on spare loopback addresses (127.77.0.N:8080/:9835)
  verify.py doctor                 are the stubs up and serving their fixtures? (read-only)
  verify.py drive <feature|all>    run mapped journeys (harness on the app's real code + host/check.sh)
  verify.py cleanup                stop only the stub process launch started; evidence is kept
  verify.py compose <feature|all>  launch -> doctor -> drive -> cleanup in one go
  verify.py gate --json            checker verdict: tests, typecheck, lint, private_scan, scope_check
  verify.py features               list mapped features and check map/scenario consistency
  verify.py probe-live HOST --yes-read-only   opt-in: two GETs against a real host; never in drive/gate

dry-run: always. Drive and gate only talk to stubs this skill launched on 127.x;
http_get refuses any other URL. The only path to a real host is probe-live, which is
read-only (GET /v1/models, GET /metrics) and needs an explicit flag.
"""


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0] + " App: llm-host-monitor.",
                                 epilog=EPILOG, formatter_class=argparse.RawDescriptionHelpFormatter)
    sp = ap.add_subparsers(dest="cmd", required=True)

    def add(name, fn, help_):
        p = sp.add_parser(name, help=help_)
        p.add_argument("--json", "-j", action="store_true", help="machine-readable output")
        p.set_defaults(fn=fn)
        return p
    p = add("launch", cmd_launch, "start stub hosts (fixtures) on spare loopback addresses")
    p.add_argument("--plan", action="store_true", help="print the address plan only; start nothing")
    add("doctor", cmd_doctor, "read-only: stubs up and serving fixtures; dotnet/bash/curl present")
    for name, fn, h in (("drive", cmd_drive, "run one mapped journey (or all)"), ("compose", cmd_compose, "launch -> doctor -> drive -> cleanup")):
        p = add(name, fn, h)
        p.add_argument("feature", help="feature id (file stem in checks/scenarios/) or 'all'")
        p.add_argument("--plan", action="store_true", help="print steps only")
    add("features", cmd_features, "list mapped features; check scenario/map consistency")
    p = add("gate", cmd_gate, "checker verdict; never fixes code")
    p.add_argument("--diff-base", help="git ref for scope_check (default: merge-base HEAD origin/main)")
    p.add_argument("--allow-path", action="append", help="glob of intended product changes (repeatable)")
    add("cleanup", cmd_cleanup, "stop only the stubs launch started; keep evidence/")
    p = add("probe-live", cmd_probe_live, "opt-in read-only probe of a real host (never in drive/gate)")
    p.add_argument("host")
    p.add_argument("--yes-read-only", action="store_true", help="confirm: GETs only")
    p = sp.add_parser("_serve", help=argparse.SUPPRESS)
    p.add_argument("plan")
    a = ap.parse_args()
    if a.cmd == "_serve":
        serve_stubs(json.loads(a.plan))
        return 0
    res = a.fn(a)
    out(res, a.json)
    ok = res.get("ok", res.get("verdict") == "pass")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
