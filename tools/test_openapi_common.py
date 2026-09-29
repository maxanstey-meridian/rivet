"""Unit tests for the OpenAPI helpers shared by the round-trip tools."""

import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).parent))

import openapi_common  # noqa: E402
import roundtrip_diff  # noqa: E402
import roundtrip_inventory  # noqa: E402


DOCUMENT = {
    "components": {"schemas": {"a/b": {"type": "string"}, "c~d": {"type": "integer"}}},
    "paths": {"/items": {"get": {"parameters": [{"name": "id"}]}}},
}


class LocalReferenceTests(unittest.TestCase):
    def test_references_resolve_per_rfc_6901_with_percent_decoding_per_token(self):
        cases = {
            "#/components/schemas/a~1b": {"type": "string"},
            "#/components/schemas/a%2Fb": {"type": "string"},
            "#/components/schemas/c~0d": {"type": "integer"},
            "#/components/schemas/c%7Ed": {"type": "integer"},
            "#/paths/~1items/get/parameters/0": {"name": "id"},
            "#/paths/~1items/get/parameters/1": None,
            "#/components/schemas/missing": None,
            "components/schemas/a~1b": None,
        }
        for reference, expected in cases.items():
            with self.subTest(reference=reference):
                self.assertEqual(
                    expected, openapi_common.resolve_local_reference(DOCUMENT, reference)
                )

    def test_diff_and_inventory_share_one_resolver(self):
        # BUG-12: the comparator percent-decoded tokens and the inventory did not.
        self.assertIs(
            roundtrip_diff.resolve_local_reference, roundtrip_inventory.resolve_local_reference
        )
        self.assertEqual(
            {"type": "string"},
            roundtrip_inventory.resolve_local_reference(DOCUMENT, "#/components/schemas/a%2Fb"),
        )


class ComponentCountTests(unittest.TestCase):
    def test_counts_only_openapi_component_namespaces(self):
        document = {
            "definitions": {"A": {}},
            "components": {"schemas": {"B": {}}, "x-vendor": {"C": {}}, "custom": {"D": {}}},
        }
        self.assertEqual(
            ({"components.schemas": 1, "definitions": 1}, {"schemas": 2}),
            roundtrip_inventory.component_counts(document),
        )


if __name__ == "__main__":
    unittest.main()
