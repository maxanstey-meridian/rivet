<?php

declare(strict_types=1);

namespace Rivet\PhpReflector;

class Diagnostics
{
    /** @var list<array{severity: string, message: string, context: array}> */
    private array $items = [];

    public function warning(string $message, array $context = []): void
    {
        $this->items[] = ['severity' => 'warning', 'message' => $message, 'context' => $context];
    }

    public function error(string $message, array $context = []): void
    {
        $this->items[] = ['severity' => 'error', 'message' => $message, 'context' => $context];
    }

    public function hasErrors(): bool
    {
        foreach ($this->items as $item) {
            if ($item['severity'] === 'error') {
                return true;
            }
        }
        return false;
    }

    public function merge(Diagnostics $other): void
    {
        array_push($this->items, ...$other->items);
    }

    /** @return list<array{severity: string, message: string, context: array}> */
    public function all(): array
    {
        return $this->items;
    }
}
