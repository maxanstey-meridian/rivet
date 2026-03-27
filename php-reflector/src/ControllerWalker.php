<?php

declare(strict_types=1);

namespace Rivet\PhpReflector;

use Rivet\PhpReflector\Attribute\RivetResponse;
use Rivet\PhpReflector\Attribute\RivetRoute;

class ControllerWalker
{
    private static function collectRefsFromType(array $typeNode, string $namespace, array &$fqcns): void
    {
        if ($typeNode['kind'] === 'ref') {
            $name = $typeNode['name'];
            $fqcn = $namespace !== '' ? $namespace . '\\' . $name : $name;
            if (class_exists($fqcn)) {
                $fqcns[$fqcn] = true;
            }
            return;
        }

        foreach (['inner', 'element', 'value'] as $key) {
            if (isset($typeNode[$key]) && is_array($typeNode[$key])) {
                self::collectRefsFromType($typeNode[$key], $namespace, $fqcns);
            }
        }

        if (isset($typeNode['properties']) && is_array($typeNode['properties'])) {
            foreach ($typeNode['properties'] as $prop) {
                if (isset($prop['type'])) {
                    self::collectRefsFromType($prop['type'], $namespace, $fqcns);
                }
            }
        }

        if (isset($typeNode['typeArgs']) && is_array($typeNode['typeArgs'])) {
            foreach ($typeNode['typeArgs'] as $arg) {
                self::collectRefsFromType($arg, $namespace, $fqcns);
            }
        }
    }

    public static function walk(string ...$classNames): array
    {
        $endpoints = [];
        $referencedFqcns = [];

        foreach ($classNames as $fqcn) {
            $ref = new \ReflectionClass($fqcn);
            $controllerName = lcfirst(preg_replace('/Controller$/', '', $ref->getShortName()));

            foreach ($ref->getMethods(\ReflectionMethod::IS_PUBLIC) as $method) {
                $routeAttrs = $method->getAttributes(RivetRoute::class);
                if ($routeAttrs === []) {
                    continue;
                }

                $route = $routeAttrs[0]->newInstance();

                preg_match_all('/\{(\w+)\}/', $route->route, $routeMatches);
                $routeParamNames = array_flip($routeMatches[1]);

                $params = [];
                foreach ($method->getParameters() as $param) {
                    $paramType = $param->getType();
                    if (!$paramType instanceof \ReflectionNamedType) {
                        continue;
                    }

                    $typeName = $paramType->getName();

                    if (class_exists($typeName)) {
                        $params[] = [
                            'name' => $param->getName(),
                            'type' => ['kind' => 'ref', 'name' => (new \ReflectionClass($typeName))->getShortName()],
                            'source' => 'body',
                        ];
                        $referencedFqcns[$typeName] = true;
                    } else {
                        $source = isset($routeParamNames[$param->getName()]) ? 'route' : 'query';
                        $params[] = [
                            'name' => $param->getName(),
                            'type' => TypeParser::parse($typeName),
                            'source' => $source,
                        ];
                    }
                }

                // Collect response FQCN if it's a class ref
                $responseAttrs = $method->getAttributes(RivetResponse::class);
                if ($responseAttrs !== []) {
                    $responseType = $responseAttrs[0]->newInstance()->type;
                    if (class_exists($responseType)) {
                        $referencedFqcns[$responseType] = true;
                    }
                }

                $returnType = ResponseResolver::resolve($method);

                // Recurse into inline response type nodes to find class refs
                if ($returnType !== null) {
                    $namespace = $ref->getNamespaceName();
                    self::collectRefsFromType($returnType, $namespace, $referencedFqcns);
                }
                $responses = $returnType !== null
                    ? [['statusCode' => 200, 'dataType' => $returnType]]
                    : [];

                $endpoints[] = [
                    'name' => $method->getName(),
                    'httpMethod' => $route->method,
                    'routeTemplate' => $route->route,
                    'controllerName' => $controllerName,
                    'params' => $params,
                    'returnType' => $returnType,
                    'responses' => $responses,
                ];
            }
        }

        if ($referencedFqcns !== []) {
            $walked = PropertyWalker::walk(...array_keys($referencedFqcns));
        } else {
            $walked = ['types' => [], 'enums' => []];
        }

        return ['types' => $walked['types'], 'enums' => $walked['enums'], 'endpoints' => $endpoints];
    }

}
