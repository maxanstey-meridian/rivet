<?php

declare(strict_types=1);

namespace Rivet\PhpReflector;

class LaravelRouteWalker
{
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

    /**
     * @param array<array{httpMethod: string, uri: string, controller: string, action: string}> $routes
     */
    public static function walk(array $routes, array $extraFqcns = []): array
    {
        return EndpointBuilder::walkRoutes($routes, $extraFqcns);
    }
}
