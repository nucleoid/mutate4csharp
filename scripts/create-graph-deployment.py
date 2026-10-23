#!/usr/bin/env python3
"""Create finite graph installation selection DATA after ordinary owned builds.

This does not build/install/run anything or certify producer/source authority.
The running tool validates source/SDK assembly stamps and every image again.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import stat

MAXIMUM = 32 * 1024 * 1024
MAXIMUM_TOTAL = 512 * 1024 * 1024
reserved = 0
LOGGER = ("Mutate4CSharp.GraphCapture.dll", "Mutate4CSharp.GraphCapture.pdb",
          "Mutate4CSharp.GraphCapture.deps.json", "Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll")
WORKER = ("Mutate4CSharp.GraphWorker.dll", "Mutate4CSharp.GraphWorker.pdb",
          "Mutate4CSharp.GraphWorker.deps.json", "Mutate4CSharp.GraphWorker.runtimeconfig.json",
          "Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll")


def image(value):
    global reserved
    path = Path(value)
    if not path.is_absolute() or len(str(path)) > 4096:
        raise ValueError("complete absolute image path required")
    for part in (path, *path.parents):
        attributes = part.lstat()
        if part.is_symlink() or getattr(attributes, "st_file_attributes", 0) & 0x400:
            raise ValueError("redirected deployment refused")
    with path.open("rb") as stream:
        before = os.fstat(stream.fileno())
        if not stat.S_ISREG(before.st_mode) or not 0 < before.st_size <= MAXIMUM:
            raise ValueError("finite complete regular image required")
        if before.st_size > MAXIMUM_TOTAL - reserved:
            raise ValueError("complete deployment aggregate read admission exceeded")
        reserved += before.st_size
        digest = hashlib.sha256()
        count = 0
        while count < before.st_size:
            block = stream.read(min(65536, before.st_size - count))
            if not block:
                raise ValueError("short image read")
            digest.update(block)
            count += len(block)
        if stream.read(1) or os.fstat(stream.fileno()).st_size != before.st_size:
            raise ValueError("image changed during complete read")
    return {"Path": str(path), "Bytes": count, "Sha256": digest.hexdigest()}


def revision(value):
    if len(value) != 40 or any(c not in "0123456789abcdef" for c in value):
        raise ValueError("actual full source revision required; no fixture revision alias")


def unique(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate deployment property")
        result[key] = value
    return result


def keys(value, expected):
    if not isinstance(value, dict) or set(value) != set(expected):
        raise ValueError("closed complete deployment object required")


def validate_package(path, source_revision):
    """Pack barrier: exact current source and complete relative package roles.

    This does not grant execution or replace runtime helper/SDK stamp checks.
    External host/compiler paths deliberately remain exact installation pins.
    """
    global reserved
    revision(source_revision)
    path = Path(path)
    pin = image(path)
    if pin["Bytes"] > 1048576:
        raise ValueError("finite deployment manifest bound")
    if pin["Bytes"] + 1 > MAXIMUM_TOTAL - reserved:
        raise ValueError("deployment validation readback admission exceeded")
    reserved += pin["Bytes"] + 1
    with path.open("rb") as stream:
        encoded = stream.read(1048577)
    if len(encoded) != pin["Bytes"] or hashlib.sha256(encoded).hexdigest() != pin["Sha256"]:
        raise ValueError("deployment changed during validation")
    document = json.loads(encoded, object_pairs_hook=unique)
    keys(document, ("Version", "SourceRevision", "Logger", "DotnetHost", "WorkerClosure", "CompilerClosure"))
    if type(document["Version"]) is not int or document["Version"] != 1 or document["SourceRevision"] != source_revision:
        raise ValueError("package deployment must match exact packed SourceRevisionId")
    logger = document["Logger"]
    keys(logger, ("SdkVersion", "LoggerClosure", "CscTask", "CscDefinition"))
    if logger["SdkVersion"] != "10.0.401":
        raise ValueError("exact selected401 package profile required")
    for role, rows, expected in (("logger", logger["LoggerClosure"], LOGGER),
                                 ("worker", document["WorkerClosure"], WORKER)):
        if not isinstance(rows, list) or len(rows) != len(expected):
            raise ValueError("complete package image roles required")
        seen = set()
        for row in rows:
            keys(row, ("Path", "Bytes", "Sha256"))
            relative = row["Path"]
            if not isinstance(relative, str) or relative not in {role + "/" + name for name in expected} or relative in seen:
                raise ValueError("exact nonaliased relative logger/worker package roles required")
            seen.add(relative)
        for row in rows:
            relative = row["Path"]
            actual = image(path.parent / relative)
            if type(row["Bytes"]) is not int or row["Bytes"] != actual["Bytes"] or row["Sha256"] != actual["Sha256"]:
                raise ValueError("package image differs from deployment pin")
    return pin["Sha256"]


def main():
    global reserved
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host")
    parser.add_argument("--logger-dir")
    parser.add_argument("--worker-dir")
    parser.add_argument("--task")
    parser.add_argument("--source-revision", required=True)
    parser.add_argument("--output")
    parser.add_argument("--external-manifest", action="store_true",
                        help="explicit installation selection; retain absolute installed helper paths, never package this manifest")
    parser.add_argument("--validate-package", metavar="DEPLOYMENT_JSON")
    args = parser.parse_args()
    revision(args.source_revision)
    if args.validate_package:
        if any((args.host, args.logger_dir, args.worker_dir, args.task, args.output, args.external_manifest)):
            raise ValueError("package validation and creation arguments cannot be combined")
        print(validate_package(args.validate_package, args.source_revision))
        return
    if not all((args.host, args.logger_dir, args.worker_dir, args.task, args.output)):
        parser.error("creation requires --host, --logger-dir, --worker-dir, --task and --output")
    output = Path(args.output)
    if not output.is_absolute() or output.name != "deployment.json" or output.exists():
        raise ValueError("fresh absolute deployment.json required")
    for parent in output.parents:
        if parent.is_symlink() or getattr(parent.lstat(), "st_file_attributes", 0) & 0x400:
            raise ValueError("redirected output refused")
    logger = [image(Path(args.logger_dir) / name) for name in LOGGER]
    worker = [image(Path(args.worker_dir) / name) for name in WORKER]
    for name in ("Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll"):
        if next(r["Sha256"] for r in logger if Path(r["Path"]).name == name) != next(r["Sha256"] for r in worker if Path(r["Path"]).name == name):
            raise ValueError("logger and worker compiler bytes differ")
    # If outputs have already been staged into the fixed package layout, record
    # only closed relative image roles. External SDK host/task stay exact pins.
    for role, rows in (("logger", logger), ("worker", worker)):
        if not args.external_manifest:
            if not all(Path(row["Path"]).parent == output.parent / role for row in rows):
                raise ValueError("package deployment requires colocated logger/ and worker/; use explicit --external-manifest for installed selection")
            for row in rows:
                row["Path"] = role + "/" + Path(row["Path"]).name
    document = {"Version": 1, "SourceRevision": args.source_revision,
                "Logger": {"SdkVersion": "10.0.401", "LoggerClosure": logger, "CscTask": image(args.task),
                           "CscDefinition": image(Path(args.task).parent / "Microsoft.CSharp.Core.targets")},
                "DotnetHost": image(args.host), "WorkerClosure": worker,
                "CompilerClosure": [image(Path(args.task).parent / "bincore" / name) for name in
                                    ("csc.dll", "csc.deps.json", "csc.runtimeconfig.json", "Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll")]}
    for name in ("Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll"):
        if next(r["Sha256"] for r in logger if Path(r["Path"]).name == name) != next(
                r["Sha256"] for r in document["CompilerClosure"] if Path(r["Path"]).name == name):
            raise ValueError("selected SDK compiler bytes differ from installed helper closure; rebuild same-source helpers under exact401 profile")
    encoded = (json.dumps(document, indent=2) + "\n").encode("utf-8")
    if len(encoded) > 1048576:
        raise ValueError("finite deployment manifest bound")
    # Charge the complete output and complete bounded readback without refunds.
    if 2 * len(encoded) + 1 > MAXIMUM_TOTAL - reserved:
        raise ValueError("complete deployment output/readback aggregate admission exceeded")
    reserved += 2 * len(encoded) + 1
    with output.open("xb") as stream:
        stream.write(encoded)
        stream.flush()
        os.fsync(stream.fileno())
    # A partial/failed manifest is preserved, never retried/adopted as authority.
    with output.open("rb") as stream:
        if stream.read(1048577) != encoded:
            raise ValueError("complete deployment manifest readback differs")
    print(hashlib.sha256(encoded).hexdigest())


if __name__ == "__main__":
    main()
