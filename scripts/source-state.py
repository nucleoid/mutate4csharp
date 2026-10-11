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

def add(label, value):
    for part in (label.encode("utf-8"), value):
        digest.update(len(part).to_bytes(8, "big"))
        digest.update(part)

for command in (["rev-parse", "HEAD"], ["rev-parse", "HEAD^{tree}"],
                ["status", "--porcelain=v1", "-z", "--untracked-files=all"],
                ["diff", "--cached", "--binary", "--no-ext-diff", "--no-textconv"],
                ["diff", "--binary", "--no-ext-diff", "--no-textconv"]):
    add("git:" + " ".join(command), subprocess.check_output(["/usr/bin/git", "-C", root, *command],
        env={**{key: value for key, value in os.environ.items() if not key.startswith("GIT_")},
             "GIT_OPTIONAL_LOCKS": "0", "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": "/dev/null"}, timeout=30))

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
        info = os.lstat(path)
        if stat.S_ISLNK(info.st_mode):
            raise ValueError("source-state inventory refuses links")
        if stat.S_ISREG(info.st_mode):
            paths.append((relative_path, path, info))
        elif not stat.S_ISDIR(info.st_mode):
            raise ValueError("source-state inventory refuses special files")
for relative, path, before in sorted(paths):
    content = hashlib.sha256()
    with open(path, "rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            content.update(chunk)
    after = os.lstat(path)
    if (before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns) != (
            after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns):
        raise ValueError("source changed during source-state capture")
    add("file:" + relative, str(before.st_mode & 0o777).encode() + b":" +
        str(before.st_size).encode() + b":" + content.digest())
print(digest.hexdigest())
