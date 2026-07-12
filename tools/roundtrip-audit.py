#!/usr/bin/env python3
"""Reproduce a physical audit of the retained SIX round-trip artifacts."""

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
COMPONENT_NAMESPACES = (
    "schemas",
    "responses",
    "parameters",
    "examples",
    "requestBodies",
    "headers",
    "securitySchemes",
    "links",
    "callbacks",
    "pathItems",
)
OPAQUE_KEYS = {"const", "default", "enum", "example", "examples"}
SUMMARY_FINDING_KEYS = (
    "documentFindings",
    "integrityFindings",
    "opFindings",
    "schemaFindings",
)
SUMMARY_ZERO_KEYS = (
    "inventedOperations",
    "missingOperations",
    "operationsWithFindings",
    "unmatchedOriginalComponents",
    "unmatchedOriginalSchemas",
    "unmatchedReemittedComponents",
    "unmatchedReemittedSchemas",
)
RESULT_CATEGORIES = {
    "inventory",
    "artifact",
    "sourceDefects",
    "diagnostics",
    "markers",
    "compilation",
    "document",
    "operation",
    "schema",
    "integrity",
    "fixedPoint",
}
TWILIO_DEFAULT_EQUIVALENT_PATHS = {
    "/components/schemas/api.v2010.account/properties/subresource_uris/additionalProperties",
    "/components/schemas/api.v2010.account.available_phone_number_country/properties/subresource_uris/additionalProperties",
    "/components/schemas/api.v2010.account.call/properties/subresource_uris/additionalProperties",
    "/components/schemas/api.v2010.account.conference/properties/subresource_uris/additionalProperties",
    "/components/schemas/api.v2010.account.incoming_phone_number.incoming_phone_number_assigned_add_on/properties/subresource_uris/additionalProperties",
    "/components/schemas/api.v2010.account.message/properties/subresource_uris/additionalProperties",
    "/components/schemas/api.v2010.account.recording/properties/subresource_uris/additionalProperties",
    "/components/schemas/api.v2010.account.recording.recording_add_on_result/properties/subresource_uris/additionalProperties",
    "/components/schemas/api.v2010.account.recording.recording_add_on_result.recording_add_on_result_payload/properties/subresource_uris/additionalProperties",
    "/components/schemas/api.v2010.account.sip.sip_credential_list/properties/subresource_uris/additionalProperties",
    "/components/schemas/api.v2010.account.sip.sip_domain/properties/subresource_uris/additionalProperties",
    "/components/schemas/api.v2010.account.sip.sip_ip_access_control_list/properties/subresource_uris/additionalProperties",
    "/components/schemas/api.v2010.account.usage.usage_record/properties/subresource_uris/additionalProperties",
    "/components/schemas/api.v2010.account.usage.usage_record.usage_record_all_time/properties/subresource_uris/additionalProperties",
    "/components/schemas/api.v2010.account.usage.usage_record.usage_record_daily/properties/subresource_uris/additionalProperties",
    "/components/schemas/api.v2010.account.usage.usage_record.usage_record_last_month/properties/subresource_uris/additionalProperties",
    "/components/schemas/api.v2010.account.usage.usage_record.usage_record_monthly/properties/subresource_uris/additionalProperties",
    "/components/schemas/api.v2010.account.usage.usage_record.usage_record_this_month/properties/subresource_uris/additionalProperties",
    "/components/schemas/api.v2010.account.usage.usage_record.usage_record_today/properties/subresource_uris/additionalProperties",
    "/components/schemas/api.v2010.account.usage.usage_record.usage_record_yearly/properties/subresource_uris/additionalProperties",
    "/components/schemas/api.v2010.account.usage.usage_record.usage_record_yesterday/properties/subresource_uris/additionalProperties",
}


def parse_args():
    root = pathlib.Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--results-dir", type=pathlib.Path, default=root / "TestResults" / "roundtrip")
    parser.add_argument("--manifest", type=pathlib.Path, default=root / "corpus" / "openapi-manifest.json")
    parser.add_argument("--profile", type=pathlib.Path, default=root / "corpus" / "six-profile.json")
    return parser.parse_args()


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True)


