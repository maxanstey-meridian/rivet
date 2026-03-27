<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests\Fixtures;

class SymfonyRouteStub
{
    /** @param string[] $methods */
    public function __construct(
        private array $methods,
        private string $path,
        private array $defaults,
    ) {}

    /** @return string[] */
    public function getMethods(): array
    {
        return $this->methods;
    }

    public function getPath(): string
    {
        return $this->path;
    }

    public function getDefaults(): array
    {
        return $this->defaults;
    }
}
