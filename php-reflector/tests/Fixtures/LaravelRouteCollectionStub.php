<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests\Fixtures;

class LaravelRouteCollectionStub
{
    /** @param LaravelRouteStub[] $routes */
    public function __construct(private array $routes) {}

    /** @return LaravelRouteStub[] */
    public function getRoutes(): array
    {
        return $this->routes;
    }
}