def load_json(path):
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise ValueError(f"cannot read {path}: {error}") from error
    if not isinstance(value, dict):
        raise ValueError(f"{path}: root must be a JSON object")
    return value


def sha256_bytes(data):
    return hashlib.sha256(data).hexdigest()


def operation_count(document):
    paths = document.get("paths", {})
    if not isinstance(paths, dict):
        return 0
    return sum(
        method in METHODS
        for path_item in paths.values()
        if isinstance(path_item, dict)
        for method in path_item
    )


def component_counts(document):
    counts = collections.Counter()
    for source, normalized in (
        ("definitions", "schemas"),
        ("parameters", "parameters"),
        ("responses", "responses"),
        ("securityDefinitions", "securitySchemes"),
    ):
        value = document.get(source)
        if isinstance(value, dict):
            counts[normalized] += len(value)
    components = document.get("components", {})
    if isinstance(components, dict):
        for namespace in COMPONENT_NAMESPACES:
            value = components.get(namespace)
            if isinstance(value, dict):
                counts[namespace] += len(value)
    return dict(sorted(counts.items()))


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


def collect_extensions(value, reviewed_names, found=None):
    """Collect reviewed OpenAPI extensions while ignoring opaque example payloads."""
    if found is None:
        found = collections.defaultdict(list)
    if isinstance(value, list):
        for item in value:
            collect_extensions(item, reviewed_names, found)
    elif isinstance(value, dict):
        for name, item in value.items():
            if name in reviewed_names:
                found[name].append((value_shape(item), canonical(item)))
            elif name not in OPAQUE_KEYS and not name.lower().startswith("x-"):
                collect_extensions(item, reviewed_names, found)
    return found


def extension_fact(occurrences):
    values = sorted({value for _, value in occurrences})
    return {
        "count": len(occurrences),
        "distinctValueCount": len(values),
        "valueShapes": dict(sorted(collections.Counter(shape for shape, _ in occurrences).items())),
        "valuesSha256": sha256_bytes("\n".join(values).encode()),
    }


def occurrence_digest(occurrences):
    values = sorted(value for _, value in occurrences)
    return len(values), sha256_bytes("\n".join(values).encode())


def pointer_part(value):
    return value.replace("~", "~0").replace("/", "~1")


def structural_differences(first, second, pointer=""):
    differences = []
    if type(first) is not type(second):
        return [{"path": pointer or "/", "kind": "changed", "first": first, "second": second}]
    if isinstance(first, dict):
        for key in sorted(first.keys() | second.keys()):
            child = f"{pointer}/{pointer_part(key)}"
            if key not in first:
                differences.append({"path": child, "kind": "added", "first": None, "second": second[key]})
            elif key not in second:
                differences.append({"path": child, "kind": "removed", "first": first[key], "second": None})
            else:
                differences.extend(structural_differences(first[key], second[key], child))
    elif isinstance(first, list):
        if len(first) != len(second):
            differences.append(
                {"path": pointer or "/", "kind": "array-length", "first": len(first), "second": len(second)}
            )
        for index, (left, right) in enumerate(zip(first, second)):
            differences.extend(structural_differences(left, right, f"{pointer}/{index}"))
    elif first != second:
        differences.append({"path": pointer or "/", "kind": "changed", "first": first, "second": second})
    return differences


def classify_fixed_point(corpus_id, differences):
    if not differences:
        return [], []
    if corpus_id != "twilio":
        return [], differences
    allowed = []
    rejected = []
    for difference in differences:
        if (
            difference["path"] in TWILIO_DEFAULT_EQUIVALENT_PATHS
            and difference["kind"] == "removed"
            and difference["first"] == {}
            and difference["second"] is None
        ):
            allowed.append(difference)
        else:
            rejected.append(difference)
    actual_paths = {item["path"] for item in allowed}
    if actual_paths != TWILIO_DEFAULT_EQUIVALENT_PATHS or len(allowed) != len(TWILIO_DEFAULT_EQUIVALENT_PATHS):
        rejected.append(
            {
                "path": "/",
                "kind": "equivalence-set",
                "first": len(TWILIO_DEFAULT_EQUIVALENT_PATHS),
                "second": len(allowed),
            }
        )
    return allowed, rejected


def compact_value(value):
    encoded = canonical(value)
    return encoded if len(encoded) <= 80 else f"{encoded[:77]}..."


