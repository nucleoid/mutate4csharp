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


if __name__ == "__main__":
    unittest.main()
