#!/usr/bin/env python3
"""Inventory the reviewed OpenAPI surface of the verified corpus profile.

The profile is intentionally hand-reviewed. This tool computes deterministic
facts from the artifacts and fails when the facts, keyword vocabulary, or
vendor-extension disposition no longer match that review.
"""

import argparse
import collections
import hashlib
import json
import pathlib
import sys


METHODS = {"get", "put", "post", "delete", "patch", "head", "options", "trace"}
COMPONENT_NAMESPACES = {
    "callbacks",
    "examples",
    "headers",
    "links",
    "parameters",
    "pathItems",
    "requestBodies",
    "responses",
    "schemas",
    "securitySchemes",
}

# OpenAPI 2.0/3.x and the JSON Schema vocabulary exercised by API descriptions.
# Dynamic map keys and opaque example/default/extension payloads are handled by
# the walker rather than admitted here.
STANDARD_KEYWORDS = {
    "$anchor", "$comment", "$defs", "$dynamicAnchor", "$dynamicRef", "$id",
    "$ref", "$schema", "$vocabulary", "additionalItems", "additionalProperties",
    "allOf", "allowEmptyValue", "allowReserved", "anyOf", "apiKey", "authorizationCode", "authorizationUrl",
    "basePath", "bearerFormat", "callbacks", "clientCredentials", "collectionFormat",
    "components", "const", "consume", "consumes", "contact", "content", "contentEncoding",
    "contentMediaType", "contentSchema", "cookie", "default", "definitions", "dependentRequired",
    "dependentSchemas", "deprecated", "description", "discriminator", "email", "encoding", "enum",
    "example", "examples", "exclusiveMaximum", "exclusiveMinimum", "explode", "externalDocs",
    "externalValue", "flow", "flows", "format", "formData", "headers", "host", "http", "id", "identifier",
    "implicit", "in", "info", "items", "jsonSchemaDialect", "license", "links", "mapping", "maxContains",
    "maximum", "maxItems", "maxLength", "maxProperties", "mediaType", "minContains", "minimum",
    "minItems", "minLength", "minProperties", "multipleOf", "mutualTLS", "name", "not", "nullable",
    "oauth2", "oneOf", "openIdConnect", "openIdConnectUrl", "openapi", "operationId", "parameters", "password",
    "pathItems", "paths", "pattern", "patternProperties", "prefixItems", "produces", "properties",
    "propertyName", "propertyNames", "readOnly", "refreshUrl", "requestBodies", "requestBody", "required", "responses",
    "schema", "schemas", "scheme", "schemes", "scopes", "security", "securityDefinitions", "securitySchemes",
    "servers", "source", "style", "summary", "swagger", "tags", "termsOfService", "title", "tokenUrl",
    "type", "unevaluatedItems", "unevaluatedProperties", "uniqueItems", "url", "variables", "version",
    "webhooks", "wrapped", "writeOnly", "xml",
}
OPAQUE_KEYS = {"const", "default", "enum", "example"}
MAP_KEYS = {
    "$defs", "callbacks", "content", "definitions", "dependentSchemas", "encoding", "headers", "links",
    "pathItems", "paths", "patternProperties", "properties", "requestBodies", "schemas",
    "securityDefinitions", "securitySchemes", "variables",
}
CARRIER_SHAPES = (
    "empty-schema",
    "object-no-properties-additional-properties-false",
    "object-no-properties-additional-properties-omitted",
    "object-no-properties-additional-properties-schema",
    "object-no-properties-additional-properties-true",
    "object-properties-additional-properties-false",
    "object-properties-additional-properties-omitted",
    "object-properties-additional-properties-schema",
    "object-properties-additional-properties-true",
    "nullable-composition-branch",
    "nested-discriminator",
    "explicit-discriminator",
    "parameter-content",
    "encoding-object",
    "external-value-example",
    "component-header",
    "component-example",
    "cross-path-reference",
)

