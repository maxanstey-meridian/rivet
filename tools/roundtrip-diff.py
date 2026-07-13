#!/usr/bin/env python3
"""Semantic comparison for an OpenAPI document and its Rivet round-trip.

Exit 0 means no findings, 1 means semantic or integrity findings, and 2 means
invalid arguments or input. Findings are grouped by document, operation,
schema, and integrity scope in the JSON reports.
"""

import argparse
import collections
import json
import os
import re
import sys
import urllib.parse


METHODS = ("get", "put", "post", "delete", "patch", "head", "options", "trace")
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
SWAGGER_COLLECTION_FORMATS = {
    "multi": ("form", True),
    "csv": ("form", False),
    "ssv": ("spaceDelimited", False),
    "pipes": ("pipeDelimited", False),
}
SWAGGER_PARAMETER_SCHEMA_FIELDS = (
    "type",
    "format",
    "items",
    "default",
    "maximum",
    "minimum",
    "exclusiveMaximum",
    "exclusiveMinimum",
    "maxLength",
    "minLength",
    "pattern",
    "maxItems",
    "minItems",
    "uniqueItems",
    "enum",
    "multipleOf",
)
SCHEMA_CONSTRAINT_FIELDS = (
    "multipleOf",
    "maximum",
    "exclusiveMaximum",
    "minimum",
    "exclusiveMinimum",
    "maxLength",
    "minLength",
    "pattern",
    "maxItems",
    "minItems",
    "uniqueItems",
    "maxProperties",
    "minProperties",
)
SCHEMA_ANNOTATION_FIELDS = (
    "title",
    "description",
    "default",
    "const",
    "readOnly",
    "writeOnly",
    "deprecated",
    "xml",
    "discriminator",
)
VENDOR_EXTENSION_DISPOSITIONS = {
    "x-ds-api-status": "preserve",
    "x-ds-examples": "preserve",
    "x-ds-in-sdk": "preserve",
    "x-docs-overrides": "preserve",
    "x-enum-elements": "preserve",
    "x-errors": "preserve",
    "x-is-beta": "preserve",
    "x-is-deprecated": "map",
    "x-ms-summary": "map",
    "x-oauthpermissions": "map",
    "x-patternProperties": "preserve",
    "x-public-description": "map",
    "x-read-only": "map",
    "x-release-status": "preserve",
    "x-sq-version": "preserve",
    "x-twilio": "preserve",
    "x-visibility": "preserve",
    "x-Description": "map",
    "x-desc": "map",
}


def parse_args():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("original")
    parser.add_argument("reemitted")
    parser.add_argument("--summary-json")
    parser.add_argument("--details-json")
    parser.add_argument(
        "--generated-source",
        action="append",
        default=[],
        help="generated source file or directory to scan for unsupported markers",
    )
    return parser.parse_args()


def load_document(path):
    try:
        with open(path, encoding="utf-8") as source:
            document = json.load(source)
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise ValueError(f"cannot read {path}: {error}") from error
    if not isinstance(document, dict):
        raise ValueError(f"{path}: root must be a JSON object")
    return document


def canonical_json(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True)


def contact_projection(value):
    if not isinstance(value, dict):
        return value
    return {key: value[key] for key in ("name", "url", "email") if key in value}


def tags_projection(value):
    if not isinstance(value, list):
        return value
    return [
        {key: tag[key] for key in ("name", "description", "externalDocs") if key in tag}
        if isinstance(tag, dict)
        else tag
        for tag in value
    ]


def json_value(value):
    if isinstance(value, tuple):
        return [json_value(item) for item in value]
    if isinstance(value, set):
        return sorted((json_value(item) for item in value), key=canonical_json)
    if isinstance(value, dict):
        return {key: json_value(item) for key, item in value.items()}
    if isinstance(value, list):
        return [json_value(item) for item in value]
    return value


def pointer_parts(reference):
    if not isinstance(reference, str) or not reference.startswith("#/"):
        return None
    return [
        urllib.parse.unquote(part).replace("~1", "/").replace("~0", "~")
        for part in reference[2:].split("/")
    ]


def resolve_pointer(document, reference):
    parts = pointer_parts(reference)
    if parts is None:
        return None
    current = document
    for part in parts:
        if isinstance(current, dict) and part in current:
            current = current[part]
        elif isinstance(current, list) and part.isdigit() and int(part) < len(current):
            current = current[int(part)]
        else:
            return None
    return current


def resolve_once(document, value):
    if not isinstance(value, dict) or "$ref" not in value:
        return value
    target = resolve_pointer(document, value["$ref"])
    if target is None:
        return value
    siblings = {key: item for key, item in value.items() if key != "$ref"}
    if not isinstance(target, dict):
        return siblings or target
    merged = dict(target)
    merged.update(siblings)
    return merged


def pointer_token(value):
    return str(value).replace("~", "~0").replace("/", "~1")


def normalize_owner_pointer(document, pointer):
    if not str(document.get("swagger", "")).startswith("2."):
        return pointer
    if pointer == "#/definitions":
        return "#/components/schemas"
    if pointer.startswith("#/definitions/"):
        return "#/components/schemas/" + pointer[len("#/definitions/"):]
    if pointer == "#/securityDefinitions":
        return "#/components/securitySchemes"
    if pointer.startswith("#/securityDefinitions/"):
        return "#/components/securitySchemes/" + pointer[len("#/securityDefinitions/"):]
    return pointer


def reviewed_extensions(document, disposition):
    result = {}
    swagger2 = str(document.get("swagger", "")).startswith("2.")

    def visit(value, pointer, example_object=False):
        if isinstance(value, dict):
            for name, child in value.items():
                if VENDOR_EXTENSION_DISPOSITIONS.get(name) == disposition:
                    result[(normalize_owner_pointer(document, pointer), name)] = child
                elif not swagger2 and name == "examples" and isinstance(child, dict):
                    for example_name, example in child.items():
                        visit(
                            example,
                            f"{pointer}/examples/{pointer_token(example_name)}",
                            example_object=True,
                        )
                elif not (example_object and name == "value") and not name.startswith(
                    "x-"
                ) and name not in (
                    "const",
                    "default",
                    "enum",
                    "example",
                    "examples",
                ):
                    visit(child, f"{pointer}/{pointer_token(name)}")
        elif isinstance(value, list):
            for index, child in enumerate(value):
                visit(child, f"{pointer}/{index}")

    visit(document, "#")
    return result


def extension_scope(pointer):
    if pointer.startswith("#/components/schemas/"):
        return "schema"
    if pointer.startswith("#/paths/"):
        return "operation"
    return "document"


def swagger_parameter_schema(parameter):
    schema = {
        field: parameter[field]
        for field in SWAGGER_PARAMETER_SCHEMA_FIELDS
        if field in parameter
    }
    if parameter.get("x-nullable") is True:
        schema["nullable"] = True
    if schema.get("type") == "file":
        schema["type"] = "string"
        schema["format"] = "binary"
    return schema


def project_swagger_collection_format(parameter, location=None):
    collection_format = parameter.get("collectionFormat", "csv")
    location = location or parameter.get("in")
    if parameter.get("type") != "array":
        return {}
    if collection_format not in SWAGGER_COLLECTION_FORMATS:
        return {"x-rivet-swagger-collectionFormat": collection_format}
    if location not in ("query", "formData") and collection_format != "csv":
        return {"x-rivet-swagger-collectionFormat": collection_format}
    style, explode = SWAGGER_COLLECTION_FORMATS[collection_format]
    if location in ("path", "header"):
        style = "simple"
    return {"style": style, "explode": explode}


