<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests\Fixtures;

enum Status: string
{
    case Pending = 'pending';
    case Active = 'active';
    case Archived = 'archived';
}