RECORD_EXPLICIT_OPEN_PROOF = (
    "GeneratedCarrierFidelityTests.Explicit_open_object_preserves_additional_members_at_runtime"
)
RECORD_IMPLICIT_OPEN_PROOF = (
    "GeneratedCarrierFidelityTests.Implicit_open_object_preserves_additional_members_at_runtime"
)
RECORD_CLOSED_PROOF = (
    "OpaqueCarrierFidelityTests.Closed_object_preserves_every_valid_member_at_runtime"
)
RECORD_SCHEMA_OPEN_PROOF = (
    "OpaqueCarrierFidelityTests.Open_record_preserves_typed_named_and_schema_valued_additional_properties"
)
DICTIONARY_PROOF = (
    "OpaqueCarrierFidelityTests.Inline_free_form_carriers_preserve_valid_runtime_values_and_emitted_openness"
)
CLOSED_DICTIONARY_PROOF = (
    "OpaqueCarrierFidelityTests.Inline_propertyless_closed_object_preserves_empty_value_and_emitted_closure"
)
PROPERTYLESS_RECORD_PROOF = (
    "OpaqueCarrierFidelityTests.Propertyless_named_objects_keep_open_and_closed_runtime_carriers"
)
SCHEMA_DICTIONARY_PROOF = (
    "OpaqueCarrierFidelityTests.MaxProperties_dictionary_preserves_a_valid_value_and_emits_the_constraint"
)
NULLABLE_UNION_PROOF = (
    "GeneratedCarrierFidelityTests.Box_nullable_enum_union_preserves_valid_values_at_runtime"
)
NULLABLE_COMPOSITION_PROOF = (
    "GeneratedCarrierFidelityTests.Nullable_all_of_record_and_scalar_round_trip_at_runtime"
)
DISCRIMINATOR_PROOF = (
    "GeneratedCarrierFidelityTests.Spotify_nested_track_episode_discriminator_dispatches_episode_at_runtime"
)
PARAMETER_CONTENT_PROOF = "RoundTripDiffTests.Parameter_Surface_Mutations_Are_Reported"
ENCODING_PROOF = "RoundTripDiffTests.Request_Body_Surface_Mutations_Are_Reported"
EXTERNAL_VALUE_PROOF = (
    "OpenApiReferenceNormalizationTests.ExternalValue_Example_Is_Reported_As_Unsupported"
)
COMPONENT_EXAMPLE_PROOF = (
    "CliPipelineTests.Cli_Disk_Pipeline_Preserves_Component_Referenced_By_Schema_Example"
)
COMPONENT_HEADER_PROOF = "RoundTripDiffTests.Component_Namespace_Identity_Mutations_Are_Reported"
CROSS_PATH_REFERENCE_PROOF = (
    "RoundTripDiffTests.Integrity_Findings_Include_Refs_Security_Path_Parameters_And_Operation_Ids"
)


def parse_args():
    root = pathlib.Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile", type=pathlib.Path, default=root / "corpus" / "verified-profile.json")
    parser.add_argument("--manifest", type=pathlib.Path, default=root / "corpus" / "openapi-manifest.json")
    parser.add_argument("--corpus-dir", type=pathlib.Path, default=root / "openapi")
    parser.add_argument(
        "--document",
        action="append",
        default=[],
        metavar="ID=PATH",
        help="override one corpus artifact (used by mutation controls)",
    )
    parser.add_argument("--observed", action="store_true", help="print observed facts without checking")
    parser.add_argument(
        "--update-profile",
        action="store_true",
        help="replace profile facts and reviewed hashes with the current reviewed roster",
    )
    parser.add_argument(
        "--approve-disposition-change",
        action="store_true",
        help="explicitly approve changed vendor-extension dispositions during --update-profile",
    )
    return parser.parse_args()


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True)


def pointer_part(value):
    return value.replace("~", "~0").replace("/", "~1")


