<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests;

use PHPUnit\Framework\TestCase;
use Rivet\PhpReflector\Attribute\RivetType;
use Rivet\PhpReflector\Tests\Fixtures\SharedConfigDto;

class RivetTypeAttributeTest extends TestCase
{
    public function testAttributeIsPresentOnSharedConfigDto(): void
    {
        $ref = new \ReflectionClass(SharedConfigDto::class);
        $attrs = $ref->getAttributes(RivetType::class);

        $this->assertCount(1, $attrs);
    }

    public function testAttributeTargetsClassOnly(): void
    {
        $ref = new \ReflectionClass(RivetType::class);
        $metaAttrs = $ref->getAttributes(\Attribute::class);

        $this->assertCount(1, $metaAttrs);

        $meta = $metaAttrs[0]->newInstance();
        $this->assertSame(\Attribute::TARGET_CLASS, $meta->flags);
    }

    public function testAttributeCanBeInstantiated(): void
    {
        $attr = new RivetType();

        $this->assertInstanceOf(RivetType::class, $attr);
    }
}
