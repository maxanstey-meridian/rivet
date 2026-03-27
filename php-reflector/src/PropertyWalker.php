<?php

declare(strict_types=1);

namespace Rivet\PhpReflector;

class PropertyWalker
{
    /** @var list<array> */
    private array $types = [];

    /** @var list<array> */
    private array $enums = [];

    /** @var array<string, true> */
    private array $visited = [];

    /** @var list<string> */
    private array $queue = [];

    /** @var array<string, true> */
    private array $collectedEnums = [];

    public static function walk(string ...$classNames): array
    {
        $walker = new self();
        foreach ($classNames as $fqcn) {
            $walker->enqueue($fqcn);
        }
        $walker->processQueue();
        return ['types' => $walker->types, 'enums' => $walker->enums, 'endpoints' => []];
    }

    private function enqueue(string $fqcn): void
    {
        if (!isset($this->visited[$fqcn])) {
            $this->visited[$fqcn] = true;
            $this->queue[] = $fqcn;
        }
    }

    private function processQueue(): void
    {
        while ($this->queue !== []) {
            $fqcn = array_shift($this->queue);
            $this->processClass($fqcn);
        }
    }

    private function processClass(string $fqcn): void
    {
        $ref = new \ReflectionClass($fqcn);
        $properties = [];

        foreach ($ref->getProperties(\ReflectionProperty::IS_PUBLIC) as $prop) {
            $properties[] = [
                'name' => $prop->getName(),
                'type' => $this->resolvePropertyType($prop),
                'optional' => false,
            ];
        }

        $this->types[] = [
            'name' => $ref->getShortName(),
            'typeParameters' => [],
            'properties' => $properties,
        ];
    }

    private function resolvePropertyType(\ReflectionProperty $prop): array
    {
        $nativeType = $prop->getType();
        if (!$nativeType instanceof \ReflectionNamedType) {
            return ['kind' => 'primitive', 'type' => 'unknown'];
        }

        $typeName = $nativeType->getName();
        $nullable = $nativeType->allowsNull() && $typeName !== 'null';

        if ($typeName === 'array') {
            $docType = $this->extractVarType($prop);
            if ($docType !== null) {
                $resolved = TypeParser::parse($docType);
            } else {
                trigger_error("Property {$prop->getDeclaringClass()->getName()}::\${$prop->getName()} is array without @var", E_USER_WARNING);
                $resolved = ['kind' => 'primitive', 'type' => 'unknown'];
            }

            if ($nullable) {
                return ['kind' => 'nullable', 'inner' => $resolved];
            }
            return $resolved;
        }

        if (($typeName === 'string' || $typeName === 'int') && ($docType = $this->extractVarType($prop)) !== null) {
            $resolved = TypeParser::parse($docType);
        } elseif (is_subclass_of($typeName, \BackedEnum::class)) {
            $this->collectEnum($typeName);
            $ref = new \ReflectionClass($typeName);
            $resolved = ['kind' => 'ref', 'name' => $ref->getShortName()];
        } elseif (class_exists($typeName)) {
            $this->enqueue($typeName);
            $ref = new \ReflectionClass($typeName);
            $resolved = ['kind' => 'ref', 'name' => $ref->getShortName()];
        } else {
            $resolved = TypeParser::parse($typeName);
        }

        if ($nullable) {
            return ['kind' => 'nullable', 'inner' => $resolved];
        }

        return $resolved;
    }

    private function extractVarType(\ReflectionProperty $prop): ?string
    {
        $doc = $prop->getDocComment();
        if ($doc === false) {
            return null;
        }
        if (preg_match('/@var\s+(.+?)(?:\s*\*\/|\s*$)/m', $doc, $matches)) {
            return trim($matches[1]);
        }
        return null;
    }

    private function collectEnum(string $fqcn): void
    {
        if (isset($this->collectedEnums[$fqcn])) {
            return;
        }
        $this->collectedEnums[$fqcn] = true;

        $ref = new \ReflectionEnum($fqcn);
        $backingType = $ref->getBackingType();
        if (!$backingType instanceof \ReflectionNamedType || $backingType->getName() !== 'string') {
            trigger_error("Int-backed enum {$fqcn} is not supported in contract format", E_USER_WARNING);
            return;
        }

        $values = [];
        foreach ($ref->getCases() as $case) {
            $values[] = $case->getBackingValue();
        }

        $this->enums[] = ['name' => $ref->getShortName(), 'values' => $values];
    }
}