def relative(path, results_dir):
    try:
        return path.relative_to(results_dir.parent.parent).as_posix()
    except ValueError:
        return path.as_posix()


def check_equal(errors, label, actual, expected):
    if actual != expected:
        errors.append(f"{label}: expected {expected!r}, observed {actual!r}")


def scan_generated(path, expected_operations, expected_extensions, errors):
    files = sorted(path.rglob("*.cs")) if path.is_dir() else []
    texts = []
    for file in files:
        try:
            texts.append(file.read_text(encoding="utf-8"))
        except (OSError, UnicodeError) as error:
            errors.append(f"cannot read generated C# {file}: {error}")
    text = "\n".join(texts)
    evidence = {
        "files": len(files),
        "bytes": sum(len(item.encode("utf-8")) for item in texts),
        "operationProvenance": text.count("[RivetOperationProvenance("),
        "vendorExtensions": text.count("[assembly: RivetVendorExtension("),
    }
    if not files:
        errors.append(f"{path}: no generated C# files")
    check_equal(errors, f"{path.name} operation provenance", evidence["operationProvenance"], expected_operations)
    check_equal(errors, f"{path.name} preserved extension evidence", evidence["vendorExtensions"], expected_extensions)
    return evidence


def check_summary(path, expected_operations, expected_components, expected_source_defects, errors):
    summary = load_json(path)
    for key in SUMMARY_FINDING_KEYS:
        check_equal(errors, f"{path.name} {key}", summary.get(key), {})
    for key in SUMMARY_ZERO_KEYS:
        check_equal(errors, f"{path.name} {key}", summary.get(key), 0)
    check_equal(errors, f"{path.name} source defects", summary.get("sourceDefects"), expected_source_defects)
    for key in ("originalOps", "reemittedOps", "sharedOps"):
        check_equal(errors, f"{path.name} {key}", summary.get(key), expected_operations)
    for key in ("originalComponents", "reemittedComponents", "matchedComponents"):
        check_equal(errors, f"{path.name} {key}", summary.get(key), expected_components)
    return summary


def check_result(result, corpus_id, expected_operations, expected_components, errors):
    check_equal(errors, "result corpusId", result.get("corpusId"), corpus_id)
    check_equal(errors, "result passed", result.get("passed"), True)
    categories = result.get("categories")
    if not isinstance(categories, dict):
        errors.append("result categories: expected object")
        categories = {}
    check_equal(errors, "result category names", sorted(categories), sorted(RESULT_CATEGORIES))
    for name, category in sorted(categories.items()):
        if not isinstance(category, dict):
            errors.append(f"result category {name}: expected object")
            continue
        findings = category.get("findings")
        if not isinstance(findings, list):
            errors.append(f"result category {name}: findings must be an array")
            continue
        check_equal(errors, f"result category {name} count", category.get("count"), len(findings))
        expected = 1 if corpus_id == "docusign" and name == "sourceDefects" else 0
        check_equal(errors, f"result category {name}", len(findings), expected)
    if corpus_id == "docusign":
        source_findings = categories.get("sourceDefects", {}).get("findings", [])
        message = canonical(source_findings)
        if "RIV3010" not in message or "connectOAuthConfig/properties/customParameters/additionalProperties" not in message:
            errors.append("result source defect does not identify the SIX section 12 DocuSign defect")

    metrics = result.get("metrics", {})
    operations = metrics.get("operations", {})
    for key in ("source", "reemitted", "shared"):
        check_equal(errors, f"result operations {key}", operations.get(key), expected_operations)
    for key in ("missing", "invented", "withFindings"):
        check_equal(errors, f"result operations {key}", operations.get(key), 0)
    result_components = metrics.get("components", {})
    for namespace in ("schemas", "requestBodies", "securitySchemes"):
        expected = expected_components.get(namespace, 0)
        values = result_components.get(namespace, {})
        for key in ("source", "reemitted", "matched"):
            check_equal(errors, f"result {namespace} {key}", values.get(key), expected)
        for key in ("missing", "invented"):
            check_equal(errors, f"result {namespace} {key}", values.get(key), 0)
    check_equal(errors, "result source defects metric", metrics.get("sourceDefects"), 1 if corpus_id == "docusign" else 0)
    check_equal(errors, "result comparator integrity metric", metrics.get("comparatorIntegrityFindings"), 0)


