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

    private Diagnostics $diagnostics;

    public static function walk(string ...$classNames): array
    {
        $walker = new self();
        $walker->diagnostics = new Diagnostics();
        foreach ($classNames as $fqcn) {
            $walker->enqueue($fqcn);
        }
        $walker->processQueue();
        return ['types' => $walker->types, 'enums' => $walker->enums, 'endpoints' => [], 'diagnostics' => $walker->diagnostics];
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
                $this->enqueueRefsFromType($resolved, $prop->getDeclaringClass()->getNamespaceName());
            } else {
                $this->diagnostics->warning("Property {$prop->getDeclaringClass()->getName()}::\${$prop->getName()} is array without @var");
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

    private function enqueueRefsFromType(array $typeNode, string $namespace): void
    {
        if ($typeNode['kind'] === 'ref' || $typeNode['kind'] === 'generic') {
            $name = $typeNode['name'];
            $fqcn = $namespace !== '' ? $namespace . '\\' . $name : $name;
            if (is_subclass_of($fqcn, \BackedEnum::class)) {
                $this->collectEnum($fqcn);
            } elseif (class_exists($fqcn)) {
                $this->enqueue($fqcn);
            } elseif ($this->isKnownShortName($name) || $typeNode['kind'] === 'generic') {
                $this->diagnostics->warning("Unresolvable class reference: $name", ['fqcn' => $fqcn]);
            } else {
                $this->diagnostics->error("Unresolvable class reference: $name", ['fqcn' => $fqcn]);
            }
            // For generic types, also recurse into typeArgs
            if (isset($typeNode['typeArgs']) && is_array($typeNode['typeArgs'])) {
                foreach ($typeNode['typeArgs'] as $arg) {
                    $this->enqueueRefsFromType($arg, $namespace);
                }
            }
            return;
        }

        // Recurse into compound types
        foreach (['inner', 'element', 'value'] as $key) {
            if (isset($typeNode[$key]) && is_array($typeNode[$key])) {
                $this->enqueueRefsFromType($typeNode[$key], $namespace);
            }
        }
        if (isset($typeNode['properties']) && is_array($typeNode['properties'])) {
            foreach ($typeNode['properties'] as $prop) {
                if (isset($prop['type'])) {
                    $this->enqueueRefsFromType($prop['type'], $namespace);
                }
            }
        }
    }

    private function isKnownShortName(string $name): bool
    {
        foreach ($this->visited as $fqcn => $_) {
            if ((new \ReflectionClass($fqcn))->getShortName() === $name) {
                return true;
            }
        }
        foreach ($this->collectedEnums as $fqcn => $_) {
            if ((new \ReflectionEnum($fqcn))->getShortName() === $name) {
                return true;
            }
        }
        return false;
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
        $backingName = $backingType instanceof \ReflectionNamedType ? $backingType->getName() : null;

        $values = [];
        foreach ($ref->getCases() as $case) {
            $values[] = $case->getBackingValue();
        }

        if ($backingName === 'string') {
            $this->enums[] = ['name' => $ref->getShortName(), 'values' => $values];
        } elseif ($backingName === 'int') {
            $this->enums[] = ['name' => $ref->getShortName(), 'intValues' => $values];
        }
    }
}
