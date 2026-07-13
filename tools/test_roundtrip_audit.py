#!/usr/bin/env python3
"""Focused mutation/control tests for roundtrip-audit.py."""

import collections
import copy
import hashlib
import importlib.util
import json
import pathlib
import subprocess
import tempfile
import unittest


SCRIPT = pathlib.Path(__file__).with_name("roundtrip-audit.py")
SPEC = importlib.util.spec_from_file_location("roundtrip_audit", SCRIPT)
AUDIT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(AUDIT)


class StructuralDifferenceTests(unittest.TestCase):
    def test_identical_parsed_json_is_clean(self):
        first = {"paths": {"/pets": {"get": {"responses": {"200": {}}}}}}

        differences = AUDIT.structural_differences(first, first.copy())
        allowed, rejected = AUDIT.classify_fixed_point("okta", differences)

        self.assertEqual([], allowed)
        self.assertEqual([], rejected)

    def test_unclassified_mutation_is_rejected(self):
        differences = AUDIT.structural_differences({"info": {"title": "first"}}, {"info": {"title": "second"}})

        allowed, rejected = AUDIT.classify_fixed_point("square", differences)

        self.assertEqual([], allowed)
        self.assertEqual("/info/title", rejected[0]["path"])

    def test_exact_twilio_default_equivalence_set_is_allowed(self):
        differences = [
            {"path": path, "kind": "removed", "first": {}, "second": None}
            for path in sorted(AUDIT.TWILIO_DEFAULT_EQUIVALENT_PATHS)
        ]

        allowed, rejected = AUDIT.classify_fixed_point("twilio", differences)

        self.assertEqual(21, len(allowed))
        self.assertEqual([], rejected)

    def test_nearby_twilio_mutation_is_rejected(self):
        paths = sorted(AUDIT.TWILIO_DEFAULT_EQUIVALENT_PATHS)
        differences = [
            {"path": path, "kind": "removed", "first": {}, "second": None}
            for path in paths[:-1]
        ]
        differences.append(
            {
                "path": "/components/schemas/unreviewed/additionalProperties",
                "kind": "removed",
                "first": {},
                "second": None,
            }
        )

        allowed, rejected = AUDIT.classify_fixed_point("twilio", differences)

        self.assertEqual(20, len(allowed))
        self.assertTrue(any(item["kind"] == "equivalence-set" for item in rejected))
        self.assertTrue(any(item["path"].endswith("unreviewed/additionalProperties") for item in rejected))


class ExtensionEvidenceTests(unittest.TestCase):
    def test_occurrence_digest_is_order_independent_but_multiplicity_sensitive(self):
        first = [
            ("/one", "string", '"public"'),
            ("/two", "string", '"beta"'),
            ("/three", "string", '"public"'),
        ]
        reordered = list(reversed(first))
        mutated = [
            ("/one", "string", '"public"'),
            ("/two", "string", '"beta"'),
            ("/three", "string", '"beta"'),
        ]

        self.assertEqual(AUDIT.occurrence_digest(first), AUDIT.occurrence_digest(reordered))
        self.assertNotEqual(AUDIT.occurrence_digest(first), AUDIT.occurrence_digest(mutated))

    def test_generated_evidence_counts_attribute_and_opaque_schema_channels(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory)
            (path / "Generated.cs").write_text(
                '[assembly: RivetVendorExtension("#", "x-is-beta", "true")]\n'
                '[assembly: RivetDocumentSchema(0, "Payload", "{\\"x-release-status\\":\\"PUBLIC\\"}")]\n',
                encoding="utf-8",
            )
            errors = []

            evidence = AUDIT.scan_generated(
                path,
                0,
                {"x-is-beta": 1, "x-release-status": 1},
                errors,
            )

            self.assertEqual([], errors)
            self.assertEqual(2, evidence["vendorExtensions"])

    def test_generated_evidence_rejects_unsupported_marker_comments_only(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory)
            (path / "Generated.cs").write_text(
                'const string text = "[rivet:unsupported quoted]";\n'
                '// [rivet:unsupported body content-type=text/plain]\n',
                encoding="utf-8",
            )
            errors = []

            evidence = AUDIT.scan_generated(path, 0, {}, errors)

            self.assertEqual(1, evidence["unsupportedMarkers"])
            self.assertTrue(any("unsupported markers" in error for error in errors))

    def test_generated_compilation_rejects_invalid_source(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory)
            (path / "Invalid.cs").write_text("this is not valid C#", encoding="utf-8")
            errors = []

            def reject_invalid(command, **_kwargs):
                source = pathlib.Path(command[-4])
                invalid = any("not valid C#" in file.read_text() for file in source.rglob("*.cs"))
                return subprocess.CompletedProcess(
                    command,
                    1 if invalid else 0,
                    stdout="",
                    stderr="CS1002: ; expected" if invalid else "",
                )

            evidence = AUDIT.compile_generated(path, ["fake-rivet"], errors, reject_invalid)

            self.assertEqual(1, evidence["exitCode"])
            self.assertTrue(any("compilation exited 1" in error for error in errors))


