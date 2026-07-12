#!/usr/bin/env python3
"""Inventory the reviewed OpenAPI surface of the pinned SIX corpus.

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


CORPUS_IDS = (
    "okta",
    "petstore-v2",
    "petstore-v3",
    "twilio",
    "square",
    "docusign",
)
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
    "oauth2", "openIdConnect", "openIdConnectUrl", "openapi", "operationId", "parameters", "password",
    "pathItems", "paths", "pattern", "patternProperties", "prefixItems", "produces", "properties",
    "propertyNames", "readOnly", "refreshUrl", "requestBodies", "requestBody", "required", "responses",
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


def parse_args():
    root = pathlib.Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile", type=pathlib.Path, default=root / "corpus" / "six-profile.json")
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


def load_json(path):
    try:
        with path.open(encoding="utf-8") as source:
            value = json.load(source)
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise ValueError(f"cannot read {path}: {error}") from error
    if not isinstance(value, dict):
        raise ValueError(f"{path}: root must be a JSON object")
    return value


def parse_overrides(values):
    overrides = {}
    for value in values:
        corpus_id, separator, path = value.partition("=")
        if not separator or corpus_id not in CORPUS_IDS or corpus_id in overrides:
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


def inventory(manifest_path, corpus_dir, overrides):
    manifest = load_json(manifest_path)
    entries = {item["id"]: item for item in manifest.get("corpora", [])}
    walker = Walker()
    corpora = []
    normalized_totals = collections.Counter()
    source_totals = collections.Counter()

    for corpus_id in CORPUS_IDS:
        if corpus_id not in entries:
            raise ValueError(f"manifest is missing SIX corpus '{corpus_id}'")
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
    }


def main():
    args = parse_args()
    try:
        observed = inventory(args.manifest, args.corpus_dir, parse_overrides(args.document))
        if args.observed:
            print(json.dumps(observed, indent=2, sort_keys=True))
            return 0
        profile = load_json(args.profile)
    except (ValueError, OSError, UnicodeError, json.JSONDecodeError) as error:
        print(f"roundtrip-inventory: {error}", file=sys.stderr)
        return 2

    errors = []
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
