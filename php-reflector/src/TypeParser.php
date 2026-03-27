<?php

declare(strict_types=1);

namespace Rivet\PhpReflector;

class TypeParser
{
    private string $input;
    private int $pos;

    private const SCALAR_MAP = [
        'string' => ['kind' => 'primitive', 'type' => 'string'],
        'int'    => ['kind' => 'primitive', 'type' => 'number', 'format' => 'int32'],
        'float'  => ['kind' => 'primitive', 'type' => 'number', 'format' => 'double'],
        'bool'   => ['kind' => 'primitive', 'type' => 'boolean'],
        'mixed'  => ['kind' => 'primitive', 'type' => 'unknown'],
        'null'   => ['kind' => 'primitive', 'type' => 'null'],
    ];

    private function __construct(string $input)
    {
        $this->input = $input;
        $this->pos = 0;
    }

    public static function parse(string $expr): array
    {
        if (trim($expr) === '') {
            throw new \InvalidArgumentException('Type expression cannot be empty');
        }
        $parser = new self($expr);
        $result = $parser->parseType();
        $parser->skipWhitespace();
        if ($parser->pos < strlen($parser->input)) {
            $remaining = substr($parser->input, $parser->pos);
            throw new \RuntimeException("Unexpected trailing input: '$remaining'");
        }
        return $result;
    }

    private function parseType(): array
    {
        if ($this->peek() === '?') {
            $this->pos++;
            return ['kind' => 'nullable', 'inner' => $this->parseAtom()];
        }

        $first = $this->parseAtom();
        $this->skipWhitespace();

        if ($this->peek() !== '|') {
            return $first;
        }

        $members = [$first];
        while ($this->peek() === '|') {
            $this->pos++;
            $this->skipWhitespace();
            $members[] = $this->parseAtom();
            $this->skipWhitespace();
        }

        // Extract null from the union if present
        $hasNull = false;
        $nonNull = [];
        foreach ($members as $m) {
            if ($m === ['kind' => 'primitive', 'type' => 'null']) {
                $hasNull = true;
            } else {
                $nonNull[] = $m;
            }
        }

        // Single non-null member + null → nullable
        if ($hasNull && count($nonNull) === 1) {
            return ['kind' => 'nullable', 'inner' => $nonNull[0]];
        }

        // Process remaining members (with or without null extracted)
        $remaining = $hasNull ? $nonNull : $members;

        $allStringUnions = true;
        foreach ($remaining as $m) {
            if ($m['kind'] !== 'stringUnion') {
                $allStringUnions = false;
                break;
            }
        }
        if ($allStringUnions && $remaining !== []) {
            $values = [];
            foreach ($remaining as $m) {
                array_push($values, ...$m['values']);
            }
            $result = ['kind' => 'stringUnion', 'values' => $values];
            return $hasNull ? ['kind' => 'nullable', 'inner' => $result] : $result;
        }

        $allIntUnions = true;
        foreach ($remaining as $m) {
            if ($m['kind'] !== 'intUnion') {
                $allIntUnions = false;
                break;
            }
        }
        if ($allIntUnions && $remaining !== []) {
            $values = [];
            foreach ($remaining as $m) {
                array_push($values, ...$m['values']);
            }
            $result = ['kind' => 'intUnion', 'values' => $values];
            return $hasNull ? ['kind' => 'nullable', 'inner' => $result] : $result;
        }

        throw new \RuntimeException('Unsupported union type: only T|null, string literal, and int literal unions are supported');
    }

