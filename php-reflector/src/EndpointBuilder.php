<?php

declare(strict_types=1);

namespace Rivet\PhpReflector;

class EndpointBuilder
{
    public static function collectRefsFromType(array $typeNode, string $namespace, array &$fqcns): void
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

    public static function buildEndpoint(
        \ReflectionClass $ref,
        \ReflectionMethod $method,
        string $httpMethod,
        string $routeTemplate,
        array &$referencedFqcns,
    ): array {
        $controllerName = lcfirst(preg_replace('/Controller$/', '', $ref->getShortName()));

        preg_match_all('/\{(\w+)\}/', $routeTemplate, $routeMatches);
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

        $returnType = ResponseResolver::resolve($method);

        if ($returnType !== null) {
            $namespace = $ref->getNamespaceName();
            self::collectRefsFromType($returnType, $namespace, $referencedFqcns);
        }

        $responses = $returnType !== null
            ? [['statusCode' => 200, 'dataType' => $returnType]]
            : [];

        return [
            'name' => $method->getName(),
            'httpMethod' => $httpMethod,
            'routeTemplate' => $routeTemplate,
            'controllerName' => $controllerName,
            'params' => $params,
            'returnType' => $returnType,
            'responses' => $responses,
        ];
    }

    /**
     * @param array<array{httpMethod: string, uri: string, controller: string, action: string}> $routes
     */
    public static function walkRoutes(array $routes): array
    {
        $endpoints = [];
        $referencedFqcns = [];

        foreach ($routes as $route) {
            $ref = new \ReflectionClass($route['controller']);
            $method = $ref->getMethod($route['action']);

            $endpoints[] = self::buildEndpoint(
                $ref,
                $method,
                $route['httpMethod'],
                $route['uri'],
                $referencedFqcns,
            );
        }

        return self::buildContract($endpoints, $referencedFqcns);
    }

    public static function buildContract(array $endpoints, array $referencedFqcns): array
    {
        if ($referencedFqcns !== []) {
            $walked = PropertyWalker::walk(...array_keys($referencedFqcns));
        } else {
            $walked = ['types' => [], 'enums' => []];
        }

        return ['types' => $walked['types'], 'enums' => $walked['enums'], 'endpoints' => $endpoints];
    }
}
