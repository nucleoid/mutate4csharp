#!/usr/bin/env python3
"""Metadata-only packaging regression tests; never build/run graph helpers."""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import sys
import contextlib
import io
from unittest import mock

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("deployment", Path(__file__).with_name("create-graph-deployment.py"))
deployment = importlib.util.module_from_spec(spec)
spec.loader.exec_module(deployment)


class PackageDeploymentTests(unittest.TestCase):
    def setUp(self):
        deployment.reserved = 0
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.source = "a" * 40
        self.manifest = self.root / "deployment.json"
        closures = {}
        for role, names in (("logger", deployment.LOGGER), ("worker", deployment.WORKER)):
            (self.root / role).mkdir()
            closures[role] = []
            for name in names:
                data = ("unexecutable package DATA: " + name).encode()
                (self.root / role / name).write_bytes(data)
                closures[role].append({"Path": role + "/" + name, "Bytes": len(data), "Sha256": hashlib.sha256(data).hexdigest()})
        self.document = {"Version": 1, "SourceRevision": self.source,
                         "Logger": {"SdkVersion": "10.0.401", "LoggerClosure": closures["logger"],
                                    "CscTask": {}, "CscDefinition": {}},
                         "DotnetHost": {}, "WorkerClosure": closures["worker"], "CompilerClosure": []}

    def tearDown(self):
        self.temporary.cleanup()

    def write(self, document=None):
        self.manifest.write_text(json.dumps(self.document if document is None else document), encoding="utf-8")

    def test_complete_relative_roles_match_source_and_actual_files(self):
        self.write()
        self.assertEqual(hashlib.sha256(self.manifest.read_bytes()).hexdigest(),
                         deployment.validate_package(str(self.manifest), self.source))

    def test_wrong_source_is_refused(self):
        self.write()
        with self.assertRaisesRegex(ValueError, "SourceRevisionId"):
            deployment.validate_package(str(self.manifest), "b" * 40)

    def test_absolute_traversal_wrong_role_and_duplicate_roles_are_refused(self):
        for value in (str(self.root / "logger" / deployment.LOGGER[0]), "logger/../" + deployment.LOGGER[0],
                      "worker/" + deployment.LOGGER[0], "logger/" + deployment.LOGGER[1]):
            deployment.reserved = 0
            document = copy.deepcopy(self.document)
            document["Logger"]["LoggerClosure"][0]["Path"] = value
            self.write(document)
            with self.assertRaisesRegex(ValueError, "relative.*roles"):
                deployment.validate_package(str(self.manifest), self.source)

    def test_changed_package_image_is_refused(self):
        self.write()
        (self.root / "worker" / deployment.WORKER[0]).write_bytes(b"changed")
        with self.assertRaisesRegex(ValueError, "differs"):
            deployment.validate_package(str(self.manifest), self.source)

    def test_duplicate_manifest_property_is_refused(self):
        self.manifest.write_text('{"Version":1,"Version":1}', encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "duplicate"):
            deployment.validate_package(str(self.manifest), self.source)

    def create(self, output, external=False):
        # DATA only: deliberately not executable/PE inputs. The real installed
        # test separately proves source stamps and actual helper dispatch.
        host = self.root / "dotnet"
        host.write_bytes(b"unexecutable metadata fixture")
        roslyn = self.root / "sdk" / "10.0.401" / "Roslyn"
        (roslyn / "bincore").mkdir(parents=True, exist_ok=True)
        task = roslyn / "Microsoft.Build.Tasks.CodeAnalysis.dll"
        task.write_bytes(b"unexecutable metadata task")
        (roslyn / "Microsoft.CSharp.Core.targets").write_bytes(b"metadata definition")
        for name in ("csc.dll", "csc.deps.json", "csc.runtimeconfig.json", "Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll"):
            data = (self.root / "logger" / name).read_bytes() if name in deployment.LOGGER else b"metadata compiler"
            (roslyn / "bincore" / name).write_bytes(data)
        arguments = ["create-graph-deployment.py", "--host", str(host),
                     "--logger-dir", str(self.root / "logger"), "--worker-dir", str(self.root / "worker"),
                     "--task", str(task), "--source-revision", self.source, "--output", str(output)]
        if external:
            arguments.append("--external-manifest")
        with mock.patch.object(sys, "argv", arguments), contextlib.redirect_stdout(io.StringIO()) as stdout:
            deployment.main()
        self.assertEqual(hashlib.sha256(output.read_bytes()).hexdigest(), stdout.getvalue().strip())
        return json.loads(output.read_bytes())

    def test_creation_keeps_colocated_default_relative_for_pack(self):
        document = self.create(self.manifest)
        self.assertEqual("logger/" + deployment.LOGGER[0], document["Logger"]["LoggerClosure"][0]["Path"])
        deployment.validate_package(str(self.manifest), self.source)

    def test_non_colocated_creation_requires_explicit_external_selection(self):
        other = self.root / "selection"
        other.mkdir()
        output = other / "deployment.json"
        with self.assertRaisesRegex(ValueError, "colocated"):
            self.create(output)
        self.assertFalse(output.exists())

    def test_explicit_external_creation_is_not_a_pack_manifest(self):
        other = self.root / "selection"
        other.mkdir()
        output = other / "deployment.json"
        document = self.create(output, external=True)
        self.assertEqual(str(self.root / "logger" / deployment.LOGGER[0]), document["Logger"]["LoggerClosure"][0]["Path"])
        with self.assertRaisesRegex(ValueError, "relative.*roles"):
            deployment.validate_package(str(output), self.source)


if __name__ == "__main__":
    unittest.main()
