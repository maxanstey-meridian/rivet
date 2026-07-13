#!/usr/bin/env python3
"""Focused mutation/control tests for roundtrip-audit.py."""

import importlib.util
import pathlib
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
        first = [("string", '"public"'), ("string", '"beta"'), ("string", '"public"')]
        reordered = list(reversed(first))
        mutated = [("string", '"public"'), ("string", '"beta"'), ("string", '"beta"')]

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


if __name__ == "__main__":
    unittest.main()
