#!/usr/bin/env python3
"""Fingerprint consumer bytes and Git state without executing consumer Python code."""
import hashlib
import os
import stat
import subprocess
import sys

root, state_mode = sys.argv[1:]
if state_mode not in ("no-state", "default-state"):
    raise ValueError("unknown state mode")
digest = hashlib.sha256()
if os.path.lexists(os.path.join(root, ".gitmodules")):
    raise ValueError("source-state profile refuses submodules")
git_environment = {**{key: value for key, value in os.environ.items() if not key.startswith("GIT_")},
                   "GIT_OPTIONAL_LOCKS": "0", "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": "/dev/null"}
git_prefix = ["/usr/bin/git", "-c", "core.fsmonitor=false", "-c", "core.untrackedCache=false",
              "-c", "core.hooksPath=/dev/null", "-C", root]
# Read configuration before status/diff: clean filters can execute commands while hashing work-tree inputs.
filters = subprocess.run([*git_prefix, "config", "--get-regexp", r"^filter\."],
                         env=git_environment, capture_output=True, timeout=30)
if filters.returncode not in (0, 1) or filters.returncode == 0:
    raise ValueError("source-state profile refuses configured Git filters")
if any(entry.startswith(b"160000 ") for entry in subprocess.check_output(
        [*git_prefix, "ls-files", "--stage", "-z"], env=git_environment, timeout=30).split(b"\0")):
    raise ValueError("source-state profile refuses gitlinks")

if state_mode == "default-state":
    state_root = os.path.join(root, ".mutate4csharp")
    if os.path.lexists(state_root):
        if os.path.islink(state_root) or not os.path.isdir(state_root):
            raise ValueError("sidecar root is not ordinary")
        for directory, names, files in os.walk(state_root, followlinks=False):
            for name in names + files:
                path = os.path.join(directory, name)
                relative = os.path.relpath(path, state_root).replace(os.sep, "/")
                parts = relative.split("/")
                info = os.lstat(path)
                if stat.S_ISLNK(info.st_mode) or not (stat.S_ISREG(info.st_mode) or stat.S_ISDIR(info.st_mode)):
                    raise ValueError("sidecar inventory refuses links and special files")
                if relative == ".gitignore" and stat.S_ISREG(info.st_mode):
                    with open(path, "rb") as stream:
                        if stream.read(3) != b"*\n":
                            raise ValueError("sidecar ignore file differs from the certified contract")
                    continue
                if parts[0] not in ("discovery", "proven", "locks", "reports") or len(parts) > 2:
                    raise ValueError("undocumented sidecar effect")
                if stat.S_ISDIR(info.st_mode) and len(parts) != 1:
                    raise ValueError("undocumented nested sidecar directory")
                if stat.S_ISREG(info.st_mode) and (len(parts) != 2 or not name.endswith(".lock" if parts[0] == "locks" else ".json")):
                    raise ValueError("undocumented sidecar file")

def add(label, value):
    for part in (label.encode("utf-8"), value):
        digest.update(len(part).to_bytes(8, "big"))
        digest.update(part)

for command in (["rev-parse", "HEAD"], ["rev-parse", "HEAD^{tree}"],
                ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignore-submodules=all"],
                ["ls-files", "--stage", "-z"], ["ls-files", "-v", "-z"],
                ["diff", "--cached", "--binary", "--no-ext-diff", "--no-textconv"],
                ["config", "--local", "--list", "--null"]):
    add("git:" + " ".join(command), subprocess.check_output([*git_prefix, *command],
        env=git_environment, timeout=30))

paths = []
for directory, names, files in os.walk(root, followlinks=False):
    relative = os.path.relpath(directory, root)
    if relative == ".":
        names[:] = [name for name in names if name != ".git" and
                    not (name == ".mutate4csharp" and state_mode == "default-state")]
    for name in names + files:
        path = os.path.join(directory, name)
        relative_path = os.path.relpath(path, root).replace(os.sep, "/")
        if relative_path == ".git":
            continue
        if ".git" in relative_path.split("/"):
            raise ValueError("source-state profile refuses nested repositories")
        info = os.lstat(path)
        if stat.S_ISLNK(info.st_mode):
            raise ValueError("source-state inventory refuses links")
        if stat.S_ISREG(info.st_mode):
            paths.append((relative_path, path, info))
            if len(paths) > 100_000:
                raise ValueError("source-state inventory exceeds file bound")
        elif not stat.S_ISDIR(info.st_mode):
            raise ValueError("source-state inventory refuses special files")
if sum(item.st_size for _, _, item in paths) > 1024 * 1024 * 1024:
    raise ValueError("source-state inventory exceeds byte bound")
for relative, path, before in sorted(paths):
    content = hashlib.sha256()
    def signature(info):
        return (info.st_dev, info.st_ino, info.st_mode, info.st_size, info.st_mtime_ns, info.st_ctime_ns)
    consumed = 0
    with os.fdopen(os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK), "rb") as stream:
        if signature(os.fstat(stream.fileno())) != signature(before):
            raise ValueError("source changed before source-state read")
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            consumed += len(chunk)
            if consumed > before.st_size:
                raise ValueError("source grew during source-state read")
            content.update(chunk)
    after = os.lstat(path)
    if consumed != before.st_size or signature(before) != signature(after):
        raise ValueError("source changed during source-state capture")
    add("file:" + relative, str(before.st_mode & 0o777).encode() + b":" +
        str(before.st_size).encode() + b":" + content.digest())
print(digest.hexdigest())
