#!/usr/bin/env python3
"""Focused mutation/control tests for roundtrip-audit.py."""

import importlib.util
import pathlib
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


if __name__ == "__main__":
    unittest.main()