def audit_corpus(corpus_id, results_dir, manifest_entry, profile_entry, profile, aggregate_extensions):
    corpus_dir = results_dir / corpus_id
    artifacts = corpus_dir / "artifacts"
    errors = []
    paths = {
        "source": artifacts / "source.json",
        "firstGenerated": artifacts / "first-generated",
        "first": artifacts / "first-openapi.json",
        "secondGenerated": artifacts / "second-generated",
        "second": artifacts / "second-openapi.json",
    }
    expected_hash = profile_entry.get("sha256")
    check_equal(errors, "manifest/profile source hash", manifest_entry.get("sha256"), expected_hash)
    try:
        source_bytes = paths["source"].read_bytes()
    except OSError as error:
        raise ValueError(f"cannot read {paths['source']}: {error}") from error
    source_hash = sha256_bytes(source_bytes)
    check_equal(errors, "physical source hash", source_hash, expected_hash)

    source = load_json(paths["source"])
    first = load_json(paths["first"])
    second = load_json(paths["second"])
    documents = {"source": source, "first": first, "second": second}
    expected_operations = profile_entry.get("operationCount")
    expected_components = profile_entry.get("normalizedComponentCounts", {})
    check_equal(errors, "manifest operation count", manifest_entry.get("operationCount"), expected_operations)
    check_equal(errors, "manifest path count", manifest_entry.get("pathCount"), profile_entry.get("pathCount"))
    check_equal(errors, "manifest API version", manifest_entry.get("apiVersion"), profile_entry.get("apiVersion"))
    check_equal(errors, "manifest schema count", manifest_entry.get("schemaCount"), expected_components.get("schemas", 0))
    check_equal(errors, "source path count", len(source.get("paths", {})), profile_entry.get("pathCount"))
    check_equal(errors, "source dialect", source.get("openapi", source.get("swagger")), profile_entry.get("dialect"))
    check_equal(errors, "source API version", source.get("info", {}).get("version"), profile_entry.get("apiVersion"))
    for label, document in documents.items():
        check_equal(errors, f"{label} operation count", operation_count(document), expected_operations)
        check_equal(errors, f"{label} component counts", component_counts(document), expected_components)

    reviewed = profile.get("vendorExtensionDispositions", {})
    preserved_names = {name for name, review in reviewed.items() if review.get("disposition") == "preserve"}
    extensions = {label: collect_extensions(document, set(reviewed)) for label, document in documents.items()}
    for name in reviewed:
        aggregate_extensions[name].extend(extensions["source"].get(name, []))
    preserved = {}
    for name in sorted(preserved_names):
        source_values = extensions["source"].get(name, [])
        first_values = extensions["first"].get(name, [])
        second_values = extensions["second"].get(name, [])
        if source_values or first_values or second_values:
            counts = (len(source_values), len(first_values), len(second_values))
            preserved[name] = counts
            source_evidence = occurrence_digest(source_values)
            for label, values in (("first", first_values), ("second", second_values)):
                observed_evidence = occurrence_digest(values)
                if observed_evidence != source_evidence:
                    errors.append(
                        f"{name} {label}-pass values: expected count/digest "
                        f"{source_evidence[0]}/{source_evidence[1]}, observed "
                        f"{observed_evidence[0]}/{observed_evidence[1]}"
                    )
    expected_extension_count = sum(len(extensions["source"].get(name, [])) for name in preserved_names)

    generated = {
        "first": scan_generated(paths["firstGenerated"], expected_operations, expected_extension_count, errors),
        "second": scan_generated(paths["secondGenerated"], expected_operations, expected_extension_count, errors),
    }
    expected_component_total = sum(expected_components.values())
    first_summary = check_summary(
        artifacts / "first-summary.json",
        expected_operations,
        expected_component_total,
        1 if corpus_id == "docusign" else 0,
        errors,
    )
    fixed_summary = check_summary(
        artifacts / "fixed-point-summary.json",
        expected_operations,
        expected_component_total,
        0,
        errors,
    )
    result = load_json(corpus_dir / "result.json")
    check_result(result, corpus_id, expected_operations, expected_components, errors)

    differences = structural_differences(first, second)
    allowed, rejected = classify_fixed_point(corpus_id, differences)
    for difference in rejected:
        errors.append(
            "unclassified second-pass delta "
            f"{difference['kind']} at {difference['path']}: "
            f"{compact_value(difference['first'])} -> {compact_value(difference['second'])}"
        )

    audit = {
        "id": corpus_id,
        "passed": not errors,
        "errors": errors,
        "paths": paths,
        "sourceHash": source_hash,
        "operations": expected_operations,
        "components": expected_components,
        "preserved": preserved,
        "generated": generated,
        "rawDeltas": len(differences),
        "allowedDeltas": allowed,
        "rejectedDeltas": rejected,
        "firstSummary": first_summary,
        "fixedSummary": fixed_summary,
    }
    write_corpus_report(corpus_dir / "artifacts" / "audit.md", audit, results_dir)
    return audit


