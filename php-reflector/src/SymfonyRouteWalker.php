<?php

declare(strict_types=1);

namespace Rivet\PhpReflector;

class SymfonyRouteWalker
{
    /**
     * @return array<array{httpMethod: string, uri: string, controller: string, action: string}>
     */
    public static function fromRouteCollection(object $collection): array
    {
        $routes = [];

        foreach ($collection->all() as $route) {
            $defaults = $route->getDefaults();
            $controller = $defaults['_controller'] ?? null;
            if (!is_string($controller)) {
                continue;
            }

            $methods = $route->getMethods();
            if ($methods === []) {
                continue;
            }

            if (str_contains($controller, '::')) {
                [$class, $action] = explode('::', $controller, 2);
            } else {
                $class = $controller;
                $action = '__invoke';
            }

            $routes[] = [
                'httpMethod' => $methods[0],
                'uri' => $route->getPath(),
                'controller' => $class,
                'action' => $action,
            ];
        }

        return $routes;
    }

    /**
     * @param array<array{httpMethod: string, uri: string, controller: string, action: string}> $routes
     */
    public static function walk(array $routes, array $extraFqcns = []): array
    {
        return EndpointBuilder::walkRoutes($routes, $extraFqcns);
    }
}
