<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests;

use PHPUnit\Framework\TestCase;
use Rivet\PhpReflector\PropertyWalker;
use Rivet\PhpReflector\Tests\Fixtures\OrderItemDto;
use Rivet\PhpReflector\Tests\Fixtures\OrderItemLooseDto;
use Rivet\PhpReflector\Tests\Fixtures\OrderItemNestedDto;

class DtoConventionsTest extends TestCase
{
    private static array $result;
    private static array $props;

    public static function setUpBeforeClass(): void
    {
        self::$result = PropertyWalker::walk(OrderItemDto::class);
        self::$props = self::$result['types'][0]['properties'];
    }

    public function testScalarProperties(): void
    {
        $this->assertCount(1, self::$result['types']);
        $this->assertCount(6, self::$props);

        $this->assertSame('productId', self::$props[0]['name']);
        $this->assertSame(['kind' => 'primitive', 'type' => 'string'], self::$props[0]['type']);

        $this->assertSame('quantity', self::$props[1]['name']);
        $this->assertSame(['kind' => 'primitive', 'type' => 'number', 'format' => 'int32'], self::$props[1]['type']);

        $this->assertSame('unitPrice', self::$props[2]['name']);
        $this->assertSame(['kind' => 'primitive', 'type' => 'number', 'format' => 'double'], self::$props[2]['type']);
    }

    public function testListProperty(): void
    {
        $this->assertSame('tags', self::$props[3]['name']);
        $this->assertSame(['kind' => 'array', 'element' => ['kind' => 'primitive', 'type' => 'string']], self::$props[3]['type']);
    }

    public function testShapeWithOptionalField(): void
    {
        $this->assertSame('dimensions', self::$props[4]['name']);
        $dim = self::$props[4]['type'];

        $this->assertSame('inlineObject', $dim['kind']);
        $this->assertCount(3, $dim['properties']);

        $this->assertSame('width', $dim['properties'][0]['name']);
        $this->assertSame(['kind' => 'primitive', 'type' => 'number', 'format' => 'double'], $dim['properties'][0]['type']);

        $this->assertSame('height', $dim['properties'][1]['name']);
        $this->assertSame(['kind' => 'primitive', 'type' => 'number', 'format' => 'double'], $dim['properties'][1]['type']);

        $this->assertSame('weight', $dim['properties'][2]['name']);
        $this->assertSame(
            ['kind' => 'nullable', 'inner' => ['kind' => 'primitive', 'type' => 'number', 'format' => 'double']],
            $dim['properties'][2]['type']
        );
    }

    public function testStringUnionProperty(): void
    {
        $this->assertSame('size', self::$props[5]['name']);
        $this->assertSame(['kind' => 'stringUnion', 'values' => ['small', 'medium', 'large']], self::$props[5]['type']);
    }

    public function testUntypedArrayEmitsWarning(): void
    {
        $result = PropertyWalker::walk(OrderItemLooseDto::class);

        $diag = $result['diagnostics'];
        $all = $diag->all();
        $this->assertCount(1, $all);
        $this->assertSame('warning', $all[0]['severity']);
        $this->assertStringContainsString('OrderItemLooseDto', $all[0]['message']);
        $this->assertStringContainsString('extras', $all[0]['message']);
        $this->assertStringContainsString('@var', $all[0]['message']);

        $extras = $result['types'][0]['properties'][1];
        $this->assertSame('extras', $extras['name']);
        $this->assertSame(['kind' => 'primitive', 'type' => 'unknown'], $extras['type']);
    }

    public function testNestedDtoRecursion(): void
    {
        $result = PropertyWalker::walk(OrderItemNestedDto::class);

        $this->assertCount(2, $result['types']);

        $typeNames = array_column($result['types'], 'name');
        $this->assertContains('OrderItemNestedDto', $typeNames);
        $this->assertContains('AddressDto', $typeNames);

        $nested = $result['types'][0];
        $this->assertSame('OrderItemNestedDto', $nested['name']);

        $addrProp = $nested['properties'][1];
        $this->assertSame('shippingAddress', $addrProp['name']);
        $this->assertSame(['kind' => 'ref', 'name' => 'AddressDto'], $addrProp['type']);
    }
}