def write_corpus_report(path, audit, results_dir):
    status = "PASS" if audit["passed"] else "FAIL"
    component_text = ", ".join(f"{name}={count}" for name, count in audit["components"].items()) or "none"
    lines = [
        f"# Round-trip physical audit: {audit['id']}",
        "",
        f"**Result:** {status}",
        "",
        "## Evidence chain",
        "",
        "| Stage | Path | Operations | Components | Physical evidence |",
        "|---|---|---:|---|---|",
        f"| Source | `{relative(audit['paths']['source'], results_dir)}` | {audit['operations']} | {component_text} | sha256 `{audit['sourceHash']}` |",
        f"| Generated C# (first) | `{relative(audit['paths']['firstGenerated'], results_dir)}` | {audit['generated']['first']['operationProvenance']} provenance attributes | n/a | {audit['generated']['first']['files']} files, {audit['generated']['first']['bytes']} bytes, {audit['generated']['first']['vendorExtensions']} preserved-extension attributes |",
        f"| First OpenAPI | `{relative(audit['paths']['first'], results_dir)}` | {audit['operations']} | {component_text} | parsed JSON |",
        f"| Generated C# (second) | `{relative(audit['paths']['secondGenerated'], results_dir)}` | {audit['generated']['second']['operationProvenance']} provenance attributes | n/a | {audit['generated']['second']['files']} files, {audit['generated']['second']['bytes']} bytes, {audit['generated']['second']['vendorExtensions']} preserved-extension attributes |",
        f"| Second OpenAPI | `{relative(audit['paths']['second'], results_dir)}` | {audit['operations']} | {component_text} | parsed JSON |",
        "",
        "## Comparator and fixed point",
        "",
        f"- First comparator finding groups: empty ({audit['firstSummary'].get('sourceDefects', 0)} classified source defects).",
        f"- Fixed-point comparator finding groups: empty ({audit['fixedSummary'].get('sourceDefects', 0)} classified source defects).",
        f"- Raw first-to-second structural deltas: {audit['rawDeltas']}.",
        f"- Classified default-equivalent deltas: {len(audit['allowedDeltas'])}.",
        f"- Rejected structural deltas: {len(audit['rejectedDeltas'])}.",
    ]
    if audit["allowedDeltas"]:
        lines.extend(["", "Allowed equivalence: Twilio omits `additionalProperties: {}` at these 21 pinned schema paths:"])
        lines.extend(f"- `{item['path']}`" for item in audit["allowedDeltas"])
    lines.extend(["", "## Preserved extensions", ""])
    if audit["preserved"]:
        lines.extend(["| Extension | Source | First | Second |", "|---|---:|---:|---:|"])
        lines.extend(
            f"| `{name}` | {counts[0]} | {counts[1]} | {counts[2]} |"
            for name, counts in sorted(audit["preserved"].items())
        )
    else:
        lines.append("No preserve-disposition extensions occur in this corpus.")
    lines.extend(["", "## Findings", ""])
    lines.extend(f"- {error}" for error in audit["errors"])
    if not audit["errors"]:
        lines.append("No audit findings.")
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def write_aggregate_report(path, audits, profile_errors, results_dir, manifest_path, profile_path):
    passed = not profile_errors and all(audit["passed"] for audit in audits)
    lines = [
        "# SIX round-trip physical audit",
        "",
        f"**Result:** {'PASS' if passed else 'FAIL'} ({sum(audit['passed'] for audit in audits)}/{len(CORPUS_IDS)} corpora)",
        "",
        "| Corpus | Result | Operations | Components | C# first/second | Raw deltas | Classified | Findings |",
        "|---|---|---:|---:|---:|---:|---:|---:|",
    ]
    for audit in audits:
        component_total = sum(audit["components"].values())
        generated = f"{audit['generated']['first']['files']}/{audit['generated']['second']['files']}"
        lines.append(
            f"| [{audit['id']}]({audit['id']}/artifacts/audit.md) | {'PASS' if audit['passed'] else 'FAIL'} | "
            f"{audit['operations']} | {component_total} | {generated} | {audit['rawDeltas']} | "
            f"{len(audit['allowedDeltas'])} | {len(audit['errors'])} |"
        )
    lines.extend(
        [
            "",
            "## Evidence",
            "",
            f"- Manifest: `{relative(manifest_path, results_dir)}`",
            f"- SIX profile: `{relative(profile_path, results_dir)}`",
            f"- Operations: {sum(audit['operations'] for audit in audits)}.",
            f"- Normalized components: {sum(sum(audit['components'].values()) for audit in audits)}.",
            f"- Generated C# files: {sum(audit['generated']['first']['files'] for audit in audits)} first pass, {sum(audit['generated']['second']['files'] for audit in audits)} second pass.",
            f"- Raw fixed-point deltas: {sum(audit['rawDeltas'] for audit in audits)}; classified: {sum(len(audit['allowedDeltas']) for audit in audits)}; rejected: {sum(len(audit['rejectedDeltas']) for audit in audits)}.",
            "",
            "## Profile findings",
            "",
        ]
    )
    lines.extend(f"- {error}" for error in profile_errors)
    if not profile_errors:
        lines.append(
            "Manifest hashes, corpus count facts, reviewed extension facts, and preserve-extension values match the retained sources."
        )
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return passed


