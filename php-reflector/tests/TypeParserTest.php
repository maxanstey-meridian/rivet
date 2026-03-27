<?php

declare(strict_types=1);

namespace Rivet\PhpReflector\Tests;

use PHPUnit\Framework\TestCase;
use Rivet\PhpReflector\TypeParser;

class TypeParserTest extends TestCase
{
    public function testParseString(): void
    {
        $this->assertSame(
            ['kind' => 'primitive', 'type' => 'string'],
            TypeParser::parse('string')
        );
    }

    public function testParseScalars(): void
    {
        $this->assertSame(
            ['kind' => 'primitive', 'type' => 'number', 'format' => 'int32'],
            TypeParser::parse('int')
        );
        $this->assertSame(
            ['kind' => 'primitive', 'type' => 'number', 'format' => 'double'],
            TypeParser::parse('float')
        );
        $this->assertSame(
            ['kind' => 'primitive', 'type' => 'boolean'],
            TypeParser::parse('bool')
        );
        $this->assertSame(
            ['kind' => 'primitive', 'type' => 'unknown'],
            TypeParser::parse('mixed')
        );
    }

    public function testParseNull(): void
    {
        $this->assertSame(
            ['kind' => 'primitive', 'type' => 'null'],
            TypeParser::parse('null')
        );
    }

    public function testParseNullable(): void
    {
        $this->assertSame(
            ['kind' => 'nullable', 'inner' => ['kind' => 'primitive', 'type' => 'string']],
            TypeParser::parse('?string')
        );
    }

    public function testParseClassRef(): void
    {
        $this->assertSame(
            ['kind' => 'ref', 'name' => 'UserDto'],
            TypeParser::parse('UserDto')
        );
    }

    public function testParseQualifiedClassName(): void
    {
        $this->assertSame(
            ['kind' => 'ref', 'name' => 'App\\Dto\\UserDto'],
            TypeParser::parse('App\\Dto\\UserDto')
        );
    }

    public function testParseList(): void
    {
        $this->assertSame(
            ['kind' => 'array', 'element' => ['kind' => 'primitive', 'type' => 'string']],
            TypeParser::parse('list<string>')
        );
    }

    public function testParseArraySingleArg(): void
    {
        $this->assertSame(
            ['kind' => 'array', 'element' => ['kind' => 'primitive', 'type' => 'string']],
            TypeParser::parse('array<string>')
        );
    }

    public function testParseMap(): void
    {
        $this->assertSame(
            ['kind' => 'dictionary', 'value' => ['kind' => 'primitive', 'type' => 'number', 'format' => 'int32']],
            TypeParser::parse('array<string, int>')
        );
    }

    public function testParseUnionWithNull(): void
    {
        $this->assertSame(
            ['kind' => 'nullable', 'inner' => ['kind' => 'primitive', 'type' => 'string']],
            TypeParser::parse('string|null')
        );
    }

    public function testParseUnionNullLeft(): void
    {
        $this->assertSame(
            ['kind' => 'nullable', 'inner' => ['kind' => 'primitive', 'type' => 'string']],
            TypeParser::parse('null|string')
        );
    }

    public function testParseUnionWhitespace(): void
    {
        $this->assertSame(
            ['kind' => 'nullable', 'inner' => ['kind' => 'primitive', 'type' => 'string']],
            TypeParser::parse('string | null')
        );
    }

    public function testParseSingleStringLiteral(): void
    {
        $this->assertSame(
            ['kind' => 'stringUnion', 'values' => ['pending']],
            TypeParser::parse("'pending'")
        );
    }

    public function testParseStringLiteralUnion(): void
    {
        $this->assertSame(
            ['kind' => 'stringUnion', 'values' => ['pending', 'shipped', 'delivered']],
            TypeParser::parse("'pending'|'shipped'|'delivered'")
        );
    }

    public function testParseSimpleShape(): void
    {
        $this->assertSame(
            [
                'kind' => 'inlineObject',
                'properties' => [
                    ['name' => 'name', 'type' => ['kind' => 'primitive', 'type' => 'string']],
                    ['name' => 'age', 'type' => ['kind' => 'primitive', 'type' => 'number', 'format' => 'int32']],
                ],
            ],
            TypeParser::parse('array{name: string, age: int}')
        );
    }