def swagger_media_types(document, operation, field):
    value = operation[field] if field in operation else document.get(field, [])
    return value if isinstance(value, list) else []


def project_swagger_operation(document, operation, effective_parameters):
    true_parameters = []
    body_parameters = []
    form_parameters = []
    for raw_parameter in effective_parameters:
        parameter = resolve_once(document, raw_parameter)
        if not isinstance(parameter, dict):
            continue
        location = parameter.get("in")
        if location == "body":
            body_parameters.append(parameter)
        elif location == "formData":
            form_parameters.append(parameter)
        elif location in ("path", "query", "header"):
            projected = dict(parameter)
            projected["schema"] = swagger_parameter_schema(parameter)
            projected.update(project_swagger_collection_format(parameter))
            true_parameters.append(projected)

    operation["parameters"] = true_parameters
    consumes = swagger_media_types(document, operation, "consumes")
    if len(body_parameters) == 1 and not form_parameters and consumes:
        body = body_parameters[0]
        operation["requestBody"] = {
            "description": body.get("description"),
            "required": bool(body.get("required", False)),
            "content": {
                media_type: {"schema": body.get("schema", {})} for media_type in consumes
            },
        }
    elif form_parameters and not body_parameters and consumes:
        properties = {}
        required = []
        encoding = {}
        for parameter in form_parameters:
            property_schema = swagger_parameter_schema(parameter)
            if "description" in parameter:
                property_schema["description"] = parameter["description"]
            properties[parameter.get("name")] = property_schema
            if parameter.get("required", False):
                required.append(parameter.get("name"))
            serialization = project_swagger_collection_format(parameter)
            if serialization:
                encoding[parameter.get("name")] = serialization
        schema = {"type": "object", "properties": properties}
        if required:
            schema["required"] = required
        operation["requestBody"] = {
            "required": bool(required),
            "content": {
                media_type: {
                    "schema": schema,
                    **({"encoding": encoding} if encoding else {}),
                }
                for media_type in consumes
            },
        }
    else:
        # Invalid or underspecified body semantics remain visible as parameter drift.
        operation["parameters"].extend(body_parameters)
        operation["parameters"].extend(form_parameters)

    produces = swagger_media_types(document, operation, "produces")
    responses = {}
    for status, raw_response in operation.get("responses", {}).items():
        resolved = resolve_once(document, raw_response)
        if not isinstance(resolved, dict):
            responses[status] = resolved
            continue
        response = dict(resolved)
        if "schema" in response and produces:
            examples = response.get("examples", {})
            content = {}
            for media_type in produces:
                media = {"schema": response["schema"]}
                if isinstance(examples, dict) and media_type in examples:
                    media["example"] = examples[media_type]
                content[media_type] = media
            response["content"] = content
            response.pop("schema", None)
            response.pop("examples", None)
        headers = {}
        for name, raw_header in response.get("headers", {}).items():
            header = resolve_once(document, raw_header)
            if not isinstance(header, dict):
                headers[name] = header
                continue
            projected_header = dict(header)
            projected_header["schema"] = swagger_parameter_schema(header)
            projected_header.update(
                project_swagger_collection_format(header, location="header")
            )
            for field in SWAGGER_PARAMETER_SCHEMA_FIELDS:
                projected_header.pop(field, None)
            headers[name] = projected_header
        if headers:
            response["headers"] = headers
        responses[status] = response
    operation["responses"] = responses
    return operation


def root_servers(document):
    servers = document.get("servers")
    if isinstance(servers, list):
        return servers
    if not str(document.get("swagger", "")).startswith("2."):
        return []
    host = document.get("host")
    base_path = document.get("basePath", "")
    schemes = document.get("schemes")
    if host and isinstance(schemes, list) and schemes:
        return [{"url": f"{scheme}://{host}{base_path}"} for scheme in schemes]
    if base_path and not host:
        return [{"url": base_path}]
    return []


def operations(document):
    result = {}
    root_server_set = root_servers(document)
    for path, item in document.get("paths", {}).items():
        if not isinstance(item, dict):
            continue
        path_servers = item.get("servers", root_server_set)
        for method in METHODS:
            if not isinstance(item.get(method), dict):
                continue
            operation = dict(item[method])
            parameters = {}
            for raw_parameter in item.get("parameters", []):
                parameter = resolve_once(document, raw_parameter)
                if isinstance(parameter, dict):
                    parameters[(parameter.get("name"), parameter.get("in"))] = raw_parameter
            for raw_parameter in operation.get("parameters", []):
                parameter = resolve_once(document, raw_parameter)
                if isinstance(parameter, dict):
                    parameters[(parameter.get("name"), parameter.get("in"))] = raw_parameter
            operation["parameters"] = list(parameters.values())
            if str(document.get("swagger", "")).startswith("2."):
                operation = project_swagger_operation(
                    document, operation, operation["parameters"]
                )
            result[(path, method)] = {
                "operation": operation,
                "servers": operation.get("servers", path_servers),
                "security": operation.get("security", document.get("security")),
            }
    return result


def security_projection(security):
    if security is None:
        return None
    clauses = []
    for clause in security:
        if not isinstance(clause, dict):
            clauses.append(canonical_json(clause))
            continue
        projected = []
        for scheme, scopes in clause.items():
            scopes = tuple(sorted(scopes)) if isinstance(scopes, list) else scopes
            projected.append((scheme, scopes))
        clauses.append(tuple(sorted(projected)))
    return tuple(sorted(clauses, key=repr))


def text_projection(value):
    return None if value in (None, "") else value


def examples_projection(container):
    if not isinstance(container, dict):
        return None
    if "examples" in container:
        examples = container["examples"]
        if isinstance(examples, list):
            return tuple(sorted(canonical_json(value) for value in examples))
        if isinstance(examples, dict):
            return canonical_json(examples)
    if "example" in container:
        return (canonical_json(container["example"]),)
    return None


def servers_projection(servers):
    if not isinstance(servers, list):
        return servers
    return tuple(canonical_json(server) for server in servers)


def schema_ref_identity(reference):
    parts = pointer_parts(reference)
    if parts is None:
        return reference
    if len(parts) == 3 and parts[:2] == ["components", "schemas"]:
        return ("schema", parts[2])
    if len(parts) == 2 and parts[0] == "definitions":
        return ("schema", parts[1])
    return tuple(parts)


def component_ref_identity(reference, namespace):
    parts = pointer_parts(reference)
    if parts is None:
        return reference
    if len(parts) == 3 and parts[:2] == ["components", namespace]:
        return (namespace, parts[2])
    return tuple(parts)


