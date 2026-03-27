<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests\Integration\SampleApp\Dtos;

use Rivet\PhpReflector\Tests\Integration\SampleApp\Enums\Priority;
use Rivet\PhpReflector\Tests\Integration\SampleApp\Enums\ProductStatus;

class ProductDto
{
    public string $title;
    public int $id;
    public float $price;
    public bool $active;
    public ?string $description;
    public ProductStatus $status;
    public Priority $priority;
    public UserDto $author;

    /** @var list<string> */
    public array $tags;

    /** @var array<string, int> */
    public array $metadata;

    /** @var array{width: int, height: int} */
    public array $dimensions;

    /** @var 'small'|'medium'|'large' */
    public string $size;

    /** @var 1|2|3 */
    public int $rating;
}
