<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests\Fixtures;

class BrokenRefDto
{
    /** @var list<NonExistentClass> */
    public array $items;
}