def value_shape(value):
    if value is None:
        return "null"
    if isinstance(value, bool):
        return "boolean"
    if isinstance(value, (int, float)):
        return "number"
    if isinstance(value, str):
        return "string"
    if isinstance(value, list):
        return "array"
    return "object"


class Walker:
    def __init__(self):
        self.keywords = collections.Counter()
        self.extensions = collections.defaultdict(list)
        self.unknown = []

    def walk(self, value, pointer="", mode="object"):
        if isinstance(value, list):
            for index, item in enumerate(value):
                self.walk(item, f"{pointer}/{index}", mode)
            return
        if not isinstance(value, dict):
            return
        if mode == "opaque":
            return
        if mode == "map":
            for name, item in sorted(value.items()):
                self.walk(item, f"{pointer}/{pointer_part(name)}")
            return
        if mode == "security":
            for name, scopes in sorted(value.items()):
                if not isinstance(scopes, list):
                    self.unknown.append(f"{pointer}/{pointer_part(name)} (security scopes are not an array)")
            return
        if mode == "components":
            for name, item in sorted(value.items()):
                child_pointer = f"{pointer}/{pointer_part(name)}"
                if name.lower().startswith("x-"):
                    self._extension(name, item, child_pointer)
                elif name not in COMPONENT_NAMESPACES:
                    self.unknown.append(child_pointer)
                else:
                    self.keywords[name] += 1
                    self.walk(item, child_pointer, "map")
            return

        for name, item in sorted(value.items()):
            child_pointer = f"{pointer}/{pointer_part(name)}"
            if name.lower().startswith("x-"):
                self._extension(name, item, child_pointer)
                continue
            if name not in STANDARD_KEYWORDS and name not in METHODS:
                self.unknown.append(child_pointer)
                continue
            self.keywords[name] += 1
            if name in OPAQUE_KEYS or name == "examples":
                self.walk(item, child_pointer, "opaque")
            elif name == "components":
                self.walk(item, child_pointer, "components")
            elif name == "security":
                self.walk(item, child_pointer, "security")
            elif name in {"mapping", "scopes", "dependentRequired"}:
                self.walk(item, child_pointer, "opaque")
            elif name == "responses" or name in MAP_KEYS:
                self.walk(item, child_pointer, "map")
            elif name == "parameters" and isinstance(item, dict):
                self.walk(item, child_pointer, "map")
            else:
                self.walk(item, child_pointer)

    def _extension(self, name, value, pointer):
        self.extensions[name].append((pointer, value_shape(value), canonical(value)))


def json_pointer(pointer):
    return f"#{pointer}" if pointer else "#"


def is_component_schema_root(pointer):
    parts = pointer.split("/")[1:]
    return (
        len(parts) == 3 and parts[0] == "components" and parts[1] == "schemas"
    ) or (len(parts) == 2 and parts[0] == "definitions")


def resolve_local_reference(document, reference):
    if not isinstance(reference, str) or not reference.startswith("#/"):
        return None
    value = document
    for raw_part in reference[2:].split("/"):
        part = raw_part.replace("~1", "/").replace("~0", "~")
        if not isinstance(value, dict) or part not in value:
            return None
        value = value[part]
    return value


def additional_properties_status(schema):
    if "additionalProperties" not in schema:
        return "omitted"
    value = schema["additionalProperties"]
    if value is True:
        return "true"
    if value is False:
        return "false"
    return "schema"


def admits_null(schema):
    if not isinstance(schema, dict):
        return schema is None
    if schema.get("nullable") is True:
        return True
    schema_type = schema.get("type")
    if schema_type == "null" or isinstance(schema_type, list) and "null" in schema_type:
        return True
    return isinstance(schema.get("enum"), list) and None in schema["enum"]


def has_carrier_shape(schema):
    return isinstance(schema, dict) and any(
        key in schema
        for key in (
            "$ref",
            "type",
            "properties",
            "additionalProperties",
            "items",
            "oneOf",
            "anyOf",
            "allOf",
            "enum",
        )
    )


