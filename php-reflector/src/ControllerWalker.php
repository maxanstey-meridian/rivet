<?php

declare(strict_types=1);

namespace Rivet\PhpReflector;

use Rivet\PhpReflector\Attribute\RivetRoute;

class ControllerWalker
{
    public static function walk(string ...$classNames): array
    {
        $endpoints = [];
        $referencedFqcns = [];

        foreach ($classNames as $fqcn) {
            $ref = new \ReflectionClass($fqcn);

            foreach ($ref->getMethods(\ReflectionMethod::IS_PUBLIC) as $method) {
                $routeAttrs = $method->getAttributes(RivetRoute::class);
                if ($routeAttrs === []) {
                    continue;
                }

                $route = $routeAttrs[0]->newInstance();

                $endpoints[] = EndpointBuilder::buildEndpoint(
                    $ref,
                    $method,
                    $route->method,
                    $route->route,
                    $referencedFqcns,
                );
            }
        }

        return EndpointBuilder::buildContract($endpoints, $referencedFqcns);
    }
}
