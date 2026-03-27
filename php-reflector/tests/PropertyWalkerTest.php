<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests;

use PHPUnit\Framework\TestCase;
use Rivet\PhpReflector\PropertyWalker;
use Rivet\PhpReflector\Tests\Fixtures\AddressDto;
use Rivet\PhpReflector\Tests\Fixtures\NullableDto;
use Rivet\PhpReflector\Tests\Fixtures\OrderDto;
use Rivet\PhpReflector\Tests\Fixtures\PersonDto;
use Rivet\PhpReflector\Tests\Fixtures\ScalarDto;
use Rivet\PhpReflector\Tests\Fixtures\LooseDto;
use Rivet\PhpReflector\Tests\Fixtures\MetadataDto;
use Rivet\PhpReflector\Tests\Fixtures\ProfileDto;
use Rivet\PhpReflector\Tests\Fixtures\PriorityDto;
use Rivet\PhpReflector\Tests\Fixtures\ResponseDto;
use Rivet\PhpReflector\Tests\Fixtures\TagContainerDto;
use Rivet\PhpReflector\Tests\Fixtures\TaskDto;

class PropertyWalkerTest extends TestCase
{
    public function testScalarProperties(): void
    {
        $result = PropertyWalker::walk(ScalarDto::class);

        $this->assertSame([], $result['enums']);
        $this->assertSame([], $result['endpoints']);
        $this->assertCount(1, $result['types']);

        $type = $result['types'][0];
        $this->assertSame('ScalarDto', $type['name']);
        $this->assertSame([], $type['typeParameters']);
        $this->assertCount(4, $type['properties']);

        $this->assertSame('name', $type['properties'][0]['name']);
        $this->assertSame(['kind' => 'primitive', 'type' => 'string'], $type['properties'][0]['type']);
        $this->assertFalse($type['properties'][0]['optional']);

        $this->assertSame('age', $type['properties'][1]['name']);
        $this->assertSame(['kind' => 'primitive', 'type' => 'number', 'format' => 'int32'], $type['properties'][1]['type']);

        $this->assertSame('score', $type['properties'][2]['name']);
        $this->assertSame(['kind' => 'primitive', 'type' => 'number', 'format' => 'double'], $type['properties'][2]['type']);

        $this->assertSame('active', $type['properties'][3]['name']);
        $this->assertSame(['kind' => 'primitive', 'type' => 'boolean'], $type['properties'][3]['type']);
    }

    public function testNullableScalar(): void
    {
        $result = PropertyWalker::walk(NullableDto::class);
        $type = $result['types'][0];

        $this->assertSame(
            ['kind' => 'nullable', 'inner' => ['kind' => 'primitive', 'type' => 'string']],
            $type['properties'][0]['type']
        );
        $this->assertSame(
            ['kind' => 'nullable', 'inner' => ['kind' => 'primitive', 'type' => 'number', 'format' => 'int32']],
            $type['properties'][1]['type']
        );
    }

    public function testClassRefTransitive(): void
    {
        $result = PropertyWalker::walk(PersonDto::class);

        $this->assertCount(2, $result['types']);

        $person = $result['types'][0];
        $this->assertSame('PersonDto', $person['name']);
        $this->assertSame(['kind' => 'ref', 'name' => 'AddressDto'], $person['properties'][1]['type']);

        $address = $result['types'][1];
        $this->assertSame('AddressDto', $address['name']);
        $this->assertCount(2, $address['properties']);
    }

    public function testBackedEnum(): void
    {
        $result = PropertyWalker::walk(OrderDto::class);

        $this->assertCount(1, $result['types']);
        $this->assertSame('OrderDto', $result['types'][0]['name']);
        $this->assertSame(['kind' => 'ref', 'name' => 'Status'], $result['types'][0]['properties'][1]['type']);

        $this->assertCount(1, $result['enums']);
        $this->assertSame('Status', $result['enums'][0]['name']);
        $this->assertSame(['pending', 'active', 'archived'], $result['enums'][0]['values']);
    }

    public function testArrayWithListDocblock(): void
    {
        $result = PropertyWalker::walk(TagContainerDto::class);
        $this->assertSame(
            ['kind' => 'array', 'element' => ['kind' => 'primitive', 'type' => 'string']],
            $result['types'][0]['properties'][0]['type']
        );
    }

    public function testArrayWithDictionaryDocblock(): void
    {
        $result = PropertyWalker::walk(MetadataDto::class);
        $this->assertSame(
            ['kind' => 'dictionary', 'value' => ['kind' => 'primitive', 'type' => 'number', 'format' => 'int32']],
            $result['types'][0]['properties'][0]['type']
        );
    }

    public function testArrayWithShapeDocblock(): void
    {
        $result = PropertyWalker::walk(ResponseDto::class);
        $prop = $result['types'][0]['properties'][0];
        $this->assertSame('inlineObject', $prop['type']['kind']);
        $this->assertCount(2, $prop['type']['properties']);
    }

    public function testStringLiteralUnionFromDocblock(): void
    {
        $result = PropertyWalker::walk(PriorityDto::class);
        $this->assertSame(
            ['kind' => 'stringUnion', 'values' => ['low', 'medium', 'high']],
            $result['types'][0]['properties'][0]['type']
        );
    }

    public function testArrayWithoutDocblockMapsToUnknown(): void
    {
        $warning = null;
        set_error_handler(function (int $errno, string $errstr) use (&$warning) {
            $warning = $errstr;
            return true;
        }, E_USER_WARNING);

        $result = PropertyWalker::walk(LooseDto::class);
        restore_error_handler();

        $this->assertSame(['kind' => 'primitive', 'type' => 'unknown'], $result['types'][0]['properties'][0]['type']);
        $this->assertNotNull($warning);
        $this->assertStringContainsString('items', $warning);
        $this->assertStringContainsString('@var', $warning);
    }

    public function testNullableClassRef(): void
    {
        $result = PropertyWalker::walk(ProfileDto::class);

        $this->assertCount(2, $result['types']);
        $this->assertSame(
            ['kind' => 'nullable', 'inner' => ['kind' => 'ref', 'name' => 'AddressDto']],
            $result['types'][0]['properties'][0]['type']
        );
        $this->assertSame('AddressDto', $result['types'][1]['name']);
    }

    public function testMultipleRootsDeduplication(): void
    {
        $result = PropertyWalker::walk(PersonDto::class, OrderDto::class);

        $typeNames = array_column($result['types'], 'name');
        sort($typeNames);
        $this->assertSame(['AddressDto', 'OrderDto', 'PersonDto'], $typeNames);

        $this->assertCount(1, $result['enums']);
        $this->assertSame('Status', $result['enums'][0]['name']);
    }

    public function testIntBackedEnumWarnsAndEmitsRef(): void
    {
        $warning = null;
        set_error_handler(function (int $errno, string $errstr) use (&$warning) {
            $warning = $errstr;
            return true;
        }, E_USER_WARNING);

        $result = PropertyWalker::walk(TaskDto::class);
        restore_error_handler();

        $this->assertCount(1, $result['types']);
        $this->assertSame(['kind' => 'ref', 'name' => 'Priority'], $result['types'][0]['properties'][1]['type']);
        $this->assertCount(0, $result['enums']);
        $this->assertNotNull($warning);
        $this->assertStringContainsString('Priority', $warning);
    }
}
