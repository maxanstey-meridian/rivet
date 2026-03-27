<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests;

use PHPUnit\Framework\TestCase;
use Rivet\PhpReflector\ContractEmitter;
use Rivet\PhpReflector\PropertyWalker;
use Rivet\PhpReflector\Tests\Fixtures\NullableDto;
use Rivet\PhpReflector\Tests\Fixtures\OrderDto;
use Rivet\PhpReflector\Tests\Fixtures\PersonDto;
use Rivet\PhpReflector\Tests\Fixtures\ResponseDto;
use Rivet\PhpReflector\Tests\Fixtures\ScalarDto;
use Rivet\PhpReflector\Tests\Fixtures\TaskDto;

class ContractEmitterTest extends TestCase
{
    public function testEmitScalarTypes(): void
    {
        $contract = PropertyWalker::walk(ScalarDto::class);
        $json = ContractEmitter::emit($contract);

        $decoded = json_decode($json, true);
        $this->assertIsArray($decoded);
        $this->assertArrayHasKey('types', $decoded);
        $this->assertArrayHasKey('enums', $decoded);
        $this->assertArrayHasKey('endpoints', $decoded);

        $this->assertCount(1, $decoded['types']);
        $this->assertSame('ScalarDto', $decoded['types'][0]['name']);
        $this->assertCount(4, $decoded['types'][0]['properties']);
        $this->assertFalse($decoded['types'][0]['properties'][0]['optional']);

        $this->assertStringContainsString("\n", $json);
        $this->assertStringContainsString('    ', $json);
    }

    public function testEmitStringEnum(): void
    {
        $contract = PropertyWalker::walk(OrderDto::class);
        $json = ContractEmitter::emit($contract);
        $decoded = json_decode($json, true);

        $this->assertCount(1, $decoded['enums']);
        $this->assertArrayHasKey('values', $decoded['enums'][0]);
        $this->assertArrayNotHasKey('intValues', $decoded['enums'][0]);
        $this->assertSame(['pending', 'active', 'archived'], $decoded['enums'][0]['values']);
    }

    public function testEmitIntEnum(): void
    {
        $contract = PropertyWalker::walk(TaskDto::class);
        $json = ContractEmitter::emit($contract);
        $decoded = json_decode($json, true);

        $this->assertCount(1, $decoded['enums']);
        $this->assertArrayHasKey('intValues', $decoded['enums'][0]);
        $this->assertArrayNotHasKey('values', $decoded['enums'][0]);
        $this->assertSame([1, 2, 3], $decoded['enums'][0]['intValues']);
    }

    public function testEmitNullableType(): void
    {
        $contract = PropertyWalker::walk(NullableDto::class);
        $decoded = json_decode(ContractEmitter::emit($contract), true);
        $prop = $decoded['types'][0]['properties'][0];
        $this->assertSame('nullable', $prop['type']['kind']);
        $this->assertSame('string', $prop['type']['inner']['type']);
    }

    public function testEmitNestedRef(): void
    {
        $contract = PropertyWalker::walk(PersonDto::class);
        $decoded = json_decode(ContractEmitter::emit($contract), true);
        $this->assertCount(2, $decoded['types']);
        $addressProp = $decoded['types'][0]['properties'][1];
        $this->assertSame('ref', $addressProp['type']['kind']);
        $this->assertSame('AddressDto', $addressProp['type']['name']);
    }

    public function testEmitInlineObject(): void
    {
        $contract = PropertyWalker::walk(ResponseDto::class);
        $decoded = json_decode(ContractEmitter::emit($contract), true);
        $prop = $decoded['types'][0]['properties'][0];
        $this->assertSame('inlineObject', $prop['type']['kind']);
        $this->assertCount(2, $prop['type']['properties']);
    }

    public function testNullValuesAreStripped(): void
    {
        $contract = [
            'types' => [
                [
                    'name' => 'Test',
                    'typeParameters' => [],
                    'description' => null,
                    'properties' => [
                        [
                            'name' => 'field',
                            'type' => ['kind' => 'primitive', 'type' => 'string', 'format' => null],
                            'optional' => false,
                        ],
                    ],
                ],
            ],
            'enums' => [],
            'endpoints' => [],
        ];

        $json = ContractEmitter::emit($contract);
        $decoded = json_decode($json, true);

        $this->assertArrayNotHasKey('description', $decoded['types'][0]);
        $this->assertArrayNotHasKey('format', $decoded['types'][0]['properties'][0]['type']);
        $this->assertFalse($decoded['types'][0]['properties'][0]['optional']);
        $this->assertStringNotContainsString('"null"', $json);
        $this->assertStringNotContainsString(': null', $json);
    }
}