def group_carrier_occurrences(occurrences):
    groups = collections.defaultdict(list)
    for occurrence in occurrences:
        key = (
            occurrence["corpusId"],
            occurrence["shape"],
            occurrence["carrier"],
            occurrence["behaviorTest"],
        )
        groups[key].append(occurrence["ownerPointer"])
    return [
        {
            "corpusId": corpus_id,
            "shape": shape,
            "carrier": carrier,
            "behaviorTest": behavior_test,
            "ownerPointers": sorted(pointers),
        }
        for (corpus_id, shape, carrier, behavior_test), pointers in sorted(groups.items())
    ]


class CarrierWalker:
    def __init__(self, corpus_id, document):
        self.corpus_id = corpus_id
        self.document = document
        self.occurrences = []

    def collect(self):
        self._collect_components()
        self.walk(self.document)
        return sorted(
            self.occurrences,
            key=lambda item: (
                item["corpusId"],
                item["ownerPointer"],
                item["shape"],
                item["carrier"],
            ),
        )

    def add(self, pointer, shape, carrier, behavior_test):
        self.occurrences.append(
            {
                "corpusId": self.corpus_id,
                "ownerPointer": json_pointer(pointer),
                "shape": shape,
                "carrier": carrier,
                "behaviorTest": behavior_test,
            }
        )

    def _collect_components(self):
        components = self.document.get("components")
        if not isinstance(components, dict):
            return
        for namespace, shape, proof in (
            ("headers", "component-header", COMPONENT_HEADER_PROOF),
            ("examples", "component-example", COMPONENT_EXAMPLE_PROOF),
        ):
            values = components.get(namespace)
            if not isinstance(values, dict):
                continue
            for name in sorted(values):
                self.add(
                    f"/components/{namespace}/{pointer_part(name)}",
                    shape,
                    "provenance-only",
                    proof,
                )

    def walk(
        self,
        value,
        pointer="",
        schema_context=False,
        direct_all_of_branch=False,
    ):
        if isinstance(value, list):
            for index, item in enumerate(value):
                self.walk(item, f"{pointer}/{index}", schema_context=schema_context)
            return
        if not isinstance(value, dict):
            return

        self._inspect(value, pointer, schema_context, direct_all_of_branch)
        for name, item in sorted(value.items()):
            child_pointer = f"{pointer}/{pointer_part(name)}"
            if name.lower().startswith("x-") or name in OPAQUE_KEYS:
                continue
            if name == "examples":
                self._walk_examples(item, child_pointer)
                continue
            if name in {
                "$defs",
                "definitions",
                "dependentSchemas",
                "patternProperties",
                "properties",
                "schemas",
            } and isinstance(item, dict):
                for child_name, child in sorted(item.items()):
                    self.walk(
                        child,
                        f"{child_pointer}/{pointer_part(child_name)}",
                        schema_context=True,
                    )
                continue
            if name in {"allOf", "anyOf", "oneOf", "prefixItems"} and isinstance(
                item, list
            ):
                for index, child in enumerate(item):
                    self.walk(
                        child,
                        f"{child_pointer}/{index}",
                        schema_context=True,
                        direct_all_of_branch=name == "allOf",
                    )
                continue
            self.walk(
                item,
                child_pointer,
                schema_context=name
                in {
                    "additionalProperties",
                    "contains",
                    "contentSchema",
                    "items",
                    "not",
                    "propertyNames",
                    "schema",
                    "unevaluatedProperties",
                },
            )

    def _walk_examples(self, value, pointer):
        if not isinstance(value, dict):
            return
        for name, example in sorted(value.items()):
            if isinstance(example, dict) and "externalValue" in example:
                self.add(
                    f"{pointer}/{pointer_part(name)}",
                    "external-value-example",
                    "provenance-only",
                    EXTERNAL_VALUE_PROOF,
                )

    def _inspect(self, value, pointer, schema_context, direct_all_of_branch):
        if schema_context and not value:
            self.add(pointer, "empty-schema", "JsonElement", DICTIONARY_PROOF)

        properties = value.get("properties")
        if isinstance(properties, dict) and properties:
            status = additional_properties_status(value)
            carrier = self._object_carrier(pointer, status)
            if carrier == "dictionary":
                proof = DICTIONARY_PROOF
            elif status == "omitted":
                proof = RECORD_IMPLICIT_OPEN_PROOF
            elif status == "false":
                proof = RECORD_CLOSED_PROOF
            elif status == "schema":
                proof = RECORD_SCHEMA_OPEN_PROOF
            else:
                proof = RECORD_EXPLICIT_OPEN_PROOF
            self.add(
                pointer,
                f"object-properties-additional-properties-{status}",
                carrier,
                proof,
            )
        elif (
            isinstance(properties, dict)
            or "additionalProperties" in value
            or value.get("type") == "object"
        ):
            status = additional_properties_status(value)
            component_record = direct_all_of_branch or (
                is_component_schema_root(pointer) and status in {"omitted", "false"}
            )
            if component_record:
                carrier = "record" if status == "false" else "extension-data record"
                proof = (
                    RECORD_SCHEMA_OPEN_PROOF
                    if status == "schema"
                    else PROPERTYLESS_RECORD_PROOF
                )
            else:
                carrier = "dictionary"
                proof = (
                    SCHEMA_DICTIONARY_PROOF
                    if status == "schema"
                    else CLOSED_DICTIONARY_PROOF
                    if status == "false"
                    else DICTIONARY_PROOF
                )
            self.add(
                pointer,
                f"object-no-properties-additional-properties-{status}",
                carrier,
                proof,
            )

        for composition in ("oneOf", "anyOf", "allOf"):
            branches = value.get(composition)
            if not isinstance(branches, list):
                continue
            for index, branch in enumerate(branches):
                if admits_null(branch):
                    branch_pointer = f"{pointer}/{composition}/{index}"
                    carrier = (
                        "union"
                        if composition in {"oneOf", "anyOf"}
                        else self._all_of_carrier(value, pointer)
                    )
                    self.add(
                        branch_pointer,
                        "nullable-composition-branch",
                        carrier,
                        NULLABLE_UNION_PROOF
                        if carrier == "union"
                        else NULLABLE_COMPOSITION_PROOF,
                    )

        if isinstance(value.get("discriminator"), dict):
            nested = not is_component_schema_root(pointer)
            self.add(
                pointer,
                "nested-discriminator" if nested else "explicit-discriminator",
                "union" if isinstance(value.get("oneOf"), list) or isinstance(value.get("anyOf"), list) else "record",
                DISCRIMINATOR_PROOF,
            )

        if (
            isinstance(value.get("name"), str)
            and isinstance(value.get("in"), str)
            and isinstance(value.get("content"), dict)
        ):
            schemas = [
                media.get("schema")
                for media in value["content"].values()
                if isinstance(media, dict) and isinstance(media.get("schema"), dict)
            ]
            carrier = (
                self._schema_carrier(schemas[0], pointer)
                if len(schemas) == 1
                else "provenance-only"
            )
            self.add(pointer, "parameter-content", carrier, PARAMETER_CONTENT_PROOF)

        encoding = value.get("encoding")
        if isinstance(encoding, dict):
            for name, item in sorted(encoding.items()):
                if isinstance(item, dict):
                    self.add(
                        f"{pointer}/encoding/{pointer_part(name)}",
                        "encoding-object",
                        "provenance-only",
                        ENCODING_PROOF,
                    )

        if isinstance(value.get("externalValue"), str) and "/examples/" not in pointer:
            self.add(pointer, "external-value-example", "provenance-only", EXTERNAL_VALUE_PROOF)

        reference = value.get("$ref")
        if isinstance(reference, str) and reference.startswith("#/paths/"):
            self.add(
                f"{pointer}/$ref",
                "cross-path-reference",
                "provenance-only",
                CROSS_PATH_REFERENCE_PROOF,
            )

    def _object_carrier(self, pointer, status):
        return "record" if status == "false" else "extension-data record"

    def _all_of_carrier(self, schema, pointer):
        for index, branch in enumerate(schema.get("allOf", [])):
            if not has_carrier_shape(branch):
                continue
            carrier = self._schema_carrier(branch, f"{pointer}/allOf/{index}")
            if carrier != "provenance-only":
                return carrier
        return "record"

    def _schema_carrier(self, schema, pointer, depth=0):
        if depth > 32:
            return "provenance-only"
        reference = schema.get("$ref")
        if reference is not None:
            resolved = resolve_local_reference(self.document, reference)
            if isinstance(resolved, dict):
                return self._schema_carrier(resolved, reference[1:], depth + 1)
            return "provenance-only"
        if isinstance(schema.get("oneOf"), list) or isinstance(schema.get("anyOf"), list):
            return "union"
        properties = schema.get("properties")
        if isinstance(properties, dict) and properties:
            return self._object_carrier(pointer, additional_properties_status(schema))
        if schema.get("type") == "object" or "additionalProperties" in schema:
            return "record" if is_component_schema_root(pointer) else "dictionary"
        if isinstance(schema.get("allOf"), list):
            for index, branch in enumerate(schema["allOf"]):
                if not isinstance(branch, dict) or admits_null(branch):
                    continue
                carrier = self._schema_carrier(branch, f"{pointer}/allOf/{index}", depth + 1)
                if carrier != "provenance-only":
                    return carrier
        if "type" not in schema:
            return "JsonElement"
        return "scalar"


