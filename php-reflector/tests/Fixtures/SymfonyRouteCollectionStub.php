<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests\Fixtures;

class SymfonyRouteCollectionStub
{
    /** @param array<string, SymfonyRouteStub> $routes */
    public function __construct(private array $routes) {}

    /** @return array<string, SymfonyRouteStub> */
    public function all(): array
    {
        return $this->routes;
    }
}
