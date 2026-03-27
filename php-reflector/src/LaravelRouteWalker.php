<?php

declare(strict_types=1);

namespace Rivet\PhpReflector;

class LaravelRouteWalker
{
    /**
     * @param array<array{httpMethod: string, uri: string, controller: string, action: string}> $routes
     */
    /**
     * @return array<array{httpMethod: string, uri: string, controller: string, action: string}>
     */
    public static function fromRouteCollection(object $collection): array
    {
        $routes = [];

        foreach ($collection->getRoutes() as $route) {
            $methods = array_filter($route->methods(), fn(string $m) => $m !== 'HEAD');
            if ($methods === []) {
                continue;
            }

            $actionName = $route->getActionName();
            if ($actionName === 'Closure') {
                continue;
            }

            if (str_contains($actionName, '@')) {
                [$controller, $action] = explode('@', $actionName, 2);
            } else {
                $controller = $actionName;
                $action = '__invoke';
            }

            $routes[] = [
                'httpMethod' => reset($methods),
                'uri' => $route->uri(),
                'controller' => $controller,
                'action' => $action,
            ];
        }

        return $routes;
    }

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

    public static function walk(array $routes): array
    {
        $endpoints = [];
        $referencedFqcns = [];

        foreach ($routes as $route) {
            $ref = new \ReflectionClass($route['controller']);
            $controllerName = lcfirst(preg_replace('/Controller$/', '', $ref->getShortName()));
            $method = $ref->getMethod($route['action']);

            preg_match_all('/\{(\w+)\}/', $route['uri'], $routeMatches);
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
            $responseAttrs = $method->getAttributes(Attribute\RivetResponse::class);
            if ($responseAttrs !== []) {
                $responseType = $responseAttrs[0]->newInstance()->type;
                if (class_exists($responseType)) {
                    $referencedFqcns[$responseType] = true;
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

            $endpoints[] = [
                'name' => $route['action'],
                'httpMethod' => $route['httpMethod'],
                'routeTemplate' => $route['uri'],
                'controllerName' => $controllerName,
                'params' => $params,
                'returnType' => $returnType,
                'responses' => $responses,
            ];
        }

        if ($referencedFqcns !== []) {
            $walked = PropertyWalker::walk(...array_keys($referencedFqcns));
        } else {
            $walked = ['types' => [], 'enums' => []];
        }

        return ['types' => $walked['types'], 'enums' => $walked['enums'], 'endpoints' => $endpoints];
    }
}