def load_json(path):
    try:
        with path.open(encoding="utf-8") as source:
            value = json.load(source)
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise ValueError(f"cannot read {path}: {error}") from error
    if not isinstance(value, dict):
        raise ValueError(f"{path}: root must be a JSON object")
    return value


def parse_overrides(values, corpus_ids):
    overrides = {}
    for value in values:
        corpus_id, separator, path = value.partition("=")
        if not separator or corpus_id not in corpus_ids or corpus_id in overrides:
            raise ValueError(f"invalid --document value: {value}")
        overrides[corpus_id] = pathlib.Path(path)
    return overrides


def component_counts(document):
    counts = collections.Counter()
    source_counts = collections.Counter()
    for source, normalized in (
        ("definitions", "schemas"),
        ("parameters", "parameters"),
        ("responses", "responses"),
        ("securityDefinitions", "securitySchemes"),
    ):
        value = document.get(source)
        if isinstance(value, dict):
            source_counts[source] = len(value)
            counts[normalized] += len(value)
    components = document.get("components", {})
    if isinstance(components, dict):
        for namespace, value in components.items():
            if isinstance(value, dict) and not namespace.lower().startswith("x-"):
                source_counts[f"components.{namespace}"] = len(value)
                counts[namespace] += len(value)
    return dict(sorted(source_counts.items())), dict(sorted(counts.items()))