def main():
    args = parse_args()
    try:
        manifest = load_json(args.manifest)
        profile = load_json(args.profile)
        manifest_entries = {entry.get("id"): entry for entry in manifest.get("corpora", [])}
        profile_entries = {entry.get("id"): entry for entry in profile.get("facts", {}).get("corpora", [])}
        missing = [corpus_id for corpus_id in CORPUS_IDS if corpus_id not in manifest_entries or corpus_id not in profile_entries]
        if missing:
            raise ValueError(f"manifest/profile missing SIX corpora: {', '.join(missing)}")
        dispositions = profile.get("vendorExtensionDispositions", {})
        expected_disposition_hash = profile.get("reviewedDispositionSha256")
        actual_disposition_hash = sha256_bytes(canonical(dispositions).encode())
        profile_errors = []
        check_equal(profile_errors, "reviewed disposition hash", actual_disposition_hash, expected_disposition_hash)
        aggregate_extensions = collections.defaultdict(list)
        audits = [
            audit_corpus(
                corpus_id,
                args.results_dir,
                manifest_entries[corpus_id],
                profile_entries[corpus_id],
                profile,
                aggregate_extensions,
            )
            for corpus_id in CORPUS_IDS
        ]
        expected_extension_facts = profile.get("facts", {}).get("extensions", {})
        for name, expected in sorted(expected_extension_facts.items()):
            check_equal(profile_errors, f"profile extension {name}", extension_fact(aggregate_extensions[name]), expected)
        passed = write_aggregate_report(
            args.results_dir / "audit.md",
            audits,
            profile_errors,
            args.results_dir,
            args.manifest,
            args.profile,
        )
    except (ValueError, OSError, UnicodeError, json.JSONDecodeError) as error:
        print(f"roundtrip-audit: {error}", file=sys.stderr)
        return 2
    print(f"roundtrip-audit: {'PASS' if passed else 'FAIL'} ({sum(audit['passed'] for audit in audits)}/6 corpora)")
    if profile_errors:
        for error in profile_errors:
            print(f"profile: {error}", file=sys.stderr)
    for audit in audits:
        for error in audit["errors"]:
            print(f"{audit['id']}: {error}", file=sys.stderr)
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
