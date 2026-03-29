<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests\Integration\SampleApp\Enums;

enum ProductStatus: string
{
    case Active = 'active';
    case Draft = 'draft';
    case Archived = 'archived';
}
