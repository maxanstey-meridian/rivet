<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests\Fixtures;

class LaravelRouteStub
{
    /** @param string[] $methods */
    public function __construct(
        private array $methods,
        private string $uri,
        private string $actionName,
    ) {}

    /** @return string[] */
    public function methods(): array
    {
        return $this->methods;
    }

    public function uri(): string
    {
        return $this->uri;
    }

    public function getActionName(): string
    {
        return $this->actionName;
    }
}
