<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests;

use PHPUnit\Framework\TestCase;
use Rivet\PhpReflector\ContractEmitter;
use Rivet\PhpReflector\PropertyWalker;
use Rivet\PhpReflector\Tests\Fixtures\IntDocblockDto;
use Rivet\PhpReflector\Tests\Fixtures\MetadataDto;
use Rivet\PhpReflector\Tests\Fixtures\NullableDto;
use Rivet\PhpReflector\Tests\Fixtures\OrderDto;
use Rivet\PhpReflector\Tests\Fixtures\PersonDto;
use Rivet\PhpReflector\Tests\Fixtures\PriorityDto;
use Rivet\PhpReflector\Tests\Fixtures\ResponseDto;
use Rivet\PhpReflector\Tests\Fixtures\ScalarDto;
use Rivet\PhpReflector\Tests\Fixtures\TagContainerDto;
use Rivet\PhpReflector\Tests\Fixtures\TaskDto;

class RoundTripJsonTest extends TestCase
{
    public function testFullContractMatchesExpectedStructure(): void
    {
        $contract = PropertyWalker::walk(
            ScalarDto::class,
            NullableDto::class,
            PersonDto::class,
            OrderDto::class,
            TaskDto::class,
            TagContainerDto::class,
            MetadataDto::class,
            ResponseDto::class,
            PriorityDto::class,
            IntDocblockDto::class,
        );

        $json = ContractEmitter::emit($contract);
        $decoded = json_decode($json, true);

        // Top-level structure
        $this->assertArrayHasKey('types', $decoded);
        $this->assertArrayHasKey('enums', $decoded);
        $this->assertArrayHasKey('endpoints', $decoded);
        $this->assertSame([], $decoded['endpoints']);

        // Types: 10 root + AddressDto transitively discovered = 11
        $typeNames = array_column($decoded['types'], 'name');
        $this->assertCount(11, $decoded['types']);
        $this->assertContains('AddressDto', $typeNames);

        // Every type has required structure
        foreach ($decoded['types'] as $type) {
            $this->assertArrayHasKey('name', $type);
            $this->assertArrayHasKey('typeParameters', $type);
            $this->assertIsArray($type['typeParameters']);
            $this->assertArrayHasKey('properties', $type);
            $this->assertIsArray($type['properties']);
            foreach ($type['properties'] as $prop) {
                $this->assertArrayHasKey('name', $prop);
                $this->assertArrayHasKey('type', $prop);
                $this->assertArrayHasKey('optional', $prop);
            }
        }

        // ScalarDto — all primitives
        $scalar = $this->findType($decoded, 'ScalarDto');
        $this->assertSame('primitive', $scalar['properties'][0]['type']['kind']);
        $this->assertSame('string', $scalar['properties'][0]['type']['type']);
        $this->assertSame('number', $scalar['properties'][1]['type']['type']);
        $this->assertSame('int32', $scalar['properties'][1]['type']['format']);

        // NullableDto — nullable wrapping
        $nullable = $this->findType($decoded, 'NullableDto');
        $this->assertSame('nullable', $nullable['properties'][0]['type']['kind']);
        $this->assertSame('string', $nullable['properties'][0]['type']['inner']['type']);

        // TagContainerDto — array kind
        $tags = $this->findType($decoded, 'TagContainerDto');
        $this->assertSame('array', $tags['properties'][0]['type']['kind']);
        $this->assertSame('string', $tags['properties'][0]['type']['element']['type']);

        // MetadataDto — dictionary kind
        $meta = $this->findType($decoded, 'MetadataDto');
        $this->assertSame('dictionary', $meta['properties'][0]['type']['kind']);

        // ResponseDto — inlineObject kind
        $response = $this->findType($decoded, 'ResponseDto');
        $this->assertSame('inlineObject', $response['properties'][0]['type']['kind']);
        $this->assertCount(2, $response['properties'][0]['type']['properties']);

        // OrderDto — ref to Status enum
        $order = $this->findType($decoded, 'OrderDto');
        $statusProp = $this->findProp($order, 'status');
        $this->assertSame('ref', $statusProp['type']['kind']);
        $this->assertSame('Status', $statusProp['type']['name']);

        // TaskDto — ref to Priority int enum
        $task = $this->findType($decoded, 'TaskDto');
        $priorityProp = $this->findProp($task, 'priority');
        $this->assertSame('ref', $priorityProp['type']['kind']);
        $this->assertSame('Priority', $priorityProp['type']['name']);

        // Enums
        $enumNames = array_column($decoded['enums'], 'name');
        $this->assertContains('Status', $enumNames);
        $this->assertContains('Priority', $enumNames);

        $statusEnum = $this->findEnum($decoded, 'Status');
        $this->assertArrayHasKey('values', $statusEnum);
        $this->assertSame(['pending', 'active', 'archived'], $statusEnum['values']);

        $priorityEnum = $this->findEnum($decoded, 'Priority');
        $this->assertArrayHasKey('intValues', $priorityEnum);
        $this->assertSame([1, 2, 3], $priorityEnum['intValues']);

        // PriorityDto — docblock string union
        $priorityDto = $this->findType($decoded, 'PriorityDto');
        $this->assertSame('stringUnion', $priorityDto['properties'][0]['type']['kind']);
        $this->assertSame(['low', 'medium', 'high'], $priorityDto['properties'][0]['type']['values']);

        // IntDocblockDto — docblock int union
        $intDocblock = $this->findType($decoded, 'IntDocblockDto');
        $this->assertSame('intUnion', $intDocblock['properties'][0]['type']['kind']);
        $this->assertSame([1, 2, 3], $intDocblock['properties'][0]['type']['values']);

        // PersonDto — ref to AddressDto
        $person = $this->findType($decoded, 'PersonDto');
        $addressProp = $this->findProp($person, 'address');
        $this->assertSame('ref', $addressProp['type']['kind']);
        $this->assertSame('AddressDto', $addressProp['type']['name']);
    }

    private function findType(array $decoded, string $name): array
    {
        foreach ($decoded['types'] as $type) {
            if ($type['name'] === $name) {
                return $type;
            }
        }
        $this->fail("Type '{$name}' not found");
    }

    private function findProp(array $type, string $name): array
    {
        foreach ($type['properties'] as $prop) {
            if ($prop['name'] === $name) {
                return $prop;
            }
        }
        $this->fail("Property '{$name}' not found in type '{$type['name']}'");
    }

    private function findEnum(array $decoded, string $name): array
    {
        foreach ($decoded['enums'] as $enum) {
            if ($enum['name'] === $name) {
                return $enum;
            }
        }
        $this->fail("Enum '{$name}' not found");
    }
}