class Comparator:
    def __init__(self, original, reemitted, generated_sources):
        self.original = original
        self.reemitted = reemitted
        self.generated_sources = generated_sources
        self.findings = {
            "document": collections.defaultdict(list),
            "operation": collections.defaultdict(list),
            "schema": collections.defaultdict(list),
            "integrity": collections.defaultdict(list),
        }
        self.original_operations = operations(original)
        self.reemitted_operations = operations(reemitted)
        self.shared_operations = sorted(
            set(self.original_operations) & set(self.reemitted_operations)
        )
        self.missing_operations = sorted(
            set(self.original_operations) - set(self.reemitted_operations)
        )
        self.invented_operations = sorted(
            set(self.reemitted_operations) - set(self.original_operations)
        )
        self.original_schemas = self.schemas(original)
        self.reemitted_schemas = self.schemas(reemitted)
        self.missing_schemas = sorted(set(self.original_schemas) - set(self.reemitted_schemas))
        self.invented_schemas = sorted(set(self.reemitted_schemas) - set(self.original_schemas))
        self.original_components = self.component_identities(original)
        self.reemitted_components = self.component_identities(reemitted)
        self.missing_components = sorted(self.original_components - self.reemitted_components)
        self.invented_components = sorted(self.reemitted_components - self.original_components)
        self.source_defects = []
        self.collect_source_defects(self.original, "#")
        self.collect_reserved_header_source_defects()

    @staticmethod
    def schemas(document):
        if str(document.get("swagger", "")).startswith("2."):
            return document.get("definitions", {})
        return document.get("components", {}).get("schemas", {})

    @staticmethod
    def component_maps(document):
        if str(document.get("swagger", "")).startswith("2."):
            return {
                "schemas": document.get("definitions", {}),
                "responses": document.get("responses", {}),
                "parameters": document.get("parameters", {}),
                "securitySchemes": document.get("securityDefinitions", {}),
            }
        components = document.get("components", {})
        return {
            namespace: components.get(namespace, {})
            for namespace in COMPONENT_NAMESPACES
        }

    @classmethod
    def component_identities(cls, document):
        return {
            (namespace, name)
            for namespace, values in cls.component_maps(document).items()
            if isinstance(values, dict)
            for name in values
        }

    def add(self, scope, category, path, original=None, reemitted=None):
        self.findings[scope][category].append(
            {
                "path": path,
                "original": json_value(original),
                "reemitted": json_value(reemitted),
            }
        )

    @staticmethod
    def invalid_additional_properties(schema):
        if not isinstance(schema, dict) or "additionalProperties" not in schema:
            return False
        declared_type = schema.get("type")
        if isinstance(declared_type, list):
            non_null_types = [value for value in declared_type if value != "null"]
            return "object" not in non_null_types
        return declared_type is not None and declared_type != "object"

    def collect_source_defects(self, document, path):
        def collect_schema(schema, schema_path):
            if not isinstance(schema, dict):
                return
            for key in (
                "items",
                "additionalProperties",
                "not",
                "propertyNames",
                "contains",
                "if",
                "then",
                "else",
                "unevaluatedItems",
                "unevaluatedProperties",
            ):
                collect_schema(schema.get(key), f"{schema_path}/{key}")
            for key in (
                "properties",
                "patternProperties",
                "dependentSchemas",
                "$defs",
                "definitions",
            ):
                values = schema.get(key, {})
                if isinstance(values, dict):
                    for name, child in values.items():
                        collect_schema(child, f"{schema_path}/{key}/{pointer_token(name)}")
            for key in ("allOf", "oneOf", "anyOf", "prefixItems"):
                values = schema.get(key, [])
                if isinstance(values, list):
                    for index, child in enumerate(values):
                        collect_schema(child, f"{schema_path}/{key}/{index}")

        def collect_parameter(parameter, parameter_path):
            if not isinstance(parameter, dict):
                return
            if parameter.get("name") == "" and parameter.get("in") in (
                "query",
                "header",
                "path",
                "cookie",
                "body",
                "formData",
            ):
                self.source_defects.append(
                    {
                        "path": f"{parameter_path}/name",
                        "reason": "parameter name is empty and therefore invalid",
                    }
                )
            if "schema" in parameter:
                collect_schema(parameter["schema"], f"{parameter_path}/schema")
            elif parameter.get("in") in ("query", "header", "path", "formData"):
                collect_schema(parameter, parameter_path)
            content = parameter.get("content", {})
            if isinstance(content, dict):
                for media_type, media in content.items():
                    if isinstance(media, dict):
                        collect_schema(
                            media.get("schema"),
                            f"{parameter_path}/content/{pointer_token(media_type)}/schema",
                        )

        def collect_parameters(parameters, parameters_path):
            if isinstance(parameters, list):
                for index, parameter in enumerate(parameters):
                    collect_parameter(parameter, f"{parameters_path}/{index}")
            elif isinstance(parameters, dict):
                for name, parameter in parameters.items():
                    collect_parameter(parameter, f"{parameters_path}/{pointer_token(name)}")

        def collect_content(content, content_path):
            if not isinstance(content, dict):
                return
            for media_type, media in content.items():
                if isinstance(media, dict):
                    collect_schema(
                        media.get("schema"),
                        f"{content_path}/{pointer_token(media_type)}/schema",
                    )

        def collect_response(response, response_path):
            if not isinstance(response, dict):
                return
            collect_schema(response.get("schema"), f"{response_path}/schema")
            collect_content(response.get("content"), f"{response_path}/content")
            headers = response.get("headers", {})
            if isinstance(headers, dict):
                for name, header in headers.items():
                    collect_parameter(header, f"{response_path}/headers/{pointer_token(name)}")

        for name, schema in document.get("definitions", {}).items():
            collect_schema(schema, f"{path}/definitions/{pointer_token(name)}")
        collect_parameters(document.get("parameters"), f"{path}/parameters")
        for name, response in document.get("responses", {}).items():
            collect_response(response, f"{path}/responses/{pointer_token(name)}")

        components = document.get("components", {})
        if isinstance(components, dict):
            for name, schema in components.get("schemas", {}).items():
                collect_schema(schema, f"{path}/components/schemas/{pointer_token(name)}")
            collect_parameters(
                components.get("parameters"), f"{path}/components/parameters"
            )
            for name, request_body in components.get("requestBodies", {}).items():
                if isinstance(request_body, dict):
                    collect_content(
                        request_body.get("content"),
                        f"{path}/components/requestBodies/{pointer_token(name)}/content",
                    )
            for name, response in components.get("responses", {}).items():
                collect_response(
                    response, f"{path}/components/responses/{pointer_token(name)}"
                )
            for name, header in components.get("headers", {}).items():
                collect_parameter(
                    header, f"{path}/components/headers/{pointer_token(name)}"
                )

        for route, path_item in document.get("paths", {}).items():
            if not isinstance(path_item, dict):
                continue
            route_path = f"{path}/paths/{pointer_token(route)}"
            collect_parameters(path_item.get("parameters"), f"{route_path}/parameters")
            for method in METHODS:
                operation = path_item.get(method)
                if not isinstance(operation, dict):
                    continue
                operation_path = f"{route_path}/{method}"
                collect_parameters(
                    operation.get("parameters"), f"{operation_path}/parameters"
                )
                request_body = operation.get("requestBody")
                if isinstance(request_body, dict):
                    collect_content(
                        request_body.get("content"), f"{operation_path}/requestBody/content"
                    )
                for status, response in operation.get("responses", {}).items():
                    collect_response(
                        response,
                        f"{operation_path}/responses/{pointer_token(status)}",
                    )

    @staticmethod
    def reserved_header_reason(document, raw_parameter):
        parameter = resolve_once(document, raw_parameter)
        if (
            not isinstance(parameter, dict)
            or parameter.get("in") != "header"
        ):
            return None
        name = str(parameter.get("name", "")).lower()
        return {
            "content-type": "reserved Content-Type header parameter is ignored by OpenAPI; request media types are represented by requestBody.content",
            "accept": "reserved Accept header parameter is ignored by OpenAPI; response media types are represented by responses content",
            "authorization": "reserved Authorization header parameter is ignored by OpenAPI; authentication is represented by security schemes",
        }.get(name)

    def collect_reserved_header_source_defects(self):
        for route, path_item in self.original.get("paths", {}).items():
            if not isinstance(path_item, dict):
                continue
            for method in METHODS:
                operation = path_item.get(method)
                if not isinstance(operation, dict):
                    continue
                for index, raw_parameter in enumerate(operation.get("parameters", [])):
                    reason = self.reserved_header_reason(self.original, raw_parameter)
                    if reason is None:
                        continue
                    self.source_defects.append(
                        {
                            "path": f"#/paths/{pointer_token(route)}/{method}/parameters/{index}/name",
                            "reason": reason,
                        }
                    )

    def compare_value(self, scope, category, path, original, reemitted, normalize=None):
        if normalize:
            original = normalize(original)
            reemitted = normalize(reemitted)
        if original != reemitted:
            self.add(scope, category, path, original, reemitted)

    def compare_document(self):
        original_info = self.original.get("info", {})
        reemitted_info = self.reemitted.get("info", {})
        for field in (
            "title",
            "version",
            "description",
            "termsOfService",
            "contact",
            "license",
        ):
            normalize = (
                text_projection
                if field == "description"
                else contact_projection
                if field == "contact"
                else None
            )
            self.compare_value(
                "document",
                "info",
                f"#/info/{field}",
                original_info.get(field),
                reemitted_info.get(field),
                normalize,
            )
        self.compare_value(
            "document",
            "servers",
            "#/servers",
            root_servers(self.original),
            root_servers(self.reemitted),
            servers_projection,
        )
        self.compare_value(
            "document",
            "tags",
            "#/tags",
            self.original.get("tags"),
            self.reemitted.get("tags"),
            tags_projection,
        )
        self.compare_value(
            "document",
            "externalDocs",
            "#/externalDocs",
            self.original.get("externalDocs"),
            self.reemitted.get("externalDocs"),
            canonical_json,
        )
        self.compare_value(
            "document",
            "security",
            "#/security",
            self.original.get("security"),
            self.reemitted.get("security"),
            security_projection,
        )
        self.compare_value(
            "document",
            "security-schemes",
            "#/components/securitySchemes",
            self.security_schemes(self.original),
            self.security_schemes(self.reemitted),
            canonical_json,
        )
        for identity in self.missing_components:
            self.add("document", "component-missing", "#/components", identity, None)
        for identity in self.invented_components:
            self.add("document", "component-invented", "#/components", None, identity)

    @staticmethod
    def security_schemes(document):
        if not str(document.get("swagger", "")).startswith("2."):
            return document.get("components", {}).get("securitySchemes", {})
        projected = {}
        flow_names = {
            "implicit": "implicit",
            "password": "password",
            "application": "clientCredentials",
            "accessCode": "authorizationCode",
        }
        for name, definition in document.get("securityDefinitions", {}).items():
            if not isinstance(definition, dict):
                projected[name] = definition
                continue
            scheme_type = definition.get("type")
            if scheme_type == "basic":
                scheme = {"type": "http", "scheme": "basic"}
            elif scheme_type == "oauth2":
                flow_name = definition.get("flow")
                flow = {"scopes": definition.get("scopes", {})}
                if "authorizationUrl" in definition:
                    flow["authorizationUrl"] = definition["authorizationUrl"]
                if "tokenUrl" in definition:
                    flow["tokenUrl"] = definition["tokenUrl"]
                scheme = {
                    "type": "oauth2",
                    "flows": {flow_names.get(flow_name, flow_name): flow},
                }
            else:
                scheme = {
                    key: value
                    for key, value in definition.items()
                    if key in ("type", "name", "in")
                }
            if "description" in definition:
                scheme["description"] = definition["description"]
            projected[name] = scheme
        return projected

    def compare_operations(self):
        for key in self.shared_operations:
            original_entry = self.original_operations[key]
            reemitted_entry = self.reemitted_operations[key]
            original = original_entry["operation"]
            reemitted = reemitted_entry["operation"]
            path = f"#/paths/{key[0]}/{key[1]}"
            for field, category in (
                ("operationId", "operation-id"),
                ("summary", "operation-summary"),
                ("description", "operation-description"),
            ):
                normalize = text_projection if field in ("summary", "description") else None
                self.compare_value(
                    "operation",
                    category,
                    f"{path}/{field}",
                    original.get(field),
                    reemitted.get(field),
                    normalize,
                )
            self.compare_value(
                "operation",
                "operation-tags",
                f"{path}/tags",
                original.get("tags", []),
                reemitted.get("tags", []),
                lambda value: tuple(sorted(value)) if isinstance(value, list) else value,
            )
            self.compare_value(
                "operation",
                "operation-deprecated",
                f"{path}/deprecated",
                bool(original.get("deprecated", original.get("x-is-deprecated", False))),
                bool(reemitted.get("deprecated", reemitted.get("x-is-deprecated", False))),
            )
            self.compare_value(
                "operation",
                "operation-security",
                f"{path}/security",
                original_entry["security"],
                reemitted_entry["security"],
                security_projection,
            )
            self.compare_value(
                "operation",
                "operation-servers",
                f"{path}/servers",
                original_entry["servers"],
                reemitted_entry["servers"],
                servers_projection,
            )
            original_extensions = {
                key: value for key, value in original.items() if key.startswith("x-rivet-")
            }
            reemitted_extensions = {
                key: value for key, value in reemitted.items() if key.startswith("x-rivet-")
            }
            self.compare_value(
                "operation",
                "operation-extensions",
                path,
                original_extensions,
                reemitted_extensions,
                canonical_json,
            )
            self.compare_parameters(key, original, reemitted)
            self.compare_request_body(key, original, reemitted)
            self.compare_responses(key, original, reemitted)

    def parameter_map(self, document, operation, omit_reserved_headers=False):
        result = {}
        for raw_parameter in operation.get("parameters", []):
            parameter = resolve_once(document, raw_parameter)
            if (
                isinstance(parameter, dict)
                and parameter.get("name") != ""
                and not (
                    omit_reserved_headers
                    and self.reserved_header_reason(document, raw_parameter) is not None
                )
            ):
                result[(parameter.get("name"), parameter.get("in"))] = parameter
        return result

    def parameter_reference_map(self, document, operation, omit_reserved_headers=False):
        result = {}
        for raw_parameter in operation.get("parameters", []):
            parameter = resolve_once(document, raw_parameter)
            if (
                not isinstance(parameter, dict)
                or parameter.get("name") == ""
                or omit_reserved_headers
                and self.reserved_header_reason(document, raw_parameter) is not None
            ):
                continue
            reference = raw_parameter.get("$ref") if isinstance(raw_parameter, dict) else None
            result[(parameter.get("name"), parameter.get("in"))] = (
                component_ref_identity(reference, "parameters") if reference else None
            )
        return result

    @staticmethod
    def parameter_projection(parameter):
        location = parameter.get("in")
        default_style = {"query": "form", "cookie": "form", "path": "simple", "header": "simple"}.get(location)
        style = parameter.get("style", default_style)
        default_explode = style == "form"
        return {
            "required": bool(parameter.get("required", False)),
            "description": text_projection(parameter.get("description")),
            "deprecated": bool(
                parameter.get("deprecated", parameter.get("x-is-deprecated", False))
            ),
            "style": style,
            "explode": parameter.get("explode", default_explode),
            "allowReserved": bool(parameter.get("allowReserved", False)),
            "allowEmptyValue": bool(parameter.get("allowEmptyValue", False)),
            "example": examples_projection(parameter),
            "contentTypes": sorted(parameter.get("content", {})),
            "unsupportedCollectionFormat": parameter.get(
                "x-rivet-swagger-collectionFormat"
            ),
        }

    def compare_parameters(self, key, original, reemitted):
        all_original_parameters = self.parameter_map(self.original, original)
        original_parameters = self.parameter_map(
            self.original, original, omit_reserved_headers=True
        )
        reemitted_parameters = self.parameter_map(self.reemitted, reemitted)
        for identity in set(all_original_parameters) - set(original_parameters):
            reemitted_parameters.pop(identity, None)
        path = f"#/paths/{key[0]}/{key[1]}/parameters"
        original_keys = set(original_parameters)
        reemitted_keys = set(reemitted_parameters)
        original_references = self.parameter_reference_map(
            self.original, original, omit_reserved_headers=True
        )
        reemitted_references = self.parameter_reference_map(self.reemitted, reemitted)
        for identity in sorted(original_keys - reemitted_keys, key=repr):
            self.add("operation", "parameter-missing", path, identity, None)
        for identity in sorted(reemitted_keys - original_keys, key=repr):
            self.add("operation", "parameter-invented", path, None, identity)
        for identity in sorted(original_keys & reemitted_keys, key=repr):
            original_parameter = original_parameters[identity]
            reemitted_parameter = reemitted_parameters[identity]
            parameter_path = f"{path}/{identity[1]}:{identity[0]}"
            self.compare_value(
                "operation",
                "parameter-ref-identity",
                parameter_path,
                original_references.get(identity),
                reemitted_references.get(identity),
            )
            self.compare_parameter_value(
                original_parameter,
                reemitted_parameter,
                parameter_path,
                "operation",
                "parameter",
            )

    def compare_parameter_value(self, original, reemitted, path, scope, category):
        if not isinstance(original, dict) or not isinstance(reemitted, dict):
            self.compare_value(scope, category, path, original, reemitted)
            return
        self.compare_value(
            scope,
            f"{category}-metadata",
            path,
            self.parameter_projection(original),
            self.parameter_projection(reemitted),
        )
        original_content = original.get("content", {})
        reemitted_content = reemitted.get("content", {})
        if original_content or reemitted_content:
            for media_type in set(original_content) & set(reemitted_content):
                original_media = original_content[media_type] or {}
                reemitted_media = reemitted_content[media_type] or {}
                self.compare_value(
                    scope,
                    f"{category}-examples",
                    f"{path}/content/{media_type}",
                    examples_projection(original_media),
                    examples_projection(reemitted_media),
                )
                self.compare_schema(
                    original_media.get("schema", {}),
                    reemitted_media.get("schema", {}),
                    f"{path}/content/{media_type}/schema",
                    scope,
                    f"{category}-schema",
                    set(),
                )
        else:
            self.compare_schema(
                original.get("schema", {}),
                reemitted.get("schema", {}),
                f"{path}/schema",
                scope,
                f"{category}-schema",
                set(),
            )

    def compare_request_body(self, key, original, reemitted):
        path = f"#/paths/{key[0]}/{key[1]}/requestBody"
        self.compare_request_body_value(
            original.get("requestBody"),
            reemitted.get("requestBody"),
            path,
            "operation",
            "request-body",
            "request",
        )

    def compare_request_body_value(
        self, original_raw, reemitted_raw, path, scope, category, content_category
    ):
        original_ref = original_raw.get("$ref") if isinstance(original_raw, dict) else None
        reemitted_ref = reemitted_raw.get("$ref") if isinstance(reemitted_raw, dict) else None
        original_identity = (
            component_ref_identity(original_ref, "requestBodies") if original_ref else None
        )
        reemitted_identity = (
            component_ref_identity(reemitted_ref, "requestBodies") if reemitted_ref else None
        )
        if original_identity != reemitted_identity:
            self.add(
                scope,
                f"{category}-ref-identity",
                path,
                original_identity,
                reemitted_identity,
            )
        original_body = resolve_once(self.original, original_raw)
        reemitted_body = resolve_once(self.reemitted, reemitted_raw)
        if not isinstance(original_body, dict) or not isinstance(reemitted_body, dict):
            if original_body != reemitted_body:
                self.add(scope, f"{category}-presence", path, original_body, reemitted_body)
            return
        self.compare_value(
            scope,
            f"{category}-metadata",
            path,
            {
                "required": bool(original_body.get("required", False)),
                "description": text_projection(original_body.get("description")),
            },
            {
                "required": bool(reemitted_body.get("required", False)),
                "description": text_projection(reemitted_body.get("description")),
            },
        )
        self.compare_content(
            original_body.get("content", {}),
            reemitted_body.get("content", {}),
            f"{path}/content",
            content_category,
            scope,
        )

    def compare_responses(self, key, original, reemitted):
        original_responses = original.get("responses", {})
        reemitted_responses = reemitted.get("responses", {})
        original_keys = set(original_responses)
        reemitted_keys = set(reemitted_responses)
        path = f"#/paths/{key[0]}/{key[1]}/responses"
        missing = sorted(original_keys - reemitted_keys)
        invented = sorted(reemitted_keys - original_keys)
        if missing:
            self.add("operation", "response-key-missing", path, missing, None)
        if invented:
            self.add("operation", "response-key-invented", path, None, invented)
        for status in sorted(original_keys & reemitted_keys):
            response_path = f"{path}/{status}"
            original_raw = original_responses[status]
            reemitted_raw = reemitted_responses[status]
            original_ref = original_raw.get("$ref") if isinstance(original_raw, dict) else None
            reemitted_ref = reemitted_raw.get("$ref") if isinstance(reemitted_raw, dict) else None
            self.compare_value(
                "operation",
                "response-ref-identity",
                response_path,
                component_ref_identity(original_ref, "responses") if original_ref else None,
                component_ref_identity(reemitted_ref, "responses") if reemitted_ref else None,
            )
            self.compare_response_value(
                original_raw,
                reemitted_raw,
                response_path,
                "operation",
                "response",
            )

    def compare_response_value(self, original_raw, reemitted_raw, path, scope, category):
        original = resolve_once(self.original, original_raw)
        reemitted = resolve_once(self.reemitted, reemitted_raw)
        if not isinstance(original, dict) or not isinstance(reemitted, dict):
            self.compare_value(scope, category, path, original, reemitted)
            return
        self.compare_value(
            scope,
            f"{category}-description",
            f"{path}/description",
            original.get("description"),
            reemitted.get("description"),
            text_projection,
        )
        self.compare_headers(
            original.get("headers", {}),
            reemitted.get("headers", {}),
            f"{path}/headers",
            scope,
            f"{category}-header",
        )
        self.compare_value(
            scope,
            f"{category}-links",
            f"{path}/links",
            original.get("links", {}),
            reemitted.get("links", {}),
            canonical_json,
        )
        self.compare_content(
            original.get("content", {}),
            reemitted.get("content", {}),
            f"{path}/content",
            category,
            scope,
        )

    def compare_headers(
        self,
        original_headers,
        reemitted_headers,
        path,
        scope="operation",
        category="response-header",
    ):
        original_names = {name.lower(): (name, value) for name, value in original_headers.items()}
        reemitted_names = {name.lower(): (name, value) for name, value in reemitted_headers.items()}
        self.compare_value(
            scope,
            f"{category}-set",
            path,
            sorted(original_names),
            sorted(reemitted_names),
        )
        for name in set(original_names) & set(reemitted_names):
            original_raw = original_names[name][1]
            reemitted_raw = reemitted_names[name][1]
            original_ref = original_raw.get("$ref") if isinstance(original_raw, dict) else None
            reemitted_ref = reemitted_raw.get("$ref") if isinstance(reemitted_raw, dict) else None
            self.compare_value(
                scope,
                f"{category}-ref-identity",
                f"{path}/{name}",
                component_ref_identity(original_ref, "headers") if original_ref else None,
                component_ref_identity(reemitted_ref, "headers") if reemitted_ref else None,
            )
            original = resolve_once(self.original, original_raw)
            reemitted = resolve_once(self.reemitted, reemitted_raw)
            if not isinstance(original, dict) or not isinstance(reemitted, dict):
                continue
            self.compare_parameter_value(
                {"in": "header", **original},
                {"in": "header", **reemitted},
                f"{path}/{name}",
                scope,
                category,
            )

    def compare_content(self, original, reemitted, path, label, scope="operation"):
        original_types = set(original)
        reemitted_types = set(reemitted)
        self.compare_value(
            scope,
            f"{label}-content-types",
            path,
            sorted(original_types),
            sorted(reemitted_types),
        )
        for media_type in sorted(original_types & reemitted_types):
            original_media = original[media_type] or {}
            reemitted_media = reemitted[media_type] or {}
            media_path = f"{path}/{media_type}"
            self.compare_value(
                scope,
                f"{label}-examples",
                media_path,
                examples_projection(original_media),
                examples_projection(reemitted_media),
            )
            self.compare_value(
                scope,
                f"{label}-encoding",
                f"{media_path}/encoding",
                original_media.get("encoding", {}),
                reemitted_media.get("encoding", {}),
                canonical_json,
            )
            self.compare_schema(
                original_media.get("schema", {}),
                reemitted_media.get("schema", {}),
                f"{media_path}/schema",
                scope,
                f"{label}-schema",
                set(),
            )

    @staticmethod
    def schema_node_key(schema):
        if isinstance(schema, dict) and "$ref" in schema:
            siblings = {key: value for key, value in schema.items() if key != "$ref"}
            return ("ref", schema.get("$ref"), canonical_json(siblings))
        return ("node", id(schema))

    @staticmethod
    def nullable(schema):
        if not isinstance(schema, dict):
            return False
        if schema.get("nullable") is True:
            return True
        declared_type = schema.get("type")
        if isinstance(declared_type, list) and "null" in declared_type:
            return True
        for composition in ("oneOf", "anyOf"):
            branches = schema.get(composition)
            if isinstance(branches, list) and any(
                isinstance(branch, dict) and branch.get("type") == "null"
                for branch in branches
            ):
                return True
        return False

    def schema_shape(self, document, schema):
        schema = resolve_once(document, schema)
        if not isinstance(schema, dict):
            return schema
        for composition in ("oneOf", "anyOf"):
            branches = schema.get(composition)
            if not isinstance(branches, list) or len(branches) != 2:
                continue
            non_null = [
                branch
                for branch in branches
                if not (isinstance(branch, dict) and branch.get("type") == "null")
            ]
            if len(non_null) == 1:
                inner = resolve_once(document, non_null[0])
                if isinstance(inner, dict):
                    merged = dict(inner)
                    merged.update(
                        {
                            key: value
                            for key, value in schema.items()
                            if key not in (composition, "nullable")
                        }
                    )
                    merged["nullable"] = True
                    return merged
        return schema

    @staticmethod
    def schema_type(schema):
        declared_type = schema.get("type")
        if isinstance(declared_type, list):
            non_null = sorted(value for value in declared_type if value != "null")
            return non_null[0] if len(non_null) == 1 else tuple(non_null)
        if declared_type is None and "properties" in schema:
            return "object"
        return declared_type

    @staticmethod
    def enum_projection(value):
        if not isinstance(value, list):
            return value
        return tuple(sorted(canonical_json(item) for item in value))

    @staticmethod
    def annotation_projection(schema):
        result = {}
        for field in SCHEMA_ANNOTATION_FIELDS:
            if field in ("readOnly", "writeOnly", "deprecated"):
                mapped = {
                    "readOnly": "x-read-only",
                    "deprecated": "x-is-deprecated",
                }.get(field)
                result[field] = bool(schema.get(field, schema.get(mapped, False)))
            elif field == "description":
                description = schema.get(field)
                if description is None:
                    description = next(
                        (
                            schema[name]
                            for name in (
                                "x-ms-summary",
                                "x-Description",
                                "x-desc",
                                "x-public-description",
                            )
                            if name in schema
                        ),
                        None,
                    )
                result[field] = text_projection(description)
            elif field in ("default", "const"):
                result[field] = {
                    "present": field in schema,
                    "value": schema.get(field),
                }
            else:
                result[field] = schema.get(field)
        result["examples"] = examples_projection(schema)
        return result

    def compare_schema(self, original, reemitted, path, scope, category, visited):
        pair = (self.schema_node_key(original), self.schema_node_key(reemitted))
        if pair in visited:
            return
        visited.add(pair)

        original_ref = original.get("$ref") if isinstance(original, dict) else None
        reemitted_ref = reemitted.get("$ref") if isinstance(reemitted, dict) else None
        if original_ref and reemitted_ref:
            original_identity = schema_ref_identity(original_ref)
            reemitted_identity = schema_ref_identity(reemitted_ref)
            if original_identity != reemitted_identity:
                self.add(scope, f"{category}-ref-identity", path, original_identity, reemitted_identity)

            resolved_original = resolve_once(self.original, original)
            resolved_reemitted = resolve_once(self.reemitted, reemitted)
            if resolved_original is not original and resolved_reemitted is not reemitted:
                self.compare_schema(
                    resolved_original,
                    resolved_reemitted,
                    path,
                    scope,
                    category,
                    visited,
                )
            return
        if original_ref:
            resolved = resolve_once(self.original, original)
            if resolved is not original:
                self.compare_schema(
                    resolved, reemitted, path, scope, category, visited
                )
            return
        if reemitted_ref:
            resolved = resolve_once(self.reemitted, reemitted)
            if resolved is not reemitted:
                self.compare_schema(
                    original, resolved, path, scope, category, visited
                )
            return

        original = self.schema_shape(self.original, original)
        reemitted = self.schema_shape(self.reemitted, reemitted)
        if not isinstance(original, dict) or not isinstance(reemitted, dict):
            if original != reemitted:
                self.add(scope, category, path, original, reemitted)
            return

        for suffix, original_value, reemitted_value in (
            ("type", self.schema_type(original), self.schema_type(reemitted)),
            ("format", original.get("format"), reemitted.get("format")),
            ("nullable", self.nullable(original), self.nullable(reemitted)),
            ("enum", self.enum_projection(original.get("enum")), self.enum_projection(reemitted.get("enum"))),
            (
                "constraints",
                {field: original.get(field) for field in SCHEMA_CONSTRAINT_FIELDS},
                {field: reemitted.get(field) for field in SCHEMA_CONSTRAINT_FIELDS},
            ),
            ("annotations", self.annotation_projection(original), self.annotation_projection(reemitted)),
        ):
            if original_value != reemitted_value:
                self.add(
                    scope,
                    f"{category}-{suffix}",
                    f"{path}/{suffix}",
                    original_value,
                    reemitted_value,
                )

        original_properties = original.get("properties", {})
        reemitted_properties = reemitted.get("properties", {})
        original_names = set(original_properties)
        reemitted_names = set(reemitted_properties)
        if original_names != reemitted_names:
            self.add(
                scope,
                f"{category}-properties",
                f"{path}/properties",
                sorted(original_names),
                sorted(reemitted_names),
            )
        original_required = set(original.get("required", []))
        reemitted_required = set(reemitted.get("required", []))
        if original_required != reemitted_required:
            self.add(
                scope,
                f"{category}-required",
                f"{path}/required",
                sorted(original_required),
                sorted(reemitted_required),
            )
        for property_name in sorted(original_names & reemitted_names):
            self.compare_schema(
                original_properties[property_name],
                reemitted_properties[property_name],
                f"{path}/properties/{property_name}",
                scope,
                category,
                visited,
            )

        # JSON Schema ignores object-only applicators for non-object instances. Their
        # presence or absence on a scalar therefore has no wire-contract effect.
        if not self.invalid_additional_properties(original):
            original_additional = original.get("additionalProperties", True)
            reemitted_additional = reemitted.get("additionalProperties", True)
            if original_additional == {}:
                original_additional = True
            if reemitted_additional == {}:
                reemitted_additional = True
            if isinstance(original_additional, dict) and isinstance(reemitted_additional, dict):
                self.compare_schema(
                    original_additional,
                    reemitted_additional,
                    f"{path}/additionalProperties",
                    scope,
                    category,
                    visited,
                )
            elif isinstance(original_additional, dict) != isinstance(reemitted_additional, dict) or original_additional != reemitted_additional:
                self.add(
                    scope,
                    f"{category}-additional-properties",
                    f"{path}/additionalProperties",
                    original_additional,
                    reemitted_additional,
                )

        original_items = original.get("items", {})
        reemitted_items = reemitted.get("items", {})
        if original_items or reemitted_items:
            self.compare_schema(
                original_items,
                reemitted_items,
                f"{path}/items",
                scope,
                category,
                visited,
            )
        for composition in ("allOf", "oneOf", "anyOf"):
            original_branches = original.get(composition, [])
            reemitted_branches = reemitted.get(composition, [])
            if len(original_branches) != len(reemitted_branches):
                self.add(
                    scope,
                    f"{category}-{composition}",
                    f"{path}/{composition}",
                    len(original_branches),
                    len(reemitted_branches),
                )
            for index, (original_branch, reemitted_branch) in enumerate(
                zip(original_branches, reemitted_branches)
            ):
                self.compare_schema(
                    original_branch,
                    reemitted_branch,
                    f"{path}/{composition}/{index}",
                    scope,
                    category,
                    visited,
                )

    def compare_component_schemas(self):
        for name in sorted(set(self.original_schemas) & set(self.reemitted_schemas)):
            self.compare_schema(
                self.original_schemas[name],
                self.reemitted_schemas[name],
                f"#/components/schemas/{name}",
                "schema",
                "schema",
                set(),
            )

    def compare_component_content(self):
        original_components = self.component_maps(self.original)
        reemitted_components = self.component_maps(self.reemitted)
        for namespace in sorted(
            namespace
            for namespace in COMPONENT_NAMESPACES
            if namespace not in ("schemas", "securitySchemes")
        ):
            original_values = original_components.get(namespace, {})
            reemitted_values = reemitted_components.get(namespace, {})
            if not isinstance(original_values, dict) or not isinstance(
                reemitted_values, dict
            ):
                continue
            for name in sorted(set(original_values) & set(reemitted_values)):
                original = original_values[name]
                reemitted = reemitted_values[name]
                path = f"#/components/{namespace}/{pointer_token(name)}"
                if namespace == "parameters":
                    original_parameter = resolve_once(self.original, original)
                    reemitted_parameter = resolve_once(self.reemitted, reemitted)
                    if isinstance(original_parameter, dict) and isinstance(
                        reemitted_parameter, dict
                    ):
                        self.compare_value(
                            "document",
                            "component-parameter-identity",
                            path,
                            (original_parameter.get("name"), original_parameter.get("in")),
                            (reemitted_parameter.get("name"), reemitted_parameter.get("in")),
                        )
                    self.compare_parameter_value(
                        original_parameter,
                        reemitted_parameter,
                        path,
                        "document",
                        "component-parameter",
                    )
                elif namespace == "requestBodies":
                    self.compare_request_body_value(
                        original,
                        reemitted,
                        path,
                        "document",
                        "component-request-body",
                        "component-request-body",
                    )
                elif namespace == "responses":
                    self.compare_response_value(
                        original,
                        reemitted,
                        path,
                        "document",
                        "component-response",
                    )
                elif namespace == "headers":
                    self.compare_headers(
                        {name: original},
                        {name: reemitted},
                        "#/components/headers",
                        "document",
                        "component-header",
                    )
                else:
                    component_category = {
                        "pathItems": "path-item",
                    }.get(
                        namespace,
                        namespace[:-1] if namespace.endswith("s") else namespace,
                    )
                    self.compare_value(
                        "document",
                        f"component-{component_category}",
                        path,
                        original,
                        reemitted,
                        canonical_json,
                    )

    def compare_reviewed_extensions(self):
        original = reviewed_extensions(self.original, "preserve")
        reemitted = reviewed_extensions(self.reemitted, "preserve")
        for owner_name in sorted(set(original) | set(reemitted)):
            owner, name = owner_name
            if owner_name not in original or owner_name not in reemitted:
                self.add(
                    extension_scope(owner),
                    "vendor-extension-preserve",
                    f"{owner}/{name}",
                    original.get(owner_name),
                    reemitted.get(owner_name),
                )
            elif canonical_json(original[owner_name]) != canonical_json(reemitted[owner_name]):
                self.add(
                    extension_scope(owner),
                    "vendor-extension-preserve",
                    f"{owner}/{name}",
                    original[owner_name],
                    reemitted[owner_name],
                )

        for (owner, name), expected in reviewed_extensions(self.original, "map").items():
            emitted_owner = resolve_pointer(self.reemitted, owner)
            actual = None
            matches = False
            if isinstance(emitted_owner, dict):
                if name == "x-is-deprecated":
                    actual = bool(emitted_owner.get("deprecated", False))
                    matches = isinstance(expected, bool) and actual == expected
                elif name == "x-read-only":
                    actual = bool(emitted_owner.get("readOnly", False))
                    matches = isinstance(expected, bool) and actual == expected
                elif name == "x-ms-summary":
                    actual = emitted_owner.get("description")
                    matches = isinstance(expected, str) and actual == expected
                elif name in ("x-Description", "x-desc", "x-public-description"):
                    actual = emitted_owner.get("description")
                    matches = isinstance(expected, str) and actual == expected
                elif name == "x-oauthpermissions":
                    security = emitted_owner.get("security")
                    actual = [
                        clause.get("oauth2")
                        for clause in security or []
                        if isinstance(clause, dict) and "oauth2" in clause
                    ] if isinstance(security, list) else security
                    matches = isinstance(expected, list) and any(
                        isinstance(scopes, list) and sorted(scopes) == sorted(expected)
                        for scopes in actual or []
                    )
            if not matches:
                self.add(
                    extension_scope(owner),
                    "vendor-extension-map",
                    f"{owner}/{name}",
                    expected,
                    actual,
                )

    def validate_integrity(self):
        for side, document in (("original", self.original), ("reemitted", self.reemitted)):
            if not (str(document.get("openapi", "")).startswith("3.") or str(document.get("swagger", "")).startswith("2.")):
                self.add("integrity", "document-structure", "#/openapi", side, "missing or unsupported dialect")
            if not isinstance(document.get("info"), dict) or not isinstance(document.get("paths"), dict):
                self.add("integrity", "document-structure", "#", side, "info and paths must be objects")
            self.validate_references(side, document, document, "#")
            self.validate_security(side, document)
            self.validate_operations(side, document)
        self.scan_markers()

    def validate_references(self, side, document, value, path):
        if isinstance(value, dict):
            reference = value.get("$ref")
            if reference is not None and (
                not isinstance(reference, str)
                or not reference.startswith("#/")
                or resolve_pointer(document, reference) is None
            ):
                self.add("integrity", "unresolved-reference", path, side, reference)
            for key, child in value.items():
                self.validate_references(side, document, child, f"{path}/{key}")
        elif isinstance(value, list):
            for index, child in enumerate(value):
                self.validate_references(side, document, child, f"{path}/{index}")

    def validate_security(self, side, document):
        schemes = set(self.security_schemes(document))
        requirements = [("#/security", document.get("security"))]
        for key, entry in operations(document).items():
            requirements.append((f"#/paths/{key[0]}/{key[1]}/security", entry["security"]))
        for path, security in requirements:
            if not isinstance(security, list):
                continue
            for clause in security:
                if not isinstance(clause, dict):
                    continue
                for scheme in set(clause) - schemes:
                    self.add("integrity", "undefined-security-scheme", path, side, scheme)

    def validate_operations(self, side, document):
        operation_ids = collections.defaultdict(list)
        for (path, method), entry in operations(document).items():
            operation = entry["operation"]
            operation_path = f"#/paths/{path}/{method}"
            if not isinstance(operation.get("responses"), dict) or not operation.get("responses"):
                self.add("integrity", "document-structure", f"{operation_path}/responses", side, "responses must be a non-empty object")
            else:
                for response_key in operation["responses"]:
                    if not re.fullmatch(r"default|[1-5][0-9]{2}|[1-5]XX", response_key):
                        self.add(
                            "integrity",
                            "document-structure",
                            f"{operation_path}/responses/{response_key}",
                            side,
                            "invalid response key",
                        )
            operation_id = operation.get("operationId")
            if operation_id is not None:
                operation_ids[operation_id].append(operation_path)
            route_tokens = set(re.findall(r"\{([^{}]+)\}", path))
            parameter_tokens = {
                parameter.get("name")
                for parameter in self.parameter_map(document, operation).values()
                if parameter.get("in") == "path"
            }
            if route_tokens != parameter_tokens:
                self.add("integrity", "path-parameter-mismatch", operation_path, sorted(route_tokens), sorted(parameter_tokens))
        for operation_id, paths in operation_ids.items():
            if len(paths) > 1:
                self.add("integrity", "duplicate-operation-id", "#", side, {"operationId": operation_id, "paths": paths})

    def scan_markers(self):
        marker = "[rivet:unsupported"
        for source_path in self.generated_sources:
            paths = []
            if os.path.isdir(source_path):
                for root, _, names in os.walk(source_path):
                    paths.extend(os.path.join(root, name) for name in names if name.endswith(".cs"))
            else:
                paths.append(source_path)
            for path in paths:
                try:
                    with open(path, encoding="utf-8") as source:
                        for line_number, line in enumerate(source, 1):
                            if marker in line:
                                self.add("integrity", "unsupported-marker", f"{path}:{line_number}", None, line.strip())
                except (OSError, UnicodeError) as error:
                    raise ValueError(f"cannot scan generated source {path}: {error}") from error

    def run(self):
        self.compare_document()
        self.compare_operations()
        self.compare_component_schemas()
        self.compare_component_content()
        self.compare_reviewed_extensions()
        self.validate_integrity()

    def category_counts(self, scope):
        return {
            category: len(items)
            for category, items in sorted(self.findings[scope].items())
        }

    def has_findings(self):
        return any(
            (
                self.missing_operations,
                self.invented_operations,
                self.missing_schemas,
                self.invented_schemas,
                self.missing_components,
                self.invented_components,
                *(self.findings[scope] for scope in self.findings),
            )
        )

    def summary(self):
        operation_finding_paths = {
            item["path"].split("/responses", 1)[0].split("/parameters", 1)[0].split("/requestBody", 1)[0]
            for items in self.findings["operation"].values()
            for item in items
        }
        return {
            "originalOps": len(self.original_operations),
            "reemittedOps": len(self.reemitted_operations),
            "sharedOps": len(self.shared_operations),
            "missingOperations": len(self.missing_operations),
            "inventedOperations": len(self.invented_operations),
            "operationsWithFindings": len(operation_finding_paths),
            "originalSchemas": len(self.original_schemas),
            "reemittedSchemas": len(self.reemitted_schemas),
            "matchedSchemas": len(set(self.original_schemas) & set(self.reemitted_schemas)),
            "unmatchedOriginalSchemas": len(self.missing_schemas),
            "unmatchedReemittedSchemas": len(self.invented_schemas),
            "originalComponents": len(self.original_components),
            "reemittedComponents": len(self.reemitted_components),
            "matchedComponents": len(
                self.original_components & self.reemitted_components
            ),
            "unmatchedOriginalComponents": len(self.missing_components),
            "unmatchedReemittedComponents": len(self.invented_components),
            "documentFindings": self.category_counts("document"),
            "opFindings": self.category_counts("operation"),
            "schemaFindings": self.category_counts("schema"),
            "integrityFindings": self.category_counts("integrity"),
            "sourceDefects": len(self.source_defects),
        }

    def details(self):
        return {
            "missingOperations": [list(key) for key in self.missing_operations],
            "inventedOperations": [list(key) for key in self.invented_operations],
            "unmatchedOriginalSchemas": self.missing_schemas,
            "unmatchedReemittedSchemas": self.invented_schemas,
            "unmatchedOriginalComponents": [list(value) for value in self.missing_components],
            "unmatchedReemittedComponents": [list(value) for value in self.invented_components],
            "documentFindings": dict(self.findings["document"]),
            "opFindings": dict(self.findings["operation"]),
            "schemaFindings": dict(self.findings["schema"]),
            "integrityFindings": dict(self.findings["integrity"]),
            "sourceDefects": self.source_defects,
        }