    private function parseAtom(): array
    {
        if ($this->peek() === "'") {
            $value = $this->consumeQuotedString();
            return ['kind' => 'stringUnion', 'values' => [$value]];
        }

        $ch = $this->peek();
        if ($ch !== null && (ctype_digit($ch) || ($ch === '-' && $this->pos + 1 < strlen($this->input) && ctype_digit($this->input[$this->pos + 1])))) {
            $value = $this->consumeInteger();
            return ['kind' => 'intUnion', 'values' => [$value]];
        }

        $id = $this->consumeIdentifier();
        if (isset(self::SCALAR_MAP[$id])) {
            return self::SCALAR_MAP[$id];
        }

        if ($this->peek() === '{' && $id === 'array') {
            return $this->parseShapeFields();
        }

        if ($this->peek() === '<') {
            $args = $this->parseGenericArgs();
            if ($id === 'list' || $id === 'array') {
                if (count($args) === 1) {
                    return ['kind' => 'array', 'element' => $args[0]];
                }
                if (count($args) === 2 && $id === 'array') {
                    return ['kind' => 'dictionary', 'value' => $args[1]];
                }
            } else {
                return ['kind' => 'generic', 'name' => $id, 'typeArgs' => $args];
            }
        }

        return ['kind' => 'ref', 'name' => $id];
    }

    private function parseGenericArgs(): array
    {
        $this->expect('<');
        $this->skipWhitespace();
        $args = [$this->parseType()];
        $this->skipWhitespace();
        while ($this->peek() === ',') {
            $this->pos++;
            $this->skipWhitespace();
            $args[] = $this->parseType();
            $this->skipWhitespace();
        }
        $this->expect('>');
        return $args;
    }

    private function expect(string $char): void
    {
        if ($this->peek() !== $char) {
            throw new \RuntimeException("Expected '$char' at position {$this->pos}");
        }
        $this->pos++;
    }

    private function consumeIdentifier(): string
    {
        $start = $this->pos;
        while ($this->pos < strlen($this->input) && (ctype_alnum($this->input[$this->pos]) || $this->input[$this->pos] === '_' || $this->input[$this->pos] === '\\')) {
            $this->pos++;
        }

        if ($this->pos === $start) {
            throw new \RuntimeException("Expected identifier at position {$this->pos}");
        }

        return substr($this->input, $start, $this->pos - $start);
    }

    private function parseShapeFields(): array
    {
        $this->expect('{');
        $this->skipWhitespace();
        $properties = [];
        while ($this->peek() !== '}') {
            $this->skipWhitespace();
            if ($this->peek() === "'") {
                $key = $this->consumeQuotedString();
            } else {
                $key = $this->consumeIdentifier();
            }
            $optional = false;
            if ($this->peek() === '?') {
                $optional = true;
                $this->pos++;
            }
            $this->expect(':');
            $this->skipWhitespace();
            $type = $this->parseType();
            if ($optional) {
                $type = ['kind' => 'nullable', 'inner' => $type];
            }
            $properties[] = ['name' => $key, 'type' => $type];
            $this->skipWhitespace();
            if ($this->peek() === ',') {
                $this->pos++;
                $this->skipWhitespace();
            }
        }
        $this->expect('}');
        return ['kind' => 'inlineObject', 'properties' => $properties];
    }

    private function consumeInteger(): int
    {
        $start = $this->pos;
        if ($this->peek() === '-') {
            $this->pos++;
        }
        while ($this->pos < strlen($this->input) && ctype_digit($this->input[$this->pos])) {
            $this->pos++;
        }
        return (int) substr($this->input, $start, $this->pos - $start);
    }

    private function consumeQuotedString(): string
    {
        $this->expect("'");
        $start = $this->pos;
        while ($this->pos < strlen($this->input) && $this->input[$this->pos] !== "'") {
            $this->pos++;
        }
        $value = substr($this->input, $start, $this->pos - $start);
        $this->expect("'");
        return $value;
    }

    private function peek(): ?string
    {
        return $this->pos < strlen($this->input) ? $this->input[$this->pos] : null;
    }

    private function skipWhitespace(): void
    {
        while ($this->pos < strlen($this->input) && $this->input[$this->pos] === ' ') {
            $this->pos++;
        }
    }
}