def operation_count(document):
    total = 0
    for path_item in document.get("paths", {}).values():
        if isinstance(path_item, dict):
            total += sum(name in METHODS for name in path_item)
    return total


def inventory(manifest_path, corpus_dir, overrides, corpus_ids):
    manifest = load_json(manifest_path)
    entries = {item["id"]: item for item in manifest.get("corpora", [])}
    walker = Walker()
    corpora = []
    normalized_totals = collections.Counter()
    source_totals = collections.Counter()
    carrier_occurrences = []

    for corpus_id in corpus_ids:
        if corpus_id not in entries:
            raise ValueError(f"manifest is missing verified corpus '{corpus_id}'")
        entry = entries[corpus_id]
        path = overrides.get(corpus_id, corpus_dir / entry["file"])
        data = path.read_bytes()
        document = json.loads(data)
        if not isinstance(document, dict):
            raise ValueError(f"{path}: root must be a JSON object")
        source_components, normalized_components = component_counts(document)
        source_totals.update(source_components)
        normalized_totals.update(normalized_components)
        before_unknown = len(walker.unknown)
        walker.walk(document)
        carrier_occurrences.extend(CarrierWalker(corpus_id, document).collect())
        corpora.append(
            {
                "id": corpus_id,
                "sha256": hashlib.sha256(data).hexdigest(),
                "dialect": document.get("openapi", document.get("swagger")),
                "apiVersion": document.get("info", {}).get("version"),
                "pathCount": len(document.get("paths", {})),
                "operationCount": operation_count(document),
                "sourceComponentCounts": source_components,
                "normalizedComponentCounts": normalized_components,
                "unknownKeywordCount": len(walker.unknown) - before_unknown,
            }
        )

    extensions = {}
    for name, occurrences in sorted(walker.extensions.items()):
        values = sorted({value for _, _, value in occurrences})
        extensions[name] = {
            "count": len(occurrences),
            "ownerPointers": sorted(
                pointer.rpartition("/")[0] or "/" for pointer, _, _ in occurrences
            ),
            "valueShapes": dict(sorted(collections.Counter(shape for _, shape, _ in occurrences).items())),
            "distinctValueCount": len(values),
            "valuesSha256": hashlib.sha256("\n".join(values).encode()).hexdigest(),
        }

    return {
        "corpora": corpora,
        "sourceComponentTotals": dict(sorted(source_totals.items())),
        "normalizedComponentTotals": dict(sorted(normalized_totals.items())),
        "standardKeywordCounts": dict(sorted(walker.keywords.items())),
        "extensions": extensions,
        "unknownKeywords": sorted(walker.unknown),
        "carrierSensitiveGroups": group_carrier_occurrences(carrier_occurrences),
        "carrierSensitiveCounts": dict(
            sorted(
                {
                    shape: sum(
                        occurrence["shape"] == shape
                        for occurrence in carrier_occurrences
                    )
                    for shape in CARRIER_SHAPES
                }.items()
            )
        ),
    }


