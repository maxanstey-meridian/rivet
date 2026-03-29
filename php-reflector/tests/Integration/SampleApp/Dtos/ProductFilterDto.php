<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests\Integration\SampleApp\Dtos;

use Rivet\PhpReflector\Tests\Integration\SampleApp\Enums\Priority;

class ProductFilterDto
{
    public string $query;
    public int $page;

    /** @var list<Priority> */
    public array $priorities;
}