    public function testParseShapeOptionalField(): void
    {
        $result = TypeParser::parse('array{name: string, nickname?: string}');
        $this->assertSame('inlineObject', $result['kind']);
        $this->assertSame('name', $result['properties'][0]['name']);
        $this->assertSame(['kind' => 'primitive', 'type' => 'string'], $result['properties'][0]['type']);
        $this->assertSame('nickname', $result['properties'][1]['name']);
        $this->assertSame(
            ['kind' => 'nullable', 'inner' => ['kind' => 'primitive', 'type' => 'string']],
            $result['properties'][1]['type']
        );
    }

    public function testParseShapeQuotedKey(): void
    {
        $result = TypeParser::parse("array{'created_at': string}");
        $this->assertSame('created_at', $result['properties'][0]['name']);
        $this->assertSame(['kind' => 'primitive', 'type' => 'string'], $result['properties'][0]['type']);
    }

    public function testParseNestedGenerics(): void
    {
        $result = TypeParser::parse('list<array<string, int>>');
        $this->assertSame('array', $result['kind']);
        $this->assertSame('dictionary', $result['element']['kind']);
        $this->assertSame(
            ['kind' => 'primitive', 'type' => 'number', 'format' => 'int32'],
            $result['element']['value']
        );
    }

    public function testParseNullableClassRef(): void
    {
        $this->assertSame(
            ['kind' => 'nullable', 'inner' => ['kind' => 'ref', 'name' => 'UserDto']],
            TypeParser::parse('?UserDto')
        );
    }

    public function testParseGenericWhitespace(): void
    {
        $this->assertSame(
            ['kind' => 'array', 'element' => ['kind' => 'primitive', 'type' => 'string']],
            TypeParser::parse('list< string >')
        );
    }

    public function testParseShapeNested(): void
    {
        $result = TypeParser::parse('array{items: list<string>, meta: array{count: int}}');
        $this->assertSame('inlineObject', $result['kind']);
        $this->assertSame('items', $result['properties'][0]['name']);
        $this->assertSame(['kind' => 'array', 'element' => ['kind' => 'primitive', 'type' => 'string']], $result['properties'][0]['type']);
        $this->assertSame('meta', $result['properties'][1]['name']);
        $this->assertSame('inlineObject', $result['properties'][1]['type']['kind']);
        $this->assertSame('count', $result['properties'][1]['type']['properties'][0]['name']);
        $this->assertSame(
            ['kind' => 'primitive', 'type' => 'number', 'format' => 'int32'],
            $result['properties'][1]['type']['properties'][0]['type']
        );
    }

    public function testParseGenericClassRef(): void
    {
        $this->assertSame(
            ['kind' => 'generic', 'name' => 'Collection', 'typeArgs' => [['kind' => 'ref', 'name' => 'UserDto']]],
            TypeParser::parse('Collection<UserDto>')
        );
    }

    public function testParseEmptyThrows(): void
    {
        $this->expectException(\InvalidArgumentException::class);
        TypeParser::parse('');
    }

    public function testParseMalformedThrows(): void
    {
        $this->expectException(\RuntimeException::class);
        TypeParser::parse('@#$');
    }

    public function testParseIdentifierWithDigits(): void
    {
        $this->assertSame(
            ['kind' => 'ref', 'name' => 'UserV2Dto'],
            TypeParser::parse('UserV2Dto')
        );
    }

    public function testParseTrailingGarbageThrows(): void
    {
        $this->expectException(\RuntimeException::class);
        TypeParser::parse('string GARBAGE');
    }

    public function testParseUnsupportedUnionThrows(): void
    {
        $this->expectException(\RuntimeException::class);
        $this->expectExceptionMessage('Unsupported union type');
        TypeParser::parse('int|string|bool');
    }

    public function testParseSingleIntLiteral(): void
    {
        $this->assertSame(
            ['kind' => 'intUnion', 'values' => [42]],
            TypeParser::parse('42')
        );
    }

    public function testParseIntLiteralUnion(): void
    {
        $this->assertSame(
            ['kind' => 'intUnion', 'values' => [1, 2, 3]],
            TypeParser::parse('1|2|3')
        );
    }

    public function testParseNegativeIntLiteral(): void
    {
        $this->assertSame(
            ['kind' => 'intUnion', 'values' => [-1]],
            TypeParser::parse('-1')
        );
    }
}