def write_json(path, value):
    if not path:
        return
    try:
        with open(path, "w", encoding="utf-8") as destination:
            json.dump(value, destination, indent=2, sort_keys=True)
            destination.write("\n")
    except OSError as error:
        raise ValueError(f"cannot write {path}: {error}") from error


def print_report(comparator):
    print(
        f"ops: orig={len(comparator.original_operations)} "
        f"reemit={len(comparator.reemitted_operations)} "
        f"shared={len(comparator.shared_operations)} "
        f"only_orig={len(comparator.missing_operations)} "
        f"only_reemit={len(comparator.invented_operations)}"
    )
    print(
        f"schemas: orig={len(comparator.original_schemas)} "
        f"reemit={len(comparator.reemitted_schemas)} "
        f"matched={len(set(comparator.original_schemas) & set(comparator.reemitted_schemas))} "
        f"only_orig={len(comparator.missing_schemas)} "
        f"only_reemit={len(comparator.invented_schemas)}"
    )
    for scope in ("document", "operation", "schema", "integrity"):
        print(f"\n===== {scope.upper()} =====")
        for category, count in comparator.category_counts(scope).items():
            print(f"{category}: {count}")


def main():
    args = parse_args()
    try:
        original = load_document(args.original)
        reemitted = load_document(args.reemitted)
        comparator = Comparator(original, reemitted, args.generated_source)
        comparator.run()
        summary = comparator.summary()
        details = comparator.details()
        write_json(args.summary_json, summary)
        write_json(args.details_json, details)
        print_report(comparator)
        return 1 if comparator.has_findings() else 0
    except ValueError as error:
        print(f"roundtrip-diff: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