def validate_profile(profile, manifest):
    errors = []
    roster = profile.get("verifiedCorpusIds")
    if not isinstance(roster, list) or not roster or any(not isinstance(item, str) for item in roster):
        raise ValueError("profile verifiedCorpusIds must be a non-empty string array")
    if len(roster) != len(set(roster)):
        errors.append("verified roster contains duplicate corpus IDs")

    facts = profile.get("facts", {})
    fact_corpora = facts.get("corpora", []) if isinstance(facts, dict) else []
    fact_ids = [item.get("id") for item in fact_corpora if isinstance(item, dict)]
    if roster != fact_ids:
        errors.append("verified roster does not match profile facts")

    manifest_corpora = manifest.get("corpora", [])
    if profile.get("manifestCorpusCount") != len(manifest_corpora):
        errors.append(
            f"manifest corpus denominator changed: expected {profile.get('manifestCorpusCount')}, "
            f"observed {len(manifest_corpora)}"
        )

    source_defects = profile.get("sourceDefects", [])
    if not isinstance(source_defects, list):
        raise ValueError("profile sourceDefects must be an array")
    source_defect_sha256 = hashlib.sha256(canonical(source_defects).encode()).hexdigest()
    if profile.get("reviewedSourceDefectsSha256") != source_defect_sha256:
        errors.append("reviewed source-defect policy changed")
    required_keys = {
        "corpusId", "sourceSha256", "pointer", "reason", "diagnostic", "cardinality"
    }
    for defect in source_defects:
        if not isinstance(defect, dict) or set(defect) != required_keys:
            errors.append("source-defect policy entry has an invalid shape")
            continue
        if not isinstance(defect["cardinality"], int) or defect["cardinality"] < 1:
            errors.append("source-defect policy cardinality must be a positive integer")

    return roster, errors


