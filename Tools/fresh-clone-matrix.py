#!/usr/bin/env python3
"""
Fresh-clone matrix test for Confluence.

Confluence is built for any grid owner, on SQLite, MySQL/MariaDB or PostgreSQL,
in standalone or grid mode. This script proves that from a clean checkout, using
only the shipped templates - no hand-patched configuration:

  1. clones the repository (a local path or a URL) into a work folder,
  2. builds it by the README steps (runprebuild, then dotnet build),
  3. for every database x mode combination, generates a deployment from the
     shipped *.ini.example files, on its own ports and in its own throwaway
     database, starts it, and checks it,
  4. compares plugin/service load failures with Tools/fresh-clone-matrix-expected.json
     (the known gaps), so a NEW failure fails the run and a CLOSED gap is called out,
  5. stops only the processes it started and drops only the databases it made.

Usage:
  python Tools/fresh-clone-matrix.py [--dbs sqlite,mysql,pgsql] [--modes standalone,grid]
                                     [--source PATH_OR_URL] [--branch NAME]
                                     [--work DIR] [--keep] [--skip-build]

Databases other than SQLite need an administrator login, given through the
environment (never on the command line, never stored):
  CFX_MYSQL_ADMIN="user:password@host:port"     (host and port optional)
  CFX_PGSQL_ADMIN="user:password@host:port"     (default: postgres@localhost:5432, no password)
A combination whose database is not configured is reported as SKIPPED.
Client programs are found on PATH, or set CFX_MYSQL_BIN / CFX_PSQL_BIN.

Exit code: 0 if nothing regressed, 1 otherwise.
"""

import argparse
import json
import os
import re
import secrets
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
IS_WINDOWS = os.name == "nt"

ROBUST_WAIT = 120     # seconds to wait for Robust to answer
REGION_WAIT = 240     # seconds to wait for a region to finish starting

PLUGIN_FAIL = re.compile(r"Error loading plugin ([\w.]+?)\.dll: ")


# --------------------------------------------------------------------------- helpers

def log(msg):
    print(msg, flush=True)


def run(cmd, cwd=None, env=None, check=True, timeout=None):
    res = subprocess.run(cmd, cwd=cwd, env=env, capture_output=True, text=True,
                         errors="replace", timeout=timeout)
    if check and res.returncode != 0:
        raise RuntimeError("command failed (%s): %s\n%s" % (res.returncode, " ".join(cmd), (res.stdout + res.stderr)[-2000:]))
    return res


def rmtree(path):
    """Remove a folder even when it holds read-only files (git objects on Windows)."""
    import stat

    def fix(func, p, _exc):
        try:
            os.chmod(p, stat.S_IWRITE)
            func(p)
        except OSError:
            pass

    if os.path.exists(path):
        shutil.rmtree(path, onerror=fix)


def read(path):
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        return f.read().replace("\r\n", "\n")