class ComponentMetricTests(unittest.TestCase):
    def test_required_component_namespaces_are_counted(self):
        document = {
            "components": {
                "schemas": {"schema": {}},
                "requestBodies": {"body": {}},
                "parameters": {"parameter": {}},
                "responses": {"response": {}},
                "securitySchemes": {"security": {}},
            }
        }

        self.assertEqual(
            {
                "parameters": 1,
                "requestBodies": 1,
                "responses": 1,
                "schemas": 1,
                "securitySchemes": 1,
            },
            AUDIT.component_counts(document),
        )

    def test_result_requires_every_gate_component_namespace(self):
        result = {
            "corpusId": "okta",
            "passed": True,
            "categories": {
                name: {"count": 0, "findings": []}
                for name in AUDIT.RESULT_CATEGORIES
            },
            "metrics": {
                "operations": {
                    "source": 1,
                    "reemitted": 1,
                    "shared": 1,
                    "missing": 0,
                    "invented": 0,
                    "withFindings": 0,
                },
                "components": {
                    name: {
                        "source": 0,
                        "reemitted": 0,
                        "matched": 0,
                        "missing": 0,
                        "invented": 0,
                    }
                    for name in ("schemas", "requestBodies", "securitySchemes")
                },
                "sourceDefects": 0,
                "comparatorIntegrityFindings": 0,
            },
        }
        errors = []

        AUDIT.check_result(result, "okta", 1, {}, [], errors)

        self.assertTrue(any("result parameters source" in error for error in errors))
        self.assertTrue(any("result responses source" in error for error in errors))


class SourceToFirstAuditTests(unittest.TestCase):
    def test_matching_mutated_passes_do_not_hide_source_to_first_schema_loss(self):
        with tempfile.TemporaryDirectory() as directory:
            results_dir = pathlib.Path(directory) / "roundtrip"
            corpus_dir = results_dir / "probe"
            artifacts = corpus_dir / "artifacts"
            artifacts.mkdir(parents=True)

            source = {
                "openapi": "3.0.0",
                "info": {"title": "Probe", "version": "1"},
                "paths": {
                    "/items": {
                        "get": {"responses": {"200": {"description": "OK"}}}
                    }
                },
                "components": {"schemas": {"Item": {"type": "string"}}},
            }
            mutated = copy.deepcopy(source)
            mutated["components"]["schemas"]["Item"]["type"] = "integer"
            source_text = json.dumps(source, sort_keys=True)
            (artifacts / "source.json").write_text(source_text, encoding="utf-8")
            for name in ("first-openapi.json", "second-openapi.json"):
                (artifacts / name).write_text(
                    json.dumps(mutated, sort_keys=True), encoding="utf-8"
                )
            for name in ("first-generated", "second-generated"):
                generated = artifacts / name
                generated.mkdir()
                (generated / "Contract.cs").write_text(
                    "[RivetOperationProvenance(\n", encoding="utf-8"
                )

            summary = {
                **{key: {} for key in AUDIT.SUMMARY_FINDING_KEYS},
                **{key: 0 for key in AUDIT.SUMMARY_ZERO_KEYS},
                "sourceDefects": 0,
                "originalOps": 1,
                "reemittedOps": 1,
                "sharedOps": 1,
                "originalComponents": 1,
                "reemittedComponents": 1,
                "matchedComponents": 1,
            }
            for name in ("first-summary.json", "fixed-point-summary.json"):
                (artifacts / name).write_text(json.dumps(summary), encoding="utf-8")
            for name in ("first-details.json", "fixed-point-details.json"):
                (artifacts / name).write_text(
                    json.dumps({"sourceDefects": []}), encoding="utf-8"
                )

            component_metrics = {
                namespace: {
                    "source": 1 if namespace == "schemas" else 0,
                    "reemitted": 1 if namespace == "schemas" else 0,
                    "matched": 1 if namespace == "schemas" else 0,
                    "missing": 0,
                    "invented": 0,
                }
                for namespace in AUDIT.GATE_COMPONENT_NAMESPACES
            }
            result = {
                "corpusId": "probe",
                "passed": True,
                "categories": {
                    name: {"count": 0, "findings": []}
                    for name in AUDIT.RESULT_CATEGORIES
                },
                "metrics": {
                    "operations": {
                        "source": 1,
                        "reemitted": 1,
                        "shared": 1,
                        "missing": 0,
                        "invented": 0,
                        "withFindings": 0,
                    },
                    "components": component_metrics,
                    "sourceDefects": 0,
                    "comparatorIntegrityFindings": 0,
                },
            }
            (corpus_dir / "result.json").write_text(json.dumps(result), encoding="utf-8")

            source_hash = hashlib.sha256(source_text.encode()).hexdigest()
            profile_entry = {
                "id": "probe",
                "sha256": source_hash,
                "dialect": "3.0.0",
                "apiVersion": "1",
                "pathCount": 1,
                "operationCount": 1,
                "normalizedComponentCounts": {"schemas": 1},
            }
            manifest_entry = {
                "id": "probe",
                "sha256": source_hash,
                "apiVersion": "1",
                "pathCount": 1,
                "operationCount": 1,
                "schemaCount": 1,
            }
            audit = AUDIT.audit_corpus(
                "probe",
                results_dir,
                manifest_entry,
                profile_entry,
                {"sourceDefects": [], "vendorExtensionDispositions": {}},
                collections.defaultdict(list),
            )

            self.assertFalse(audit["passed"])
            self.assertTrue(
                any("source-to-first" in error for error in audit["errors"]),
                audit["errors"],
            )


if __name__ == "__main__":
    unittest.main()