def main():
    args = parse_args()
    try:
        if args.approve_disposition_change and not args.update_profile:
            raise ValueError("--approve-disposition-change requires --update-profile")
        profile = load_json(args.profile)
        manifest = load_json(args.manifest)
        corpus_ids, profile_errors = validate_profile(profile, manifest)
        observed = inventory(
            args.manifest,
            args.corpus_dir,
            parse_overrides(args.document, set(corpus_ids)),
            corpus_ids,
        )
        if args.update_profile:
            if profile_errors:
                raise ValueError("; ".join(profile_errors))
            if observed["unknownKeywords"]:
                raise ValueError("cannot update a profile with unknown keywords")
            dispositions = profile.get("vendorExtensionDispositions", {})
            disposition_sha256 = hashlib.sha256(canonical(dispositions).encode()).hexdigest()
            if (
                profile.get("reviewedDispositionSha256") != disposition_sha256
                and not args.approve_disposition_change
            ):
                raise ValueError("reviewed vendor-extension disposition changed")
            observed_extensions = set(observed["extensions"])
            reviewed_extensions = set(dispositions)
            if observed_extensions != reviewed_extensions:
                missing = sorted(observed_extensions - reviewed_extensions)
                stale = sorted(reviewed_extensions - observed_extensions)
                raise ValueError(
                    "cannot update an incomplete extension review: "
                    f"unreviewed={missing}, stale={stale}"
                )
            for name, review in dispositions.items():
                if review.get("disposition") not in {"preserve", "map", "exclude"}:
                    raise ValueError(f"invalid disposition for {name}")
                if not review.get("evidence"):
                    raise ValueError(f"missing disposition evidence for {name}")
            profile["facts"] = observed
            profile["reviewedDispositionSha256"] = hashlib.sha256(
                canonical(dispositions).encode()
            ).hexdigest()
            args.profile.write_text(json.dumps(profile, indent=2) + "\n", encoding="utf-8")
            print(f"roundtrip-inventory: updated {args.profile}")
            return 0
        if args.observed:
            print(json.dumps(observed, indent=2, sort_keys=True))
            return 0
    except (ValueError, OSError, UnicodeError, json.JSONDecodeError) as error:
        print(f"roundtrip-inventory: {error}", file=sys.stderr)
        return 2

    errors = list(profile_errors)
    if observed["unknownKeywords"]:
        errors.extend(f"unknown keyword: {pointer}" for pointer in observed["unknownKeywords"])
    dispositions = profile.get("vendorExtensionDispositions", {})
    disposition_sha256 = hashlib.sha256(canonical(dispositions).encode()).hexdigest()
    if profile.get("reviewedDispositionSha256") != disposition_sha256:
        errors.append("reviewed vendor-extension disposition changed")
    observed_extensions = set(observed["extensions"])
    reviewed_extensions = set(dispositions)
    for name in sorted(observed_extensions - reviewed_extensions):
        errors.append(f"unknown extension: {name}")
    for name in sorted(reviewed_extensions - observed_extensions):
        errors.append(f"reviewed extension disappeared: {name}")
    for name, review in sorted(dispositions.items()):
        if review.get("disposition") not in {"preserve", "map", "exclude"}:
            errors.append(f"invalid disposition for {name}")
        if not review.get("evidence"):
            errors.append(f"missing disposition evidence for {name}")
    expected = profile.get("facts")
    if expected != observed:
        errors.append("profile facts changed")

    result = {
        "passed": not errors,
        "errors": errors,
        "facts": observed,
        "vendorExtensionDispositions": dispositions,
    }
    print(json.dumps(result, indent=2, sort_keys=True))
    return 0 if not errors else 1


if __name__ == "__main__":
    sys.exit(main())