def write(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def sub_active(text, pattern, replacement, count=1, required=True, what=""):
    """Replace the first ACTIVE (uncommented) line matching pattern."""
    new, n = re.subn(r"(?m)^([ \t]*)" + pattern, replacement, text, count=count)
    if n == 0 and required:
        raise RuntimeError("template no longer has an active line matching %s (%s) - the generator needs updating" % (pattern, what))
    return new


def section_bounds(text, name):
    m = re.search(r"(?m)^\[%s\][ \t]*$" % re.escape(name), text)
    if not m:
        raise RuntimeError("template has no [%s] section" % name)
    nxt = re.search(r"(?m)^\[[^\]]+\][ \t]*$", text[m.end():])
    return m.end(), (m.end() + nxt.start() if nxt else len(text))


def replace_in_section(text, name, pattern, replacement, required=True):
    a, b = section_bounds(text, name)
    body = re.sub(r"(?m)^([ \t]*)" + pattern, replacement, text[a:b], count=1)
    if body == text[a:b] and required:
        raise RuntimeError("[%s] has no active line matching %s" % (name, pattern))
    return text[:a] + body + text[b:]


def free_port_block(start):
    """First port >= start with the next 3 also free."""
    p = start
    while True:
        ok = True
        for q in range(p, p + 3):
            with socket.socket() as s:
                if s.connect_ex(("127.0.0.1", q)) == 0:
                    ok = False
                    break
        if ok:
            return p
        p += 10


def http(url, data=None, headers=None, timeout=8):
    req = urllib.request.Request(url, data=data, headers=headers or {})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status, r.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace")
    except Exception as e:  # noqa
        return 0, str(e)


def app_cmd(folder, name, args):
    exe = os.path.join(folder, name + (".exe" if IS_WINDOWS else ".dll"))
    if IS_WINDOWS:
        return [exe] + args
    return ["dotnet", exe] + args


# --------------------------------------------------------------------------- databases

class Database:
    """Creates and drops throwaway databases; knows the connection string per engine."""

    def __init__(self, kind):
        self.kind = kind
        self.created = []
        self.ready = False
        self.reason = ""
        self.tmp = None
        if kind == "sqlite":
            self.ready = True
        elif kind == "mysql":
            self._init_mysql()
        elif kind == "pgsql":
            self._init_pgsql()

    # ---- parsing "user:password@host:port"
    @staticmethod
    def _parse(spec, default_port, default_user=None):
        user, password, host, port = default_user, "", "localhost", default_port
        if spec:
            cred, _, hp = spec.partition("@") if "@" in spec else (spec, "", "")
            user, _, password = cred.partition(":")
            if hp:
                host, _, p = hp.partition(":")
                port = int(p) if p else default_port
        return user, password, host, port

    def _init_mysql(self):
        spec = os.environ.get("CFX_MYSQL_ADMIN")
        if not spec:
            self.reason = "CFX_MYSQL_ADMIN is not set"
            return
        self.user, self.password, self.host, self.port = self._parse(spec, 3306)
        self.bin = os.environ.get("CFX_MYSQL_BIN") or shutil.which("mysql") or shutil.which("mariadb")
        if not self.bin:
            self.reason = "mysql client not found (set CFX_MYSQL_BIN)"
            return
        self.tmp = tempfile.mkdtemp(prefix="cfx-my-")
        self.cnf = os.path.join(self.tmp, "my.cnf")
        with open(self.cnf, "w") as f:
            f.write('[client]\nhost=%s\nport=%d\nuser=%s\npassword="%s"\n' % (self.host, self.port, self.user, self.password))
        r = run([self.bin, "--defaults-extra-file=" + self.cnf, "--connect-timeout=8", "-e", "select 1"], check=False)
        if r.returncode != 0:
            self.reason = "cannot log in to MySQL: " + r.stderr.strip()[:120]
            return
        self.ready = True

    def _init_pgsql(self):
        spec = os.environ.get("CFX_PGSQL_ADMIN", "postgres@localhost:5432")
        self.user, self.password, self.host, self.port = self._parse(spec, 5432, "postgres")
        self.bin = os.environ.get("CFX_PSQL_BIN") or shutil.which("psql")
        if not self.bin:
            self.reason = "psql not found (set CFX_PSQL_BIN)"
            return
        self.env = dict(os.environ, PGPASSWORD=self.password, PGCONNECT_TIMEOUT="8")
        r = run([self.bin, "-h", self.host, "-p", str(self.port), "-U", self.user, "-w", "-d", "postgres", "-tAc", "select 1"],
                env=self.env, check=False)
        if r.returncode != 0:
            self.reason = "cannot log in to PostgreSQL: " + r.stderr.strip()[:120]
            return
        self.ready = True

    def _sql(self, sql, db="postgres"):
        if self.kind == "mysql":
            run([self.bin, "--defaults-extra-file=" + self.cnf, "-e", sql])
        else:
            run([self.bin, "-h", self.host, "-p", str(self.port), "-U", self.user, "-w", "-d", db, "-c", sql], env=self.env)

    def create(self, name):
        if self.kind == "sqlite":
            return
        name = "cfx_" + name
        self.drop(name, quiet=True)
        if self.kind == "mysql":
            self._sql("CREATE DATABASE `%s` CHARACTER SET utf8mb4" % name)
        else:
            self._sql('CREATE DATABASE "%s"' % name)
        self.created.append(name)

    def drop(self, name, quiet=False):
        try:
            if self.kind == "mysql":
                self._sql("DROP DATABASE IF EXISTS `%s`" % name)
            elif self.kind == "pgsql":
                self._sql('DROP DATABASE IF EXISTS "%s" WITH (FORCE)' % name)
        except Exception:
            if not quiet:
                raise

    def cleanup(self):
        for n in self.created:
            self.drop(n, quiet=True)
        if self.tmp:
            rmtree(self.tmp)

    def conn(self, name):
        """(StorageProvider, ConnectionString) for the region/robust core stores."""
        if self.kind == "sqlite":
            return "OpenSim.Data.SQLite.dll", "URI=file:%s.db,version=3" % name
        n = "cfx_" + name
        if self.kind == "mysql":
            return ("OpenSim.Data.MySQL.dll",
                    "Data Source=%s;Port=%d;Database=%s;User ID=%s;Password=%s;Old Guids=true;SslMode=None;"
                    % (self.host, self.port, n, self.user, self.password))
        return ("OpenSim.Data.PGSQL.dll",
                "Server=%s;Port=%d;Database=%s;User Id=%s;Password=%s;"
                % (self.host, self.port, n, self.user, self.password))


# --------------------------------------------------------------------------- config generation

def set_storage(text, section, db, name, sqlite_include=None):
    """Point [section] at the database, the way the templates document it.

    SQLite keeps what the template ships (its Include-Storage file gives every
    service its own database file); Robust's template ships MySQL, so it is
    switched to SQLiteRobust.ini. MySQL and PostgreSQL replace the storage lines
    with one provider and connection string that every service inherits.
    """
    a, b = section_bounds(text, section)
    body = text[a:b]
    if db.kind == "sqlite" and not sqlite_include:
        return text
    for key in ("Include-Storage", "StorageProvider", "ConnectionString"):
        body = re.sub(r"(?m)^[ \t]*" + key + r"\s*=.*\n?", "", body)
    if db.kind == "sqlite":
        add = '\n    Include-Storage = "config-include/storage/%s";\n' % sqlite_include
    else:
        provider, conn = db.conn(name)
        add = '\n    StorageProvider = "%s"\n    ConnectionString = "%s"\n' % (provider, conn)
    return text[:a] + add + body + text[b:]


def estate_block(owner, email, password, uuid=None):
    """A default estate so a region starts unattended instead of prompting on the console."""
    text = ("\n[Estates]\n    DefaultEstateName = Matrix Estate\n    DefaultEstateOwnerName = %s\n"
            "    DefaultEstateOwnerEMail = %s\n    DefaultEstateOwnerPassword = %s\n" % (owner, email, password))
    if uuid:
        text += "    DefaultEstateOwnerUUID = %s\n" % uuid
    return text


def apply_worktree(source, clone):
    """Bring the source repository's uncommitted work into the clone, so a change can be
    tested from a clean build BEFORE it is committed (the standing rule: test first)."""
    patch = subprocess.run(["git", "-C", source, "diff", "HEAD", "--binary"], capture_output=True).stdout
    if patch.strip():
        pf = os.path.join(clone, "..", "worktree.patch")
        with open(pf, "wb") as f:
            f.write(patch)
        run(["git", "-C", clone, "apply", "--whitespace=nowarn", os.path.abspath(pf)])
    others = run(["git", "-C", source, "ls-files", "--others", "--exclude-standard"]).stdout.splitlines()
    for rel in others:
        s, d = os.path.join(source, rel), os.path.join(clone, rel)
        if os.path.isfile(s):
            os.makedirs(os.path.dirname(d), exist_ok=True)
            shutil.copy2(s, d)
    return len(others), bool(patch.strip())


def generate(src_bin, dst, mode, db, ports, idx, label):
    """Create a deployment folder from the shipped templates only."""
    shutil.copytree(src_bin, dst, dirs_exist_ok=True)
    pub, priv, reg = ports
    cfgdir = os.path.join(dst, "config-include")
    info = {"public": pub, "private": priv, "region": reg}

    # Every include the templates reference ships only as an .example; a real owner copies them.
    for name in ("osslEnable.ini", "FlotsamCache.ini"):
        ex = os.path.join(cfgdir, name + ".example")
        if os.path.exists(ex) and not os.path.exists(os.path.join(cfgdir, name)):
            shutil.copy(ex, os.path.join(cfgdir, name))

    ini = read(os.path.join(dst, "OpenSim.ini.example"))
    ini = replace_in_section(ini, "Const", r'PublicPort\s*=.*', r'\1PublicPort = "%d"' % (pub if mode == "standalone" else pub))
    ini = replace_in_section(ini, "Const", r'PrivatePort\s*=.*', r'\1PrivatePort = "%d"' % priv)
    ini = replace_in_section(ini, "Network", r'; *http_listener_port\s*=.*', r'\1http_listener_port = %d' % (pub if mode == "standalone" else reg), required=False)
    if not re.search(r"(?m)^[ 	]*http_listener_port\s*=", ini):
        ini += "\n[Network]\n    http_listener_port = %d\n" % (pub if mode == "standalone" else reg)
    arch = "config-include/Standalone.ini" if mode == "standalone" else "config-include/GridHypergrid.ini"
    ini = sub_active(ini, r'Include-Architecture\s*=\s*"config-include/Standalone.ini"', r'\1Include-Architecture = "%s"' % arch,
                     what="architecture include")
    ini += "\n[Concierge]\n    enabled = true\n    grid_name = \"Matrix %s\"\n" % label

    password = secrets.token_hex(8)
    info["owner_password"] = password
    if mode == "standalone":
        ini += estate_block("Test Owner", "owner@example.org", password, "00000000-0000-4000-8000-%012d" % (idx + 100))
        common = read(os.path.join(cfgdir, "StandaloneCommon.ini.example"))
        common = set_storage(common, "DatabaseService", db, "standalone")
        write(os.path.join(cfgdir, "StandaloneCommon.ini"), common)
        db.create("standalone")
    else:
        # The bootstrap account the WebUI creates on first start owns the estate.
        ini += estate_block("Grid Admin", "admin@example.org", password)
        common = read(os.path.join(cfgdir, "GridCommon.ini.example"))
        common = set_storage(common, "DatabaseService", db, "region")
        write(os.path.join(cfgdir, "GridCommon.ini"), common)
        db.create("region")

        rb = read(os.path.join(dst, "Robust.HG.ini.example"))
        rb = replace_in_section(rb, "Const", r'PublicPort\s*=.*', r'\1PublicPort = "%d"' % pub)
        rb = replace_in_section(rb, "Const", r'PrivatePort\s*=.*', r'\1PrivatePort = "%d"' % priv)
        rb = set_storage(rb, "DatabaseService", db, "robust", sqlite_include="SQLiteRobust.ini")
        db.create("robust")
        # Turn the profile service on (off by default upstream) so its gate can be exercised.
        rb = sub_active(rb, r'; *UserProfilesServiceConnector\s*=', r'\1UserProfilesServiceConnector =', what="profile connector")
        rb = replace_in_section(rb, "UserProfilesService", r'Enabled\s*=\s*false', r'\1Enabled = true')
        write(os.path.join(dst, "Robust.HG.ini"), rb)

        write(os.path.join(dst, "Regions", "Regions.ini"),
              "[Matrix Region]\nRegionUUID = %s\nLocation = %d,1000\nSizeX = 256\nSizeY = 256\nSizeZ = 256\n"
              "InternalAddress = 0.0.0.0\nInternalPort = %d\nExternalHostName = 127.0.0.1\nMaxPrims = 15000\nMaxAgents = 40\n"
              % (info.setdefault("region_uuid", "00000000-0000-4000-8000-%012d" % (idx + 1)), 1000 + idx, reg))
    if mode == "standalone":
        write(os.path.join(dst, "Regions", "Regions.ini"),
              "[Matrix Region]\nRegionUUID = 00000000-0000-4000-8000-%012d\nLocation = %d,1000\nSizeX = 256\nSizeY = 256\nSizeZ = 256\n"
              "InternalAddress = 0.0.0.0\nInternalPort = %d\nExternalHostName = 127.0.0.1\nMaxPrims = 15000\nMaxAgents = 40\n"
              % (idx + 1, 1000 + idx, pub))
    write(os.path.join(dst, "OpenSim.ini"), ini)
    return info


# --------------------------------------------------------------------------- running

class Proc:
    def __init__(self, name, cmd, cwd):
        self.name = name
        self.log = os.path.join(cwd, "Robust.log" if name == "robust" else "OpenSim.log")
        for f in (self.log,):
            if os.path.exists(f):
                os.remove(f)
        self.out = open(os.path.join(cwd, name + ".stdout"), "w")
        self.p = subprocess.Popen(cmd, cwd=cwd, stdout=self.out, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL)

    def alive(self):
        return self.p.poll() is None

    def text(self):
        try:
            return read(self.log)
        except OSError:
            return ""

    def stop(self):
        if self.alive():
            if IS_WINDOWS:
                subprocess.run(["taskkill", "/T", "/F", "/PID", str(self.p.pid)], capture_output=True)
            else:
                self.p.terminate()
            try:
                self.p.wait(15)
            except Exception:
                self.p.kill()
        self.out.close()


def wait_for(pred, seconds, proc=None):
    end = time.time() + seconds
    while time.time() < end:
        if pred():
            return True
        if proc is not None and not proc.alive():
            return pred()
        time.sleep(2)
    return pred()


def failures(text):
    return sorted(set(PLUGIN_FAIL.findall(text)))


def run_combo(idx, db_kind, mode, db, src_bin, work, expected, keep):
    label = "%s/%s" % (db_kind, mode)
    res = {"combo": label, "status": "PASS", "checks": [], "failed_services": [], "notes": []}

    def check(name, ok, detail=""):
        res["checks"].append({"name": name, "ok": bool(ok), "detail": detail})
        if not ok:
            res["status"] = "FAIL"

    dst = os.path.join(work, "deploy-%s-%s" % (db_kind, mode))
    base = free_port_block(20000 + idx * 20)
    ports = (base, base + 1, base + 2)
    procs = []
    try:
        if os.path.exists(dst):
            rmtree(dst)
        info = generate(src_bin, dst, mode, db, ports, idx, label)
        res["ports"] = ports

        region_text = ""
        robust_text = ""
        if mode == "grid":
            r = Proc("robust", app_cmd(dst, "Robust", ["-inifile=Robust.HG.ini", "-background=true"]), dst)
            procs.append(r)
            up = wait_for(lambda: http("http://127.0.0.1:%d/" % ports[0], timeout=4)[0] != 0, ROBUST_WAIT, r)
            check("Robust starts and answers", up and r.alive())
            robust_text = r.text()
            if up:
                bootstrapped = wait_for(lambda: "Username: Grid Admin" in r.text(), 30, r)
                check("WebUI bootstrap admin created", bootstrapped)
                st, _ = http("http://127.0.0.1:%d/" % ports[0])
                check("WebUI home page serves (HTTP 200)", st == 200, "HTTP %s" % st)
                st, body = http("http://127.0.0.1:%d/concierge/%s" % (ports[1], info["region_uuid"]))
                check("Concierge endpoint serves the built-in welcome", st == 200 and '"welcome"' in body, "HTTP %s" % st)
                rpc = ('{"jsonrpc":"2.0","id":"1","method":"avatarnotesrequest","params":{"UserId":"00000000-0000-0000-0000-000000000001",'
                       '"TargetId":"00000000-0000-0000-0000-000000000002"}}').encode()
                hdr = {"Content-Type": "application/json-rpc"}
                st1, b1 = http("http://127.0.0.1:%d/" % ports[0], rpc, hdr)
                st2, b2 = http("http://127.0.0.1:%d/" % ports[0], rpc, dict(hdr, **{"X-SecondLife-Shard": "Production"}))
                check("Profile gate: trusted call is answered", st1 == 200 and "result" in b1, b1[:80])
                check("Profile gate: in-world-script call is refused", "Method not found" in b2, b2[:80])
            robust_text = r.text()

        if mode == "standalone" or (mode == "grid" and procs and procs[0].alive()):
            g = Proc("region", app_cmd(dst, "OpenSim", ["-background=true", "-console=rest"]), dst)
            procs.append(g)
            ready = wait_for(lambda: "Startup complete" in g.text(), REGION_WAIT, g)
            check("Region reaches 'Startup complete'", ready and g.alive())
            region_text = g.text()
            check("Concierge module initialised in the region", "[Concierge]: initialized for" in region_text)
            if mode == "standalone":
                port = ports[0]
                st, _ = http("http://127.0.0.1:%d/" % port)
                check("Standalone WebUI serves (HTTP 200)", st == 200, "HTTP %s" % st)

        failed = sorted(set(failures(robust_text)) | set(failures(region_text)))
        res["failed_services"] = failed
        known = set(expected.get(db_kind, []))
        new = [f for f in failed if f not in known]
        if new:
            check("No unexpected service load failures", False, ", ".join(new))
        else:
            check("No unexpected service load failures", True, "%d known gap(s)" % len(failed))
    except Exception as e:  # noqa
        check("Combination ran to completion", False, str(e)[:300])
    finally:
        for p in reversed(procs):
            p.stop()
        if not keep:
            time.sleep(1)
    return res


# --------------------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser(description="Fresh-clone build + deployment matrix (databases x modes).")
    ap.add_argument("--dbs", default="sqlite,mysql,pgsql")
    ap.add_argument("--modes", default="standalone,grid")
    ap.add_argument("--source", default=REPO, help="repository path or URL to clone (default: this repository)")
    ap.add_argument("--branch", default=None, help="branch to test (default: the source's current branch)")
    ap.add_argument("--work", default=os.path.join(tempfile.gettempdir(), "confluence-matrix"))
    ap.add_argument("--keep", action="store_true", help="keep deployment folders and databases")
    ap.add_argument("--skip-build", action="store_true", help="reuse the previous clone/build in --work")
    ap.add_argument("--include-uncommitted", action="store_true",
                    help="also apply the source repository's uncommitted changes to the clone (test BEFORE committing)")
    args = ap.parse_args()

    dbs = [d.strip() for d in args.dbs.split(",") if d.strip()]
    modes = [m.strip() for m in args.modes.split(",") if m.strip()]
    work = os.path.abspath(args.work)
    src = os.path.join(work, "src")
    os.makedirs(work, exist_ok=True)

    expected_path = os.path.join(HERE, "fresh-clone-matrix-expected.json")
    expected = {}
    if os.path.exists(expected_path):
        with open(expected_path, encoding="utf-8") as f:
            expected = json.load(f).get("expected_load_failures", {})

    if not args.skip_build:
        if os.path.exists(src):
            rmtree(src)
        branch = args.branch
        if not branch and os.path.isdir(args.source):
            branch = run(["git", "-C", args.source, "rev-parse", "--abbrev-ref", "HEAD"]).stdout.strip()
        log("== cloning %s (%s)" % (args.source, branch or "default branch"))
        cmd = ["git", "clone", "--quiet", "--single-branch"] + (["--branch", branch] if branch else []) + [args.source, src]
        run(cmd)
        head = run(["git", "-C", src, "rev-parse", "--short", "HEAD"]).stdout.strip()
        if args.include_uncommitted and os.path.isdir(args.source):
            n_new, had_patch = apply_worktree(args.source, src)
            head += "+uncommitted"
            log("== applied uncommitted work (%s tracked changes, %d new files)" % ("with" if had_patch else "no", n_new))
        log("== testing commit %s" % head)
        log("== prebuild")
        pb = ["cmd", "/c", ".\\runprebuild.bat"] if IS_WINDOWS else ["bash", "./runprebuild.sh"]
        r = run(pb, cwd=src, check=False)
        if "Unhandled error" in (r.stdout + r.stderr) or not os.path.exists(os.path.join(src, "OpenSim.sln")):
            log((r.stdout + r.stderr)[-1500:])
            log("RESULT: FAIL - prebuild did not produce OpenSim.sln")
            return 1
        log("== dotnet build (Release) - this takes a few minutes")
        r = run(["dotnet", "build", "OpenSim.sln", "-c", "Release", "-v", "q", "-nologo"], cwd=src, check=False)
        if r.returncode != 0:
            errs = [l for l in (r.stdout + r.stderr).splitlines() if ": error " in l][:8]
            log("\n".join(errs))
            log("RESULT: FAIL - the clone does not build by the README steps")
            return 1
        log("== build OK")
    else:
        head = run(["git", "-C", src, "rev-parse", "--short", "HEAD"]).stdout.strip()

    src_bin = os.path.join(src, "bin")
    results = []
    dbobjs = {}
    idx = 0
    try:
        for kind in dbs:
            db = dbobjs.setdefault(kind, Database(kind))
            for mode in modes:
                idx += 1
                label = "%s/%s" % (kind, mode)
                if not db.ready:
                    log("== %-16s SKIPPED (%s)" % (label, db.reason))
                    results.append({"combo": label, "status": "SKIPPED", "checks": [], "failed_services": [], "notes": [db.reason]})
                    continue
                log("== %-16s running" % label)
                r = run_combo(idx, kind, mode, db, src_bin, work, expected, args.keep)
                results.append(r)
                log("   -> %s" % r["status"])
    finally:
        if not args.keep:
            for db in dbobjs.values():
                db.cleanup()

    # A gap is only closed when the service loads in EVERY mode that was run for that database
    # (some services, e.g. RegionHGService, only exist in grid mode).
    for kind in dbs:
        ran = [r for r in results if r["combo"].startswith(kind + "/") and r["status"] != "SKIPPED"]
        if not ran or any(not r["checks"] for r in ran):
            continue
        union = set()
        for r in ran:
            union |= set(r["failed_services"])
        closed = [k for k in expected.get(kind, []) if k not in union]
        if closed and set(m for m in modes) == set(r["combo"].split("/")[1] for r in ran):
            ran[-1]["notes"].append("gap closed for %s - remove from Tools/fresh-clone-matrix-expected.json: %s" % (kind, ", ".join(closed)))

    # ---- report
    log("\n================ matrix result for commit %s ================" % head)
    worst = 0
    for r in results:
        log("%-16s %s" % (r["combo"], r["status"]))
        for c in r["checks"]:
            if not c["ok"]:
                log("    FAIL  %s  %s" % (c["name"], c["detail"]))
        if r["failed_services"]:
            log("    services that did not load: " + ", ".join(r["failed_services"]))
        for n in r["notes"]:
            log("    note: " + n)
        if r["status"] == "FAIL":
            worst = 1
    with open(os.path.join(work, "matrix-results.json"), "w", encoding="utf-8") as f:
        json.dump({"commit": head, "results": results}, f, indent=2)
    log("\nresults: %s" % os.path.join(work, "matrix-results.json"))
    log("RESULT: %s" % ("FAIL" if worst else "PASS"))
    return worst


if __name__ == "__main__":
    sys.exit(main())
