<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests;

use PHPUnit\Framework\TestCase;
use Rivet\PhpReflector\EndpointBuilder;
use Rivet\PhpReflector\Tests\Fixtures\SharedConfigDto;

class EndpointBuilderTest extends TestCase
{
    public function testExtraFqcnsAreWalkedWithEmptyEndpoints(): void
    {
        $result = EndpointBuilder::buildContract([], [], null, [SharedConfigDto::class]);

        $this->assertCount(1, $result['types']);
        $this->assertSame('SharedConfigDto', $result['types'][0]['name']);
        $this->assertSame([], $result['endpoints']);
    }

    public function testExtraFqcnsDeduplicateWithReferencedFqcns(): void
    {
        $result = EndpointBuilder::buildContract([], [SharedConfigDto::class => true], null, [SharedConfigDto::class]);

        $this->assertCount(1, $result['types']);
        $this->assertSame('SharedConfigDto', $result['types'][0]['name']);
    }

    public function testWalkRoutesForwardsExtraFqcns(): void
    {
        $result = EndpointBuilder::walkRoutes([], [SharedConfigDto::class]);

        $this->assertCount(1, $result['types']);
        $this->assertSame('SharedConfigDto', $result['types'][0]['name']);
    }

    public function testBuildContractWithNonexistentExtraFqcnThrowsReflectionException(): void
    {
        $this->expectException(\ReflectionException::class);

        EndpointBuilder::buildContract([], [], null, ['NonExistent\\FakeClass']);
    }
}
